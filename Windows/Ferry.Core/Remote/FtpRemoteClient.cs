using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Ferry.Core.Remote;

/// <summary>FTP / FTPS（Explicit・Implicit）。FluentFTP を使う。</summary>
public sealed class FtpRemoteClient : IRemoteClient
{
    public ServerConfig Server { get; }

    private string? _password;
    private AsyncFtpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>LIST だけでは .htaccess などの隠しファイルを返さないサーバー（vsFTPd など）があるので、
    /// MLSD 非対応のときは LIST -a を使う。受け付けないサーバーでは LIST に戻す</summary>
    private bool _supportsListAll = true;
    /// <summary>アップロード時に作成済み・存在を確認済みのフォルダ</summary>
    private readonly HashSet<string> _knownDirectories = new(StringComparer.Ordinal);
    /// <summary>証明書の検証に失敗した理由（接続のエラーを分かりやすくするため）</summary>
    private string? _certificateProblem;

    public FtpRemoteClient(ServerConfig server, string? password)
    {
        Server = server;
        _password = password;
    }

    public void SetPassword(string? password)
    {
        _password = password;
        // 次の操作でつなぎ直す
        _ = DisconnectAsync();
    }

    public Task<string> HomeDirectoryAsync(CancellationToken cancellation = default) =>
        RunAsync(async (client, token) => await client.GetWorkingDirectory(token), cancellation);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default) =>
        RunAsync<IReadOnlyList<RemoteItem>>(async (client, token) =>
        {
            FtpListItem[] items;
            if (client.HasFeature(FtpCapability.MLST))
            {
                items = await client.GetListing(directory, FtpListOption.Auto, token);
            }
            else
            {
                items = [];
                if (_supportsListAll)
                {
                    try
                    {
                        items = await client.GetListing(directory, FtpListOption.AllFiles, token);
                    }
                    catch (FtpCommandException)
                    {
                        _supportsListAll = false;
                    }
                }
                // -a をパスとして解釈して空を返すサーバーもあるので、空のときは LIST で取り直す
                if (items.Length == 0)
                {
                    items = await client.GetListing(directory, FtpListOption.ForceList, token);
                }
            }
            return items
                .Where(item => item.Name is not ("." or ".."))
                .Select(item => Convert(item, directory))
                .ToList();
        }, cancellation);

    public Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null,
                              TransferProgress? progress = null, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) =>
        {
            var partPath = localPath + ".ferrypart";
            var reporter = progress is null ? null : new Progress(p => progress(p.TransferredBytes, expectedSize ?? 0));
            try
            {
                var status = await client.DownloadFile(partPath, remotePath, FtpLocalExists.Overwrite, FtpVerify.None,
                                                       reporter, token);
                if (status == FtpStatus.Failed)
                    throw FerryException.Other($"「{RemotePath.Name(remotePath)}」をダウンロードできませんでした。");
                // 0 バイトのファイルは FluentFTP が作らないことがある
                if (!File.Exists(partPath)) File.WriteAllBytes(partPath, []);
                File.Move(partPath, localPath, overwrite: true);
            }
            catch
            {
                TryDelete(partPath);
                throw;
            }
            return true;
        }, cancellation);

    public Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                            CancellationToken cancellation = default) =>
        RunAsync(async (client, token) =>
        {
            var total = new FileInfo(localPath).Length;
            await EnsureDirectoryAsync(client, RemotePath.Parent(remotePath), token);
            var reporter = progress is null ? null : new Progress(p => progress(p.TransferredBytes, total));
            var status = await client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, false, FtpVerify.None,
                                                 reporter, token);
            if (status == FtpStatus.Failed)
                throw FerryException.Other($"「{Path.GetFileName(localPath)}」をアップロードできませんでした。");
            return true;
        }, cancellation);

    public Task MakeDirectoryAsync(string path, CancellationToken cancellation = default) =>
        ExecuteAsync($"MKD {path}", cancellation);

    public Task DeleteFileAsync(string path, CancellationToken cancellation = default) =>
        ExecuteAsync($"DELE {path}", cancellation);

    public async Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default)
    {
        await ExecuteAsync($"RMD {path}", cancellation);
        lock (_knownDirectories) _knownDirectories.RemoveWhere(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal));
    }

    public async Task RenameAsync(string from, string to, CancellationToken cancellation = default)
    {
        await ExecuteAsync($"RNFR {from}", cancellation);
        await ExecuteAsync($"RNTO {to}", cancellation);
        lock (_knownDirectories) _knownDirectories.Clear();
    }

    public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default) =>
        ExecuteAsync($"SITE CHMOD {mode} {path}", cancellation);

    // MARK: 内部処理

    /// <summary>
    /// フォルダが無ければ 1 段ずつ作る。/a/b/c を 1 回の MKD で作ろうとすると、
    /// 途中が無いサーバーでは失敗する（Mac 版 v0.7 で踏んだ）
    /// </summary>
    private async Task EnsureDirectoryAsync(AsyncFtpClient client, string directory, CancellationToken token)
    {
        var current = "";
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            lock (_knownDirectories)
            {
                if (_knownDirectories.Contains(current)) continue;
            }
            if (!await client.DirectoryExists(current, token))
            {
                var reply = await client.Execute($"MKD {current}", token);
                // 他の転送が同時に作った場合などは、作れなくても存在すればよい
                if (!reply.Success && !await client.DirectoryExists(current, token))
                    throw new FtpCommandException(reply);
            }
            lock (_knownDirectories) _knownDirectories.Add(current);
        }
    }

    private Task ExecuteAsync(string command, CancellationToken cancellation) =>
        RunAsync(async (client, token) =>
        {
            var reply = await client.Execute(command, token);
            if (!reply.Success) throw new FtpCommandException(reply);
            return true;
        }, cancellation);

    private async Task<T> RunAsync<T>(Func<AsyncFtpClient, CancellationToken, Task<T>> body, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // 接続が切れていたら（サーバー側のタイムアウトなど）、1 回だけつなぎ直してやり直す
            for (var attempt = 0; ; attempt++)
            {
                var client = await ConnectedClientAsync(cancellation);
                try
                {
                    return await body(client, cancellation);
                }
                catch (Exception e) when (attempt == 0 && IsConnectionLost(e, client) && !cancellation.IsCancellationRequested)
                {
                    await DisposeClientAsync();
                }
            }
        }
        catch (FerryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Translate(e, cancellation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsConnectionLost(Exception e, AsyncFtpClient client) =>
        !client.IsConnected || e is FtpMissingSocketException
        || (e is System.IO.IOException or SocketException && e is not FileNotFoundException and not DirectoryNotFoundException);

    private async Task<AsyncFtpClient> ConnectedClientAsync(CancellationToken cancellation)
    {
        if (_client is { IsConnected: true }) return _client;
        await DisposeClientAsync();

        var client = new AsyncFtpClient(Server.Host, Server.Username, _password ?? "", Server.EffectivePort);
        var config = client.Config;
        config.EncryptionMode = Server.ServerProtocol switch
        {
            ServerProtocol.Ftps => FtpEncryptionMode.Explicit,
            ServerProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };
        config.DataConnectionEncryption = true;
        // PASV 応答の IP は無視して制御接続と同じホストへつなぐ（NAT 内のサーバー対策）
        config.DataConnectionType = Server.PassiveMode ? FtpDataConnectionType.PASVEX : FtpDataConnectionType.AutoActive;
        config.ConnectTimeout = 20_000;
        config.DataConnectionConnectTimeout = 20_000;
        // 60 秒間通信が止まったら失敗とみなす
        config.ReadTimeout = 60_000;
        config.DataConnectionReadTimeout = 60_000;
        config.SocketKeepAlive = true;
        config.RetryAttempts = 1;
        config.TimeConversion = FtpDate.UTC;
        config.ServerTimeZone = TimeZoneInfo.Utc;
        config.ClientTimeZone = TimeZoneInfo.Utc;
        // ファイル名は UTF-8 で扱う（OPTS UTF8 ON は FluentFTP が送る。非対応のサーバーでは無視される）
        client.Encoding = Encoding.UTF8;

        _certificateProblem = null;
        client.ValidateCertificate += (_, e) =>
        {
            if (!Server.VerifyCertificate || e.PolicyErrors == SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }
            _certificateProblem = DescribeCertificateProblem(e.PolicyErrors);
            e.Accept = false;
        };

        var started = DateTime.UtcNow;
        try
        {
            await client.Connect(cancellation);
        }
        catch (Exception e) when (e is System.IO.IOException or SocketException or TimeoutException
                                  && !cancellation.IsCancellationRequested && _certificateProblem is null)
        {
            client.Dispose();
            // FluentFTP は理由を「Failed to connect to host.」にまとめてしまうので、自分で調べ直す
            throw await DiagnoseConnectFailureAsync(e, DateTime.UtcNow - started);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        // IPv6 では PASV が使えないので EPSV にする
        if (Server.PassiveMode && client.SocketRemoteEndPoint?.AddressFamily == AddressFamily.InterNetworkV6)
        {
            config.DataConnectionType = FtpDataConnectionType.EPSV;
        }
        _client = client;
        return client;
    }

    /// <summary>接続できなかった理由を、ホスト名の解決とかかった時間から判断する</summary>
    private async Task<FerryException> DiagnoseConnectFailureAsync(Exception e, TimeSpan elapsed)
    {
        if (FindSocketException(e) is { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
            return FerryException.HostNotFound(Server.Host, e);
        try
        {
            using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if ((await Dns.GetHostAddressesAsync(Server.Host, lookup.Token)).Length == 0)
                return FerryException.HostNotFound(Server.Host, e);
        }
        catch (SocketException)
        {
            return FerryException.HostNotFound(Server.Host, e);
        }
        catch (OperationCanceledException)
        {
            return FerryException.Timeout(e);
        }
        // 接続のタイムアウト（20 秒）近くかかったなら応答が無い、すぐ失敗したなら断られた
        return elapsed >= TimeSpan.FromSeconds(15)
            ? FerryException.Timeout(e)
            : FerryException.ConnectionRefused(Server.Host, Server.EffectivePort, e);
    }

    private static string DescribeCertificateProblem(SslPolicyErrors errors)
    {
        var reasons = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) reasons.Add("証明書のホスト名が接続先と一致しません");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) reasons.Add("自己署名か、信頼できない発行元の証明書です");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) reasons.Add("証明書がありません");
        return reasons.Count > 0 ? string.Join("・", reasons) : errors.ToString();
    }

    private Exception Translate(Exception e, CancellationToken cancellation)
    {
        if (e is OperationCanceledException && cancellation.IsCancellationRequested) return FerryException.Cancelled();
        if (_certificateProblem is { } problem
            && (e is FtpInvalidCertificateException or AuthenticationException || e.InnerException is AuthenticationException))
            return FerryException.UntrustedCertificate(problem, e);

        var connected = _client?.IsConnected == true;
        switch (e)
        {
            case FtpAuthenticationException auth:
                return auth.CompletionCode is "530" or null
                    ? FerryException.LoginFailed(e)
                    : FerryException.Other("ログインできませんでした: " + auth.Message, e);
            case FtpInvalidCertificateException:
                return FerryException.UntrustedCertificate("検証に失敗しました", e);
            case FtpSecurityNotAvailableException:
                return FerryException.Other(
                    "サーバーが FTPS（暗号化）に対応していません。方式を「FTP」にするか、サーバーの設定を確認してください。", e);
            case FtpCommandException command:
                return FromReply(command.CompletionCode, command.Message, e);
            case FtpException { InnerException: { } inner } when inner is not FtpException:
                return Translate(inner, cancellation);
        }
        if (FindSocketException(e) is { } socket)
        {
            if (socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
                return FerryException.HostNotFound(Server.Host, e);
            if (connected) return FerryException.DataConnectionFailed(e);
            if (socket.SocketErrorCode == SocketError.ConnectionRefused)
                return FerryException.ConnectionRefused(Server.Host, Server.EffectivePort, e);
            if (socket.SocketErrorCode == SocketError.TimedOut) return FerryException.Timeout(e);
        }
        if (e is TimeoutException || e.InnerException is TimeoutException)
            return connected ? FerryException.DataConnectionFailed(e) : FerryException.Timeout(e);
        return FerryException.Other(e.Message, e);
    }

    /// <summary>FTP の応答コードから分類する（550 は「無い」と「権限が無い」の両方に使われるので文面も見る）</summary>
    internal static FerryException FromReply(string? code, string message, Exception? inner = null)
    {
        var lower = message.ToLowerInvariant();
        if (code == "530") return FerryException.LoginFailed(inner);
        if (lower.Contains("permission") || lower.Contains("denied") || lower.Contains("not allowed") || code is "553" or "532")
            return FerryException.PermissionDenied(message, inner);
        if (code == "550" && (lower.Contains("no such") || lower.Contains("not found") || lower.Contains("not exist")))
            return FerryException.NotFound(message, inner);
        if (code is "425" or "426") return FerryException.DataConnectionFailed(inner);
        return FerryException.Other($"サーバーがエラーを返しました: {code} {message}".TrimEnd(), inner);
    }

    private static SocketException? FindSocketException(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket) return socket;
        }
        return null;
    }

    private static RemoteItem Convert(FtpListItem item, string directory)
    {
        var kind = item.Type switch
        {
            FtpObjectType.Directory => RemoteItem.ItemKind.Directory,
            FtpObjectType.Link => RemoteItem.ItemKind.Symlink,
            _ => RemoteItem.ItemKind.File,
        };
        string? permissions = null;
        if (item.RawPermissions is { Length: 10 } raw) permissions = raw[1..];
        else if (item.RawPermissions is { Length: 9 } raw9) permissions = raw9;
        else if (item.Chmod > 0) permissions = Permissions.FromOctal(item.Chmod.ToString("000"));
        return new RemoteItem
        {
            Name = item.Name,
            Path = RemotePath.Join(directory, item.Name),
            Kind = kind,
            Size = kind == RemoteItem.ItemKind.Directory || item.Size < 0 ? null : item.Size,
            Modified = item.Modified == DateTime.MinValue ? null : DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc),
            Permissions = permissions,
            SymlinkTarget = item.LinkTarget,
        };
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await DisposeClientAsync(); }
        finally { _gate.Release(); }
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) await _client.Disconnect();
        }
        catch (Exception) { }
        _client.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private sealed class Progress(Action<FtpProgress> report) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => report(value);
    }
}
