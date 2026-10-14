import Foundation
import Security

/// 接続先のパスワードをログインキーチェーンに保存する（iCloud では同期しない）。
enum Keychain {
    private static let service = "com.yama.Ferry"

    static func password(for id: UUID) -> String? {
        secret(account: id.uuidString)
    }

    /// 空文字や nil を渡すと削除する
    static func setPassword(_ password: String?, for id: UUID) {
        setSecret(password, account: id.uuidString)
    }

    static func secret(account: String) -> String? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func setSecret(_ password: String?, account: String) {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
        ]
        guard let password, !password.isEmpty else {
            SecItemDelete(query as CFDictionary)
            return
        }
        let data = Data(password.utf8)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData: data] as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData] = data
            item[kSecAttrLabel] = "Ferry"
            SecItemAdd(item as CFDictionary, nil)
        }
    }

    /// 他のアプリ（Transmit など）がキーチェーンに保存したインターネットパスワードを探す。
    /// 他のアプリの項目なので、macOS がアクセスを許可するか確認を出す。
    static func findInternetPassword(host: String, account: String) -> String? {
        var query: [CFString: Any] = [
            kSecClass: kSecClassInternetPassword,
            kSecAttrServer: host,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        if !account.isEmpty {
            query[kSecAttrAccount] = account
        }
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
