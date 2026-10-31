using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>設定（会社の共有、除外）。エディタ・通知のタブは後で足す</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private string? _sharedDirectory;

    /// <summary>開いたときに選ぶタブ</summary>
    public enum Tab { Shared, Passwords, Exclude, Editor, Notifications }

    public SettingsDialog(XamlRoot root, Tab tab = Tab.Shared)
    {
        InitializeComponent();
        XamlRoot = root;
        Tabs.SelectedIndex = (int)tab;
        _sharedDirectory = AppServices.Store.Shared.DirectoryPath;
        ExcludeBox.Text = string.Join("\r", AppServices.Store.ExcludeRules.Patterns);
        _editorPath = AppServices.Settings.EditorPath;
        NotifySwitch.IsOn = AppServices.Settings.NotifyOnComplete;
        UpdateShared();
        UpdateEditor();
        RenderVaults();
    }

    // MARK: パスワードの同期

    private void RenderVaults()
    {
        var store = AppServices.Store;
        VaultsPanel.Children.Clear();
        VaultsPanel.Children.Add(new VaultPanel(
            "自分の Mac・Windows と同期",
            "自分の接続先のパスワードを、マスターパスワードで暗号化して iCloud Drive に置きます（Mac 版の「パスワードの同期」と同じもの）。",
            () => AppServices.Passwords.Personal,
            () => store.PersonalServers.Select(s => s.Id),
            "iCloud Drive が見つかりません。Windows 用 iCloud を入れて iCloud Drive を有効にすると使えます。"));
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

        AppServices.Settings.EditorPath = _editorPath;
        AppServices.Settings.NotifyOnComplete = NotifySwitch.IsOn;
        AppServices.Settings.Save();

        var patterns = ExcludeBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rules = new ExcludeRules(patterns);
        if (!rules.Equals(store.ExcludeRules)) store.SetExcludeRules(rules);
    }
}
