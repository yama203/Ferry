using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>よく使う確認・入力のダイアログ</summary>
public static class Dialogs
{
    /// <summary>ContentDialog は同時に 1 つしか開けないので、順番に開く</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static Task ShowErrorAsync(XamlRoot root, string title, string message) =>
        ShowAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
        });

    /// <summary>確認。destructive なら実行するボタンを既定にしない（Enter で消えないように）</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string action,
                                                bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "キャンセル",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>名前などを 1 つ入力してもらう。キャンセルなら null</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string action, string initial = "",
                                                  string placeholder = "", bool selectStem = false)
    {
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = action,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = initial.Trim().Length > 0,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            // 名前の変更では拡張子の前までを選ぶ（エクスプローラーと同じ）
            var dot = selectStem ? initial.LastIndexOf('.') : -1;
            box.Select(0, dot > 0 ? dot : initial.Length);
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
