using System.Globalization;
using Ferry.Core.Remote;

namespace Ferry.Core;

/// <summary>ローカルとリモートのフォルダを比べて、転送・削除する項目を決める</summary>
public static class SyncPlanner
{
    /// <summary>Permissions はサーバー側だけ（"rwxr-xr-x" 形式。アップロードで置き換えた後に戻す）</summary>
    public sealed record Entry(bool IsDirectory, long? Size, DateTime? Modified, string? Permissions = null);

    /// <summary>
    /// 転送の途中の一時ファイル（".&lt;名前&gt;.ferrypart" など）は、除外ルールに関係なく比べない
    /// （転送するとサーバーに一時ファイルが増え、ミラーでは消されてしまう。一覧には出るので、残っていれば気づける）
    /// </summary>
    public static bool IsTransferPart(string name) => name.EndsWith(".ferrypart", StringComparison.Ordinal);

    public enum ChangeReason { New, Changed }

    public sealed record Change(string RelativePath, ChangeReason Reason, long Size);

    public sealed record Deletion(string RelativePath, bool IsDirectory);

    public sealed class Plan
    {
        public List<Change> Changes { get; } = [];
        public List<Deletion> Deletions { get; } = [];
        public long TotalBytes => Changes.Sum(c => c.Size);
        public bool IsEmpty => Changes.Count == 0 && Deletions.Count == 0;
    }

    /// <summary>エクスプローラーと同じく、数字の部分は数として並べる</summary>
    public static readonly IComparer<string> NaturalOrder = StringComparer.Create(
        CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    // MARK: 走査（キーはルートからの相対パス、区切りは "/"）

    public static Dictionary<string, Entry> ScanLocal(string root, ExcludeRules rules)
    {
        var result = new Dictionary<string, Entry>();
        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        pending.Push((new DirectoryInfo(root), ""));
        while (pending.TryPop(out var current))
        {
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = current.Directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                if (rules.Excludes(child.Name) || IsTransferPart(child.Name)) continue;
                var relative = current.Relative.Length == 0 ? child.Name : current.Relative + "/" + child.Name;
                if (child is DirectoryInfo directory)
                {
                    result[relative] = new Entry(true, null, directory.LastWriteTimeUtc);
                    // ジャンクション（OneDrive の場所など）の先は辿らない
                    if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        pending.Push((directory, relative));
                }
                else if (child is FileInfo file)
                {
                    result[relative] = new Entry(false, file.Length, file.LastWriteTimeUtc);
                }
            }
        }
        return result;
    }

    public static async Task<Dictionary<string, Entry>> ScanRemoteAsync(
        string root, IRemoteClient client, ExcludeRules rules,
        Action<string>? onFolder = null, CancellationToken cancellation = default)
    {
        var result = new Dictionary<string, Entry>();
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.TryPop(out var relativeDirectory))
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = relativeDirectory.Length == 0 ? root : RemotePath.Join(root, relativeDirectory);
            onFolder?.Invoke(directory);
            foreach (var item in await client.ListAsync(directory, cancellation))
            {
                if (rules.Excludes(item.Name) || IsTransferPart(item.Name)) continue;
                var relative = relativeDirectory.Length == 0 ? item.Name : relativeDirectory + "/" + item.Name;
                result[relative] = new Entry(item.IsDirectory, item.Size, item.Modified, item.Permissions);
                if (item.IsDirectory) pending.Push(relative);
            }
        }
        return result;
    }

    // MARK: 比較

    /// <summary>サイズが違うか、転送元の方が（2 秒より）新しいファイルを転送する</summary>
    public static Plan Make(IReadOnlyDictionary<string, Entry> source, IReadOnlyDictionary<string, Entry> destination,
                            bool deleteExtraneous)
    {
        var plan = new Plan();
        foreach (var (path, entry) in source)
        {
            if (entry.IsDirectory) continue;
            if (!destination.TryGetValue(path, out var existing) || existing.IsDirectory)
            {
                plan.Changes.Add(new Change(path, ChangeReason.New, entry.Size ?? 0));
                continue;
            }
            var sizeChanged = entry.Size is { } a && existing.Size is { } b && a != b;
            var isNewer = entry.Modified is { } sourceDate && existing.Modified is { } destinationDate
                && (sourceDate - destinationDate).TotalSeconds > 2;
            if (sizeChanged || isNewer)
            {
                plan.Changes.Add(new Change(path, ChangeReason.Changed, entry.Size ?? 0));
            }
        }

        if (deleteExtraneous)
        {
            // 転送元に無いものを削除。フォルダごと消すものは中身を個別に挙げない。
            var extraneous = destination.Keys.Where(path => !source.ContainsKey(path)).Order(StringComparer.Ordinal);
            var removedFolders = new List<string>();
            foreach (var path in extraneous)
            {
                if (removedFolders.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal))) continue;
                var isDirectory = destination[path].IsDirectory;
                plan.Deletions.Add(new Deletion(path, isDirectory));
                if (isDirectory) removedFolders.Add(path);
            }
        }

        plan.Changes.Sort((x, y) => NaturalOrder.Compare(x.RelativePath, y.RelativePath));
        return plan;
    }
}
