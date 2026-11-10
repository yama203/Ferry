import CCurl
import Foundation

/// 暗号化なしの FTP の接続先が、FTPS（Explicit）に切り替えられるかを調べた結果
public enum FTPSCheckResult: Equatable, Sendable {
    /// 切り替えられる（証明書も検証できた）
    case available
    /// 暗号化はできるが、証明書を検証できない（証明書のホスト名が接続先と合わない、自己署名など）。
    /// 検証しなければつながることまで確かめてある。certificateNames は証明書に書かれた名前（分かれば）
    case certificateUnverified(certificateNames: [String], detail: String?)
    /// サーバーが AUTH TLS を断った（FTPS に対応していない）
    case unsupported(detail: String?)
    /// 調べられなかった（つながらない・ログインできない・パスワードが無い など）
    case failed(reason: String)
}

public enum FTPSCheck {
    /// 同じホスト・ポート・ユーザー名・パスワードで、プロトコルだけ FTPS（Explicit）にしてログインと一覧まで試す。
    /// 新しい接続で調べるので、開いている接続（一覧用・転送用）は邪魔しない
    public static func check(_ server: ServerConfig, password: String?) async -> FTPSCheckResult {
        guard server.serverProtocol == .ftp else {
            return .failed(reason: "暗号化なしの FTP の接続先ではありません")
        }
        guard password != nil || server.username == "anonymous" else {
            return .failed(reason: "パスワードが保存されていないので調べられません")
        }

        let verified = RemoteClient(server: ftpsConfig(server, verifyCertificate: true), password: password)
        let firstError: Error
        do {
            let home = try await verified.homeDirectory()
            return await confirmListing(verified, home: home, success: .available)
        } catch {
            firstError = error
        }

        switch classify(firstError) {
        case .unsupported:
            return .unsupported(detail: (firstError as? FerryError)?.detail)
        case .failed(let reason):
            return .failed(reason: reason)
        case .certificate:
            // 暗号化そのものはできるかを、証明書を検証せずに確かめる
            let unverified = RemoteClient(server: ftpsConfig(server, verifyCertificate: false), password: password)
            do {
                let (home, names) = try await unverified.homeDirectoryAndCertificateNames()
                return await confirmListing(unverified, home: home, success: .certificateUnverified(
                    certificateNames: names, detail: (firstError as? FerryError)?.detail))
            } catch {
                // パスワードが違うなどは、証明書と関係なくそのまま伝える
                if (error as? FerryError)?.kind == .loginFailed, case .failed(let reason) = classify(error) {
                    return .failed(reason: reason)
                }
                return .failed(reason: "証明書を検証できず、検証しなくても接続できませんでした。\(error.localizedDescription)")
            }
        }
    }

    /// 調べるときの接続先。プロトコルと証明書の検証だけを変える（ポートを明示していなければ、FTPS の既定の 21 のまま）
    public static func ftpsConfig(_ server: ServerConfig, verifyCertificate: Bool) -> ServerConfig {
        var config = server
        config.serverProtocol = .ftps
        config.verifyCertificate = verifyCertificate
        return config
    }

    /// 証明書を検証して試したときの失敗の分類
    enum Classification: Equatable {
        /// AUTH TLS を断られた
        case unsupported
        /// 証明書を検証できなかった（暗号化はできるかもしれない）
        case certificate
        case failed(String)
    }

    static func classify(_ error: Error) -> Classification {
        guard let error = error as? FerryError else { return .failed(error.localizedDescription) }
        switch error.curlCode.map({ CURLcode(rawValue: UInt32($0)) }) {
        case CURLE_USE_SSL_FAILED?:
            return .unsupported
        case CURLE_PEER_FAILED_VERIFICATION?:
            return .certificate
        case CURLE_LOGIN_DENIED?:
            return .failed("ログインできませんでした（ユーザー名かパスワードが違うかもしれません）")
        case CURLE_SSL_CONNECT_ERROR?:
            return .failed("暗号化の接続（TLS）に失敗しました。" + (error.detail ?? ""))
        default:
            break
        }
        switch error.kind {
        case .cancelled: return .failed("中止しました")
        case .loginFailed: return .failed("ログインできませんでした（ユーザー名かパスワードが違うかもしれません）")
        case .untrustedCertificate: return .certificate
        default: return .failed(error.localizedDescription)
        }
    }

    /// ログインできた後、データ接続（PROT P で暗号化される）も使えるかを一覧で確かめる
    private static func confirmListing(_ client: RemoteClient, home: String, success: FTPSCheckResult) async -> FTPSCheckResult {
        do {
            _ = try await client.list(home)
            return success
        } catch {
            return .failed(reason: "ログインはできましたが、FTPS ではフォルダの一覧を取れませんでした。\(error.localizedDescription)")
        }
    }
}
