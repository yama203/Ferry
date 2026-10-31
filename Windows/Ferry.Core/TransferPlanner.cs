using Ferry.Core.Remote;

namespace Ferry.Core;

public enum TransferDirection { Upload, Download }

/// <summary>同名ファイルがあったときの扱い</summary>
public enum ConflictPolicy
{
    /// <summary>転送元の方が新しい場合だけ上書き</summary>
    NewerOnly,
    Overwrite,
    Skip,
}

/// <summary>転送する 1 ファイル</summary>
public sealed record PlannedTransfer
{
    public required TransferDirection Direction { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public long Size { get; init; }
    /// <summary>転送元の更新日時（UTC）</summary>
    public DateTime? SourceModified { get; init; }
    /// <summary>転送先に同名のファイルがあるか（上書き確認用）</summary>
    public bool DestinationExists { get; init; }
    public DateTime? DestinationModified { get; init; }

    public string Name => Direction == TransferDirection.Upload ? Path.GetFileName(LocalPath) : Core.RemotePath.Name(RemotePath);

    /// <summary>転送元の方が新しいか（日時が分からない場合は新しいとみなす）</summary>
    public bool IsSourceNewer =>
        SourceModified is not { } source || DestinationModified is not { } destination
        || (source - destination).TotalSeconds > 1;
}

/// <summary>
/// 転送するファイルを洗い出す。フォルダは中のファイルに展開し、除外ルールはフォルダの中身にだけ適用する
/// （直接選んだ項目には適用しない）。
/// </summary>
public static class TransferPlanner
{
    // MARK: アップロード

    public static List<PlannedTransfer> CollectUploads(IEnumerable<string> localPaths, string remoteDirectory, ExcludeRules rules)
    {
        var batch = new List<PlannedTransfer>();
        foreach (var path in localPaths)
        {
            var remoteBase = RemotePath.Join(remoteDirectory, Path.GetFileName(path.TrimEnd('\\')));
            if (File.Exists(path))
            {
                batch.Add(Upload(new FileInfo(path), remoteBase));
            }
            else if (Directory.Exists(path))
            {
                CollectLocalFolder(new DirectoryInfo(path), remoteBase, rules, batch);
            }
        }
        return batch;
    }

    private static void CollectLocalFolder(DirectoryInfo folder, string remoteBase, ExcludeRules rules, List<PlannedTransfer> batch)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = folder.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var child in children)
        {
            if (rules.Excludes(child.Name)) continue;
            var remotePath = RemotePath.Join(remoteBase, child.Name);
            if (child is FileInfo file)
                batch.Add(Upload(file, remotePath));
            // ジャンクションの先は辿らない
            else if (child is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                CollectLocalFolder(directory, remotePath, rules, batch);
        }
    }

    private static PlannedTransfer Upload(FileInfo file, string remotePath) => new()
    {
        Direction = TransferDirection.Upload,
        LocalPath = file.FullName,
        RemotePath = remotePath,
        Size = file.Length,
        SourceModified = file.LastWriteTimeUtc,
    };

    /// <summary>サーバー側に同名のファイルがあるものに印を付ける（フォルダごとに一覧を 1 回取る）</summary>
    public static async Task<List<PlannedTransfer>> MarkRemoteConflictsAsync(IReadOnlyList<PlannedTransfer> batch,
                                                                             IRemoteClient lister,
                                                                             CancellationToken cancellation = default)
    {
        var existing = new Dictionary<string, RemoteItem>(StringComparer.Ordinal);
        foreach (var directory in batch.Select(t => RemotePath.Parent(t.RemotePath)).Distinct())
        {
            try
            {
                foreach (var item in await lister.ListAsync(directory, cancellation)) existing[item.Path] = item;
            }
            catch (FerryException)
            {
                // まだ無いフォルダは一覧を取れないが、その場合は衝突なし
            }
        }
        return batch.Select(t => existing.TryGetValue(t.RemotePath, out var remote) && !remote.IsDirectory
            ? t with { DestinationExists = true, DestinationModified = remote.Modified }
            : t).ToList();
    }

    // MARK: ダウンロード

    /// <summary>フォルダはローカルにも作る（空のフォルダも再現する）</summary>
    public static async Task<List<PlannedTransfer>> CollectDownloadsAsync(IEnumerable<RemoteItem> items, string localDirectory,
                                                                         IRemoteClient lister, ExcludeRules rules,
                                                                         CancellationToken cancellation = default)
    {
        var batch = new List<PlannedTransfer>();
        foreach (var item in items)
        {
            var localPath = Path.Combine(localDirectory, LocalName(item.Name));
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(localPath);
                await CollectRemoteFolderAsync(item.Path, localPath, lister, rules, batch, cancellation);
            }
            else
            {
                batch.Add(Download(item, localPath));
            }
        }
        return batch;
    }

    private static async Task CollectRemoteFolderAsync(string remotePath, string localPath, IRemoteClient lister,
                                                       ExcludeRules rules, List<PlannedTransfer> batch,
                                                       CancellationToken cancellation)
    {
        foreach (var child in (await lister.ListAsync(remotePath, cancellation)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (rules.Excludes(child.Name)) continue;
            var childPath = Path.Combine(localPath, LocalName(child.Name));
            if (child.IsDirectory)
            {
                Directory.CreateDirectory(childPath);
                await CollectRemoteFolderAsync(child.Path, childPath, lister, rules, batch, cancellation);
            }
            else
            {
                batch.Add(Download(child, childPath));
            }
        }
    }

    private static PlannedTransfer Download(RemoteItem item, string localPath) => new()
    {
        Direction = TransferDirection.Download,
        LocalPath = localPath,
        RemotePath = item.Path,
        Size = item.Size ?? 0,
        SourceModified = item.Modified,
    };

    public static List<PlannedTransfer> MarkLocalConflicts(IEnumerable<PlannedTransfer> batch) =>
        batch.Select(t => File.Exists(t.LocalPath)
            ? t with { DestinationExists = true, DestinationModified = File.GetLastWriteTimeUtc(t.LocalPath) }
            : t).ToList();

    /// <summary>
    /// サーバーのファイル名を Windows で使える名前にする（\ / : * ? " &lt; &gt; | は全角に置き換え、末尾の点と空白は取る）
    /// </summary>
    public static string LocalName(string name)
    {
        var chars = name.Select(c => c switch
        {
            '\\' => '＼', '/' => '／', ':' => '：', '*' => '＊', '?' => '？',
            '"' => '”', '<' => '＜', '>' => '＞', '|' => '｜',
            < ' ' => '_',
            _ => c,
        }).ToArray();
        var result = new string(chars).TrimEnd('.', ' ');
        return result.Length == 0 ? "_" : result;
    }

    // MARK: 上書き確認

    /// <summary>上書きの方針で絞り込む。転送先に無いものはそのまま転送する。</summary>
    public static List<PlannedTransfer> Apply(IEnumerable<PlannedTransfer> batch, ConflictPolicy policy) =>
        batch.Where(t => !t.DestinationExists || policy switch
        {
            ConflictPolicy.Overwrite => true,
            ConflictPolicy.Skip => false,
            _ => t.IsSourceNewer,
        }).ToList();
}
