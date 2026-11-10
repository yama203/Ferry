using System.ComponentModel;
using Ferry.Core;
using Ferry.Core.Remote;
using Ferry.ViewModels;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ferry.Views;

/// <summary>1 つの接続先の画面（左が「この PC」、右がサーバー）</summary>
public sealed partial class SessionView : UserControl
{
    public SessionViewModel ViewModel { get; }

    /// <summary>「接続設定」が押された</summary>
    public event EventHandler? EditRequested;

    private readonly FilePane _localPane;
    private readonly FilePane _remotePane;
    private bool _dragging;

    public SessionView(SessionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _localPane = new FilePane(viewModel.Local) { Session = viewModel };
        _remotePane = new FilePane(viewModel.Remote) { Session = viewModel };
        LocalHost.Content = _localPane;
        RemoteHost.Content = _remotePane;
        ApplySplit(AppServices.Settings.PaneSplit);
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Transfers.AskConflict = AskConflictAsync;
        viewModel.AskEditConflict = AskEditConflictAsync;
    }

    // MARK: 同期・ブックマーク・編集中

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Local.Path.Length == 0)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "同期するフォルダを開いてください", "左のペインで、同期するこの PC のフォルダを開いてください。");
            return;
        }
        await Dialogs.ShowAsync(new SyncDialog(XamlRoot, ViewModel));
    }

    private void BookmarkMenu_Opening(object sender, object e)
    {
        var store = AppServices.Store;
        var server = store.Server(ViewModel.Server.Id) ?? ViewModel.Server;
        var bookmarks = server.Bookmarks ?? [];
        BookmarkMenu.Items.Clear();
        foreach (var bookmark in bookmarks)
        {
            var item = new MenuFlyoutItem
            {
                Text = bookmark.Name + (bookmark.Id == server.StartBookmarkId ? "（接続時に開く）" : ""),
                Icon = new FontIcon { Glyph = "" },
            };
            ToolTipService.SetToolTip(item, $"{bookmark.LocalPath}\n{bookmark.RemotePath}");
            item.Click += async (_, _) => await ViewModel.OpenBookmarkAsync(bookmark);
            BookmarkMenu.Items.Add(item);
        }
        if (bookmarks.Count > 0) BookmarkMenu.Items.Add(new MenuFlyoutSeparator());

        var add = new MenuFlyoutItem { Text = "今の左右の場所を追加…", Icon = new FontIcon { Glyph = "" }, IsEnabled = ViewModel.IsConnected && ViewModel.Local.Path.Length > 0 };
        add.Click += async (_, _) =>
        {
            var name = await Dialogs.PromptAsync(XamlRoot, "ブックマークを追加", "追加", ViewModel.Remote.Title, "名前");
            if (name is null) return;
            store.AddBookmark(new PaneBookmark { Name = name, LocalPath = ViewModel.Local.Path, RemotePath = ViewModel.Remote.Path }, server.Id);
        };
        BookmarkMenu.Items.Add(add);
        if (bookmarks.Count == 0) return;

        var start = new MenuFlyoutSubItem { Text = "接続時に開く", Icon = new FontIcon { Glyph = "" } };
        var none = new ToggleMenuFlyoutItem { Text = "なし（接続設定のフォルダ）", IsChecked = server.StartBookmarkId is null };
        none.Click += (_, _) => store.SetStartBookmark(null, server.Id);
        start.Items.Add(none);
        var rename = new MenuFlyoutSubItem { Text = "名前を変更", Icon = new FontIcon { Glyph = "" } };
        var remove = new MenuFlyoutSubItem { Text = "削除", Icon = new FontIcon { Glyph = "" } };
        foreach (var bookmark in bookmarks)
        {
            var startItem = new ToggleMenuFlyoutItem { Text = bookmark.Name, IsChecked = bookmark.Id == server.StartBookmarkId };
            startItem.Click += (_, _) => store.SetStartBookmark(bookmark.Id, server.Id);
            start.Items.Add(startItem);

            var renameItem = new MenuFlyoutItem { Text = bookmark.Name + "…" };
            renameItem.Click += async (_, _) =>
            {
                var name = await Dialogs.PromptAsync(XamlRoot, "ブックマークの名前を変更", "変更", bookmark.Name);
                if (name is not null) store.RenameBookmark(bookmark.Id, server.Id, name);
            };
            rename.Items.Add(renameItem);

            var removeItem = new MenuFlyoutItem { Text = bookmark.Name };
            removeItem.Click += (_, _) => store.RemoveBookmark(bookmark.Id, server.Id);
            remove.Items.Add(removeItem);
        }
        BookmarkMenu.Items.Add(start);
        BookmarkMenu.Items.Add(rename);
        BookmarkMenu.Items.Add(remove);
    }

    private void EditingMenu_Opening(object sender, object e)
    {
        EditingMenu.Items.Clear();
        foreach (var file in ViewModel.EditedFiles)
        {
            var sub = new MenuFlyoutSubItem { Text = file.Name, Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(sub, file.LastUploaded is { } uploaded
                ? $"{file.RemotePath}\n最後のアップロード: {uploaded.ToLocalTime():HH:mm:ss}"
                : file.RemotePath);
            var reopen = new MenuFlyoutItem { Text = "もう一度開く" };
            reopen.Click += (_, _) => ViewModel.ReopenEdited(file);
            var stop = new MenuFlyoutItem { Text = "監視をやめる" };
            stop.Click += (_, _) => ViewModel.StopEditing(file);
            sub.Items.Add(reopen);
            sub.Items.Add(stop);
            EditingMenu.Items.Add(sub);
        }
    }

    // MARK: 転送

    private async void Upload_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.UploadAsync(_localPane.SelectedEntries.Where(entry => !entry.IsDrive).Select(entry => entry.Path));

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Local.Path.Length == 0)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "ダウンロード先を開いてください", "左のペインで、ダウンロード先のフォルダを開いてください。");
            return;
        }
        await ViewModel.DownloadAsync(_remotePane.SelectedEntries);
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => ViewModel.Transfers.ClearFinished();
    private void CancelAll_Click(object sender, RoutedEventArgs e) => ViewModel.Transfers.CancelAll();

    private void CancelItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferItem item) ViewModel.Transfers.Cancel(item);
    }

    private void RetryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferItem item) ViewModel.Transfers.Retry(item);
    }

    /// <summary>同名のファイルがあるときは、まとめて 1 回だけ聞く</summary>
    private async Task<ConflictPolicy?> AskConflictAsync(PendingConflict conflict)
    {
        var where = conflict.Direction == TransferDirection.Upload ? "サーバー" : "この PC";
        var title = conflict.ConflictCount == 1
            ? $"「{conflict.FirstConflictName}」は{where}にすでにあります"
            : $"{conflict.ConflictCount} 個のファイルが{where}にすでにあります";
        var choices = new RadioButtons();
        choices.Items.Add("新しい場合のみ上書き（転送元の方が新しいものだけ）");
        choices.Items.Add("すべて上書き");
        choices.Items.Add("既存のファイルはスキップ");
        choices.SelectedIndex = 0;
        var others = conflict.Items.Count - conflict.ConflictCount;
        var content = new StackPanel { Spacing = 12 };
        if (others > 0)
            content.Children.Add(new TextBlock { Text = $"ほかの {others} 個のファイルはそのまま転送します。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(choices);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "転送",
            CloseButtonText = "転送をやめる",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        return choices.SelectedIndex switch
        {
            1 => ConflictPolicy.Overwrite,
            2 => ConflictPolicy.Skip,
            _ => ConflictPolicy.NewerOnly,
        };
    }

    /// <summary>
    /// エディタで開いた後に、他の人がサーバー側のファイルを変えた・消したとき。上書きするかを聞く。
    /// 別の接続先を表示している（この画面が表示されていない）こともあるので、ウィンドウの XamlRoot を使う
    /// </summary>
    private async Task<bool> AskEditConflictAsync(EditedFile file, EditConflict conflict)
    {
        if ((XamlRoot ?? App.MainWindow?.Content.XamlRoot) is not { } root) return false;
        var name = Ferry.Core.RemotePath.Name(file.RemotePath);
        var what = conflict == EditConflict.Deleted ? "削除されています" : "変更されています";
        return await Dialogs.ConfirmAsync(root, $"「{name}」はサーバーで{what}",
            $"サーバー（{ViewModel.Server.Name}）の「{name}」は、エディタで開いた後に{what}。上書きしますか？",
            "上書きする", destructive: true, cancel: "アップロードしない");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.EditedFiles))
        {
            EditingButton.Visibility = ViewModel.EditedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EditingButton.Label = $"編集中（{ViewModel.EditedFiles.Count}）";
            return;
        }
        if (e.PropertyName != nameof(SessionViewModel.Status)) return;
        if (ViewModel.ShowPasswordPrompt)
        {
            PasswordInput.Password = "";
            PasswordInput.Focus(FocusState.Programmatic);
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ReplaceServerAsync(AppServices.Store.Server(ViewModel.Server.Id) ?? ViewModel.Server);
        await ViewModel.ConnectAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private async void SubmitPassword_Click(object sender, RoutedEventArgs e) => await SubmitPasswordAsync();

    private async void PasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SubmitPasswordAsync();
    }

    private Task SubmitPasswordAsync() =>
        ViewModel.ConnectWithPasswordAsync(PasswordInput.Password, SavePasswordCheck.IsChecked == true);

    private async void TrustCertificate_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ConnectWithoutCertificateVerificationAsync();

    private async void TrustHostKey_Click(object sender, RoutedEventArgs e) => await ViewModel.TrustNewHostKeyAsync();

    // MARK: FTPS への切り替えの案内

    /// <summary>「暗号化されていません」の印。この接続先だけを調べて、結果と次にできることを出す</summary>
    private async void FtpsHint_Click(object sender, RoutedEventArgs e)
    {
        FtpsHintButton.IsEnabled = false;
        FtpsHintText.Text = "調べています…";
        FtpsCheckResult result;
        try
        {
            result = await ViewModel.CheckFtpsAsync();
        }
        catch (Exception error)
        {
            AppServices.Log(error, "FTPS の確認");
            result = new FtpsCheckResult(FtpsCheckOutcome.Failed, error.Message);
        }
        finally
        {
            FtpsHintButton.IsEnabled = true;
            FtpsHintText.Text = "暗号化されていません";
        }
        await ShowFtpsResultAsync(result);
    }

    private async Task ShowFtpsResultAsync(FtpsCheckResult result)
    {
        var shared = AppServices.Store.IsShared(ViewModel.Server.Id)
            ? "会社の共有の接続先なので、切り替えると会社の全員の設定が FTPS になります。"
            : null;
        var (title, lines, action) = result.Outcome switch
        {
            FtpsCheckOutcome.Switchable => ("FTPS に切り替えられます",
                new string?[] { "この接続先は FTPS（Explicit）でもつながり、サーバーの証明書も確認できました。切り替えると、パスワードもファイルの中身も暗号化されます。", shared },
                "FTPS に切り替える"),
            FtpsCheckOutcome.CertificateUnverified => ("FTPS でつながりますが、証明書を確認できません",
                new string?[] { $"理由: {result.Reason}", result.Advice,
                        "証明書を検証せずに FTPS にすると、盗み見は防げますが、なりすましは防げません（暗号化なしよりは安全です）。", shared },
                "証明書を検証せずに FTPS にする"),
            FtpsCheckOutcome.NotSupported => ("この接続先は FTPS に対応していません",
                new string?[] { result.Reason, "次からは「暗号化されていません」の印を出しません。" }, (string?)null),
            _ => ("FTPS に切り替えられるか調べられませんでした", new string?[] { result.Reason }, (string?)null),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = string.Join("\n\n", lines.Where(line => !string.IsNullOrEmpty(line))),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = action ?? "",
            // 対応していないと分かったときは、もう印を出さないので「今後表示しない」は要らない
            SecondaryButtonText = result.Outcome == FtpsCheckOutcome.NotSupported ? "" : "今後表示しない",
            CloseButtonText = "閉じる",
            // 証明書を検証しないで切り替えるのは、Enter で選ばれないようにする
            DefaultButton = result.Outcome == FtpsCheckOutcome.Switchable ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        switch (await Dialogs.ShowAsync(dialog))
        {
            case ContentDialogResult.Primary when result.CanSwitch:
                var running = ViewModel.Transfers.ActiveCount;
                if (running > 0 && !await Dialogs.ConfirmAsync(XamlRoot, "転送中です",
                        $"{running} 件の転送が終わっていません。FTPS に切り替えてつなぎ直すと中止されます。", "切り替える", destructive: true))
                    return;
                await ViewModel.SwitchToFtpsAsync(verifyCertificate: result.Outcome == FtpsCheckOutcome.Switchable);
                break;
            case ContentDialogResult.Secondary:
                ViewModel.HideFtpsHint();
                break;
        }
    }

    // MARK: 左右のペインの幅（変えたら覚える。境界線のダブルクリックで半分に戻す）

    private void ApplySplit(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.15, 0.85);
        LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var x = e.GetCurrentPoint(PanesGrid).Position.X;
        var width = PanesGrid.ActualWidth - Splitter.ActualWidth;
        if (width > 0) ApplySplit((x - Splitter.ActualWidth / 2) / width);
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        Splitter.ReleasePointerCapture(e.Pointer);
        SaveSplit();
    }

    private void Splitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) SaveSplit();
    }

    private void SaveSplit()
    {
        _dragging = false;
        var total = LeftColumn.Width.Value + RightColumn.Width.Value;
        AppServices.Settings.PaneSplit = LeftColumn.Width.Value / total;
        AppServices.Settings.Save();
    }

    private void Splitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ApplySplit(0.5);
        AppServices.Settings.PaneSplit = 0.5;
        AppServices.Settings.Save();
    }
}
