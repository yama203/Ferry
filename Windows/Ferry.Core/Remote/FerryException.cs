using System.Net.Security;

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
    /// <summary>サーバーの同時接続数の上限に達している（FTP の 421 など）</summary>
    TooManyConnections,
    /// <summary>FTPS を選んでいるが、サーバーが AUTH TLS を断った（暗号化に対応していない）</summary>
    FtpsNotSupported,
    PermissionDenied,
    NotFound,
    Cancelled,
    Other,
}

/// <summary>画面にそのまま出せる日本語のメッセージ付きのエラー</summary>
public sealed class FerryException : Exception
{
    public FerryErrorKind Kind { get; }

    /// <summary>証明書を検証できなかった理由（UntrustedCertificate のとき）</summary>
    public SslPolicyErrors CertificateErrors { get; init; }

    /// <summary>ホスト鍵が変わっていたときに、サーバーが出した新しい鍵（HostKeyChanged のとき。画面で見せて承認してもらう）</summary>
    public HostKeyInfo? NewHostKey { get; init; }

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

    public static FerryException TooManyConnections(string? detail = null, Exception? inner = null) =>
        new(FerryErrorKind.TooManyConnections,
            "サーバーの同時接続数の上限に達しているため、接続できませんでした。しばらくしてからやり直してください。" + Detail(detail), inner);

    public static FerryException UntrustedCertificate(string reason, Exception? inner = null,
                                                      SslPolicyErrors errors = SslPolicyErrors.None) =>
        new(FerryErrorKind.UntrustedCertificate, $"サーバーの証明書を確認できません（{reason}）。", inner) { CertificateErrors = errors };

    public static FerryException FtpsNotSupported(Exception? inner = null) =>
        new(FerryErrorKind.FtpsNotSupported,
            "サーバーが FTPS（暗号化）に対応していません。方式を「FTP」にするか、サーバーの設定を確認してください。", inner);

    public static FerryException HostKeyChanged(string host, HostKeyInfo? newKey = null) =>
        new(FerryErrorKind.HostKeyChanged,
            $"サーバー「{host}」のホスト鍵が前回の接続時と違います。別のサーバーにすり替えられている可能性があるため、接続を止めました。"
            + "サーバーを移転した場合などは、管理者に確認してください。") { NewHostKey = newKey };

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

/// <summary>SFTP のホスト鍵（種類と SHA256 のフィンガープリント）</summary>
/// <param name="KeyType">"ssh-ed25519" など</param>
/// <param name="Fingerprint">SSH.NET の FingerPrintSHA256 の形（パディング無しの base64。KnownHosts に記録する形）</param>
public sealed record HostKeyInfo(string KeyType, string Fingerprint)
{
    /// <summary>ssh-keygen -lf と同じ表記（"SHA256:" + パディング無しの base64）</summary>
    public string DisplayFingerprint => Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? Fingerprint : "SHA256:" + Fingerprint;
}
