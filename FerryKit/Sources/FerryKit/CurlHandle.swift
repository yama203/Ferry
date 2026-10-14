import CCurl
import Foundation

public struct FerryError: Error, LocalizedError, Sendable {
    public enum Kind: Sendable {
        case cancelled, loginFailed, connectionFailed, tlsFailed, notFound, permissionDenied, other
    }

    public let kind: Kind
    public let message: String
    /// libcurl が返した詳細（サーバーの応答など）
    public let detail: String?

    public var errorDescription: String? {
        if let detail, !detail.isEmpty { return "\(message)\n\(detail)" }
        return message
    }

    static func from(code: CURLcode, detail: String?) -> FerryError {
        let (kind, message): (Kind, String) = switch code {
        case CURLE_ABORTED_BY_CALLBACK:
            (.cancelled, "キャンセルしました")
        case CURLE_LOGIN_DENIED:
            (.loginFailed, "ログインできませんでした。ユーザー名とパスワードを確認してください")
        case CURLE_COULDNT_RESOLVE_HOST:
            (.connectionFailed, "サーバーが見つかりません。ホスト名を確認してください")
        case CURLE_COULDNT_CONNECT:
            (.connectionFailed, "サーバーに接続できません。ホスト名・ポート・プロトコルを確認してください")
        case CURLE_OPERATION_TIMEDOUT:
            (.connectionFailed, "タイムアウトしました")
        case CURLE_FTP_ACCEPT_FAILED, CURLE_FTP_ACCEPT_TIMEOUT, CURLE_FTP_PORT_FAILED:
            (.connectionFailed, "データ接続に失敗しました。パッシブモードの設定を確認してください")
        case CURLE_FTP_WEIRD_PASV_REPLY, CURLE_FTP_CANT_GET_HOST:
            (.connectionFailed, "パッシブモードのデータ接続に失敗しました")
        case CURLE_USE_SSL_FAILED:
            (.tlsFailed, "サーバーが FTPS（Explicit）に対応していません。プロトコルを確認してください")
        case CURLE_PEER_FAILED_VERIFICATION:
            (.tlsFailed, "サーバー証明書を検証できませんでした。共用サーバーの場合は「証明書を検証」をオフにすると接続できることがあります")
        case CURLE_SSL_CONNECT_ERROR:
            (.tlsFailed, "TLS（暗号化）の接続に失敗しました。Explicit / Implicit の選択を確認してください")
        case CURLE_SSH:
            (.connectionFailed, "SSH の接続に失敗しました。ホスト鍵が以前と変わっている可能性もあります")
        case CURLE_REMOTE_FILE_NOT_FOUND:
            (.notFound, "ファイルまたはフォルダが見つかりません")
        case CURLE_REMOTE_ACCESS_DENIED, CURLE_UPLOAD_FAILED:
            (.permissionDenied, "アクセスが拒否されました。権限を確認してください")
        case CURLE_QUOTE_ERROR:
            (.other, "サーバーがコマンドを拒否しました")
        default:
            (.other, String(cString: curl_easy_strerror(code)))
        }
        return FerryError(kind: kind, message: message, detail: detail)
    }
}

/// curl の easy ハンドルの薄いラッパー。スレッドセーフではないので RemoteClient のキューからのみ使う。
final class CurlHandle {
    private static let globalInit: Void = { _ = ccurl_global_init() }()

    let raw: UnsafeMutableRawPointer
    private let errorBuffer: UnsafeMutablePointer<CChar>

    init() throws {
        _ = CurlHandle.globalInit
        guard let raw = curl_easy_init() else {
            throw FerryError(kind: .other, message: "libcurl を初期化できませんでした", detail: nil)
        }
        self.raw = raw
        errorBuffer = .allocate(capacity: Int(CURL_ERROR_SIZE))
        errorBuffer.initialize(repeating: 0, count: Int(CURL_ERROR_SIZE))
    }

    deinit {
        curl_easy_cleanup(raw)
        errorBuffer.deallocate()
    }

    /// 設定だけを初期化する。確立済みの接続は残るので、同じサーバーへの次の操作で再利用される。
    func reset() {
        curl_easy_reset(raw)
        errorBuffer[0] = 0
        _ = ccurl_setopt_pointer(raw, CURLOPT_ERRORBUFFER, errorBuffer)
    }

    func set(_ option: CURLoption, _ value: Int) {
        _ = ccurl_setopt_long(raw, option, value)
    }

    func set(_ option: CURLoption, _ value: Bool) {
        _ = ccurl_setopt_long(raw, option, value ? 1 : 0)
    }

    func set(_ option: CURLoption, _ value: String) {
        _ = value.withCString { ccurl_setopt_string(raw, option, $0) }
    }

    func set(_ option: CURLoption, offset value: curl_off_t) {
        _ = ccurl_setopt_off(raw, option, value)
    }

    func set(_ option: CURLoption, pointer value: UnsafeMutableRawPointer?) {
        _ = ccurl_setopt_pointer(raw, option, value)
    }

    func set(_ option: CURLoption, callback: ccurl_io_cb) {
        _ = ccurl_setopt_io(raw, option, callback)
    }

    func setProgress(_ callback: ccurl_xferinfo_cb) {
        _ = ccurl_setopt_xferinfo(raw, callback)
    }

    func useKnownHosts(at path: String) {
        _ = path.withCString { ccurl_use_known_hosts(raw, $0) }
    }

    func enableDebugLog() {
        _ = ccurl_enable_debug_log(raw)
    }

    func perform() throws {
        let code = curl_easy_perform(raw)
        guard code != CURLE_OK else { return }
        let detail = String(cString: errorBuffer)
        throw FerryError.from(code: code, detail: detail.isEmpty ? nil : detail)
    }

    var entryPath: String? {
        var value: UnsafePointer<CChar>?
        guard ccurl_getinfo_string(raw, CURLINFO_FTP_ENTRY_PATH, &value) == CURLE_OK, let value else { return nil }
        return String(cString: value)
    }
}

/// curl_slist の所有者。perform が終わるまで生かしておく。
final class CurlStringList {
    private(set) var raw: UnsafeMutablePointer<curl_slist>?

    init(_ items: [String]) {
        for item in items {
            raw = curl_slist_append(raw, item)
        }
    }

    deinit {
        curl_slist_free_all(raw)
    }
}
