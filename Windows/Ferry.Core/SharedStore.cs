namespace Ferry.Core;

/// <summary>
/// 会社で共有する接続先。Dropbox などの共有フォルダに、接続先 1 件につき 1 ファイルで保存する
/// （複数人が同時に編集しても衝突しにくいように）。Mac 版と同じ形式。
/// <code>
/// &lt;共有フォルダ&gt;/ferry-shared.json      … 目印（形式のバージョン）
/// &lt;共有フォルダ&gt;/folders.json           … サイドバーのフォルダ
/// &lt;共有フォルダ&gt;/servers/&lt;ID&gt;.json      … 接続先
/// </code>
/// ローカルのフォルダ・ブックマーク・秘密鍵の場所は PC ごとに違うので共有せず、
/// 各自の設定（SharedServerOverride）に保存する。パスワードも共有しない。
/// </summary>
public sealed class SharedStore : IDisposable
{
    public IReadOnlyList<ServerConfig> Servers { get; private set; } = [];
    public IReadOnlyList<ServerFolder> Folders { get; private set; } = [];
    public string? DirectoryPath { get; private set; }
    public bool IsEnabled => DirectoryPath is not null;

    /// <summary>中身が変わったとき（他の人の変更が届いたときも）。UI スレッドとは限らない。</summary>
    public event EventHandler? Changed;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly object _lock = new();

    /// <summary>共有フォルダを設定する（null で共有をやめる。ファイルは消さない）</summary>
    public void SetDirectory(string? path, bool watch = true)
    {
        StopWatching();
        DirectoryPath = path;
        if (path is null)
        {
            Servers = [];
            Folders = [];
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        Directory.CreateDirectory(Path.Combine(path, "servers"));
        var marker = Path.Combine(path, "ferry-shared.json");
        if (!File.Exists(marker))
        {
            FerryJson.WriteAtomically(marker, """{"version":1}""");
        }
        Load(force: true);
        if (watch) StartWatching(path);
    }

    public void Load() => Load(force: false);

    private void Load(bool force)
    {
        var directory = DirectoryPath;
        if (directory is null) return;
        var loaded = new List<ServerConfig>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "servers"), "*.json"))
            {
                // Dropbox の「競合コピー」などは、名前が <ID>.json の形でないので読まない
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out _)) continue;
                var server = TryRead<ServerConfig>(file);
                if (server is not null) loaded.Add(server);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
        var loadedFolders = TryRead<List<ServerFolder>>(Path.Combine(directory, "folders.json")) ?? [];

        var changed = force;
        lock (_lock)
        {
            if (!loaded.SequenceEqual(Servers.OrderBy(s => s.Id)))
            {
                Servers = loaded;
                changed = true;
            }
            if (!loadedFolders.SequenceEqual(Folders))
            {
                Folders = loadedFolders;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    // MARK: 書き込み

    /// <summary>PC ごとの設定を除いて保存する。戻り値は共有フォルダに書いた内容。</summary>
    public ServerConfig? Save(ServerConfig server)
    {
        if (DirectoryPath is null) return null;
        var shared = server with { LocalPath = null, Bookmarks = null, StartBookmarkId = null, PrivateKeyPath = null };
        FerryJson.WriteAtomically(ServerFile(server.Id), FerryJson.Serialize(shared));
        lock (_lock)
        {
            Servers = Servers.Where(s => s.Id != server.Id).Append(shared).OrderBy(s => s.Id).ToList();
        }
        return shared;
    }

    public void Delete(Guid id)
    {
        if (DirectoryPath is null) return;
        try { File.Delete(ServerFile(id)); } catch (IOException) { }
        lock (_lock)
        {
            Servers = Servers.Where(s => s.Id != id).ToList();
        }
    }

    public void SaveFolders(IReadOnlyList<ServerFolder> folders)
    {
        if (DirectoryPath is null) return;
        lock (_lock)
        {
            Folders = folders.ToList();
        }
        FerryJson.WriteAtomically(Path.Combine(DirectoryPath, "folders.json"), FerryJson.Serialize(folders));
    }

    private string ServerFile(Guid id) => Path.Combine(DirectoryPath!, "servers", FerryJson.IdString(id) + ".json");

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return FerryJson.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // MARK: 監視

    /// <summary>他の人の変更が Dropbox から届いたら読み直す（まとめて届くので 0.3 秒待ってから）</summary>
    private void StartWatching(string path)
    {
        _debounce = new Timer(_ => Load(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce?.Change(300, Timeout.Infinite);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => _debounce?.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    public void Dispose() => StopWatching();
}
