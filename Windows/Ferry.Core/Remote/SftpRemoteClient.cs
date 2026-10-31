using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Ferry.Core.Remote;

/// <summary>SFTP（パスワード・秘密鍵）。SSH.NET を使う。</summary>
public sealed class SftpRemoteClient : IRemoteClient
{
    public ServerConfig Server { get; }

    private string? _password;
    private SftpClient? _client;
    private readonly KnownHosts _knownHosts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _knownDirectories = new(StringComparer.Ordinal);
    /// <summary>ホスト鍵が前回と違ったので接続を止めた</summary>
    private bool _hostKeyChanged;

    public SftpRemoteClient(ServerConfig server, string? password, KnownHosts knownHosts)
    {
        Server = server;
        _password = password;
        _knownHosts = knownHosts;
    }

    public void SetPassword(string? password)
    {
        _password = password;
        _ = DisconnectAsync();
    }

    public Task<string> HomeDirectoryAsync(CancellationToken cancellation = default) =>
        RunAsync((client, _) => Task.FromResult(client.WorkingDirectory), cancellation);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default) =>
        RunAsync<IReadOnlyList<RemoteItem>>(async (client, token) =>
        {
            var items = new List<RemoteItem>();
            await foreach (var file in client.ListDirectoryAsync(directory, token))
            {
                if (file.Name is "." or "..") continue;
                items.Add(Convert(file, directory));
            }
            return items;
        }, cancellation);

    public Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null,
                              TransferProgress? progress = null, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) =>
        {
            var partPath = localPath + ".ferrypart";
            try
            {
                await using (var output = File.Create(partPath))
                {
                    var reporter = progress is null
                        ? null
                        : new Progress<DownloadFileProgressReport>(p => progress((long)p.TotalBytesDownloaded, expectedSize ?? 0));
                    await client.DownloadFileAsync(remotePath, output, reporter, token);
                }
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
            await EnsureDirectoryAsync(client, RemotePath.Parent(remotePath), token);
            await using var input = File.OpenRead(localPath);
            var total = input.Length;
            var reporter = progress is null
                ? null
                : new Progress<UploadFileProgressReport>(p => progress((long)p.TotalBytesUploaded, total));
            await client.UploadFileAsync(input, remotePath, reporter, token);
            return true;
        }, cancellation);

    public Task MakeDirectoryAsync(string path, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) => { await client.CreateDirectoryAsync(path, token); return true; }, cancellation);

    public Task DeleteFileAsync(string path, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) => { await client.DeleteFileAsync(path, token); return true; }, cancellation);

    public Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) =>
        {
            await client.DeleteDirectoryAsync(path, token);
            lock (_knownDirectories) _knownDirectories.RemoveWhere(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal));
            return true;
        }, cancellation);

    public Task RenameAsync(string from, string to, CancellationToken cancellation = default) =>
        RunAsync(async (client, token) =>
        {
            await client.RenameFileAsync(from, to, token);
            lock (_knownDirectories) _knownDirectories.Clear();
            return true;
        }, cancellation);

    public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default) =>
        RunAsync((client, _) =>
        {
            if (Permissions.FromOctal(mode) is null) throw FerryException.Other($"パーミッション「{mode}」が正しくありません（755 のような 3 桁の数字）。");
            // SSH.NET は 755 のような 10 進の見た目の数を受け取る
            client.ChangePermissions(path, short.Parse(mode[^3..]));
            return Task.FromResult(true);
        }, cancellation);

    // MARK: 内部処理

    /// <summary>フォルダが無ければ 1 段ずつ作る</summary>
    private async Task EnsureDirectoryAsync(SftpClient client, string directory, CancellationToken token)
    {
        var current = "";
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            lock (_knownDirectories)
            {
                if (_knownDirectories.Contains(current)) continue;
            }
            if (!await client.ExistsAsync(current, token))
            {
                try
                {
                    await client.CreateDirectoryAsync(current, token);
                }
                catch (SftpException)
                {
                    // 他の転送が同時に作った場合などは、作れなくても存在すればよい
                    if (!await client.ExistsAsync(current, token)) throw;
                }
            }
            lock (_knownDirectories) _knownDirectories.Add(current);
        }
    }

    private async Task<T> RunAsync<T>(Func<SftpClient, CancellationToken, Task<T>> body, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var client = await ConnectedClientAsync(cancellation);
                try
                {
                    return await body(client, cancellation);
                }
                catch (Exception e) when (attempt == 0 && !client.IsConnected && e is SshConnectionException or SocketException
                                              or ObjectDisposedException && !cancellation.IsCancellationRequested)
                {
                    DisposeClient();
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

    private async Task<SftpClient> ConnectedClientAsync(CancellationToken cancellation)
    {
        if (_client is { IsConnected: true }) return _client;
        DisposeClient();

        var methods = new List<AuthenticationMethod>();
        if (Server.PrivateKeyPath is { Length: > 0 } keyPath)
        {
            // 秘密鍵が指定されている場合は、パスワードを鍵のパスフレーズとして使う
            var path = Environment.ExpandEnvironmentVariables(keyPath);
            if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(_password) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, _password);
            }
            catch (FileNotFoundException e)
            {
                throw FerryException.Other($"秘密鍵のファイル「{keyPath}」が見つかりません。", e);
            }
            catch (SshPassPhraseNullOrEmptyException e)
            {
                throw new FerryException(FerryErrorKind.LoginFailed, "秘密鍵のパスフレーズを入力してください。", e);
            }
            catch (SshException e)
            {
                throw new FerryException(FerryErrorKind.LoginFailed,
                    "秘密鍵を読み込めませんでした。パスフレーズが正しいか、鍵の形式を確認してください。", e);
            }
            methods.Add(new PrivateKeyAuthenticationMethod(Server.Username, key));
        }
        if (_password is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(Server.Username, _password));
            var keyboard = new KeyboardInteractiveAuthenticationMethod(Server.Username);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = _password;
            };
            methods.Add(keyboard);
        }
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(Server.Username));

        var info = new ConnectionInfo(Server.Host, Server.EffectivePort, Server.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
            Encoding = System.Text.Encoding.UTF8,
        };
        var client = new SftpClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(60),
        };
        _hostKeyChanged = false;
        client.HostKeyReceived += (_, e) =>
        {
            var result = _knownHosts.Check(Server.Host, Server.EffectivePort, e.HostKeyName, e.FingerPrintSHA256);
            _hostKeyChanged = result == KnownHosts.Result.Changed;
            e.CanTrust = !_hostKeyChanged;
        };
        try
        {
            await client.ConnectAsync(cancellation);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        return client;
    }

    private Exception Translate(Exception e, CancellationToken cancellation)
    {
        if (e is OperationCanceledException && cancellation.IsCancellationRequested) return FerryException.Cancelled();
        if (_hostKeyChanged) return FerryException.HostKeyChanged(Server.Host);
        switch (e)
        {
            case SshAuthenticationException:
                return FerryException.LoginFailed(e);
            case SftpPermissionDeniedException:
                return FerryException.PermissionDenied(e.Message, e);
            case SftpPathNotFoundException:
                return FerryException.NotFound(e.Message, e);
            case SshOperationTimeoutException:
                return FerryException.Timeout(e);
        }
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => FerryException.HostNotFound(Server.Host, e),
                    SocketError.ConnectionRefused => FerryException.ConnectionRefused(Server.Host, Server.EffectivePort, e),
                    SocketError.TimedOut => FerryException.Timeout(e),
                    _ => FerryException.Other("サーバーとの通信が切れました: " + socket.Message, e),
                };
            }
        }
        if (e is SshConnectionException)
            return FerryException.Other("サーバーとの接続が切れました: " + e.Message, e);
        if (e is SftpException)
            return FerryException.Other("サーバーがエラーを返しました: " + e.Message, e);
        return FerryException.Other(e.Message, e);
    }

    private static RemoteItem Convert(ISftpFile file, string directory)
    {
        var kind = file.IsDirectory ? RemoteItem.ItemKind.Directory
            : file.IsSymbolicLink ? RemoteItem.ItemKind.Symlink
            : RemoteItem.ItemKind.File;
        var permissions = string.Concat(
            file.OwnerCanRead ? "r" : "-", file.OwnerCanWrite ? "w" : "-", file.OwnerCanExecute ? "x" : "-",
            file.GroupCanRead ? "r" : "-", file.GroupCanWrite ? "w" : "-", file.GroupCanExecute ? "x" : "-",
            file.OthersCanRead ? "r" : "-", file.OthersCanWrite ? "w" : "-", file.OthersCanExecute ? "x" : "-");
        return new RemoteItem
        {
            Name = file.Name,
            Path = RemotePath.Join(directory, file.Name),
            Kind = kind,
            Size = kind == RemoteItem.ItemKind.Directory ? null : file.Length,
            Modified = DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc),
            Permissions = permissions,
        };
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { DisposeClient(); }
        finally { _gate.Release(); }
    }

    private void DisposeClient()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) _client.Disconnect();
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
}
