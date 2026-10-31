using System.Text.Json;
using System.Text.Json.Serialization;
using Ferry.Core.Import;

namespace Ferry.Core;

/// <summary>
/// 接続先リスト。自分の接続先は servers.json に保存し、会社で共有する接続先は SharedStore（Dropbox などの共有フォルダ）に保存する。
/// <para>
/// servers.json は iCloud Drive にあれば Mac 版と同じファイルを使う（自分の Mac と Windows で同期）。
/// その場合、ローカルのフォルダ・ブックマーク・秘密鍵の場所（Mac のパス）は Windows では使えないので、
/// この PC の分は localOverridesPath のファイルに分けて持ち、servers.json の中の Mac の値は書き換えずに残す。
/// </para>
/// </summary>
public sealed class ServerStore : IDisposable
{
    /// <summary>自分だけの接続先とフォルダ（この PC の設定を重ねたもの）</summary>
    public IReadOnlyList<ServerConfig> PersonalServers => _personalServers.Select(Present).ToList();
    public IReadOnlyList<ServerFolder> PersonalFolders => _personalFolders;
    /// <summary>共有の接続先のうち、この人のこの PC の設定（キーは大文字の ID）</summary>
    public IReadOnlyDictionary<string, SharedServerOverride> SharedOverrides => SharedOverridesForThisPc;
    /// <summary>servers.json を他の PC（Mac）と共有していて、PC ごとの設定を別のファイルに持っているか</summary>
    public bool KeepsPerPcSettingsLocally => _localOverridesPath is not null;
    /// <summary>会社で共有する接続先</summary>
    public SharedStore Shared { get; } = new();
    /// <summary>転送・同期で除外するファイル名のパターン</summary>
    public ExcludeRules ExcludeRules { get; private set; } = ExcludeRules.Defaults;
    public string FilePath { get; }

    /// <summary>自分の接続先か共有の接続先が変わったとき。UI スレッドとは限らない。</summary>
    public event EventHandler? Changed;

    /// <summary>servers.json に書いてあるとおりの自分の接続先（PC ごとの設定は Mac の値のことがある）</summary>
    private List<ServerConfig> _personalServers = [];
    private List<ServerFolder> _personalFolders = [];
    /// <summary>servers.json の sharedOverrides（ファイルを共有しているときは Mac の分なので、そのまま残す）</summary>
    private Dictionary<string, SharedServerOverride> _sharedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, JsonElement>? _extra;
    private readonly IPasswordStore? _passwords;
    private readonly string? _localOverridesPath;
    /// <summary>この PC の設定（ファイルを共有しているときだけ使う）</summary>
    private Dictionary<string, SharedServerOverride> _localPersonal = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SharedServerOverride> _localShared = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private Dictionary<string, SharedServerOverride> SharedOverridesForThisPc =>
        KeepsPerPcSettingsLocally ? _localShared : _sharedOverrides;

    /// <summary>この PC だけの設定のファイル（overrides.json）の中身</summary>
    internal sealed class LocalOverrides
    {
        /// <summary>自分の接続先の、この PC でのローカルのフォルダ・ブックマーク・秘密鍵</summary>
        public Dictionary<string, SharedServerOverride> Personal { get; set; } = [];
        /// <summary>共有の接続先の、この PC での設定</summary>
        public Dictionary<string, SharedServerOverride> Shared { get; set; } = [];
    }

    private static ServerConfig WithoutPerPcSettings(ServerConfig server) =>
        server with { LocalPath = null, Bookmarks = null, StartBookmarkId = null, PrivateKeyPath = null };

    /// <summary>画面に出す形（ファイルを共有しているときは、この PC の設定に差し替える）</summary>
    private ServerConfig Present(ServerConfig stored) =>
        !KeepsPerPcSettingsLocally ? stored
        : _localPersonal.TryGetValue(FerryJson.IdString(stored.Id), out var local) ? local.ApplyTo(stored)
        : WithoutPerPcSettings(stored);

    /// <summary>自分の接続先を入れる・置き換える（ファイルを共有しているときは、PC ごとの設定はファイルの値のまま残す）</summary>
    private void PutPersonal(ServerConfig server)
    {
        var index = _personalServers.FindIndex(s => s.Id == server.Id);
        var stored = server;
        if (KeepsPerPcSettingsLocally)
        {
            var previous = index >= 0 ? _personalServers[index] : WithoutPerPcSettings(server);
            stored = server with
            {
                LocalPath = previous.LocalPath, Bookmarks = previous.Bookmarks,
                StartBookmarkId = previous.StartBookmarkId, PrivateKeyPath = previous.PrivateKeyPath,
            };
            var local = SharedServerOverride.From(server);
            if (local.IsEmpty) _localPersonal.Remove(FerryJson.IdString(server.Id));
            else _localPersonal[FerryJson.IdString(server.Id)] = local;
        }
        if (index >= 0) _personalServers[index] = stored;
        else _personalServers.Add(stored);
    }

