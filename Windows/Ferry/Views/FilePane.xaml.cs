using System.ComponentModel;
using System.Diagnostics;
using Ferry.Core.Remote;
using Ferry.Models;
using Ferry.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Ferry.Views;

/// <summary>片側のペイン（見出し、パンくず、一覧、件数）</summary>
public sealed partial class FilePane : UserControl
{
    public BrowserViewModel ViewModel { get; }

    public FilePane(BrowserViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        List.ItemTemplate = (DataTemplate)Resources[viewModel.IsRemote ? "RemoteTemplate" : "LocalTemplate"];
        if (viewModel.IsRemote)
        {
            HeaderGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["FerryRemoteHeaderBrush"];
            SideIcon.Glyph = "";
            SideLabel.Text = "サーバー";
            LocalSeparator.Visibility = ChooseFolderItem.Visibility =
                ExplorerWindowsItem.Visibility = OpenInExplorerItem.Visibility = Visibility.Collapsed;
        }
        else
        {
            SideIcon.Glyph = "";
            SideLabel.Text = "この PC";
            PermissionsColumn.Width = new GridLength(0);
            PermissionsHeader.Visibility = Visibility.Collapsed;
        }
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSortHeaders();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserViewModel.SortKey) or nameof(BrowserViewModel.Ascending)) UpdateSortHeaders();
    }

    private void UpdateSortHeaders()
    {
        var arrow = ViewModel.Ascending ? " ▲" : " ▼";
        NameHeader.Content = "名前" + (ViewModel.SortKey == SortKey.Name ? arrow : "");
        SizeHeader.Content = "サイズ" + (ViewModel.SortKey == SortKey.Size ? arrow : "");
        DateHeader.Content = "更新日" + (ViewModel.SortKey == SortKey.Date ? arrow : "");
    }

    /// <summary>転送に使う接続先の画面（SessionView が設定する）</summary>
    public SessionViewModel? Session { get; set; }

    public IReadOnlyList<FileEntry> SelectedEntries => List.SelectedItems.OfType<FileEntry>().ToList();

    // MARK: 見出し

    private async void Back_Click(object sender, RoutedEventArgs e) => await ViewModel.GoBackAsync();
    private async void Up_Click(object sender, RoutedEventArgs e) => await ViewModel.GoUpAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb && crumb.Path != ViewModel.Path) await ViewModel.LoadAsync(crumb.Path);
    }

    private void SortName_Click(object sender, RoutedEventArgs e) => ViewModel.SortBy(SortKey.Name);
    private void SortSize_Click(object sender, RoutedEventArgs e) => ViewModel.SortBy(SortKey.Size);
    private void SortDate_Click(object sender, RoutedEventArgs e) => ViewModel.SortBy(SortKey.Date);

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = null;

    private void PaneMenu_Opening(object sender, object e)
    {
        HiddenToggle.IsChecked = ViewModel.ShowHidden;
        OpenInExplorerItem.IsEnabled = ViewModel.Path.Length > 0;
        if (ViewModel.IsRemote) return;
        // エクスプローラーで開いているフォルダへ移動できるように
        ExplorerWindowsItem.Items.Clear();
        var folders = ExplorerWindows.OpenFolders();
        foreach (var folder in folders)
        {
            var item = new MenuFlyoutItem { Text = folder };
            item.Click += async (_, _) => await ViewModel.LoadAsync(folder);
            ExplorerWindowsItem.Items.Add(item);
        }
        if (folders.Count == 0)
            ExplorerWindowsItem.Items.Add(new MenuFlyoutItem { Text = "開いているフォルダはありません", IsEnabled = false });
    }

    private void HiddenToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowHidden = HiddenToggle.IsChecked;
        if (ViewModel.IsRemote) AppServices.Settings.ShowHiddenRemote = ViewModel.ShowHidden;
        else AppServices.Settings.ShowHiddenLocal = ViewModel.ShowHidden;
        AppServices.Settings.Save();
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await NewFolderAsync();

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null) await ViewModel.LoadAsync(result.Path);
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ViewModel.Path}\"") { UseShellExecute = true });

    // MARK: 一覧

    private CancellationTokenSource? _prefetchDelay;

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedCount = List.SelectedItems.Count;
        if (List.SelectedItems.Count != 1 || List.SelectedItem is not FileEntry entry) return;
        // プレビューを開いたまま ↑↓ で次のファイル
        if (PreviewWindow.IsOpen) _ = PreviewWindow.UpdateAsync(entry, () => PreviewPathAsync(entry));
        // サーバー側で小さいファイルを選んで 0.25 秒止まったら、裏で先にダウンロードしておく
        if (ViewModel.IsRemote && Session is { } session && !entry.IsDirectory)
        {
            _prefetchDelay?.Cancel();
            _prefetchDelay = new CancellationTokenSource();
            try
            {
                await Task.Delay(250, _prefetchDelay.Token);
                session.PrefetchPreview(entry);
            }
            catch (TaskCanceledException) { }
        }
    }

    private Task<string?> PreviewPathAsync(FileEntry entry) =>
        ViewModel.IsRemote
            ? Session?.PreviewPathAsync(entry) ?? Task.FromResult<string?>(null)
            : Task.FromResult<string?>(entry.Path);

    /// <summary>スペースでプレビューを開閉する（項目より先に受け取る）</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || List.SelectedItem is not FileEntry entry) return;
        e.Handled = true;
        _ = PreviewWindow.ToggleAsync(entry, () => PreviewPathAsync(entry));
    }

    private async void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry entry) await OpenAsync(entry);
    }

    /// <summary>フォルダは開く、サーバーのファイルはダウンロード、ローカルのファイルは既定のアプリで開く</summary>
    private async Task OpenAsync(FileEntry entry)
    {
        if (ViewModel.IsRemote && !entry.IsDirectory && !entry.IsSymlink)
        {
            if (Session is not null) await Session.DownloadAsync([entry]);
            return;
        }
        await ViewModel.OpenAsync(entry);
    }

    private async void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter when List.SelectedItem is FileEntry entry:
                e.Handled = true;
                await OpenAsync(entry);
                break;
            case VirtualKey.Delete when List.SelectedItems.Count > 0:
                e.Handled = true;
                await DeleteAsync(SelectedEntries);
                break;
            case VirtualKey.F2 when List.SelectedItem is FileEntry entry:
                e.Handled = true;
                await RenameAsync(entry);
                break;
            case VirtualKey.F5:
                e.Handled = true;
                await ViewModel.RefreshAsync();
                break;
            case VirtualKey.Back:
            case VirtualKey.Left when alt:
                e.Handled = true;
                await ViewModel.GoBackAsync();
                break;
            case VirtualKey.Up when alt:
                e.Handled = true;
                await ViewModel.GoUpAsync();
                break;
        }
    }

    /// <summary>右クリックした項目を選んでからメニューを出す</summary>
    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry clicked && !List.SelectedItems.Contains(clicked))
        {
            List.SelectedItem = clicked;
        }
        var selected = SelectedEntries;
        var menu = new MenuFlyout();
        if (selected.Count > 0)
        {
            var single = selected.Count == 1 ? selected[0] : null;
            if (single is not null && (single.IsDirectory || !ViewModel.IsRemote))
                Add(menu, "開く", "", () => OpenAsync(single));
            if (single is not null && !single.IsDirectory)
                Add(menu, "プレビュー（スペース）", "", () => PreviewWindow.ToggleAsync(single, () => PreviewPathAsync(single)));
            if (Session is { IsConnected: true } editSession && ViewModel.IsRemote && single is { IsDirectory: false })
                Add(menu, $"エディタで開く（{Editor.DisplayName}）", "", () => editSession.OpenInEditorAsync(single));
            if (Session is { IsConnected: true } session)
            {
                if (ViewModel.IsRemote && session.Local.Path.Length > 0)
                    Add(menu, "ダウンロード", "", () => session.DownloadAsync(selected));
                else if (!ViewModel.IsRemote && !selected.Any(s => s.IsDrive))
                    Add(menu, "アップロード", "", () => session.UploadAsync(selected.Select(s => s.Path)));
            }
            if (single is not null && !single.IsDrive)
                Add(menu, "名前を変更…", "", () => RenameAsync(single));
            if (ViewModel.IsRemote)
                Add(menu, "パーミッション…", "", () => ChangePermissionsAsync(selected));
            if (!ViewModel.IsRemote && single is not null)
                Add(menu, "エクスプローラーで表示", "", () =>
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{single.Path}\"") { UseShellExecute = true });
                    return Task.CompletedTask;
                });
            Add(menu, "パスをコピー", "", () =>
            {
                var package = new DataPackage();
                package.SetText(string.Join(Environment.NewLine, selected.Select(s => s.Path)));
                Clipboard.SetContent(package);
                return Task.CompletedTask;
            });
            if (!selected.Any(s => s.IsDrive))
                Add(menu, "削除", "", () => DeleteAsync(selected));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (!(ViewModel.Path.Length == 0 && !ViewModel.IsRemote))
            Add(menu, "新規フォルダ…", "", NewFolderAsync);
        Add(menu, "再読み込み", "", ViewModel.RefreshAsync);

        if (e.TryGetPosition(List, out var point)) menu.ShowAt(List, point);
        else menu.ShowAt(List);
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
    }

    // MARK: ファイル操作

    private async Task NewFolderAsync()
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "新規フォルダ", "作成", placeholder: "フォルダ名");
        if (name is not null) await ViewModel.MakeFolderAsync(name);
    }

    private async Task RenameAsync(FileEntry entry)
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "名前を変更", "変更", entry.Name, selectStem: !entry.IsDirectory);
        if (name is not null && name != entry.Name) await ViewModel.RenameAsync(entry, name);
    }

    /// <summary>ローカルはごみ箱へ（確認なし）、サーバーは確認してから完全に削除</summary>
    private async Task DeleteAsync(IReadOnlyList<FileEntry> targets)
    {
        if (targets.Count == 0 || targets.Any(t => t.IsDrive)) return;
        if (ViewModel.IsRemote)
        {
            var what = targets.Count == 1 ? $"「{targets[0].Name}」" : $"{targets.Count} 項目";
            var folders = targets.Any(t => t.IsDirectory) ? "フォルダは中身ごと削除されます。" : "";
            if (!await Dialogs.ConfirmAsync(XamlRoot, $"{what}をサーバーから削除しますか？",
                                            $"{folders}ごみ箱には入らず、元に戻せません。", "削除", destructive: true))
                return;
        }
        await ViewModel.DeleteAsync(targets);
    }

    private async Task ChangePermissionsAsync(IReadOnlyList<FileEntry> targets)
    {
        var current = targets.Count == 1 && targets[0].Permissions is { } p ? Permissions.ToOctal(p) ?? "" : "";
        var mode = await Dialogs.PromptAsync(XamlRoot, "パーミッションを変更", "変更", current, placeholder: "755 や 644 など");
        if (mode is null) return;
        if (Permissions.FromOctal(mode) is null)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "パーミッションが正しくありません", "755 や 644 のような 3 桁の数字（0〜7）で入力してください。");
            return;
        }
        await ViewModel.ChangePermissionsAsync(targets, mode);
    }

    public void FocusList() => List.Focus(FocusState.Programmatic);
}

/// <summary>エクスプローラーで開いているフォルダ（Shell.Application の Windows）</summary>
internal static class ExplorerWindows
{
    public static IReadOnlyList<string> OpenFolders()
    {
        var result = new List<string>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return result;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (var window in shell.Windows())
            {
                try
                {
                    string? path = window.Document?.Folder?.Self?.Path;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !result.Contains(path)) result.Add(path);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return result;
    }
}
