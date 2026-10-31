namespace Ferry.Core.Remote;

/// <summary>転送の進み具合（done バイト / total バイト。total が分からないときは 0）</summary>
public delegate void TransferProgress(long done, long total);

/// <summary>
/// 1 つのサーバーへの接続。操作は 1 つずつ順に実行され、接続は操作間で使い回す。
/// 並行して転送したい場合や、一覧と転送を分けたい場合はクライアントを複数作る。
/// </summary>
public interface IRemoteClient : IAsyncDisposable
{
    ServerConfig Server { get; }

    /// <summary>パスワードを後から設定する（接続時に入力された場合など）。次の接続から使われる。</summary>
    void SetPassword(string? password);

    /// <summary>ログイン直後のフォルダ（ホームディレクトリ）。ログインの確認も兼ねる。</summary>
    Task<string> HomeDirectoryAsync(CancellationToken cancellation = default);

    /// <summary>フォルダの中身（隠しファイルも含む。"." と ".." は含まない）</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellation = default);

    /// <summary>"&lt;ファイル名&gt;.ferrypart" に書いてから置き換える（途中で止まっても元のファイルを壊さない）</summary>
    Task DownloadAsync(string remotePath, string localPath, long? expectedSize = null,
                       TransferProgress? progress = null, CancellationToken cancellation = default);

    /// <summary>アップロード先のフォルダが無ければ 1 段ずつ作る</summary>
    Task UploadAsync(string localPath, string remotePath, TransferProgress? progress = null,
                     CancellationToken cancellation = default);

    Task MakeDirectoryAsync(string path, CancellationToken cancellation = default);
    Task DeleteFileAsync(string path, CancellationToken cancellation = default);
    /// <summary>空のフォルダを削除する。中身ごと消す場合は DeleteRecursivelyAsync を使う。</summary>
    Task RemoveDirectoryAsync(string path, CancellationToken cancellation = default);
    Task RenameAsync(string from, string to, CancellationToken cancellation = default);
    /// <summary>mode は "755" のような 8 進数の文字列</summary>
    Task ChangePermissionsAsync(string path, string mode, CancellationToken cancellation = default);
}

public static class RemoteClientExtensions
{
    public static async Task DeleteRecursivelyAsync(this IRemoteClient client, RemoteItem item,
                                                    CancellationToken cancellation = default)
    {
        if (!item.IsDirectory)
        {
            await client.DeleteFileAsync(item.Path, cancellation);
            return;
        }
        foreach (var child in await client.ListAsync(item.Path, cancellation))
        {
            await client.DeleteRecursivelyAsync(child, cancellation);
        }
        await client.RemoveDirectoryAsync(item.Path, cancellation);
    }
}

public static class RemoteClientFactory
{
    /// <summary>SFTP のホスト鍵を記録するファイル</summary>
    public static string KnownHostsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ferry", "known_hosts.json");

    public static IRemoteClient Create(ServerConfig server, string? password) =>
        server.ServerProtocol.IsFtpFamily()
            ? new FtpRemoteClient(server, password)
            : new SftpRemoteClient(server, password, new KnownHosts(KnownHostsPath));
}

/// <summary>パーミッションの表記の変換</summary>
public static class Permissions
{
    /// <summary>"755" → "rwxr-xr-x"</summary>
    public static string? FromOctal(string mode)
    {
        if (mode.Length is < 3 or > 4 || mode.Any(c => c is < '0' or > '7')) return null;
        return string.Concat(mode[^3..].Select(digit =>
        {
            var value = digit - '0';
            return $"{((value & 4) != 0 ? 'r' : '-')}{((value & 2) != 0 ? 'w' : '-')}{((value & 1) != 0 ? 'x' : '-')}";
        }));
    }

    /// <summary>"rwxr-xr-x" → "755"（読めない形式なら null）</summary>
    public static string? ToOctal(string permissions)
    {
        if (permissions.Length == 10) permissions = permissions[1..];
        if (permissions.Length != 9) return null;
        var digits = new char[3];
        for (var group = 0; group < 3; group++)
        {
            var value = 0;
            if (permissions[group * 3] == 'r') value += 4;
            if (permissions[group * 3 + 1] == 'w') value += 2;
            if (permissions[group * 3 + 2] is 'x' or 's' or 't') value += 1;
            digits[group] = (char)('0' + value);
        }
        return new string(digits);
    }
}
