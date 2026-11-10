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
    /// <summary>接続できなかった接続先の一覧が変わったとき（UI スレッド）</summary>
    public static event EventHandler? ConnectionProblemsChanged;

    /// <summary>接続できなかった理由を覚える（null で消す）。次に起動したときもサイドバーに印が出る</summary>
    public static void SetConnectionProblem(Guid id, string? problem)
    {
        var problems = Settings.ConnectionProblems;
        if (problem is null ? !problems.Remove(id) : problems.TryGetValue(id, out var old) && old == problem) return;
        if (problem is not null) problems[id] = problem;
        Settings.Save();
        ConnectionProblemsChanged?.Invoke(null, EventArgs.Empty);
    }

    private const string PersonalVaultKey = "vault-key";
    private const string SharedVaultKey = "shared-vault-key";

    /// <summary>自分の接続先の保存場所（iCloud Drive・選んだフォルダ・この PC だけ）</summary>
    public static PersonalLocation Personal { get; private set; } = null!;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = AppSettings.Load();
        Passwords = new PasswordService(Credentials);

        // 同期する場所（iCloud Drive・選んだフォルダ）なら、他の PC（Mac 版も）と同じファイルを使う。
        // PC ごとの設定は別のファイルに分け、パスワードも同じ場所の保管庫で同期する
        Personal = PersonalLocation.Resolve(Settings.PersonalDirectory);
        if (Personal.IsSynced)
        {
            var localOverrides = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "overrides.json");
            Store = new ServerStore(Personal.ServersPath, Passwords, localOverrides, watch: true);
            MigrateFromThisPc();
            Passwords.Personal = new PasswordVault(Personal.VaultPath, Credentials, PersonalVaultKey);
            Passwords.Personal.Changed += (_, _) => VaultsChanged?.Invoke(null, EventArgs.Empty);
        }
        else
        {
            Store = new ServerStore(Personal.ServersPath, Passwords);
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

    /// <summary>同期する場所を使う前に、この PC だけ（%APPDATA%）に保存していた接続先を一度だけ取り込む</summary>
    private static void MigrateFromThisPc()
    {
        var old = ServerStore.DefaultFilePath;
        if (!File.Exists(old)) return;
        try
        {
            Store.MergePersonalFrom(old);
            File.Move(old, old + ".同期する場所に移しました", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(e, "同期する場所への移行");
        }
    }

    /// <summary>
    /// 自分の接続先の保存場所を変える（次の起動から）。新しい場所に一覧が無ければ今のものをコピーする。
    /// 戻り値は「新しい場所にすでに一覧があったので、そちらを使う」か。
    /// </summary>
    public static bool ChangePersonalLocation(PersonalLocation location)
    {
        var usedExisting = PersonalLocation.PrepareMove(Personal, location);
        Settings.PersonalDirectory = location.Setting;
        Settings.Save();
        return usedExisting;
    }

    /// <summary>Ferry を起動し直す（保存場所を変えたときなど）</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Microsoft.UI.Xaml.Application.Current.Exit();
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

    /// <summary>マスターパスワードの入力が必要な保管庫があるか（他の PC で同期を始めたが、この PC ではまだ。または他の PC で変更された）</summary>
    public static bool NeedsMasterPassword =>
        Passwords.Personal?.State == PasswordVault.VaultState.Locked
        || Passwords.Shared?.State == PasswordVault.VaultState.Locked;

    /// <summary>マスターパスワードの入力を求める帯の文面（鍵は保存してあるのに開けないときは、変更されたらしいと伝える）</summary>
    public static string MasterPasswordMessage =>
        Passwords.Personal?.HasOutdatedKey == true || Passwords.Shared?.HasOutdatedKey == true
            ? "この PC に保存してあるマスターパスワードでは、パスワードの保管庫を開けなくなりました。他の PC でマスターパスワードが変更されたかもしれません。新しいマスターパスワードを入力してください。"
            : "他の PC で設定されたパスワードの同期があります。マスターパスワードを入力すると、この PC でも使えます。";

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
}
