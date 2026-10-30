import CryptoKit
import Foundation
import FerryKit
import Security

/// アプリ（Ferry.app）に保存した接続先とパスワードを読む。
/// 接続先は iCloud Drive/Ferry/servers.json と共有フォルダの servers/<ID>.json、
/// パスワードはこの Mac のキーチェーンか、同期用の保管庫（passwords.vault）から読む。
/// キーチェーンはアプリの項目なので、初回は macOS がアクセスを許可するか確認を出す。
enum SavedServers {
    private static let keychainService = "com.yama.Ferry"

    private struct PersonalFile: Decodable {
        var servers: [ServerConfig]?
        var sharedOverrides: [String: Override]?
    }

    /// 共有の接続先のうち、この Mac だけの設定
    private struct Override: Decodable {
        var privateKeyPath: String?
    }

    private struct VaultFile: Decodable {
        var sealed: Data
    }

    static var directory: URL {
        let fileManager = FileManager.default
        let iCloudDrive = fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Mobile Documents/com~apple~CloudDocs")
        if fileManager.fileExists(atPath: iCloudDrive.path) {
            return iCloudDrive.appendingPathComponent("Ferry")
        }
        return fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Ferry")
    }

    /// 共有の接続先 + 自分の接続先
    static func all() -> [ServerConfig] {
        let decoder = JSONDecoder()
        let personal = (try? Data(contentsOf: directory.appendingPathComponent("servers.json")))
            .flatMap { try? decoder.decode(PersonalFile.self, from: $0) }

        var shared: [ServerConfig] = []
        if let path = UserDefaults(suiteName: keychainService)?.string(forKey: "sharedDirectory") {
            let folder = URL(fileURLWithPath: path).appendingPathComponent("servers")
            let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
            shared = files
                .filter { $0.pathExtension == "json" && UUID(uuidString: $0.deletingPathExtension().lastPathComponent) != nil }
                .compactMap { try? decoder.decode(ServerConfig.self, from: Data(contentsOf: $0)) }
                .map { server in
                    var merged = server
                    if let keyPath = personal?.sharedOverrides?[server.id.uuidString]?.privateKeyPath {
                        merged.privateKeyPath = keyPath
                    }
                    return merged
                }
        }
        return shared + (personal?.servers ?? [])
    }

    /// 名前で探す。完全一致を優先し、なければ大文字小文字を区別しない部分一致が 1 件だけのとき
    static func find(_ name: String) -> Result<ServerConfig, SavedServerError> {
        let servers = all()
        if let exact = servers.first(where: { $0.name == name }) {
            return .success(exact)
        }
        let matches = servers.filter { $0.name.localizedCaseInsensitiveContains(name) }
        switch matches.count {
        case 1: return .success(matches[0])
        case 0: return .failure(.notFound(name))
        default: return .failure(.ambiguous(name, matches.map(\.name)))
        }
    }

    static func password(for id: UUID) -> String? {
        if let local = keychainSecret(account: id.uuidString) {
            return local
        }
        return vaultPasswords()?[id.uuidString]
    }

    private static func vaultPasswords() -> [String: String]? {
        guard let stored = keychainSecret(account: "vault-key"), let keyData = Data(base64Encoded: stored),
              let data = try? Data(contentsOf: directory.appendingPathComponent("passwords.vault")),
              let file = try? JSONDecoder().decode(VaultFile.self, from: data),
              let box = try? AES.GCM.SealedBox(combined: file.sealed),
              let opened = try? AES.GCM.open(box, using: SymmetricKey(data: keyData)) else { return nil }
        return try? JSONDecoder().decode([String: String].self, from: opened)
    }

    private static func keychainSecret(account: String) -> String? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: keychainService,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

enum SavedServerError: LocalizedError {
    case notFound(String)
    case ambiguous(String, [String])

    var errorDescription: String? {
        switch self {
        case .notFound(let name): "「\(name)」という接続先は見つかりませんでした（ferry-cli servers で一覧を表示できます）"
        case .ambiguous(let name, let names): "「\(name)」に当てはまる接続先が複数あります: \(names.joined(separator: "、"))"
        }
    }
}
