using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Ferry;

/// <summary>Windows の通知（App Notifications）</summary>
public static class Notifier
{
    private static bool _registered;

    /// <summary>起動時に呼ぶ</summary>
    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception e)
        {
            // 通知が使えない環境でも、転送などはそのまま使えるようにする
            AppServices.Log(e, "通知の登録");
        }
    }

    public static void Unregister()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception) { }
    }

    public static void Show(string title, string body)
    {
        if (!_registered) return;
        try
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            AppServices.Log(e, "通知");
        }
    }
}
