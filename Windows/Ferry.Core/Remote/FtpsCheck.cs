using System.Net.Security;

namespace Ferry.Core.Remote;

/// <summary>FTPS（Explicit）に切り替えられるかを調べた結果の種類</summary>
public enum FtpsCheckOutcome
{
    /// <summary>証明書も検証できた（そのまま切り替えられる）</summary>
    Switchable,
    /// <summary>暗号化はできるが、証明書を検証できない（検証しなければつながる）</summary>
    CertificateUnverified,
    /// <summary>サーバーが AUTH TLS を断った</summary>
    NotSupported,
    /// <summary>つながらない・ログインできない・パスワードが無い、など</summary>
    Failed,
}

/// <summary>FTPS（Explicit）に切り替えられるかを調べた結果</summary>
/// <param name="Reason">画面に出す理由（Switchable 以外）</param>
/// <param name="CertificateErrors">証明書を検証できなかった理由（CertificateUnverified のとき）</param>
public sealed record FtpsCheckResult(FtpsCheckOutcome Outcome, string? Reason = null,
                                     SslPolicyErrors CertificateErrors = SslPolicyErrors.None)
{
    /// <summary>証明書のホスト名が接続先と合わない（独自ドメインで接続していて、証明書はサーバー会社のホスト名のことが多い）</summary>
    public bool IsNameMismatch => CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);

    /// <summary>切り替えられる（証明書を検証せずに、も含む）</summary>
    public bool CanSwitch => Outcome is FtpsCheckOutcome.Switchable or FtpsCheckOutcome.CertificateUnverified;

    /// <summary>証明書を検証できないときの対処（画面に出す）</summary>
    public string? Advice => Outcome != FtpsCheckOutcome.CertificateUnverified ? null
        : IsNameMismatch
            ? "証明書のホスト名が接続先と合いません。ホスト名をサーバー会社のもの（例: sv1234.xserver.jp）に変えると検証できることがあります。"
            : "自己署名か、信頼できない発行元の証明書です。サーバーの管理者に確かめてください。";

    public static FtpsCheckResult NoPassword { get; } =
        new(FtpsCheckOutcome.Failed, "パスワードが保存されていないので調べられません。");
}

/// <summary>
/// 暗号化なしの FTP の接続先が、FTPS（Explicit）でもつながるかを調べる。
/// 同じホスト・ポート・ユーザー名・パスワードで方式だけを変えた別の接続でログインし、フォルダの一覧まで取ってから閉じる
/// （元の接続（一覧用・転送用）は使わない）
/// </summary>
public static class FtpsCheck
{
    /// <summary>調べるときの接続（テストで差し替える）</summary>
    public delegate IRemoteClient ClientFactory(ServerConfig server, string? password);

    public static async Task<FtpsCheckResult> CheckAsync(ServerConfig server, string? password,
                                                         CancellationToken cancellation = default,
                                                         ClientFactory? createClient = null)
    {
        if (server.ServerProtocol != ServerProtocol.Ftp)
            return new FtpsCheckResult(FtpsCheckOutcome.Failed, "暗号化なしの FTP の接続先ではありません。");
        if (string.IsNullOrEmpty(password) && server.Username != "anonymous") return FtpsCheckResult.NoPassword;
        createClient ??= (s, p) => new FtpRemoteClient(s, p);

        // ポートはそのまま（Port が null なら FTP・FTPS（Explicit）どちらも 21）
        var ftps = server with { ServerProtocol = ServerProtocol.Ftps, VerifyCertificate = true };
        var first = await TryAsync(createClient(ftps, password), cancellation);
        if (first is null) return new FtpsCheckResult(FtpsCheckOutcome.Switchable);
        var result = Classify(first);
        if (result.Outcome != FtpsCheckOutcome.CertificateUnverified) return result;

        // 証明書を検証しなければつながるかを続けて確かめる（暗号化はできても、その先で断られることがある）
        var second = await TryAsync(createClient(ftps with { VerifyCertificate = false }, password), cancellation);
        if (second is null) return result;
        var retry = Classify(second);
        return retry.Outcome == FtpsCheckOutcome.CertificateUnverified
            ? new FtpsCheckResult(FtpsCheckOutcome.Failed, second.Message)
            : retry;
    }

    /// <summary>ログインして、ホームの一覧を取る。成功したら null、失敗したらその例外</summary>
    private static async Task<FerryException?> TryAsync(IRemoteClient client, CancellationToken cancellation)
    {
        await using (client)
        {
            try
            {
                var home = await client.HomeDirectoryAsync(cancellation);
                // ログインだけでなく、データ接続（一覧・転送）も暗号化してできるかを確かめる（切り替えた後に一覧が出ないと困るため）
                await client.ListAsync(home, cancellation);
                return null;
            }
            catch (FerryException e)
            {
                return e;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return FerryException.Other(e.Message, e);
            }
        }
    }

    /// <summary>FTPS で接続したときのエラーから結果の種類を決める</summary>
    internal static FtpsCheckResult Classify(FerryException error) => error.Kind switch
    {
        FerryErrorKind.UntrustedCertificate => new FtpsCheckResult(
            FtpsCheckOutcome.CertificateUnverified,
            error.CertificateErrors == SslPolicyErrors.None
                ? "証明書を検証できませんでした"
                : FtpRemoteClient.DescribeCertificateProblem(error.CertificateErrors),
            error.CertificateErrors),
        FerryErrorKind.FtpsNotSupported => new FtpsCheckResult(
            FtpsCheckOutcome.NotSupported, "サーバーが FTPS（AUTH TLS）に対応していません。"),
        FerryErrorKind.LoginFailed => new FtpsCheckResult(
            FtpsCheckOutcome.Failed, "ログインできませんでした。保存されているパスワードが違うかもしれません。"),
        FerryErrorKind.Cancelled => new FtpsCheckResult(FtpsCheckOutcome.Failed, "中止しました。"),
        FerryErrorKind.DataConnectionFailed => new FtpsCheckResult(
            FtpsCheckOutcome.Failed, "FTPS でログインはできましたが、フォルダの一覧を取れませんでした。" + error.Message),
        _ => new FtpsCheckResult(FtpsCheckOutcome.Failed, error.Message),
    };
}
