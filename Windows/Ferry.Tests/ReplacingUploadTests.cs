using Ferry.Core;
using Ferry.Core.Remote;

namespace Ferry.Tests;

/// <summary>置き換えの手順で出した操作を順に記録する偽のサーバー（ファイルの有無も覚える）</summary>
internal sealed class RecordingSteps : ReplacingUpload.ISteps
{
    public List<string> Log { get; } = [];
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
    /// <summary>置き換え先があると名前を変えられないサーバー（OpenSSH の通常の rename、IIS の FTP など）</summary>
    public bool RenameFailsIfExists { get; set; }
    public Func<string, Exception?> UploadError { get; set; } = _ => null;
    public Func<int, Exception?> RenameError { get; set; } = _ => null;
    public Exception? DeleteError { get; set; }
    public bool CleanupFails { get; set; }
    private int _renames;

    public Task UploadAsync(string remotePath, CancellationToken cancellation)
    {
        Log.Add($"upload {remotePath}");
        Files.Add(remotePath);
        if (UploadError(remotePath) is { } error) return Task.FromException(error);
        cancellation.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RenameAsync(string from, string to, CancellationToken cancellation)
    {
        Log.Add($"rename {from} {to}");
        if (RenameError(_renames++) is { } error) return Task.FromException(error);
        if (!Files.Contains(from) || (RenameFailsIfExists && Files.Contains(to))) return Task.FromException(new IOException("rename failed"));
        Files.Remove(from);
        Files.Add(to);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellation)
    {
        Log.Add($"delete {path}");
        if (DeleteError is { } error) return Task.FromException(error);
        Files.Remove(path);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellation)
    {
        Log.Add($"exists {path}");
        return Task.FromResult(Files.Contains(path));
    }

    public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation)
    {
        Log.Add($"chmod {mode} {path}");
        return Task.CompletedTask;
    }

    public Task SetModifiedTimeAsync(string path, CancellationToken cancellation)
    {
        Log.Add($"mtime {path}");
        return Task.CompletedTask;
    }

    public Task<bool> TryCleanupAsync(string path)
    {
        Log.Add($"cleanup {path}");
        if (CleanupFails) return Task.FromResult(false);
        Files.Remove(path);
        return Task.FromResult(true);
    }

    public bool IsPermissionDenied(Exception e) => e is UnauthorizedAccessException;
}

public class ReplacingUploadTests
{
    private const string Target = "/www/wp-config.php";
    private const string Part = "/www/.wp-config.php.ferrypart";

    private static RecordingSteps Server() => new() { Files = { Target } };

    [Fact]
    public void 一時ファイルの名前は同じフォルダの点で始まる名前()
    {
        Assert.Equal(Part, ReplacingUpload.TemporaryPathFor(Target));
        Assert.Equal("/..htaccess.ferrypart", ReplacingUpload.TemporaryPathFor("/.htaccess"));
    }

