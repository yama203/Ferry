using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Core;

namespace Ferry.ViewModels;

/// <summary>サイドバーの 1 行（フォルダの見出しか接続先）</summary>
public sealed partial class SidebarRow : ObservableObject
{
    public bool IsHeader { get; init; }
    public bool IsServer => !IsHeader;
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>会社の共有の見出し・接続先か</summary>
    public bool IsShared { get; init; }
    /// <summary>見出しのフォルダ（null は「フォルダなし」）</summary>
    public Guid? FolderId { get; init; }
    /// <summary>本物のフォルダの見出し（開閉・名前の変更ができる）</summary>
    public bool IsFolder { get; init; }
    /// <summary>接続先をドラッグして移せる見出し</summary>
    public bool AcceptsDrop { get; init; }
    public bool IsExpanded { get; init; } = true;
    public ServerConfig? Server { get; init; }

    /// <summary>表示が同じか（一覧を作り直すとき、同じ行はそのまま残してスクロール位置や選択を保つ）</summary>
    public bool SameAs(SidebarRow other) =>
        IsHeader == other.IsHeader && Title == other.Title && Subtitle == other.Subtitle && IsShared == other.IsShared
        && FolderId == other.FolderId && IsFolder == other.IsFolder && AcceptsDrop == other.AcceptsDrop
        && IsExpanded == other.IsExpanded && Equals(Server, other.Server);

    public string ChevronGlyph => IsExpanded ? "" : "";
    public string ServerGlyph => IsShared ? "" : "";
    public double ChevronOpacity => IsFolder ? 1 : 0;
}

/// <summary>
/// サイドバーの一覧。「会社の共有」のフォルダ → 「会社の共有・フォルダなし」 → 自分のフォルダ → 「フォルダなし」の順。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public ObservableCollection<SidebarRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string StorageDescription { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsSearchEmpty { get; private set; }

    private readonly HashSet<Guid> _collapsed;

    public SidebarViewModel()
    {
        _collapsed = [.. AppServices.Settings.CollapsedFolders];
    }

    public bool IsSearching => SearchText.Trim().Length > 0;

    partial void OnSearchTextChanged(string value) => Rebuild();

    public void ToggleFolder(Guid folderId)
    {
        if (!_collapsed.Remove(folderId)) _collapsed.Add(folderId);
        AppServices.Settings.CollapsedFolders = [.. _collapsed];
        AppServices.Settings.Save();
        Rebuild();
    }

    /// <summary>検索で最初に出ている接続先（Enter で開く）</summary>
    public ServerConfig? FirstResult => Rows.FirstOrDefault(r => r.IsServer)?.Server;

    public void Rebuild()
    {
        var store = AppServices.Store;
        var rows = new List<SidebarRow>();
        if (IsSearching)
        {
            var results = Sorted(store.Servers.Where(Matches));
            rows.Add(new SidebarRow { IsHeader = true, Title = "検索結果" });
            rows.AddRange(results.Select(s => ServerRow(s, store.IsShared(s.Id))));
            IsSearchEmpty = results.Count == 0;
        }
        else
        {
            IsSearchEmpty = false;
            // 会社の共有（Dropbox）を先に、その下に自分の接続先
            if (store.Shared.IsEnabled)
            {
                foreach (var folder in store.Shared.Folders)
                {
                    AddFolder(rows, folder, shared: true);
                }
                rows.Add(new SidebarRow
                {
                    IsHeader = true, IsShared = true, AcceptsDrop = true,
                    Title = store.Shared.Folders.Count == 0 ? "会社の共有" : "会社の共有・フォルダなし",
                });
                rows.AddRange(Sorted(store.ServersInFolder(null, shared: true)).Select(s => ServerRow(s, true)));
            }
            foreach (var folder in store.PersonalFolders)
            {
                AddFolder(rows, folder, shared: false);
            }
            rows.Add(new SidebarRow
            {
                IsHeader = true, AcceptsDrop = true,
                Title = store.PersonalFolders.Count > 0 ? "フォルダなし" : store.Shared.IsEnabled ? "自分の接続先" : "接続先",
            });
            rows.AddRange(Sorted(store.ServersInFolder(null, shared: false)).Select(s => ServerRow(s, false)));
        }

        ApplyRows(rows);
        StorageDescription = AppServices.StorageDescription;
    }

    /// <summary>
    /// 変わった部分だけを入れ替える（全部消して作り直すと、フォルダを開閉するたびに一番上へスクロールしてしまう）。
    /// 前後の同じ行はそのまま残し、間の違うところだけを差し替える。
    /// </summary>
    private void ApplyRows(List<SidebarRow> rows)
    {
        var prefix = 0;
        while (prefix < Rows.Count && prefix < rows.Count && Rows[prefix].SameAs(rows[prefix])) prefix++;
        var suffix = 0;
        while (suffix < Rows.Count - prefix && suffix < rows.Count - prefix
               && Rows[Rows.Count - 1 - suffix].SameAs(rows[rows.Count - 1 - suffix]))
            suffix++;
        for (var i = Rows.Count - suffix - 1; i >= prefix; i--) Rows.RemoveAt(i);
        for (var i = prefix; i < rows.Count - suffix; i++) Rows.Insert(i, rows[i]);
    }

    private void AddFolder(List<SidebarRow> rows, ServerFolder folder, bool shared)
    {
        var expanded = !_collapsed.Contains(folder.Id);
        var servers = Sorted(AppServices.Store.ServersInFolder(folder.Id, shared));
        rows.Add(new SidebarRow
        {
            IsHeader = true, IsFolder = true, IsShared = shared, AcceptsDrop = true, FolderId = folder.Id,
            Title = folder.Name, IsExpanded = expanded,
            Subtitle = expanded || servers.Count == 0 ? "" : servers.Count.ToString(),
        });
        if (expanded) rows.AddRange(servers.Select(s => ServerRow(s, shared)));
    }

    private static SidebarRow ServerRow(ServerConfig server, bool shared) => new()
    {
        Server = server,
        IsShared = shared,
        Title = server.Name,
        Subtitle = string.IsNullOrEmpty(server.Username) ? server.Host : $"{server.Username}@{server.Host}",
    };

    private static List<ServerConfig> Sorted(IEnumerable<ServerConfig> servers) =>
        servers.OrderBy(s => s.Name, SyncPlanner.NaturalOrder).ToList();

    /// <summary>名前・ホスト・ユーザー名・フォルダ名。空白区切りで AND。</summary>
    private bool Matches(ServerConfig server)
    {
        var folderName = AppServices.Store.Folders.FirstOrDefault(f => f.Id == server.FolderId)?.Name ?? "";
        var haystack = string.Join("\n", server.Name, server.Host, server.Username, folderName);
        return SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }
}
