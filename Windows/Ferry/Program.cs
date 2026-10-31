using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Ferry;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // インストール・更新・アンインストールのときに Velopack から呼ばれた場合は、ここで処理して終わる。
        // ダウンロード済みの更新があれば、ここで入れ替えてから起動する
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
