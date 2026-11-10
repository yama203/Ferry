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
    /// <summary>ホスト鍵が前回と違ったので接続を止めた（そのときサーバーが出した鍵。画面で見せて承認してもらう）</summary>
    private HostKeyInfo? _changedHostKey;

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

    public async Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                                  CancellationToken cancellation = default, UploadOptions? options = null)
    {
        if (options is not { ViaTemporaryFile: true })
        {
            await RunAsync(async (client, token) =>
            {
                var modified = File.GetLastWriteTimeUtc(localPath);
                await EnsureDirectoryAsync(client, RemotePath.Parent(remotePath), token);
                await UploadFileAsync(client, localPath, remotePath, progress, token);
                // SSH.NET の属性の変更は同期の API なので、UI スレッドを止めないよう別のスレッドで
                await Task.Run(() => SetModifiedTime(client, remotePath, modified), CancellationToken.None);
                return true;
            }, cancellation);
            return;
        }
        // 接続が切れてやり直すときも同じ手順を使い、済んだところから続ける
        var replace = new ReplacingUpload(remotePath, options.Permissions);
        try
        {
            await RunAsync(async (client, token) =>
            {
                await EnsureDirectoryAsync(client, RemotePath.Parent(remotePath), token);
                await replace.RunAsync(new ReplaceSteps(this, client, localPath, progress), token);
                return true;
            }, cancellation);
        }
        catch (FerryException e)
        {
            throw replace.Explain(e);
        }
    }

    private static async Task UploadFileAsync(SftpClient client, string localPath, string remotePath,
                                              TransferProgress? progress, CancellationToken token)
    {
        await using var input = File.OpenRead(localPath);
        var total = input.Length;
        var reporter = progress is null
            ? null
            : new Progress<UploadFileProgressReport>(p => progress((long)p.TotalBytesUploaded, total));
        await client.UploadFileAsync(input, remotePath, reporter, token);
    }

    /// <summary>更新日時をローカルのファイルに合わせる（同期や「新しい場合のみ上書き」で、アップロードしたものが新しく見えないように）</summary>
    private static void SetModifiedTime(SftpClient client, string remotePath, DateTime modifiedUtc)
    {
        try
        {
            client.SetLastWriteTimeUtc(remotePath, modifiedUtc);
        }
        catch (Exception)
        {
            // 属性を変えられないサーバーもある。アップロードは済んでいるので成功として扱う
        }
    }

    /// <summary>posix-rename（置き換え先があっても置き換える）を使うか。テストで通常の rename の場合を確かめるために切り替える</summary>
    internal bool UsePosixRename { get; set; } = true;

    /// <summary>一時ファイルへ送ってから置き換えるときの SFTP の操作</summary>
    private sealed class ReplaceSteps(SftpRemoteClient owner, SftpClient client, string localPath, TransferProgress? progress)
        : ReplacingUpload.ISteps
    {
        private SftpClient _client = client;
        /// <summary>送る前に読んでおく（送っている間にエディタで保存されても、送った内容の日時にする）</summary>
        private readonly DateTime _modified = File.GetLastWriteTimeUtc(localPath);

        public Task UploadAsync(string remotePath, CancellationToken cancellation) =>
            UploadFileAsync(_client, localPath, remotePath, progress, cancellation);

        public async Task RenameAsync(string from, string to, CancellationToken cancellation)
        {
            // 通常の SFTP の rename は置き換え先があると失敗する（OpenSSH など）ので、対応していれば posix-rename を使う
            if (owner.UsePosixRename)
            {
                try
                {
                    // 同期の API なので、UI スレッドを止めないよう別のスレッドで
                    await Task.Run(() => _client.RenameFile(from, to, isPosix: true), CancellationToken.None);
                    return;
                }
                catch (NotSupportedException) { }
            }
            await _client.RenameFileAsync(from, to, cancellation);
        }

        public Task DeleteAsync(string path, CancellationToken cancellation) => _client.DeleteFileAsync(path, cancellation);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellation) => _client.ExistsAsync(path, cancellation);

        public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation) =>
            // 同期の API なので、UI スレッドを止めないよう別のスレッドで
            Task.Run(() =>
            {
                try
                {
                    // SSH.NET は 755 のような 10 進の見た目の数を受け取る
                    _client.ChangePermissions(path, short.Parse(mode[^3..]));
                }
                catch (Exception)
                {
                    // 権限を変えられないサーバーもある。置き換えは済んでいるので成功として扱う
                }
            }, CancellationToken.None);

        public Task SetModifiedTimeAsync(string path, CancellationToken cancellation) =>
            Task.Run(() => SetModifiedTime(_client, path, _modified), CancellationToken.None);

        public async Task<bool> TryCleanupAsync(string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt > 0 || !_client.IsConnected) _client = await owner.ReconnectAsync(timeout.Token);
                    await _client.DeleteFileAsync(path, timeout.Token);
                    return true;
                }
                catch (SftpPathNotFoundException)
                {
                    return true;
                }
                catch (SftpException)
                {
                    // サーバーに断られた（権限など）。つなぎ直しても同じ
                    return false;
                }
                catch (Exception) { }
            }
            return false;
        }

        public bool IsPermissionDenied(Exception e) => e is SftpPermissionDeniedException;
    }

    /// <summary>接続をつなぎ直す（一時ファイルの後片付け用。RunAsync の中から呼ぶ）</summary>
    private async Task<SftpClient> ReconnectAsync(CancellationToken cancellation)
    {
        DisposeClient();
        return await ConnectedClientAsync(cancellation);
    }

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
        _changedHostKey = null;
        client.HostKeyReceived += (_, e) =>
        {
            var result = _knownHosts.Check(Server.Host, Server.EffectivePort, e.HostKeyName, e.FingerPrintSHA256);
            _changedHostKey = result == KnownHosts.Result.Changed ? new HostKeyInfo(e.HostKeyName, e.FingerPrintSHA256) : null;
            e.CanTrust = _changedHostKey is null;
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
        if (_changedHostKey is { } changed) return FerryException.HostKeyChanged(Server.Host, changed);
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
