import CryptoKit
import Foundation
import Observation

/// 同期するパスワードの保管庫。マスターパスワードから作った鍵で暗号化して passwords.vault に保存する。
/// - 自分の保管庫: iCloud Drive/Ferry/passwords.vault（自分の Mac・Windows の間で同期。鍵の名前 vault-key）
/// - 会社の保管庫: <共有フォルダ>/passwords.vault（会社のマスターパスワードを知っている人の間で同期。鍵の名前 shared-vault-key）
/// 鍵は各 PC のキーチェーン（Windows は資格情報マネージャー）に保存するので、マスターパスワードの入力は PC ごとに 1 回だけ。
/// Windows 版の PasswordVault と同じ形式（形式と暗号は VaultCipher）。
@MainActor @Observable
final class PasswordVault {
    enum State: Equatable {
        /// 同期していない
        case off
        /// 他の Mac で同期が設定されているが、この Mac ではまだマスターパスワードを入力していない
        case locked
        case unlocked
    }

    private(set) var state: State = .off
    private(set) var count = 0
    /// ロック中のうち、この Mac に鍵はあるのに開けない場合（他の PC でマスターパスワードが変更されたとき）
    private(set) var keyRejected = false

    let fileURL: URL
    /// キーチェーンに鍵を保存する名前（自分用と会社用で分ける）
    let keyAccount: String
    @ObservationIgnored private var key: SymmetricKey?
    @ObservationIgnored private var passwords: [String: String] = [:]
    @ObservationIgnored private var presenter: VaultPresenter?

    private typealias VaultFile = VaultCipher.File

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
        let salt = VaultCipher.newSalt()
        let derived = try VaultCipher.deriveKey(masterPassword, salt: salt, iterations: VaultCipher.iterations)
        let previousKey = key
        key = derived
        passwords = Dictionary(uniqueKeysWithValues: initial.map { ($0.key.uuidString, $0.value) })
        do {
            try writeFile(salt: salt, iterations: VaultCipher.iterations)
        } catch {
            // 書けなかったら始めていないのと同じにする
            key = previousKey
            reload()
            throw error
        }
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: keyAccount)
        reload()
    }

    /// 他の Mac で始めた同期を、この Mac でも使えるようにする
    func unlock(masterPassword: String) throws {
        guard let file = readFile() else { throw VaultError.unreadable }
        let derived = try VaultCipher.deriveKey(masterPassword, salt: file.salt, iterations: file.iterations)
        guard (try? VaultCipher.open(file.sealed, with: derived)) != nil else { throw VaultError.wrongPassword }
        key = derived
        Keychain.setSecret(derived.withUnsafeBytes { Data($0) }.base64EncodedString(), account: keyAccount)
        reload()
    }

    /// マスターパスワードを変える（すべての PC で）。開いているときだけ。
    /// 戻り値は、この Mac のキーチェーンにも新しい鍵を保存できたか（できなくてもファイルは変わっていて、この起動中は使える）。
    /// 他の PC は手元の鍵で開けなくなってロック中になり、新しいマスターパスワードを入れると使える（Windows 版も同じ）。
    @discardableResult
    func changeMasterPassword(current: String, new: String) async throws -> Bool {
        guard state == .unlocked else { throw VaultError.locked }
        // キーチェーンの鍵ではなく、入力した今のマスターパスワードで最新のファイルを開けることを確かめる
        // （席を外した隙に変えられないように。最新を開くので、他の PC での変更も引き継ぐ）
        guard let file = readFile() else { throw VaultError.unreadable }
        // 鍵を 2 回作るので時間がかかる。画面が固まらないよう、メインスレッドの外で行う
        let rekeyed = try await Task.detached { try VaultCipher.rekeyed(file, current: current, new: new) }.value

        // 待っている間に同期をやめた・他の PC の変更が届いた場合は、古い中身で上書きしない
        guard state == .unlocked else { throw VaultError.locked }
        guard readFile() == file else { throw VaultError.changedMeanwhile }

        // 先にファイルを書く。失敗したらメモリ・キーチェーンの鍵もファイルも元のまま
        try write(JSONEncoder().encode(rekeyed.file))
        key = rekeyed.key
        passwords = rekeyed.passwords
        // キーチェーンに保存できなかったら、次の起動でロック中になり、新しいマスターパスワードを聞くだけで済む
        let saved = Keychain.setSecret(rekeyed.key.withUnsafeBytes { Data($0) }.base64EncodedString(), account: keyAccount)
        reload()
        return saved
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
            keyRejected = false
            passwords = [:]
            count = 0
            return
        }
        guard let key, let dictionary = VaultCipher.passwords(in: file, with: key) else {
            // 鍵が無い（この Mac でまだ入力していない）、または鍵が合わない（他の PC でマスターパスワードが変わった）
            state = .locked
            keyRejected = key != nil
            passwords = [:]
            count = 0
            return
        }
        passwords = dictionary
        count = dictionary.count
        keyRejected = false
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
        let sealed = try VaultCipher.seal(passwords, with: key)
        try write(JSONEncoder().encode(VaultFile(salt: salt, iterations: iterations, sealed: sealed)))
        count = passwords.count
    }

    /// 書き込みの失敗も知らせる（マスターパスワードの変更では、書けたかどうかで鍵を切り替えるため）
    private func write(_ data: Data) throws {
        var coordinatorError: NSError?
        var writeError: Error?
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: fileURL, options: .forReplacing,
                                                               error: &coordinatorError) { url in
            do {
                try data.write(to: url, options: .atomic)
            } catch {
                writeError = error
            }
        }
        if let error = coordinatorError ?? writeError {
            throw VaultError.writeFailed(error.localizedDescription)
        }
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

    /// 保存してあるパスワード（password(for:) と違い、保管庫への追加はしない）
    static func storedPassword(for id: UUID) -> String? {
        (isShared(id) ? sharedVault?.password(for: id) : nil) ?? vault?.password(for: id) ?? Keychain.password(for: id)
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

    /// この Mac のキーチェーン（会社の保管庫へは、自分の保管庫も）にあって同期にまだ無いパスワードを追加する。
    /// 戻り値は追加した件数と、読めなかったもの（OSStatus ごとの件数）。
    static func addLocalPasswordsToVault(_ vault: PasswordVault?, serverIDs: [UUID]) -> (added: Int, failures: [OSStatus: Int]) {
        guard let vault, vault.state == .unlocked else { return (0, [:]) }
        var entries: [UUID: String] = [:]
        var failures: [OSStatus: Int] = [:]
        for id in serverIDs where !vault.contains(id) {
            // 会社の保管庫へ入れるときは、自分の保管庫にしか無いもの（他の PC で保存したもの）も使う
            if vault !== Passwords.vault, let synced = Passwords.vault?.password(for: id) {
                entries[id] = synced
                continue
            }
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
