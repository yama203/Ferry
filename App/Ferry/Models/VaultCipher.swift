import CommonCrypto
import CryptoKit
import Foundation

/// パスワードの保管庫のファイル（passwords.vault）の形式と暗号。
/// キーチェーンやファイルの監視に頼らないので、これだけを取り出して試せる。Windows 版の PasswordVault と同じ形式:
/// {"version":1, "salt":"<base64>", "iterations":600000, "sealed":"<base64>"}
/// sealed = AES-256-GCM の combined 形式（nonce 12 + 暗号文 + タグ 16）で暗号化した {サーバー ID: パスワード} の JSON
/// 鍵 = PBKDF2-HMAC-SHA256（マスターパスワードの UTF-8、salt 16 バイト、iterations 回、32 バイト）
enum VaultCipher {
    static let iterations = 600_000

    struct File: Codable, Equatable, Sendable {
        var version = 1
        var salt: Data
        var iterations: Int
        /// AES-GCM で暗号化した {サーバー ID: パスワード} の JSON
        var sealed: Data
    }

    /// マスターパスワードを変えた結果（まだどこにも書いていない）
    struct Rekeyed: Sendable {
        var file: File
        var key: SymmetricKey
        var passwords: [String: String]
    }

    static func newSalt() -> Data {
        Data((0..<16).map { _ in UInt8.random(in: 0...255) })
    }

    static func deriveKey(_ password: String, salt: Data, iterations: Int) throws -> SymmetricKey {
        var derived = Data(count: 32)
        let passwordData = Data(password.utf8)
        let status = derived.withUnsafeMutableBytes { derivedBytes in
            salt.withUnsafeBytes { saltBytes in
                passwordData.withUnsafeBytes { passwordBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        passwordBytes.baseAddress?.assumingMemoryBound(to: CChar.self), passwordData.count,
                        saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations),
                        derivedBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), 32)
                }
            }
        }
        guard status == kCCSuccess else { throw VaultError.unreadable }
        return SymmetricKey(data: derived)
    }

    static func open(_ sealed: Data, with key: SymmetricKey) throws -> Data {
        try AES.GCM.open(AES.GCM.SealedBox(combined: sealed), using: key)
    }

    /// 鍵が違う（マスターパスワードが変わった）か、中身が壊れていれば nil
    static func passwords(in file: File, with key: SymmetricKey) -> [String: String]? {
        guard let plain = try? open(file.sealed, with: key) else { return nil }
        return try? JSONDecoder().decode([String: String].self, from: plain)
    }

    static func seal(_ passwords: [String: String], with key: SymmetricKey) throws -> Data {
        let plain = try JSONEncoder().encode(passwords)
        return try AES.GCM.seal(plain, using: key).combined!
    }

    /// 今のマスターパスワードで file を開けることを確かめてから、新しい salt と新しいマスターパスワードで暗号化し直す。
    /// 中身は file に入っているもの（呼ぶ側が読んだ最新のファイル）をそのまま引き継ぐ。
    static func rekeyed(_ file: File, current: String, new: String) throws -> Rekeyed {
        let currentKey = try deriveKey(current, salt: file.salt, iterations: file.iterations)
        guard let passwords = passwords(in: file, with: currentKey) else { throw VaultError.wrongPassword }
        // salt も新しくする（前の salt に合わせて用意した総当たりの計算を使い回せないように）
        let salt = newSalt()
        let key = try deriveKey(new, salt: salt, iterations: iterations)
        let sealed = try seal(passwords, with: key)
        return Rekeyed(file: File(salt: salt, iterations: iterations, sealed: sealed), key: key, passwords: passwords)
    }
}

enum VaultError: LocalizedError {
    case wrongPassword, unreadable
    /// 開いていない（ロック中・同期していない）保管庫を変えようとした
    case locked
    /// マスターパスワードを変えている間に、他の PC での変更が届いた
    case changedMeanwhile
    case writeFailed(String)

    var errorDescription: String? {
        switch self {
        case .wrongPassword: "マスターパスワードが違います"
        case .unreadable: "パスワードの保管ファイルを読めませんでした"
        case .locked: "パスワードの保管庫が開いていません"
        case .changedMeanwhile: "変更している間に、ほかの PC で保管庫が更新されました。もう一度やり直してください"
        case .writeFailed(let reason): "パスワードの保管ファイルに書き込めませんでした（\(reason)）"
        }
    }
}
