import FerryKit
import Foundation
import Observation

/// 暗号化なしの FTP の接続先に出す「暗号化されていません」の印を、出さないことにした接続先。
/// この Mac だけの記録なので、接続先の一覧（共有・同期される）ではなく UserDefaults に置く（ConnectionProblems と同じ）
@MainActor @Observable
final class FTPSNotice {
    static let shared = FTPSNotice()

    enum Reason: String {
        /// 調べたら FTPS に対応していなかった
        case unsupported
        /// 「今後表示しない」を選んだ
        case dismissed
    }

    private(set) var hidden: [UUID: Reason]
    private static let key = "ftpsNoticeHidden"

    private init() {
        let stored = UserDefaults.standard.dictionary(forKey: Self.key) as? [String: String] ?? [:]
        hidden = Dictionary(uniqueKeysWithValues: stored.compactMap { key, value in
            guard let id = UUID(uuidString: key), let reason = Reason(rawValue: value) else { return nil }
            return (id, reason)
        })
    }

    func isHidden(_ id: UUID) -> Bool { hidden[id] != nil }

    func hide(_ id: UUID, because reason: Reason) {
        guard hidden[id] != reason else { return }
        hidden[id] = reason
        UserDefaults.standard.set(Dictionary(uniqueKeysWithValues: hidden.map { ($0.key.uuidString, $0.value.rawValue) }),
                                  forKey: Self.key)
    }
}

/// FTP の接続先を FTPS に切り替える処理（まとめて調べる画面と、接続中の画面の印で使う）
@MainActor
enum FTPSUpgrade {
    /// 保存してあるパスワードで調べる。保管庫に足す副作用のある Passwords.password(for:) は使わない
    static func check(_ server: ServerConfig) async -> FTPSCheckResult {
        let password = Passwords.storedPassword(for: server.id)
        let result = await FTPSCheck.check(server, password: password)
        if case .unsupported = result {
            FTPSNotice.shared.hide(server.id, because: .unsupported)
        }
        return result
    }

    /// 調べた結果で切り替えられるか。切り替えるときの証明書の検証（検証できなかったものは検証しない）
    static func verifiesCertificate(after result: FTPSCheckResult) -> Bool? {
        switch result {
        case .available: true
        case .certificateUnverified: false
        case .unsupported, .failed: nil
        }
    }

    enum SwitchError: LocalizedError {
        case changed, transferring, notSwitchable

        var errorDescription: String? {
            switch self {
            case .changed: "調べた後に接続設定が変わったので、切り替えませんでした。もう一度調べてください"
            case .transferring: "転送中なので切り替えませんでした。転送が終わってから切り替えてください"
            case .notSwitchable: "FTPS に切り替えられることを確かめられていません"
            }
        }
    }

    /// 調べた接続先を FTPS にして保存する。ポートはそのまま（明示していなければ FTPS の既定の 21、明示した 21 もそのまま）。
    /// 開いている接続は閉じる（次に開いたとき、または表示中ならすぐに FTPS でつなぎ直す）
    static func switchToFTPS(_ checked: ServerConfig, result: FTPSCheckResult,
                             store: ServerStore, sessions: SessionManager) throws {
        guard let verify = verifiesCertificate(after: result) else { throw SwitchError.notSwitchable }
        guard var server = store.server(withID: checked.id), server.serverProtocol == .ftp,
              server.sameConnection(as: checked) else { throw SwitchError.changed }
        guard !sessions.hasActiveTransfers(server.id) else { throw SwitchError.transferring }
        server.serverProtocol = .ftps
        server.verifyCertificate = verify
        sessions.close(server.id)
        store.upsert(server)
    }
}

extension FTPSCheckResult {
    /// 一覧に出す短い結果
    var title: String {
        switch self {
        case .available: "FTPS に切り替えられます"
        case .certificateUnverified: "証明書を検証できません"
        case .unsupported: "FTPS に対応していません"
        case .failed: "調べられませんでした"
        }
    }

    var systemImage: String {
        switch self {
        case .available: "lock.fill"
        case .certificateUnverified: "lock.trianglebadge.exclamationmark"
        case .unsupported: "lock.slash"
        case .failed: "questionmark.circle"
        }
    }

    /// 結果の説明（理由と対処）。host は接続先のホスト名（証明書の名前と合っているかで説明を変える）
    func explanation(host: String) -> String {
        switch self {
        case .available:
            return "FTPS（Explicit）でログインでき、サーバー証明書も検証できました。"
        case .certificateUnverified(let names, _):
            var text = Self.certificate(names, matches: host)
                ? "暗号化はできますが、証明書を発行元まで確かめられません（自己署名の証明書など）。"
                : "証明書のホスト名が接続先と合いません。ホスト名をサーバー会社のもの（例: sv1234.xserver.jp）に変えると検証できることがあります。"
            if !names.isEmpty {
                text += "\n証明書の名前: " + names.joined(separator: "、")
            }
            text += "\n証明書を検証せずに FTPS にすることもできます（盗み見は防げますが、なりすましは防げません。暗号化なしよりは安全です）。"
            return text
        case .unsupported:
            return "サーバーが暗号化（AUTH TLS）を断りました。"
        case .failed(let reason):
            return reason
        }
    }

    /// 証明書の名前のどれかが接続先のホスト名と合うか（*.example.jp は 1 段だけ合う）
    static func certificate(_ names: [String], matches host: String) -> Bool {
        let host = host.lowercased()
        return names.contains { name in
            let name = name.lowercased()
            if name.hasPrefix("*.") {
                guard let dot = host.firstIndex(of: ".") else { return false }
                return host[host.index(after: dot)...] == name.dropFirst(2)
            }
            return name == host
        }
    }
}
