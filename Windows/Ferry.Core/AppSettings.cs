namespace Ferry.Core;

/// <summary>
/// この PC だけの設定（%LOCALAPPDATA%\Ferry\settings.json）。Mac 版の UserDefaults にあたる。
/// 接続先のファイルとは別にしているのは、PC ごとに Dropbox の場所や画面の状態が違うため。
/// </summary>
public sealed class AppSettings
{
    /// <summary>会社の共有フォルダ（Dropbox など）。null なら共有しない。</summary>
    public string? SharedDirectory { get; set; }
    /// <summary>サイドバーで閉じているフォルダの ID</summary>
    public List<Guid> CollapsedFolders { get; set; } = [];
    /// <summary>前回開いていた接続先（次回の起動時に開く）</summary>
    public Guid? LastServerId { get; set; }
    /// <summary>左のペインの幅の割合（0〜1）</summary>
    public double PaneSplit { get; set; } = 0.5;
    /// <summary>ローカル側で隠しファイルを表示するか（サーバー側は既定で表示）</summary>
    public bool ShowHiddenLocal { get; set; }
    public bool ShowHiddenRemote { get; set; } = true;
    /// <summary>エディタの exe。null なら VS Code（入っていれば）</summary>
    public string? EditorPath { get; set; }
    /// <summary>自動更新の置き場所。null なら 共有フォルダ\App\Windows</summary>
    public string? UpdateDirectory { get; set; }
    /// <summary>転送・同期が終わったら通知する</summary>
    public bool NotifyOnComplete { get; set; } = true;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry", "settings.json");

    private string _path = DefaultPath;

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings? settings = null;
        try
        {
            settings = FerryJson.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        settings ??= new AppSettings();
        settings._path = path;
        return settings;
    }

    public void Save()
    {
        try
        {
            FerryJson.WriteAtomically(_path, FerryJson.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
