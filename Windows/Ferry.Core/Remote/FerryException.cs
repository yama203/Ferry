namespace Ferry.Core.Remote;

public enum FerryErrorKind
{
    /// <summary>ユーザー名かパスワードが違う（パスワード入力画面を出す）</summary>
    LoginFailed,
    /// <summary>FTPS の証明書を検証できない（「証明書を確認できません」画面を出す）</summary>
    UntrustedCertificate,
    /// <summary>SFTP のホスト鍵が前回と違う</summary>
    HostKeyChanged,
    HostNotFound,
    ConnectionRefused,
    Timeout,
    /// <summary>制御接続はできたが、データ接続（一覧・転送）ができない</summary>
    DataConnectionFailed,
    PermissionDenied,
    NotFound,
    Cancelled,
    Other,
}

/// <summary>画面にそのまま出せる日本語のメッセージ付きのエラー</summary>
public sealed class FerryException : Exception
{
    public FerryErrorKind Kind { get; }

    public FerryException(FerryErrorKind kind, string message, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    public static FerryException LoginFailed(Exception? inner = null) =>
        new(FerryErrorKind.LoginFailed, "ログインできませんでした。ユーザー名とパスワードを確認してください。", inner);

    public static FerryException HostNotFound(string host, Exception? inner = null) =>
        new(FerryErrorKind.HostNotFound, $"サーバー「{host}」が見つかりません。ホスト名を確認してください。", inner);

    public static FerryException ConnectionRefused(string host, int port, Exception? inner = null) =>
        new(FerryErrorKind.ConnectionRefused,
            $"サーバー「{host}」のポート {port} に接続できませんでした。ポート番号と方式（FTP / SFTP など）を確認してください。", inner);

    public static FerryException Timeout(Exception? inner = null) =>
        new(FerryErrorKind.Timeout, "サーバーから応答がありません（タイムアウト）。ネットワークの状態を確認してください。", inner);

    public static FerryException DataConnectionFailed(Exception? inner = null) =>
        new(FerryErrorKind.DataConnectionFailed,
            "データ接続（一覧・転送用の接続）ができませんでした。接続設定の「パッシブモード」を切り替えると直ることがあります。", inner);

    public static FerryException UntrustedCertificate(string reason, Exception? inner = null) =>
        new(FerryErrorKind.UntrustedCertificate, $"サーバーの証明書を確認できません（{reason}）。", inner);

    public static FerryException HostKeyChanged(string host) =>
        new(FerryErrorKind.HostKeyChanged,
            $"サーバー「{host}」のホスト鍵が前回の接続時と違います。別のサーバーにすり替えられている可能性があるため、接続を止めました。"
            + "サーバーを移転した場合などは、管理者に確認してください。");

    public static FerryException PermissionDenied(string? detail = null, Exception? inner = null) =>
        new(FerryErrorKind.PermissionDenied, "権限がありません。" + Detail(detail), inner);

    public static FerryException NotFound(string? detail = null, Exception? inner = null) =>
        new(FerryErrorKind.NotFound, "ファイルまたはフォルダが見つかりません。" + Detail(detail), inner);

    public static FerryException Cancelled() => new(FerryErrorKind.Cancelled, "中止しました。");

    public static FerryException Other(string message, Exception? inner = null) =>
        new(FerryErrorKind.Other, message, inner);

    private static string Detail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "" : $"（サーバーの応答: {detail.Trim()}）";
}
