using System.Text;
using Ferry.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Ferry.Views;

/// <summary>
/// Mac 版の Quick Look にあたるプレビュー（画像、テキスト・コード、HTML・PDF は WebView2）。
/// スペースで開閉し、開いたまま ↑↓ で次のファイル（一覧のキー操作を奪わないよう、開いてもアクティブにしない）。
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private static PreviewWindow? _shared;
    /// <summary>今表示しようとしている項目（ダウンロード中に選択が変わったら古い結果は捨てる）</summary>
    private int _request;

    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"];
    private static readonly HashSet<string> WebExtensions = [".html", ".htm", ".pdf", ".svg"];
    private const long TextSizeLimit = 2 * 1024 * 1024;

    public static bool IsOpen => _shared is not null;

    private PreviewWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var scale = (App.MainWindow?.Content.XamlRoot?.RasterizationScale) ?? 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900 * scale), (int)(700 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        Closed += (_, _) => _shared = null;
    }

    /// <summary>開いていれば閉じ、閉じていれば開く</summary>
    public static Task ToggleAsync(FileEntry entry, Func<Task<string?>> resolvePath)
    {
        if (_shared is not null)
        {
            Close_();
            return Task.CompletedTask;
        }
        return ShowAsync(entry, resolvePath);
    }

    /// <summary>開いているときだけ、表示する項目を変える（選択が変わったとき）</summary>
    public static Task UpdateAsync(FileEntry entry, Func<Task<string?>> resolvePath) =>
        _shared is null ? Task.CompletedTask : ShowAsync(entry, resolvePath);

    public static void Close_()
    {
        var window = _shared;
        _shared = null;
        window?.Close();
    }

    private static async Task ShowAsync(FileEntry entry, Func<Task<string?>> resolvePath)
    {
        if (_shared is null)
        {
            _shared = new PreviewWindow();
            // 一覧でキー操作を続けられるように、アクティブにしないで出す
            _shared.AppWindow.Show(activateWindow: false);
        }
        await _shared.DisplayAsync(entry, resolvePath);
    }

    private async Task DisplayAsync(FileEntry entry, Func<Task<string?>> resolvePath)
    {
        var request = ++_request;
        TitleText.Text = entry.Name;
        Title = entry.Name;
        HideAll();
        if (entry.IsDirectory || entry.IsDrive)
        {
            ShowInfo(entry.IsDrive ? "" : "", entry.Name, entry.IsDrive ? "ドライブ" : "フォルダ");
            return;
        }
        Loading.IsActive = true;
        var path = await resolvePath();
        if (request != _request || _shared != this) return;
        Loading.IsActive = false;
        if (path is null || !File.Exists(path))
        {
            ShowInfo("", entry.Name, "プレビューを用意できませんでした");
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ImageExtensions.Contains(extension))
            {
                ImageControl.Source = new BitmapImage(new Uri(path));
                ImageView.Visibility = Visibility.Visible;
            }
            else if (WebExtensions.Contains(extension))
            {
                await WebView.EnsureCoreWebView2Async();
                if (request != _request) return;
                WebView.Source = new Uri(path);
                WebView.Visibility = Visibility.Visible;
            }
            else if (await Task.Run(() => ReadText(path)) is { } text)
            {
                if (request != _request) return;
                TextControl.Text = text;
                TextView.ChangeView(0, 0, null, disableAnimation: true);
                TextView.Visibility = Visibility.Visible;
            }
            else
            {
                ShowInfo("", entry.Name, $"{entry.SizeText}・このファイルはプレビューできません");
            }
        }
        catch (Exception e)
        {
            AppServices.Log(e, $"プレビュー {entry.Name}");
            ShowInfo("", entry.Name, "プレビューできませんでした: " + e.Message);
        }
    }

    private void HideAll()
    {
        ImageView.Visibility = TextView.Visibility = WebView.Visibility = InfoView.Visibility = Visibility.Collapsed;
        ImageControl.Source = null;
        TextControl.Text = "";
        Loading.IsActive = false;
    }

    private void ShowInfo(string glyph, string name, string detail)
    {
        InfoIcon.Glyph = glyph;
        InfoName.Text = name;
        InfoDetail.Text = detail;
        InfoView.Visibility = Visibility.Visible;
    }

    /// <summary>テキストなら中身（UTF-8 で読めなければ Shift_JIS）、そうでなければ null</summary>
    private static string? ReadText(string path)
    {
        if (new FileInfo(path).Length > TextSizeLimit) return null;
        var bytes = File.ReadAllBytes(path);
        // NUL を含むものはバイナリとみなす
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Escape or VirtualKey.Space)
        {
            e.Handled = true;
            Close_();
        }
    }
}
