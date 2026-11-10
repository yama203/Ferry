using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ferry;

/// <summary>起動の入口は Program.Main（Velopack の処理を先に行うため）</summary>
public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>Ferry が動いている印（閉じるまで持っておく。プロセスが終われば OS が消す）</summary>
    private static Mutex? _running;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // 想定外のエラーでもアプリを落とさず、内容を知らせる
            e.Handled = true;
            AppServices.Log(e.Exception, "UnhandledException");
            if (MainWindow is not null)
                _ = Views.Dialogs.ShowErrorAsync(MainWindow.Content.XamlRoot, "エラーが発生しました", e.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CleanUpTempFiles();
        // Shift_JIS のテキストのプレビュー用
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // WebView2（プレビュー）のデータは exe の隣ではなくユーザーのフォルダに置く
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry", "WebView2"));
        AppServices.Initialize(DispatcherQueue.GetForCurrentThread());
        Notifier.Register();
        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) =>
        {
            Views.PreviewWindow.Close_();
            Notifier.Unregister();
        };
        MainWindow.Activate();
    }

    /// <summary>
    /// 前回までにプレビュー・編集・ドラッグ用に落としたファイルを消す（起動を遅くしないよう UI スレッドの外で）。
    /// Ferry は複数起動できるので、他の Ferry が動いているとき（編集の監視中かもしれない）は消さない
    /// </summary>
    private static void CleanUpTempFiles()
    {
        bool isFirst;
        try
        {
            _running = new Mutex(false, @"Local\Ferry.Running", out isFirst);
        }
        catch (Exception e)
        {
            AppServices.Log(e, "起動の確認");
            return;
        }
        if (!isFirst) return;
        var startedAt = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            try { Core.TempFiles.CleanUp(startedAt); }
            catch (Exception e) { AppServices.Log(e, "一時ファイルの削除"); }
        });
    }
}
