import CCurl
import Foundation

public struct FerryError: Error, LocalizedError, Sendable {
    public enum Kind: Sendable {
        case cancelled, loginFailed, connectionFailed, tlsFailed, untrustedCertificate, notFound, permissionDenied, other
        /// SFTP のホスト鍵が known_hosts の記録と違う（hostKey にサーバーが出した鍵が入る）
        case hostKeyChanged
    }

    public let kind: Kind
    public let message: String
    /// libcurl が返した詳細（サーバーの応答など）
    public let detail: String?
    /// libcurl のエラーの番号（CURLcode）。libcurl 以外のエラーでは nil
    public let curlCode: Int?
    /// kind が hostKeyChanged のとき、サーバーが出した鍵
    public let hostKey: HostKey?

    public init(kind: Kind, message: String, detail: String?, curlCode: Int? = nil, hostKey: HostKey? = nil) {
        self.kind = kind
        self.message = message
        self.detail = detail
        self.curlCode = curlCode
        self.hostKey = hostKey
    }

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
            (.untrustedCertificate, "サーバー証明書を検証できませんでした（自己署名の証明書や、ホスト名が一致しない証明書など）")
        case CURLE_SSL_CONNECT_ERROR:
            (.tlsFailed, "TLS（暗号化）の接続に失敗しました。Explicit / Implicit の選択を確認してください")
        case CURLE_SSH:
            (.connectionFailed, "SSH の接続に失敗しました")
        case CURLE_REMOTE_FILE_NOT_FOUND:
            (.notFound, "ファイルまたはフォルダが見つかりません")
        case CURLE_REMOTE_ACCESS_DENIED, CURLE_UPLOAD_FAILED:
            (.permissionDenied, "アクセスが拒否されました。権限を確認してください")
        case CURLE_QUOTE_ERROR:
            (.other, "サーバーがコマンドを拒否しました")
        default:
            (.other, String(cString: curl_easy_strerror(code)))
        }
        return FerryError(kind: kind, message: message, detail: detail, curlCode: Int(code.rawValue))
    }

    /// ホスト鍵が known_hosts の記録と違ったので接続しなかった
    static func hostKeyChanged(_ key: HostKey) -> FerryError {
        FerryError(kind: .hostKeyChanged, message: "サーバーのホスト鍵が、前回と変わっています",
                   detail: "新しい鍵（\(key.typeName)）: \(key.fingerprint)", curlCode: Int(CURLE_PEER_FAILED_VERIFICATION.rawValue),
                   hostKey: key)
    }
}

/// curl の easy ハンドルの薄いラッパー。スレッドセーフではないので RemoteClient のキューからのみ使う。
final class CurlHandle {
    private static let globalInit: Void = { _ = ccurl_global_init() }()

    let raw: UnsafeMutableRawPointer
    private let errorBuffer: UnsafeMutablePointer<CChar>
    /// SFTP のホスト鍵の照合の結果（コールバックが書く）。perform の間は生かしておく必要があるので、ハンドルと同じだけ持つ
    private let hostKeyState: UnsafeMutablePointer<ccurl_hostkey_state>

    init() throws {
        _ = CurlHandle.globalInit
        guard let raw = curl_easy_init() else {
            throw FerryError(kind: .other, message: "libcurl を初期化できませんでした", detail: nil)
        }
        self.raw = raw
        errorBuffer = .allocate(capacity: Int(CURL_ERROR_SIZE))
        errorBuffer.initialize(repeating: 0, count: Int(CURL_ERROR_SIZE))
        hostKeyState = .allocate(capacity: 1)
        memset(hostKeyState, 0, MemoryLayout<ccurl_hostkey_state>.size)
    }

    deinit {
        curl_easy_cleanup(raw)
        errorBuffer.deallocate()
        hostKeyState.deallocate()
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

    /// approvedKey … ユーザーが承認した、変わった後のホスト鍵。この鍵のときだけ known_hosts の記録を置き換えて接続する
    func useKnownHosts(at path: String, approvedKey: HostKey?) {
        if let approvedKey {
            approvedKey.data.withUnsafeBytes { bytes in
                ccurl_hostkey_state_pin(hostKeyState, bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count)
            }
        } else {
            ccurl_hostkey_state_pin(hostKeyState, nil, 0)
        }
        _ = path.withCString { ccurl_use_known_hosts(raw, $0, hostKeyState) }
    }

    /// 承認した鍵を受け入れて known_hosts に記録したか。読んだら戻す
    func takeAcceptedApprovedKey() -> Bool {
        defer { hostKeyState.pointee.accepted_pinned = 0 }
        return hostKeyState.pointee.accepted_pinned != 0
    }

    /// サーバー証明書の名前（FTPS。CURLOPT_CERTINFO を有効にして接続した後に読む）。CN と DNS 名を重複なく返す
    var certificateNames: [String] {
        let size = 8192
        let buffer = UnsafeMutablePointer<CChar>.allocate(capacity: size)
        defer { buffer.deallocate() }
        guard ccurl_leaf_certificate_names(raw, buffer, size) > 0 else { return [] }
        return Self.parseCertificateNames(String(cString: buffer))
    }

    /// 「Subject:CN = a.example.jp」「X509v3 Subject Alternative Name:DNS:a.example.jp, DNS:*.example.jp」から名前を取り出す
    static func parseCertificateNames(_ text: String) -> [String] {
        var names: [String] = []
        for line in text.split(separator: "\n") {
            if line.hasPrefix("X509v3 Subject Alternative Name:") {
                for part in line.dropFirst("X509v3 Subject Alternative Name:".count).split(separator: ",") {
                    let value = part.trimmingCharacters(in: .whitespaces)
                    if value.hasPrefix("DNS:") { names.append(String(value.dropFirst(4))) }
                }
            } else if line.hasPrefix("Subject:") {
                // 「CN = name」「CN=name」のどちらの書き方もある。区切りは「, 」か「/」
                for part in line.dropFirst("Subject:".count).split(whereSeparator: { $0 == "," || $0 == "/" }) {
                    let pair = part.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                    if pair.count == 2, pair[0] == "CN", !pair[1].isEmpty { names.append(pair[1]) }
                }
            }
        }
        var seen = Set<String>()
        return names.filter { seen.insert($0.lowercased()).inserted }
    }

    func enableDebugLog() {
        _ = ccurl_enable_debug_log(raw)
    }

    func perform() throws {
        ccurl_hostkey_state_clear(hostKeyState)
        let code = curl_easy_perform(raw)
        guard code != CURLE_OK else { return }
        // 記録と違うホスト鍵を断ったときは、他の「SSH の接続に失敗」と分けて、サーバーが出した鍵を渡す
        let rejected = hostKeyState.pointee.checked != 0 && hostKeyState.pointee.rejected != 0
        var keyLength = 0
        if rejected, hostKeyState.pointee.match == Int32(bitPattern: CURLKHMATCH_MISMATCH.rawValue),
           let key = ccurl_hostkey_state_found_key(hostKeyState, &keyLength) {
            throw FerryError.hostKeyChanged(HostKey(data: Data(bytes: key, count: keyLength)))
        }
        let detail = String(cString: errorBuffer)
        if rejected {
            // 承認した鍵とも記録とも違う鍵（承認した後に、さらに鍵が変わったなど）
            throw FerryError(kind: .connectionFailed, message: "サーバーのホスト鍵を確かめられなかったので、接続しませんでした",
                             detail: detail.isEmpty ? nil : detail, curlCode: Int(code.rawValue))
        }
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
