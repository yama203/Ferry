using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>設定（会社の共有、除外）。エディタ・通知のタブは後で足す</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private string? _sharedDirectory;

    public SettingsDialog(XamlRoot root)
    {
        InitializeComponent();
        XamlRoot = root;
        _sharedDirectory = AppServices.Store.Shared.DirectoryPath;
        ExcludeBox.Text = string.Join("\r", AppServices.Store.ExcludeRules.Patterns);
        _editorPath = AppServices.Settings.EditorPath;
        NotifySwitch.IsOn = AppServices.Settings.NotifyOnComplete;
        UpdateShared();
        UpdateEditor();
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
        }

        AppServices.Settings.EditorPath = _editorPath;
        AppServices.Settings.NotifyOnComplete = NotifySwitch.IsOn;
        AppServices.Settings.Save();

        var patterns = ExcludeBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rules = new ExcludeRules(patterns);
        if (!rules.Equals(store.ExcludeRules)) store.SetExcludeRules(rules);
    }
}
