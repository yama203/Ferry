namespace Ferry.Core;

/// <summary>
/// この PC だけの設定（%LOCALAPPDATA%\Ferry\settings.json）。Mac 版の UserDefaults にあたる。
/// 接続先のファイルとは別にしているのは、PC ごとに Dropbox の場所や画面の状態が違うため。
/// </summary>
public sealed class AppSettings
{
    /// <summary>会社の共有フォルダ（Dropbox など）。null なら共有しない。</summary>
    public string? SharedDirectory { get; set; }
    /// <summary>自分の接続先の保存場所。null なら自動（iCloud Drive があればそこ）、"local" でこの PC だけ、それ以外はフォルダ（PersonalLocation）</summary>
    public string? PersonalDirectory { get; set; }
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
    /// <summary>同時に転送する数（1〜4）。転送用の接続をこの数まで使う。変えたら次に始まる転送から効く</summary>
    public int ConcurrentTransfers { get; set; } = 3;
    /// <summary>サーバーにすでにあるファイルへのアップロードは、一時ファイルに送ってから置き換える（途中で切れても書きかけを残さない）</summary>
    public bool UploadViaTemporaryFile { get; set; } = true;
    /// <summary>最後に接続できなかった接続先と、その理由（サイドバーに印を出す。接続できたら消す）</summary>
    public Dictionary<Guid, string> ConnectionProblems { get; set; } = [];
    /// <summary>
    /// 接続中の画面に「暗号化されていません」の印を出さない接続先（FTPS に対応していないと分かったもの・「今後表示しない」を選んだもの）。
    /// 値は理由（"notSupported" か "dismissed"）
    /// </summary>
    public Dictionary<Guid, string> HiddenFtpsHints { get; set; } = [];

    /// <summary>FTP（暗号化なし）の接続先に「暗号化されていません」の印を出すか</summary>
    public bool ShowsFtpsHint(ServerConfig server) =>
        server.ServerProtocol == ServerProtocol.Ftp && !HiddenFtpsHints.ContainsKey(server.Id);

    /// <summary>調べた結果を覚える（対応していないと分かったら、印を出さない）。戻り値は変わったか（変わったら保存する）</summary>
    public bool RememberFtpsCheck(Guid serverId, Remote.FtpsCheckResult result)
    {
        if (result.Outcome != Remote.FtpsCheckOutcome.NotSupported) return false;
        return HiddenFtpsHints.TryAdd(serverId, "notSupported");
    }

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
