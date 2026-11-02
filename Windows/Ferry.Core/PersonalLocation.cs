namespace Ferry.Core;

/// <summary>
/// 自分の接続先（servers.json）とパスワードの保管庫（passwords.vault）の保存場所。
/// <list type="bullet">
/// <item>iCloud Drive の Ferry フォルダ（Mac 版と同じ。Windows 用 iCloud があれば既定）</item>
/// <item>選んだフォルダ（自分だけの Dropbox・OneDrive など）</item>
/// <item>この PC だけ（%APPDATA%\Ferry。どことも同期しない）</item>
/// </list>
/// 設定（AppSettings.PersonalDirectory）は null で自動（iCloud Drive があればそこ、無ければこの PC）、"local" でこの PC、
/// それ以外はフォルダのパス。
/// </summary>
public sealed record PersonalLocation(PersonalLocation.LocationKind Kind, string Directory)
{
    public enum LocationKind { ICloud, Folder, Local }

    public const string LocalSetting = "local";

    public string ServersPath => Path.Combine(Directory, "servers.json");
    public string VaultPath => Path.Combine(Directory, "passwords.vault");
    /// <summary>他の PC と同期する場所か（この PC だけでなければ、PC ごとの設定を別のファイルに分け、パスワードの保管庫を使う）</summary>
    public bool IsSynced => Kind != LocationKind.Local;

    /// <summary>この PC だけの場所（%APPDATA%\Ferry）</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ferry");

    /// <summary>iCloud Drive の Ferry フォルダ（Windows 用 iCloud が無ければ null）</summary>
    public static string? ICloudDirectory
    {
        get
        {
            var iCloud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive");
            return System.IO.Directory.Exists(iCloud) ? Path.Combine(iCloud, "Ferry") : null;
        }
    }

    /// <summary>設定から場所を決める。選んだフォルダが無くなっていたら自動に戻す</summary>
    public static PersonalLocation Resolve(string? setting, string? iCloudDirectory, string localDirectory)
    {
        if (setting == LocalSetting) return new PersonalLocation(LocationKind.Local, localDirectory);
        if (!string.IsNullOrEmpty(setting) && System.IO.Directory.Exists(setting))
        {
            return iCloudDirectory is not null && SamePath(setting, iCloudDirectory)
                ? new PersonalLocation(LocationKind.ICloud, iCloudDirectory)
                : new PersonalLocation(LocationKind.Folder, setting);
        }
        return iCloudDirectory is not null
            ? new PersonalLocation(LocationKind.ICloud, iCloudDirectory)
            : new PersonalLocation(LocationKind.Local, localDirectory);
    }

    public static PersonalLocation Resolve(string? setting) => Resolve(setting, ICloudDirectory, LocalDirectory);

    /// <summary>
    /// 新しい場所へ移る準備。新しい場所にまだ servers.json が無ければ、今の servers.json と passwords.vault をコピーする。
    /// すでにあれば（2 台目の PC など）そちらを使うので何もしない。戻り値は「すでにあったか」。
    /// </summary>
    public static bool PrepareMove(PersonalLocation from, PersonalLocation to)
    {
        if (File.Exists(to.ServersPath)) return true;
        System.IO.Directory.CreateDirectory(to.Directory);
        if (File.Exists(from.ServersPath)) File.Copy(from.ServersPath, to.ServersPath);
        if (File.Exists(from.VaultPath) && !File.Exists(to.VaultPath)) File.Copy(from.VaultPath, to.VaultPath);
        return false;
    }

    /// <summary>設定に書く値（iCloud Drive は null＝自動）</summary>
    public string? Setting => Kind switch
    {
        LocationKind.Local => LocalSetting,
        LocationKind.Folder => Directory,
        _ => null,
    };

    public string Description => Kind switch
    {
        LocationKind.ICloud => "iCloud Drive（自分の Mac と同期）",
        LocationKind.Folder => Directory,
        _ => "この PC だけ（同期しない）",
    };

    /// <summary>
    /// 会社の共有フォルダ（またはその中）か。自分の一覧が会社の全員に見え、
    /// パスワードの保管庫も会社のもの（同じ passwords.vault）と重なるので、保存場所にはさせない
    /// </summary>
    public bool IsInside(string? sharedDirectory)
    {
        if (string.IsNullOrEmpty(sharedDirectory)) return false;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory));
        var shared = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedDirectory));
        return string.Equals(path, shared, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(shared + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
