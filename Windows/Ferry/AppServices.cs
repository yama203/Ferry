using Ferry.Core;
using Microsoft.UI.Dispatching;

namespace Ferry;

/// <summary>アプリ全体で 1 つのもの（接続先、パスワード、この PC の設定）</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    public static PasswordService Passwords { get; private set; } = null!;
    public static ServerStore Store { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;
    /// <summary>資格情報マネージャー（パスワードと、保管庫の鍵の置き場）</summary>
    public static CredentialPasswordStore Credentials { get; } = new();

    /// <summary>保管庫の状態が変わったとき（UI スレッドとは限らない）</summary>
    public static event EventHandler? VaultsChanged;

    private const string PersonalVaultKey = "vault-key";
    private const string SharedVaultKey = "shared-vault-key";

    public static void Initialize(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = AppSettings.Load();
        Passwords = new PasswordService(Credentials);

        // iCloud Drive があれば、自分の接続先とパスワードは Mac 版と同じファイルを使う（自分の Mac と Windows で同期）
        if (ServerStore.ICloudFilePath is { } iCloud)
        {
            var localOverrides = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "overrides.json");
            Store = new ServerStore(iCloud, Passwords, localOverrides, watch: true);
            MigrateToICloud();
            Passwords.Personal = new PasswordVault(Path.Combine(Path.GetDirectoryName(iCloud)!, "passwords.vault"),
                                                   Credentials, PersonalVaultKey);
            Passwords.Personal.Changed += (_, _) => VaultsChanged?.Invoke(null, EventArgs.Empty);
        }
        else
        {
            Store = new ServerStore(passwords: Passwords);
        }
        Passwords.IsShared = Store.IsShared;

        if (Settings.SharedDirectory is { } shared && Directory.Exists(shared))
        {
            try
            {
                Store.Shared.SetDirectory(shared);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        UpdateSharedVault();
    }

    /// <summary>iCloud Drive を使う前に %APPDATA% に保存していた接続先を、一度だけ取り込む</summary>
    private static void MigrateToICloud()
    {
        var old = ServerStore.DefaultFilePath;
        if (!File.Exists(old)) return;
        try
        {
            Store.MergePersonalFrom(old);
            File.Move(old, old + ".iCloud に移しました", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(e, "iCloud Drive への移行");
        }
    }

    /// <summary>会社の共有フォルダが変わったら、会社のパスワードの保管庫も作り直す</summary>
    public static void UpdateSharedVault()
    {
        Passwords.Shared?.Dispose();
        Passwords.Shared = null;
        if (Store.Shared.DirectoryPath is { } directory)
        {
            Passwords.Shared = new PasswordVault(Path.Combine(directory, "passwords.vault"), Credentials, SharedVaultKey);
            Passwords.Shared.Changed += (_, _) => VaultsChanged?.Invoke(null, EventArgs.Empty);
        }
        VaultsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>マスターパスワードの入力が必要な保管庫があるか（他の PC で同期を始めたが、この PC ではまだ）</summary>
    public static bool NeedsMasterPassword =>
        Passwords.Personal?.State == PasswordVault.VaultState.Locked
        || Passwords.Shared?.State == PasswordVault.VaultState.Locked;

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
        : Store.KeepsPerPcSettingsLocally
            ? "保存場所: iCloud Drive（Mac と同期）"
            : $"保存場所: {Path.GetDirectoryName(Store.FilePath)}";
}
