using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>設定（会社の共有、保存場所、パスワード、除外、転送、エディタ、通知）</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private string? _sharedDirectory;

    /// <summary>開いたときに選ぶタブ（左のタブの並び順）</summary>
    public enum Tab { Shared, Storage, Passwords, Exclude, Transfer, Editor, Notifications }

    /// <summary>自分の接続先の保存場所を変えたときの説明（再起動を勧める）。変えていなければ null</summary>
    public string? StorageMessage { get; private set; }

    private PersonalLocation _storage = AppServices.Personal;

    public SettingsDialog(XamlRoot root, Tab tab = Tab.Shared)
    {
        InitializeComponent();
        XamlRoot = root;
        Tabs.SelectedIndex = (int)tab;
        _sharedDirectory = AppServices.Store.Shared.DirectoryPath;
        ExcludeBox.Text = string.Join("\r", AppServices.Store.ExcludeRules.Patterns);
        _editorPath = AppServices.Settings.EditorPath;
        NotifySwitch.IsOn = AppServices.Settings.NotifyOnComplete;
        ConcurrencyBox.SelectedIndex = Math.Clamp(AppServices.Settings.ConcurrentTransfers, 1, TransferLanes<object>.MaxLanes) - 1;
        SafeUploadSwitch.IsOn = AppServices.Settings.UploadViaTemporaryFile;
        UpdateShared();
        UpdateEditor();
        UpdateStorage();
        RenderVaults();
    }

    /// <summary>左のタブを選んだら、右の中身を切り替える（並びは Tab の順）</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UIElement[] pages = [SharedPage, StoragePage, PasswordsPage, ExcludePage, TransferPage, EditorPage, NotificationsPage];
        for (var i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == Tabs.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: 自分の接続先の保存場所（保存したときに切り替える）

    private void UpdateStorage()
    {
        StorageIcon.Glyph = _storage.Kind switch
        {
            PersonalLocation.LocationKind.ICloud => "",
            PersonalLocation.LocationKind.Folder => "",
            _ => "",
        };
        StorageText.Text = _storage.Description + (_storage == AppServices.Personal ? "" : "（保存すると切り替えます）");
        UseICloudButton.Visibility = PersonalLocation.ICloudDirectory is null ? Visibility.Collapsed : Visibility.Visible;
        UseICloudButton.IsEnabled = _storage.Kind != PersonalLocation.LocationKind.ICloud;
        UseLocalButton.IsEnabled = _storage.Kind != PersonalLocation.LocationKind.Local;
    }

    private async void ChooseStorage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is null) return;
        _storage = PersonalLocation.Resolve(result.Path);
        UpdateStorage();
    }

    private void UseICloud_Click(object sender, RoutedEventArgs e)
    {
        _storage = PersonalLocation.Resolve(null);
        UpdateStorage();
    }

    private void UseLocal_Click(object sender, RoutedEventArgs e)
    {
        _storage = PersonalLocation.Resolve(PersonalLocation.LocalSetting);
        UpdateStorage();
    }

    // MARK: パスワードの同期

    private void RenderVaults()
    {
        var store = AppServices.Store;
        VaultsPanel.Children.Clear();
        VaultsPanel.Children.Add(new VaultPanel(
            "自分の PC の間で同期",
            "自分の接続先のパスワードを、マスターパスワードで暗号化して、自分の接続先と同じ場所（「保存場所」のタブ）に置きます（Mac 版の「パスワードの同期」と同じもの）。",
            () => AppServices.Passwords.Personal,
            () => store.PersonalServers.Select(s => s.Id),
            "自分の接続先を「この PC だけ」に保存しているので使えません。「保存場所」のタブで iCloud Drive か同期するフォルダを選ぶと使えます。"));
        VaultsPanel.Children.Add(new VaultPanel(
            "会社で同期",
            "会社の共有の接続先のパスワードを、会社のマスターパスワードで暗号化して共有フォルダに置きます。会社のマスターパスワードを知っている人が使えます。",
            () => AppServices.Passwords.Shared,
            () => store.SharedServers.Select(s => s.Id),
            "会社の共有フォルダを設定すると使えます（「会社の共有」のタブ）。"));
    }

    // MARK: エディタ

    private string? _editorPath;

    private void UpdateEditor()
    {
        var path = _editorPath is { } chosen && File.Exists(chosen) ? chosen : Editor.FindVisualStudioCode();
        EditorNameText.Text = path is null
            ? "既定のアプリ（VS Code が見つかりません）"
            : System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(path);
        EditorPathText.Text = path ?? "ファイルの種類ごとに Windows で設定されたアプリで開きます。";
        ResetEditorButton.IsEnabled = _editorPath is not null;
    }

    private async void ChooseEditor_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".exe");
        var result = await picker.PickSingleFileAsync();
        if (result is null) return;
        _editorPath = result.Path;
        UpdateEditor();
    }

    private void ResetEditor_Click(object sender, RoutedEventArgs e)
    {
        _editorPath = null;
        UpdateEditor();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e) =>
        Notifier.Show("Ferry の通知", "転送が終わると、このように知らせます。");

    private void UpdateShared()
    {
        SharedPathText.Text = _sharedDirectory ?? "共有していません";
        StopSharingButton.IsEnabled = _sharedDirectory is not null;
    }

    private async void ChooseShared_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is null) return;
        _sharedDirectory = result.Path;
        UpdateShared();
    }

    private void StopSharing_Click(object sender, RoutedEventArgs e)
    {
        _sharedDirectory = null;
        UpdateShared();
    }

    private void ResetExclude_Click(object sender, RoutedEventArgs e) =>
        ExcludeBox.Text = string.Join("\r", ExcludeRules.Defaults.Patterns);

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var store = AppServices.Store;
        // 自分の保存場所を会社の共有フォルダ（またはその中）にはさせない（どちらを変えた場合も）
        if (_storage.IsInside(_sharedDirectory))
        {
            args.Cancel = true;
            const string problem = "自分の接続先の保存場所と会社の共有フォルダを重ねられません。自分の接続先が会社の全員に見えてしまいます。";
            if (_storage != AppServices.Personal) StorageText.Text = problem;
            else SharedPathText.Text = problem;
            return;
        }
        if (_sharedDirectory != store.Shared.DirectoryPath)
        {
            try
            {
                store.Shared.SetDirectory(_sharedDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                SharedPathText.Text = $"このフォルダは使えません: {e.Message}";
                return;
            }
            AppServices.Settings.SharedDirectory = _sharedDirectory;
            AppServices.Settings.Save();
            AppServices.UpdateSharedVault();
        }

        if (_storage != AppServices.Personal)
        {
            try
            {
                var usedExisting = AppServices.ChangePersonalLocation(_storage);
                StorageMessage = usedExisting
                    ? $"自分の接続先を「{_storage.Description}」にある一覧に切り替えます（すでにあった一覧を使います）。"
                    : $"今の一覧とパスワードの保管庫を「{_storage.Description}」にコピーしました。再起動すると切り替わります。";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                StorageText.Text = $"このフォルダは使えません: {e.Message}";
                return;
            }
        }

        AppServices.Settings.EditorPath = _editorPath;
        AppServices.Settings.NotifyOnComplete = NotifySwitch.IsOn;
        AppServices.Settings.ConcurrentTransfers = Math.Max(ConcurrencyBox.SelectedIndex, 0) + 1;
        AppServices.Settings.UploadViaTemporaryFile = SafeUploadSwitch.IsOn;
        AppServices.Settings.Save();

        var patterns = ExcludeBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rules = new ExcludeRules(patterns);
        if (!rules.Equals(store.ExcludeRules)) store.SetExcludeRules(rules);
    }
}