    private void RemovePersonal(Guid id)
    {
        _personalServers.RemoveAll(s => s.Id == id);
        _localPersonal.Remove(FerryJson.IdString(id));
    }

    /// <summary>servers.json の中身（Mac 版の FileContents と同じ形式）</summary>
    internal sealed class FileContents
    {
        public int Version { get; set; } = 2;
        public List<ServerConfig> Servers { get; set; } = [];
        public List<ServerFolder> Folders { get; set; } = [];
        public List<string>? ExcludePatterns { get; set; }
        public Dictionary<string, SharedServerOverride>? SharedOverrides { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ferry", "servers.json");

    /// <summary>iCloud Drive にある Mac 版の servers.json（無ければ null）</summary>
    public static string? ICloudFilePath
    {
        get
        {
            var iCloud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive");
            return Directory.Exists(iCloud) ? Path.Combine(iCloud, "Ferry", "servers.json") : null;
        }
    }

    /// <param name="filePath">servers.json の場所</param>
    /// <param name="passwords">削除・複製・移動のときにパスワードも扱う</param>
    /// <param name="localOverridesPath">servers.json を他の PC と共有しているとき、この PC の設定を置くファイル</param>
    /// <param name="watch">他の PC の変更が届いたら読み直す</param>
    public ServerStore(string? filePath = null, IPasswordStore? passwords = null, string? localOverridesPath = null,
                       bool watch = false)
    {
        FilePath = filePath ?? DefaultFilePath;
        _passwords = passwords;
        _localOverridesPath = localOverridesPath;
        Shared.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Load();
        if (watch) StartWatching();
    }

    // MARK: 一覧（自分の接続先 + 共有の接続先）

    /// <summary>共有の接続先には、この PC の設定を重ねて返す</summary>
    public IReadOnlyList<ServerConfig> SharedServers =>
        Shared.Servers
            .Select(server => SharedOverridesForThisPc.TryGetValue(FerryJson.IdString(server.Id), out var o) ? o.ApplyTo(server) : server)
            .ToList();

    public IReadOnlyList<ServerConfig> Servers => [.. SharedServers, .. PersonalServers];
    public IReadOnlyList<ServerFolder> Folders => [.. Shared.Folders, .. _personalFolders];

    public bool IsShared(Guid id) => Shared.Servers.Any(s => s.Id == id);
    public bool IsSharedFolder(Guid id) => Shared.Folders.Any(f => f.Id == id);

    public ServerConfig? Server(Guid id) => Servers.FirstOrDefault(s => s.Id == id);

    /// <summary>フォルダの中の接続先。folderId が null の場合は、shared で共有か自分のどちらの「フォルダなし」かを選ぶ。</summary>
    public IReadOnlyList<ServerConfig> ServersInFolder(Guid? folderId, bool shared) =>
        (shared ? SharedServers : PersonalServers).Where(s => s.FolderId == folderId).ToList();

    // MARK: 読み書き

    public void Load()
    {
        if (_localOverridesPath is not null)
        {
            try
            {
                var local = FerryJson.Deserialize<LocalOverrides>(File.ReadAllText(_localOverridesPath));
                _localPersonal = new(local?.Personal ?? [], StringComparer.OrdinalIgnoreCase);
                _localShared = new(local?.Shared ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }

        FileContents? loaded;
        try
        {
            loaded = FerryJson.Deserialize<FileContents>(File.ReadAllText(FilePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        if (loaded is null) return;
        _personalServers = loaded.Servers;
        _personalFolders = loaded.Folders;
        _sharedOverrides = new(loaded.SharedOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        ExcludeRules = loaded.ExcludePatterns is { } patterns ? new ExcludeRules(patterns) : ExcludeRules.Defaults;
        _extra = loaded.Extra;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        var contents = new FileContents
        {
            Servers = _personalServers,
            Folders = _personalFolders,
            ExcludePatterns = ExcludeRules.Patterns.ToList(),
            SharedOverrides = _sharedOverrides.ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value),
            Extra = _extra,
        };
        _lastWrite = DateTime.UtcNow;
        FerryJson.WriteAtomically(FilePath, FerryJson.Serialize(contents));
        if (_localOverridesPath is not null)
        {
            FerryJson.WriteAtomically(_localOverridesPath, FerryJson.Serialize(new LocalOverrides
            {
                Personal = _localPersonal.ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value),
                Shared = _localShared.ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value),
            }));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 別の servers.json の自分の接続先を取り込む（iCloud Drive を使い始める前の %APPDATA% の分など）。
    /// 同じ ID か同じ接続先のものは飛ばし、フォルダは同名のものにまとめる。戻り値は取り込んだ件数。
    /// </summary>
    public int MergePersonalFrom(string path)
    {
        FileContents? other;
        try
        {
            other = FerryJson.Deserialize<FileContents>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
        if (other is null) return 0;
        var folderMap = new Dictionary<Guid, Guid>();
        foreach (var folder in other.Folders)
        {
            var existing = _personalFolders.FirstOrDefault(f => f.Id == folder.Id || f.Name == folder.Name);
            if (existing is null)
            {
                _personalFolders.Add(folder);
                existing = folder;
            }
            folderMap[folder.Id] = existing.Id;
        }
        var added = 0;
        foreach (var server in other.Servers)
        {
            if (Servers.Any(s => s.Id == server.Id || s.IsSameDestination(server))) continue;
            PutPersonal(server with { FolderId = server.FolderId is { } id && folderMap.TryGetValue(id, out var mapped) ? mapped : null });
            added++;
        }
        if (added > 0 || other.Folders.Count > 0) Save();
        return added;
    }

    // MARK: 監視（他の PC で servers.json が変わったら読み直す）

    /// <summary>自分で書いた直後の通知では読み直さない</summary>
    private DateTime _lastWrite = DateTime.MinValue;

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        _debounce = new Timer(_ =>
        {
            if ((DateTime.UtcNow - _lastWrite).TotalSeconds < 2) return;
            Load();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => _debounce?.Change(500, Timeout.Infinite);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (_, _) => _debounce?.Change(500, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>追加・変更。新しい接続先は shared で共有か自分のどちらに入れるかを選ぶ（既存のものは今の場所のまま）。</summary>
    public void Upsert(ServerConfig server, bool shared = false)
    {
        var personalIndex = _personalServers.FindIndex(s => s.Id == server.Id);
        var toShared = IsShared(server.Id) || (personalIndex < 0 && shared);
        if (toShared)
        {
            Shared.Save(server);
            SetOverride(SharedServerOverride.From(server), server.Id);
        }
        else
        {
            PutPersonal(server);
        }
        Save();
    }

    /// <summary>共有の接続先を消すと、会社の全員の一覧から消える</summary>
    public void Delete(Guid id)
    {
        if (IsShared(id))
        {
            Shared.Delete(id);
            SharedOverridesForThisPc.Remove(FerryJson.IdString(id));
        }
        else
        {
            RemovePersonal(id);
        }
        _passwords?.SetPassword(id, null);
        Save();
    }

    /// <summary>接続先を複製する（新しい ID で、名前に「のコピー」を付ける）</summary>
    public ServerConfig Duplicate(Guid id)
    {
        var source = Server(id) ?? throw new ArgumentException("接続先が見つかりません", nameof(id));
        var copy = source with { Id = Guid.NewGuid(), Name = source.Name + " のコピー" };
        Upsert(copy, shared: IsShared(id));
        if (_passwords?.GetPassword(id) is { } password)
        {
            _passwords.SetPassword(copy.Id, password);
        }
        return copy;
    }

    /// <summary>フォルダへ移す。共有のフォルダと自分のフォルダの間で移すと、共有する・しないも切り替わる。</summary>
    public void MoveServer(Guid id, Guid? folderId, bool toShared)
    {
        if (Server(id) is not { } found) return;
        var server = found with { FolderId = folderId };
        if (toShared == IsShared(id))
        {
            Upsert(server);
            return;
        }
        // 共有 ⇄ 自分の間を移す（ID は変えないので、この PC に保存済みのパスワードもそのまま使える）
        var password = _passwords?.GetPassword(id);
        if (toShared)
        {
            RemovePersonal(id);
            Shared.Save(server);
            SetOverride(SharedServerOverride.From(server), id);
        }
        else
        {
            Shared.Delete(id);
            SharedOverridesForThisPc.Remove(FerryJson.IdString(id));
            PutPersonal(server);
        }
        Save();
        // 同期しているパスワードは、移った先の保管庫（会社・自分）に入れ直す
        if (password is { Length: > 0 }) _passwords!.SetPassword(id, password);
    }

    private void SetOverride(SharedServerOverride value, Guid id)
    {
        if (value.IsEmpty)
            SharedOverridesForThisPc.Remove(FerryJson.IdString(id));
        else
            SharedOverridesForThisPc[FerryJson.IdString(id)] = value;
    }

    private void Update(Guid id, Func<ServerConfig, ServerConfig> change)
    {
        if (Server(id) is { } server) Upsert(change(server));
    }

    // MARK: ブックマーク（左右の場所の組み合わせ）

    public void AddBookmark(PaneBookmark bookmark, Guid serverId) =>
        Update(serverId, s => s with { Bookmarks = [.. s.Bookmarks ?? [], bookmark] });

    public void RenameBookmark(Guid bookmarkId, Guid serverId, string name) =>
        Update(serverId, s => s with
        {
            Bookmarks = s.Bookmarks?.Select(b => b.Id == bookmarkId ? b with { Name = name } : b).ToList(),
        });

    /// <summary>接続時に開くブックマークを設定する（null で解除）</summary>
    public void SetStartBookmark(Guid? bookmarkId, Guid serverId) =>
        Update(serverId, s => s with { StartBookmarkId = bookmarkId });

    public void RemoveBookmark(Guid bookmarkId, Guid serverId) =>
        Update(serverId, s =>
        {
            var bookmarks = s.Bookmarks?.Where(b => b.Id != bookmarkId).ToList();
            return s with
            {
                StartBookmarkId = s.StartBookmarkId == bookmarkId ? null : s.StartBookmarkId,
                Bookmarks = bookmarks is { Count: > 0 } ? bookmarks : null,
            };
        });

    // MARK: フォルダ

    public ServerFolder AddFolder(string name, bool shared = false)
    {
        var folder = new ServerFolder { Id = Guid.NewGuid(), Name = name };
        if (shared)
        {
            Shared.SaveFolders([.. Shared.Folders, folder]);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _personalFolders.Add(folder);
            Save();
        }
        return folder;
    }

    public void RenameFolder(Guid id, string name)
    {
        if (IsSharedFolder(id))
        {
            Shared.SaveFolders(Shared.Folders.Select(f => f.Id == id ? f with { Name = name } : f).ToList());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            var index = _personalFolders.FindIndex(f => f.Id == id);
            if (index < 0) return;
            _personalFolders[index] = _personalFolders[index] with { Name = name };
            Save();
        }
    }

    /// <summary>フォルダを削除する。中の接続先はフォルダの外に出す。</summary>
    public void DeleteFolder(Guid id)
    {
        if (IsSharedFolder(id))
        {
            foreach (var server in Shared.Servers.Where(s => s.FolderId == id).ToList())
            {
                Shared.Save(server with { FolderId = null });
            }
            Shared.SaveFolders(Shared.Folders.Where(f => f.Id != id).ToList());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _personalFolders.RemoveAll(f => f.Id == id);
            _personalServers = _personalServers.Select(s => s.FolderId == id ? s with { FolderId = null } : s).ToList();
            Save();
        }
    }

    // MARK: 読み込み（FileZilla・WinSCP から）

    /// <summary>
    /// 読み込んだ接続先を自分の接続先に追加する。同じ接続先（ホスト・ユーザー・ポート・プロトコル）は飛ばす。
    /// フォルダは同名のものがあればそこに入れる。読めたパスワードは保存する。戻り値は追加したもの。
    /// </summary>
    public IReadOnlyList<ImportedServer> Import(ImportResult result)
    {
        var added = new List<ImportedServer>();
        foreach (var (folderName, servers) in result.Folders)
        {
            Guid? folderId = null;
            if (folderName is not null)
            {
                folderId = _personalFolders.FirstOrDefault(f => f.Name == folderName)?.Id;
                if (folderId is null)
                {
                    var folder = new ServerFolder { Id = Guid.NewGuid(), Name = folderName };
                    _personalFolders.Add(folder);
                    folderId = folder.Id;
                }
            }
            foreach (var imported in servers)
            {
                if (Servers.Any(s => s.IsSameDestination(imported.Server))) continue;
                var server = imported.Server with { FolderId = folderId };
                PutPersonal(server);
                if (imported.Password is { Length: > 0 } password) _passwords?.SetPassword(server.Id, password);
                added.Add(imported with { Server = server });
            }
        }
        Save();
        return added;
    }

    public int DuplicateCount(ImportResult result) =>
        result.AllServers.Count(imported => Servers.Any(s => s.IsSameDestination(imported.Server)));

    // MARK: 除外ルール

    public void SetExcludeRules(ExcludeRules rules)
    {
        ExcludeRules = rules;
        Save();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        Shared.Dispose();
    }
}
