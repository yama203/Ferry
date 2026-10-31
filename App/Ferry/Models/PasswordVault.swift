import CommonCrypto
import CryptoKit
import Foundation
import Observation

/// 同期するパスワードの保管庫。マスターパスワードから作った鍵で暗号化して passwords.vault に保存する。
/// - 自分の保管庫: iCloud Drive/Ferry/passwords.vault（自分の Mac・Windows の間で同期。鍵の名前 vault-key）
/// - 会社の保管庫: <共有フォルダ>/passwords.vault（会社のマスターパスワードを知っている人の間で同期。鍵の名前 shared-vault-key）
/// 鍵は各 PC のキーチェーン（Windows は資格情報マネージャー）に保存するので、マスターパスワードの入力は PC ごとに 1 回だけ。
/// Windows 版の PasswordVault と同じ形式。
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
    /// キーチェーンに鍵を保存する名前（自分用と会社用で分ける）
    let keyAccount: String
    @ObservationIgnored private var key: SymmetricKey?
    @ObservationIgnored private var passwords: [String: String] = [:]
    @ObservationIgnored private var presenter: VaultPresenter?

    private static let iterations = 600_000

    private struct VaultFile: Codable {
        var version = 1
        var salt: Data
        var iterations: Int
        /// AES-GCM で暗号化した {サーバー ID: パスワード} の JSON
        var sealed: Data
    }

    init(directory: URL, keyAccount: String = "vault-key") {
        fileURL = directory.appendingPathComponent("passwords.vault")
        self.keyAccount = keyAccount
        if let stored = Keychain.secret(account: keyAccount), let data = Data(base64Encoded: stored) {
            key = SymmetricKey(data: data)
        }
        reload()

        // 他の PC での変更が iCloud・Dropbox から届いたら読み直す
        let presenter = VaultPresenter(url: fileURL) { [weak self] in
            Task { @MainActor in self?.reload() }
        }
        NSFileCoordinator.addFilePresenter(presenter)
        self.presenter = presenter
    }

    /// 使わなくなったとき（会社の共有フォルダを変えたときなど）にファイルの監視をやめる
    func close() {
        if let presenter {
            NSFileCoordinator.removeFilePresenter(presenter)
            self.presenter = nil
        }
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

    /// まだ同期していないパスワードをまとめて追加する
    func add(_ entries: [UUID: String]) {
        guard state == .unlocked, !entries.isEmpty else { return }
        reload()
        for (id, password) in entries where passwords[id.uuidString] == nil {
            passwords[id.uuidString] = password
        }
        persist()
    }

    func contains(_ id: UUID) -> Bool {
        passwords[id.uuidString] != nil
    }

    /// 同期を始める。この Mac に保存してあるパスワードを最初の中身にする。
    func enable(masterPassword: String, initial: [UUID: String]) throws {
        let salt = Data((0..<16).map { _ in UInt8.random(in: 0...255) })
        let derived = try Self.deriveKey(masterPassword, salt: salt, iterations: Self.iterations)
        key = derived
        passwords = Dictionary(uniqueKeysWithValues: initial.map { ($0.key.uuidString, $0.value) })
        try writeFile(salt: salt, iterations: Self.iterations)
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: keyAccount)
        reload()
    }

    /// 他の Mac で始めた同期を、この Mac でも使えるようにする
    func unlock(masterPassword: String) throws {
        guard let file = readFile() else { throw VaultError.unreadable }
        let derived = try Self.deriveKey(masterPassword, salt: file.salt, iterations: file.iterations)
        guard (try? Self.open(file.sealed, with: derived)) != nil else { throw VaultError.wrongPassword }
        key = derived
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: keyAccount)
        reload()
    }

    /// 同期をやめる（すべての PC で）。同期していたパスワードはこの Mac のキーチェーンに残す。
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
        Keychain.setSecret(nil, account: keyAccount)
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
/// - 共有の接続先: 会社の保管庫 → 自分の保管庫 → キーチェーンの順に探す。保存は キーチェーン ＋ 会社の保管庫
/// - 自分の接続先: 自分の保管庫 → キーチェーンの順に探す。保存は キーチェーン ＋ 自分の保管庫
@MainActor
enum Passwords {
    /// 自分の保管庫（iCloud Drive）
    static var vault: PasswordVault?
    /// 会社の保管庫（共有フォルダ。SharedStore が共有フォルダに合わせて作る）
    static var sharedVault: PasswordVault?
    /// 接続先が会社の共有かどうか（FerryApp で ServerStore につなぐ）
    static var isShared: @MainActor (UUID) -> Bool = { _ in false }

    /// その接続先のパスワードを入れる保管庫
    static func targetVault(for id: UUID) -> PasswordVault? {
        isShared(id) ? sharedVault : vault
    }

    static func password(for id: UUID) -> String? {
        if isShared(id), let synced = sharedVault?.password(for: id) {
            return synced
        }
        if let synced = vault?.password(for: id) {
            return synced
        }
        let local = Keychain.password(for: id)
        // この Mac にだけあるパスワードは、使ったときに同期へ追加する
        if let local, let target = targetVault(for: id), target.state == .unlocked {
            // 画面の描画中に呼ばれることがあるので、状態の変更は後で行う
            Task { @MainActor in target.add([id: local]) }
        }
        return local
    }

    /// この Mac のキーチェーンにあって同期にまだ無いパスワードを追加する。
    /// 戻り値は追加した件数と、読めなかったもの（OSStatus ごとの件数）。
    static func addLocalPasswordsToVault(_ vault: PasswordVault?, serverIDs: [UUID]) -> (added: Int, failures: [OSStatus: Int]) {
        guard let vault, vault.state == .unlocked else { return (0, [:]) }
        var entries: [UUID: String] = [:]
        var failures: [OSStatus: Int] = [:]
        for id in serverIDs where !vault.contains(id) {
            let (value, status) = Keychain.readSecret(account: id.uuidString)
            if let value {
                entries[id] = value
            } else if status != errSecItemNotFound {
                failures[status, default: 0] += 1
            }
        }
        vault.add(entries)
        return (entries.count, failures)
    }

    static func setPassword(_ password: String?, for id: UUID) {
        Keychain.setPassword(password, for: id)
        targetVault(for: id)?.setPassword(password, for: id)
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
