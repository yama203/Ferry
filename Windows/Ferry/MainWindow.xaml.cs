using Ferry.Core;
using Ferry.ViewModels;
using Ferry.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Ferry;

public sealed partial class MainWindow : Window
{
    public SidebarViewModel Sidebar { get; } = new();

    /// <summary>開いた接続先（切り替えても状態を残す）</summary>
    private readonly Dictionary<Guid, (SessionViewModel ViewModel, SessionView View)> _sessions = [];
    private Guid? _currentServerId;
    /// <summary>一覧を作り直して選択を戻している間は、接続先を開かない</summary>
    private bool _restoringSelection;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // 大きさは実際のピクセルで指定するので、画面の拡大率を掛ける
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1360 * scale), (int)(860 * scale)));

        var icon = Path.Combine(AppContext.BaseDirectory, "Ferry.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        VersionText.Text = $"Ferry {Updater.CurrentVersion}";

        Sidebar.Rebuild();
        AppServices.Store.Changed += (_, _) => AppServices.OnUi(OnStoreChanged);
        AppServices.VaultsChanged += (_, _) => AppServices.OnUi(UpdateVaultBar);
        UpdateVaultBar();
        _ = CheckForUpdateAsync();

        // 前回開いていた接続先を開く
        if (AppServices.Settings.LastServerId is { } last && AppServices.Store.Server(last) is { } server)
        {
            OpenServer(server);
        }
        Closed += async (_, _) =>
        {
            foreach (var (viewModel, _) in _sessions.Values) await viewModel.DisposeAsync();
            AppServices.Store.Dispose();
        };
    }

    private XamlRoot XamlRootOrThrow => Root.XamlRoot;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // MARK: 接続先を開く

    private void OpenServer(ServerConfig server)
    {
        if (!_sessions.TryGetValue(server.Id, out var session))
        {
            try
            {
                session = CreateSession(server);
            }
            catch (Exception e)
            {
                AppServices.Log(e, $"OpenServer {server.Name}");
                _ = Dialogs.ShowErrorAsync(XamlRootOrThrow, "接続先を開けませんでした", e.Message);
                return;
            }
        }
        _currentServerId = server.Id;
        SessionHost.Content = session.View;
        EmptyState.Visibility = Visibility.Collapsed;
        SelectCurrentRow();
        AppServices.Settings.LastServerId = server.Id;
        AppServices.Settings.Save();
        if (session.ViewModel.Status == SessionStatus.Idle) _ = session.ViewModel.ConnectAsync();
    }

    private (SessionViewModel ViewModel, SessionView View) CreateSession(ServerConfig server, PaneBookmark? bookmark = null)
    {
        var viewModel = new SessionViewModel(server) { PendingBookmark = bookmark };
        var view = new SessionView(viewModel);
        view.EditRequested += async (_, _) => await EditServerAsync(viewModel.Server.Id);
        var session = (viewModel, view);
        _sessions[server.Id] = session;
        return session;
    }

    private async Task CloseSessionAsync(Guid id)
    {
        if (!_sessions.Remove(id, out var session)) return;
        if (_currentServerId == id)
        {
            _currentServerId = null;
            SessionHost.Content = null;
            EmptyState.Visibility = Visibility.Visible;
        }
        await session.ViewModel.DisposeAsync();
    }

    private void OnStoreChanged()
    {
        Sidebar.Rebuild();
        SelectCurrentRow();
        // 他の人が共有の接続先を消した場合など、無くなった接続先の画面は閉じる
        foreach (var id in _sessions.Keys.Where(id => AppServices.Store.Server(id) is null).ToList())
        {
            _ = CloseSessionAsync(id);
        }
    }

    private void SelectCurrentRow()
    {
        _restoringSelection = true;
        ServerList.SelectedItem = Sidebar.Rows.FirstOrDefault(r => r.Server?.Id == _currentServerId);
        _restoringSelection = false;
    }

    // MARK: サイドバー

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        Sidebar.SearchText = sender.Text;
        SelectCurrentRow();
    }

    /// <summary>Enter で最初の結果を開く</summary>
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Sidebar.FirstResult is { } server) OpenServer(server);
    }

    /// <summary>サイドバーで選んでいる接続先（Ctrl・Shift で複数選べる）</summary>
    private List<ServerConfig> SelectedServers =>
        ServerList.SelectedItems.OfType<SidebarRow>().Select(r => r.Server).OfType<ServerConfig>().ToList();

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        var selected = ServerList.SelectedItems.OfType<SidebarRow>().ToList();
        if (selected.Count > 1)
        {
            // 複数選んでいる間は開かない。見出しは選ばない
            _restoringSelection = true;
            foreach (var header in e.AddedItems.OfType<SidebarRow>().Where(r => r.IsHeader).ToList())
                ServerList.SelectedItems.Remove(header);
            _restoringSelection = false;
            return;
        }
        if (selected.FirstOrDefault() is not { } row) return;
        if (row.IsHeader)
        {
            // 見出しを押したらフォルダを開閉する（選択はしない）
            if (row.IsFolder && row.FolderId is { } folderId) Sidebar.ToggleFolder(folderId);
            SelectCurrentRow();
            return;
        }
        if (row.Server is { } server && server.Id != _currentServerId) OpenServer(server);
    }

    /// <summary>選んでいる接続先をまとめて運ぶ（ID をカンマ区切りで渡す）</summary>
    private void ServerList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var servers = e.Items.OfType<SidebarRow>().Select(r => r.Server).OfType<ServerConfig>().ToList();
        if (servers.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(string.Join(",", servers.Select(s => FerryJson.IdString(s.Id))));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Header_DragOver(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SidebarRow { AcceptsDrop: true } row
            && e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"「{row.Title}」へ移動";
        }
    }

    private async void Header_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarRow { AcceptsDrop: true } row) return;
        var ids = (await e.DataView.GetTextAsync()).Split(',')
            .Select(text => Guid.TryParse(text, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        if (ids.Count > 0) await MoveServersAsync(ids, row.FolderId, row.IsShared);
    }

    /// <summary>フォルダへ移す（まとめても可）。共有と自分の間をまたぐものがあるときは、まとめて 1 回確認する</summary>
    private async Task MoveServersAsync(IReadOnlyList<Guid> ids, Guid? folderId, bool toShared)
    {
        var store = AppServices.Store;
        var servers = ids.Select(store.Server).OfType<ServerConfig>().ToList();
        string What(IReadOnlyList<ServerConfig> list) => list.Count == 1 ? $"「{list[0].Name}」" : $"{list.Count} 件の接続先";

        var toCompany = servers.Where(s => toShared && !store.IsShared(s.Id)).ToList();
        if (toCompany.Count > 0)
        {
            var passwords = AppServices.Passwords.Shared?.State == PasswordVault.VaultState.Unlocked
                ? "パスワードは会社のパスワードの同期に入ります。"
                : "パスワードは共有されません（「設定」→「パスワード」で会社の同期を使うと共有できます）。";
            if (!await Dialogs.ConfirmAsync(XamlRootOrThrow, $"{What(toCompany)}を会社で共有しますか？",
                                            $"共有フォルダに保存され、会社の全員の一覧に出ます。{passwords}", "共有する"))
                return;
        }
        var toPersonal = servers.Where(s => !toShared && store.IsShared(s.Id)).ToList();
        if (toPersonal.Count > 0
            && !await Dialogs.ConfirmAsync(XamlRootOrThrow, $"{What(toPersonal)}を自分だけの接続先にしますか？",
                                           "会社の全員の一覧から消え、自分の一覧にだけ残ります。", "自分だけにする"))
            return;
        foreach (var server in servers) store.MoveServer(server.Id, folderId, toShared);
    }

    private Task MoveServerAsync(Guid id, Guid? folderId, bool toShared) => MoveServersAsync([id], folderId, toShared);

    private void ServerList_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarRow row) return;
        var menu = new MenuFlyout();
        var selected = SelectedServers;
        if (row.Server is { } clicked && selected.Count > 1 && selected.Any(s => s.Id == clicked.Id))
        {
            // 複数選んでいるうちの 1 つを右クリックしたら、選んでいるもの全部に効くメニュー
            BuildMultipleServersMenu(menu, selected);
        }
        else if (row.Server is { } server)
        {
            BuildServerMenu(menu, server);
        }
        else if (row.IsFolder && row.FolderId is { } folderId)
        {
            Add(menu, "名前を変更…", "", async () =>
            {
                var name = await Dialogs.PromptAsync(XamlRootOrThrow, "フォルダの名前を変更", "変更", row.Title);
                if (name is not null) AppServices.Store.RenameFolder(folderId, name);
            });
            Add(menu, "フォルダを削除…", "", async () =>
            {
                var message = row.IsShared
                    ? "会社の全員の一覧からフォルダが消えます。中の接続先は「フォルダなし」に移ります。"
                    : "中の接続先は「フォルダなし」に移ります。";
                if (await Dialogs.ConfirmAsync(XamlRootOrThrow, $"フォルダ「{row.Title}」を削除しますか？", message, "削除", destructive: true))
                    AppServices.Store.DeleteFolder(folderId);
            });
        }
        else
        {
            return;
        }
        if (e.TryGetPosition(ServerList, out var point)) menu.ShowAt(ServerList, point);
        else menu.ShowAt(ServerList);
    }

    private void BuildServerMenu(MenuFlyout menu, ServerConfig server)
    {
        var store = AppServices.Store;
        var isShared = store.IsShared(server.Id);

        if (server.Bookmarks is { Count: > 0 } bookmarks)
        {
            var open = new MenuFlyoutSubItem { Text = "ブックマークで開く", Icon = new FontIcon { Glyph = "" } };
            foreach (var bookmark in bookmarks)
            {
                var item = new MenuFlyoutItem { Text = bookmark.Name };
                item.Click += async (_, _) => await OpenBookmarkAsync(server, bookmark);
                open.Items.Add(item);
            }
            menu.Items.Add(open);
        }
        Add(menu, "編集…", "", () => EditServerAsync(server.Id));
        Add(menu, "複製", "", () =>
        {
            store.Duplicate(server.Id);
            return Task.CompletedTask;
        });

        menu.Items.Add(MoveMenu([server]));

        if (store.Shared.IsEnabled)
        {
            if (isShared)
                Add(menu, "自分だけの接続先にする", "", () => MoveServerAsync(server.Id, null, false));
            else
                Add(menu, "会社で共有する", "", () => MoveServerAsync(server.Id, null, true));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "削除…", "", async () =>
        {
            var message = isShared
                ? "会社の共有の接続先です。削除すると全員の一覧から消えます。"
                : "この PC に保存したパスワードも削除します。";
            if (!await Dialogs.ConfirmAsync(XamlRootOrThrow, $"「{server.Name}」を削除しますか？", message, "削除", destructive: true))
                return;
            store.Delete(server.Id);
        });
    }

    /// <summary>「フォルダへ移動」のサブメニュー（すでにそこにあるものだけなら選べない）</summary>
    private MenuFlyoutSubItem MoveMenu(IReadOnlyList<ServerConfig> servers)
    {
        var store = AppServices.Store;
        var move = new MenuFlyoutSubItem { Text = "フォルダへ移動", Icon = new FontIcon { Glyph = "" } };
        void AddMove(string title, Guid? folderId, bool shared)
        {
            var item = new MenuFlyoutItem
            {
                Text = title,
                IsEnabled = servers.Any(s => !(s.FolderId == folderId && store.IsShared(s.Id) == shared)),
            };
            item.Click += async (_, _) => await MoveServersAsync(servers.Select(s => s.Id).ToList(), folderId, shared);
            move.Items.Add(item);
        }
        if (store.Shared.IsEnabled)
        {
            foreach (var folder in store.Shared.Folders) AddMove($"会社の共有 / {folder.Name}", folder.Id, true);
            AddMove("会社の共有・フォルダなし", null, true);
            move.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var folder in store.PersonalFolders) AddMove(folder.Name, folder.Id, false);
        AddMove(store.Shared.IsEnabled ? "自分の接続先・フォルダなし" : "フォルダなし", null, false);
        return move;
    }

    /// <summary>複数の接続先を選んでいるときの右クリックのメニュー</summary>
    private void BuildMultipleServersMenu(MenuFlyout menu, IReadOnlyList<ServerConfig> servers)
    {
        var store = AppServices.Store;
        var ids = servers.Select(s => s.Id).ToList();
        menu.Items.Add(new MenuFlyoutItem { Text = $"{servers.Count} 件の接続先", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MoveMenu(servers));
        if (store.Shared.IsEnabled)
        {
            // 共有・自分のどちらかに揃える（フォルダは「フォルダなし」に入る）
            if (servers.Any(s => !store.IsShared(s.Id)))
                Add(menu, "会社で共有する", "", () => MoveServersAsync(ids.Where(id => !store.IsShared(id)).ToList(), null, true));
            if (servers.Any(s => store.IsShared(s.Id)))
                Add(menu, "自分だけの接続先にする", "", () => MoveServersAsync(ids.Where(store.IsShared).ToList(), null, false));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, $"{servers.Count} 件を削除…", "", async () =>
        {
            var sharedCount = servers.Count(s => store.IsShared(s.Id));
            var message = sharedCount > 0
                ? $"そのうち {sharedCount} 件は会社の共有の接続先で、全員の一覧から消えます。保存したパスワードも削除します。"
                : "保存したパスワードも削除します。";
            if (!await Dialogs.ConfirmAsync(XamlRootOrThrow, $"{servers.Count} 件の接続先を削除しますか？", message, "削除", destructive: true))
                return;
            foreach (var id in ids) store.Delete(id);
        });
    }

    private async Task OpenBookmarkAsync(ServerConfig server, PaneBookmark bookmark)
    {
        if (_sessions.TryGetValue(server.Id, out var session) && session.ViewModel.IsConnected)
        {
            OpenServer(server);
            await session.ViewModel.Local.LoadAsync(bookmark.LocalPath);
            await session.ViewModel.Remote.LoadAsync(bookmark.RemotePath);
            return;
        }
        // 未接続なら接続後に開く
        if (session.ViewModel is null)
            CreateSession(server, bookmark);
        else
        {
            session.ViewModel.PendingBookmark = bookmark;
        }
        OpenServer(server);
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    // MARK: パスワードの同期

    private void UpdateVaultBar()
    {
        // 更新のお知らせと同じ場所なので、更新のお知らせが出ていればそちらを優先する
        VaultBar.IsOpen = AppServices.NeedsMasterPassword && !UpdateBar.IsOpen;
    }

    private async void OpenPasswordSettings_Click(object sender, RoutedEventArgs e)
    {
        await Dialogs.ShowAsync(new SettingsDialog(XamlRootOrThrow, SettingsDialog.Tab.Passwords));
        UpdateVaultBar();
        // マスターパスワードを入れたら、パスワード待ちの接続先をつなぎ直す
        foreach (var (viewModel, _) in _sessions.Values)
        {
            if (viewModel.Status == SessionStatus.NeedsPassword && AppServices.Passwords.GetPassword(viewModel.Server.Id) is { } password)
                await viewModel.ConnectWithPasswordAsync(password, save: false);
        }
    }

    // MARK: 自動更新

    private async Task CheckForUpdateAsync()
    {
        if (await Updater.CheckAndDownloadAsync() is not { } version) return;
        UpdateBar.Message = $"Ferry {version} をダウンロードしました。再起動すると更新されます（次に起動したときにも自動で更新されます）。";
        UpdateBar.IsOpen = true;
        UpdateVaultBar();
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        var running = _sessions.Values.Sum(s => s.ViewModel.Transfers.ActiveCount);
        if (running > 0 && !await Dialogs.ConfirmAsync(XamlRootOrThrow, "転送中です",
                $"{running} 件の転送が終わっていません。再起動すると中止されます。", "再起動して更新", destructive: true))
            return;
        Updater.ApplyAndRestart();
    }

    // MARK: 追加・編集・設定

    private void AddMenu_Opening(object sender, object e) =>
        AddSharedFolderItem.Visibility = AppServices.Store.Shared.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerEditDialog(XamlRootOrThrow, null);
        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary && dialog.Result is { } server)
            OpenServer(server);
    }

    private async Task EditServerAsync(Guid id)
    {
        if (AppServices.Store.Server(id) is not { } server) return;
        var dialog = new ServerEditDialog(XamlRootOrThrow, server);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || dialog.Result is not { } updated) return;
        // 開いている接続先は、新しい設定でつなぎ直す
        if (_sessions.TryGetValue(id, out var session))
        {
            await session.ViewModel.ReplaceServerAsync(updated);
            if (_currentServerId == id) await session.ViewModel.ConnectAsync();
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptAsync(XamlRootOrThrow, "フォルダを追加", "追加", placeholder: "フォルダ名");
        if (name is not null) AppServices.Store.AddFolder(name);
    }

    private async void AddSharedFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptAsync(XamlRootOrThrow, "会社の共有にフォルダを追加", "追加", placeholder: "フォルダ名");
        if (name is not null) AppServices.Store.AddFolder(name, shared: true);
    }

    private async void ImportFileZilla_Click(object sender, RoutedEventArgs e) =>
        await ImportDialog.ImportFileZillaAsync(XamlRootOrThrow);

    private async void ImportWinScp_Click(object sender, RoutedEventArgs e) =>
        await ImportDialog.ImportWinScpAsync(XamlRootOrThrow);

    private async void Settings_Click(object sender, RoutedEventArgs e) =>
        await Dialogs.ShowAsync(new SettingsDialog(XamlRootOrThrow));
}
