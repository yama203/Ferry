using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ferry;

/// <summary>起動の入口は Program.Main（Velopack の処理を先に行うため）</summary>
public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

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
}
