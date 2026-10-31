using Ferry.Core;
using Microsoft.UI.Dispatching;

namespace Ferry;

/// <summary>アプリ全体で 1 つのもの（接続先、パスワード、この PC の設定）</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    public static IPasswordStore Passwords { get; private set; } = null!;
    public static ServerStore Store { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = AppSettings.Load();
        Passwords = new CredentialPasswordStore();
        Store = new ServerStore(passwords: Passwords);
        if (Settings.SharedDirectory is { } shared && Directory.Exists(shared))
        {
            try
            {
                Store.Shared.SetDirectory(shared);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>UI スレッドで実行する（接続先の変更通知は別のスレッドから届くことがある）</summary>
    public static void OnUi(Action action)
    {
        if (Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }

    /// <summary>想定外のエラーを %LOCALAPPDATA%\Ferry\ferry.log に残す（会社の人から報告をもらうときに使う）</summary>
    public static void Log(Exception e, string where)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "ferry.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\n{e}\n\n");
        }
        catch (IOException) { }
    }

    public static string StorageDescription => Store.Shared.IsEnabled
        ? $"会社の共有: {Store.Shared.DirectoryPath}"
        : $"保存場所: {Path.GetDirectoryName(Store.FilePath)}";
}
