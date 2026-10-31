using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferry.Core;

/// <summary>
/// 接続先リスト。自分の接続先は %APPDATA%\Ferry\servers.json に保存し、
/// 会社で共有する接続先は SharedStore（Dropbox などの共有フォルダ）に保存する。
/// </summary>
public sealed class ServerStore : IDisposable
{
    /// <summary>自分だけの接続先とフォルダ</summary>
    public IReadOnlyList<ServerConfig> PersonalServers => _personalServers;
    public IReadOnlyList<ServerFolder> PersonalFolders => _personalFolders;
    /// <summary>共有の接続先のうち、この人の PC ごとの設定（キーは大文字の ID）</summary>
    public IReadOnlyDictionary<string, SharedServerOverride> SharedOverrides => _sharedOverrides;
    /// <summary>会社で共有する接続先</summary>
    public SharedStore Shared { get; } = new();
    /// <summary>転送・同期で除外するファイル名のパターン</summary>
    public ExcludeRules ExcludeRules { get; private set; } = ExcludeRules.Defaults;
    public string FilePath { get; }

    /// <summary>自分の接続先か共有の接続先が変わったとき。UI スレッドとは限らない。</summary>
    public event EventHandler? Changed;

    private List<ServerConfig> _personalServers = [];
    private List<ServerFolder> _personalFolders = [];
    private Dictionary<string, SharedServerOverride> _sharedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, JsonElement>? _extra;
    private readonly IPasswordStore? _passwords;

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

    public ServerStore(string? filePath = null, IPasswordStore? passwords = null)
    {
        FilePath = filePath ?? DefaultFilePath;
        _passwords = passwords;
        Shared.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Load();
    }

    // MARK: 一覧（自分の接続先 + 共有の接続先）

    /// <summary>共有の接続先には、この PC の設定を重ねて返す</summary>
    public IReadOnlyList<ServerConfig> SharedServers =>
        Shared.Servers
            .Select(server => _sharedOverrides.TryGetValue(FerryJson.IdString(server.Id), out var o) ? o.ApplyTo(server) : server)
            .ToList();

    public IReadOnlyList<ServerConfig> Servers => [.. SharedServers, .. _personalServers];
    public IReadOnlyList<ServerFolder> Folders => [.. Shared.Folders, .. _personalFolders];

    public bool IsShared(Guid id) => Shared.Servers.Any(s => s.Id == id);
    public bool IsSharedFolder(Guid id) => Shared.Folders.Any(f => f.Id == id);

    public ServerConfig? Server(Guid id) => Servers.FirstOrDefault(s => s.Id == id);

    /// <summary>フォルダの中の接続先。folderId が null の場合は、shared で共有か自分のどちらの「フォルダなし」かを選ぶ。</summary>
    public IReadOnlyList<ServerConfig> ServersInFolder(Guid? folderId, bool shared) =>
        (shared ? SharedServers : _personalServers).Where(s => s.FolderId == folderId).ToList();

    // MARK: 読み書き

    public void Load()
    {
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
        FerryJson.WriteAtomically(FilePath, FerryJson.Serialize(contents));
        Changed?.Invoke(this, EventArgs.Empty);
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
        else if (personalIndex >= 0)
        {
            _personalServers[personalIndex] = server;
        }
        else
        {
            _personalServers.Add(server);
        }
        Save();
    }

    /// <summary>共有の接続先を消すと、会社の全員の一覧から消える</summary>
    public void Delete(Guid id)
    {
        if (IsShared(id))
        {
            Shared.Delete(id);
            _sharedOverrides.Remove(FerryJson.IdString(id));
        }
        else
        {
            _personalServers.RemoveAll(s => s.Id == id);
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
        // 共有 ⇄ 自分の間を移す（ID は変えないので、保存済みのパスワードもそのまま使える）
        if (toShared)
        {
            _personalServers.RemoveAll(s => s.Id == id);
            Shared.Save(server);
            SetOverride(SharedServerOverride.From(server), id);
        }
        else
        {
            Shared.Delete(id);
            _sharedOverrides.Remove(FerryJson.IdString(id));
            _personalServers.Add(server);
        }
        Save();
    }

    private void SetOverride(SharedServerOverride value, Guid id)
    {
        if (value.IsEmpty)
            _sharedOverrides.Remove(FerryJson.IdString(id));
        else
            _sharedOverrides[FerryJson.IdString(id)] = value;
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
    /// 読み込んだ接続先を追加する。同じ接続先（ホスト・ユーザー・ポート・プロトコル）は飛ばす。
    /// フォルダは同名のものがあればそこに入れる。戻り値は追加した件数。
    /// </summary>
    public int Import(IEnumerable<(string? FolderName, IReadOnlyList<ServerConfig> Servers)> groups)
    {
        var added = 0;
        foreach (var (folderName, servers) in groups)
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
            foreach (var server in servers)
            {
                if (Servers.Any(s => s.IsSameDestination(server))) continue;
                _personalServers.Add(server with { FolderId = folderId });
                added++;
            }
        }
        Save();
        return added;
    }

    public int DuplicateCount(IEnumerable<ServerConfig> servers) =>
        servers.Count(server => Servers.Any(s => s.IsSameDestination(server)));

    // MARK: 除外ルール

    public void SetExcludeRules(ExcludeRules rules)
    {
        ExcludeRules = rules;
        Save();
    }

    public void Dispose() => Shared.Dispose();
}