    [Fact]
    public async Task 一時ファイルに送ってから置き換え権限と更新日時は置き換えた後の名前に()
    {
        var server = Server();
        await new ReplacingUpload(Target, "rw-------").RunAsync(server, default);

        Assert.Equal([$"upload {Part}", $"rename {Part} {Target}", $"chmod 600 {Target}", $"mtime {Target}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    [Fact]
    public async Task 権限が分からないときは変えない()
    {
        var server = Server();
        await new ReplacingUpload(Target, null).RunAsync(server, default);

        Assert.DoesNotContain(server.Log, l => l.StartsWith("chmod"));
        Assert.Equal($"mtime {Target}", server.Log[^1]);
    }

    [Fact]
    public async Task 置き換え先があると名前を変えられないサーバーでは元を消してから名前を変える()
    {
        var server = Server();
        server.RenameFailsIfExists = true;
        await new ReplacingUpload(Target, "rwxr-xr-x").RunAsync(server, default);

        Assert.Equal([$"upload {Part}", $"rename {Part} {Target}", $"exists {Part}", $"delete {Target}",
                      $"rename {Part} {Target}", $"chmod 755 {Target}", $"mtime {Target}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    [Fact]
    public async Task 元を消せなければ一時ファイルを消して元のまま残す()
    {
        var server = Server();
        server.RenameFailsIfExists = true;
        server.DeleteError = new IOException("delete failed");
        var replace = new ReplacingUpload(Target, null);

        var error = await Assert.ThrowsAsync<IOException>(() => replace.RunAsync(server, default));
        Assert.Equal("rename failed", error.Message);
        Assert.Equal($"cleanup {Part}", server.Log[^1]);
        Assert.Equal([Target], server.Files);
        Assert.Contains("元のまま", replace.Explain(FerryException.Other("x")).Message);
    }

    [Fact]
    public async Task 元を消した後に名前を変えられなければ一時ファイルを残してその名前を伝える()
    {
        var server = Server();
        server.RenameFailsIfExists = true;
        server.RenameError = n => n == 1 ? new IOException("second rename failed") : null;
        var replace = new ReplacingUpload(Target, null);

        await Assert.ThrowsAsync<IOException>(() => replace.RunAsync(server, default));
        Assert.DoesNotContain(server.Log, l => l.StartsWith("cleanup"));
        Assert.Equal([Part], server.Files);
        var message = replace.Explain(FerryException.Other("原因")).Message;
        Assert.Contains(".wp-config.php.ferrypart", message);
        Assert.Contains("削除済み", message);
    }

    [Fact]
    public async Task 送っている途中で中止すると一時ファイルを消して元はそのまま()
    {
        var server = Server();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var replace = new ReplacingUpload(Target, "rw-r--r--");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace.RunAsync(server, cancellation.Token));
        Assert.Equal([$"upload {Part}", $"cleanup {Part}"], server.Log);
        Assert.Equal([Target], server.Files);
        // 中止のエラーはそのまま
        var cancelled = FerryException.Cancelled();
        Assert.Same(cancelled, replace.Explain(cancelled));
    }

    [Fact]
    public async Task 送っている途中で失敗したら一時ファイルを消す()
    {
        var server = Server();
        server.UploadError = _ => new IOException("broken");

        await Assert.ThrowsAsync<IOException>(() => new ReplacingUpload(Target, null).RunAsync(server, default));
        Assert.Equal([$"upload {Part}", $"cleanup {Part}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    [Fact]
    public async Task 接続が切れてやり直すときは送り終えた一時ファイルを送り直さない()
    {
        var server = Server();
        server.RenameError = n => n == 0 ? new IOException("connection lost") : null;
        // 接続が切れているので、存在の確認も後片付けもできない
        server.CleanupFails = true;
        var replace = new ReplacingUpload(Target, null);
        var steps = new FailingExistsSteps(server);

        await Assert.ThrowsAsync<IOException>(() => replace.RunAsync(steps, default));
        Assert.Equal([Part, Target], server.Files.Order(StringComparer.Ordinal));

        // RunAsync の再試行（つなぎ直した後）
        steps.ExistsFails = false;
        server.Log.Clear();
        await replace.RunAsync(steps, default);
        Assert.Equal([$"rename {Part} {Target}", $"mtime {Target}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    [Fact]
    public async Task 名前の変更の応答が届かなかったが済んでいたときは元を消さない()
    {
        var server = Server();
        // サーバーでは名前の変更が済んだが、応答の前に接続が切れた
        server.RenameError = n =>
        {
            if (n != 0) return null;
            server.Files.Remove(Part);
            return new IOException("connection lost");
        };

        await new ReplacingUpload(Target, "rw-r--r--").RunAsync(server, default);
        Assert.DoesNotContain(server.Log, l => l.StartsWith("delete"));
        Assert.Equal([$"upload {Part}", $"rename {Part} {Target}", $"exists {Part}", $"chmod 644 {Target}", $"mtime {Target}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    [Fact]
    public async Task フォルダに書けず一時ファイルを作れないときは直接上書きする()
    {
        var server = Server();
        server.UploadError = path => path == Part ? new UnauthorizedAccessException() : null;

        await new ReplacingUpload(Target, "rw-------").RunAsync(server, default);
        Assert.Equal([$"upload {Part}", $"cleanup {Part}", $"upload {Target}", $"chmod 600 {Target}", $"mtime {Target}"], server.Log);
        Assert.Equal([Target], server.Files);
    }

    /// <summary>接続が切れていて存在の確認もできない状態をまねる</summary>
    private sealed class FailingExistsSteps(RecordingSteps inner) : ReplacingUpload.ISteps
    {
        public bool ExistsFails { get; set; } = true;
        public Task UploadAsync(string remotePath, CancellationToken cancellation) => inner.UploadAsync(remotePath, cancellation);
        public Task RenameAsync(string from, string to, CancellationToken cancellation) => inner.RenameAsync(from, to, cancellation);
        public Task DeleteAsync(string path, CancellationToken cancellation) => inner.DeleteAsync(path, cancellation);
        public Task<bool> FileExistsAsync(string path, CancellationToken cancellation) =>
            ExistsFails ? Task.FromException<bool>(new IOException("connection lost")) : inner.FileExistsAsync(path, cancellation);
        public Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation) => inner.ChangePermissionsAsync(path, mode, cancellation);
        public Task SetModifiedTimeAsync(string path, CancellationToken cancellation) => inner.SetModifiedTimeAsync(path, cancellation);
        public Task<bool> TryCleanupAsync(string path) => inner.TryCleanupAsync(path);
        public bool IsPermissionDenied(Exception e) => inner.IsPermissionDenied(e);
    }
}
