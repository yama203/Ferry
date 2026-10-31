using Ferry.Core.Import;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>FileZilla・WinSCP から読み込む内容の確認</summary>
public static class ImportDialog
{
    /// <summary>FileZilla のサイトマネージャーを読む（見つからなければファイルを選んでもらう）</summary>
    public static async Task ImportFileZillaAsync(XamlRoot root)
    {
        var path = File.Exists(FileZillaImporter.DefaultPath) ? FileZillaImporter.DefaultPath : await PickFileAsync(".xml");
        if (path is null) return;
        await ShowAsync(root, "FileZilla から読み込む", () => FileZillaImporter.Read(path));
    }

    /// <summary>WinSCP の保存したセッションを読む（レジストリ → WinSCP.ini → ファイルを選んでもらう）</summary>
    public static async Task ImportWinScpAsync(XamlRoot root)
    {
        if (WinScpImporter.ReadRegistry() is { ServerCount: > 0 } registry)
        {
            await ShowAsync(root, "WinSCP から読み込む", () => registry);
            return;
        }
        var path = WinScpImporter.IniCandidates.FirstOrDefault(File.Exists) ?? await PickFileAsync(".ini");
        if (path is null) return;
        await ShowAsync(root, "WinSCP から読み込む", () => WinScpImporter.ReadIni(path));
    }

    private static async Task<string?> PickFileAsync(string extension)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static async Task ShowAsync(XamlRoot root, string title, Func<ImportResult> read)
    {
        ImportResult result;
        try
        {
            result = read();
        }
        catch (Exception e)
        {
            await Dialogs.ShowErrorAsync(root, "読み込めませんでした", e.Message);
            return;
        }

        var store = AppServices.Store;
        var lines = new List<string>
        {
            $"{result.Folders.Count(f => f.Name is not null)} 個のフォルダ、{result.ServerCount} 件の接続先を、自分の接続先に読み込みます。",
        };
        if (store.DuplicateCount(result) is > 0 and var duplicates) lines.Add($"すでに登録済みの {duplicates} 件は飛ばします。");
        if (result.UnsupportedCount > 0) lines.Add($"対応していないプロトコル（WebDAV・S3 など）の {result.UnsupportedCount} 件は読み込めません。");
        if (result.EncryptedPasswordCount > 0) lines.Add($"マスターパスワードで暗号化された {result.EncryptedPasswordCount} 件のパスワードは読めないので、最初に接続するときに入力します。");

        var content = new StackPanel { Spacing = 10, Width = 420 };
        content.Children.Add(new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap });
        var list = new ListView { MaxHeight = 260, SelectionMode = ListViewSelectionMode.None };
        foreach (var folder in result.Folders)
            list.Items.Add($"{(folder.Name is null ? "フォルダなし" : "📁 " + folder.Name)}　{folder.Servers.Count} 件");
        content.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = "読み込む",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = result.ServerCount > 0,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var added = store.Import(result);
        var withPassword = added.Count(a => a.Password is { Length: > 0 });
        var message = $"{added.Count} 件の接続先を追加しました。";
        message += withPassword == added.Count && added.Count > 0
            ? "パスワードも一緒に保存しました。"
            : withPassword > 0
                ? $"そのうち {withPassword} 件はパスワードも保存しました。ほかは最初に接続するときに入力します。"
                : "パスワードは最初に接続するときに入力します。";
        await Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "閉じる",
        });
    }
}
