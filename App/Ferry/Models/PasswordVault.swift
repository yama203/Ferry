import CommonCrypto
import CryptoKit
import Foundation
import Observation

/// iCloud Drive で同期するパスワードの保管庫。
/// マスターパスワードから作った鍵で暗号化して iCloud Drive/Ferry/passwords.vault に保存する。
/// 鍵は各 Mac のキーチェーンに保存するので、マスターパスワードの入力は Mac ごとに 1 回だけ。
@MainActor @Observable
final class PasswordVault {
    enum State: Equatable {
        /// 同期していない
        case off
        /// 他の Mac で同期が設定されているが、この Mac ではまだマスターパスワードを入力していない
        case locked
        case unlocked
    }

    enum VaultError: LocalizedError {
        case wrongPassword, unreadable
        var errorDescription: String? {
            switch self {
            case .wrongPassword: "マスターパスワードが違います"
            case .unreadable: "パスワードの保管ファイルを読めませんでした"
            }
        }
    }

    private(set) var state: State = .off
    private(set) var count = 0

    let fileURL: URL
    @ObservationIgnored private var key: SymmetricKey?
    @ObservationIgnored private var passwords: [String: String] = [:]
    @ObservationIgnored private var presenter: VaultPresenter?

    private static let keyAccount = "vault-key"
    private static let iterations = 600_000

    private struct VaultFile: Codable {
        var version = 1
        var salt: Data
        var iterations: Int
        /// AES-GCM で暗号化した {サーバー ID: パスワード} の JSON
        var sealed: Data
    }

    init(directory: URL) {
        fileURL = directory.appendingPathComponent("passwords.vault")
        if let stored = Keychain.secret(account: Self.keyAccount), let data = Data(base64Encoded: stored) {
            key = SymmetricKey(data: data)
        }
        reload()

        // 他の Mac での変更が iCloud から届いたら読み直す
        let presenter = VaultPresenter(url: fileURL) { [weak self] in
            Task { @MainActor in self?.reload() }
        }
        NSFileCoordinator.addFilePresenter(presenter)
        self.presenter = presenter
    }

    // MARK: - パスワード

    func password(for id: UUID) -> String? {
        state == .unlocked ? passwords[id.uuidString] : nil
    }

    /// 空文字や nil で削除。同期していなければ何もしない。
    func setPassword(_ password: String?, for id: UUID) {
        guard state == .unlocked else { return }
        // 他の Mac での変更を取りこぼさないよう、最新を読んでから書く
        reload()
        if let password, !password.isEmpty {
            passwords[id.uuidString] = password
        } else {
            passwords[id.uuidString] = nil
        }
        persist()
    }

    // MARK: - 開始・解除・停止

    /// 同期を始める。この Mac に保存してあるパスワードを最初の中身にする。
    func enable(masterPassword: String, initial: [UUID: String]) throws {
        let salt = Data((0..<16).map { _ in UInt8.random(in: 0...255) })
        let derived = try Self.deriveKey(masterPassword, salt: salt, iterations: Self.iterations)
        key = derived
        passwords = Dictionary(uniqueKeysWithValues: initial.map { ($0.key.uuidString, $0.value) })
        try writeFile(salt: salt, iterations: Self.iterations)
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: Self.keyAccount)
        reload()
    }

    /// 他の Mac で始めた同期を、この Mac でも使えるようにする
    func unlock(masterPassword: String) throws {
        guard let file = readFile() else { throw VaultError.unreadable }
        let derived = try Self.deriveKey(masterPassword, salt: file.salt, iterations: file.iterations)
        guard (try? Self.open(file.sealed, with: derived)) != nil else { throw VaultError.wrongPassword }
        key = derived
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: Self.keyAccount)
        reload()
    }

    /// 同期をやめる（すべての Mac で）。同期していたパスワードはこの Mac のキーチェーンに残す。
    func disable() {
        if state == .unlocked {
            for (id, password) in passwords {
                if let uuid = UUID(uuidString: id) { Keychain.setPassword(password, for: uuid) }
            }
        }
        var coordinatorError: NSError?
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: fileURL, options: .forDeleting,
                                                               error: &coordinatorError) { url in
            try? FileManager.default.removeItem(at: url)
        }
        Keychain.setSecret(nil, account: Self.keyAccount)
        key = nil
        passwords = [:]
        reload()
    }

    // MARK: - 読み書き

    private func reload() {
        try? FileManager.default.startDownloadingUbiquitousItem(at: fileURL)
        guard let file = readFile() else {
            state = .off
            passwords = [:]
            count = 0
            return
        }
        guard let key, let decrypted = try? Self.open(file.sealed, with: key),
              let dictionary = try? JSONDecoder().decode([String: String].self, from: decrypted) else {
            // 鍵が無い、またはマスターパスワードが変わった
            state = .locked
            passwords = [:]
            count = 0
            return
        }
        passwords = dictionary
        count = dictionary.count
        state = .unlocked
    }

    private func readFile() -> VaultFile? {
        var coordinatorError: NSError?
        var file: VaultFile?
        NSFileCoordinator(filePresenter: presenter).coordinate(readingItemAt: fileURL, options: [],
                                                               error: &coordinatorError) { url in
            guard let data = try? Data(contentsOf: url) else { return }
            file = try? JSONDecoder().decode(VaultFile.self, from: data)
        }
        return file
    }

    /// 既存のファイルと同じ salt で書き直す
    private func persist() {
        guard let file = readFile() else { return }
        try? writeFile(salt: file.salt, iterations: file.iterations)
    }

    private func writeFile(salt: Data?, iterations: Int) throws {
        guard let key, let salt else { return }
        let plain = try JSONEncoder().encode(passwords)
        let sealed = try AES.GCM.seal(plain, using: key).combined!
        let data = try JSONEncoder().encode(VaultFile(salt: salt, iterations: iterations, sealed: sealed))
        var coordinatorError: NSError?
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: fileURL, options: .forReplacing,
                                                               error: &coordinatorError) { url in
            try? data.write(to: url, options: .atomic)
        }
        count = passwords.count
    }

    // MARK: - 暗号

    private static func deriveKey(_ password: String, salt: Data, iterations: Int) throws -> SymmetricKey {
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

    private static func open(_ sealed: Data, with key: SymmetricKey) throws -> Data {
        try AES.GCM.open(AES.GCM.SealedBox(combined: sealed), using: key)
    }
}

/// パスワードの読み書きの窓口。同期中は保管庫を優先し、この Mac のキーチェーンにも保存する。
@MainActor
enum Passwords {
    static var vault: PasswordVault?

    static func password(for id: UUID) -> String? {
        vault?.password(for: id) ?? Keychain.password(for: id)
    }

    static func setPassword(_ password: String?, for id: UUID) {
        Keychain.setPassword(password, for: id)
        vault?.setPassword(password, for: id)
    }
}

private final class VaultPresenter: NSObject, NSFilePresenter {
    let presentedItemURL: URL?
    let presentedItemOperationQueue = OperationQueue.main
    private let onChange: () -> Void

    init(url: URL, onChange: @escaping () -> Void) {
        presentedItemURL = url
        self.onChange = onChange
    }

    func presentedItemDidChange() {
        onChange()
    }

    func accommodatePresentedItemDeletion(completionHandler: @escaping (Error?) -> Void) {
        onChange()
        completionHandler(nil)
    }
}
