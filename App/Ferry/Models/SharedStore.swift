import FerryKit
import Foundation
import Observation

/// 会社で共有する接続先。Dropbox などの共有フォルダに、接続先 1 件につき 1 ファイルで保存する
/// （複数人が同時に編集しても衝突しにくいように）。Windows 版と同じ形式。
///
///     <共有フォルダ>/ferry-shared.json      … 目印（形式のバージョン）
///     <共有フォルダ>/folders.json           … サイドバーのフォルダ
///     <共有フォルダ>/servers/<ID>.json      … 接続先
///
/// ローカルのフォルダ・ブックマーク・秘密鍵の場所は PC ごとに違うので共有せず、
/// 各自の設定（SharedServerOverride）に保存する。パスワードも共有しない。
@MainActor @Observable
final class SharedStore {
    private(set) var servers: [ServerConfig] = []
    private(set) var folders: [ServerFolder] = []
    private(set) var directory: URL?
    /// 会社のパスワードの保管庫（<共有フォルダ>/passwords.vault。会社のマスターパスワードで暗号化）
    private(set) var passwordVault: PasswordVault?

    @ObservationIgnored private var presenter: DirectoryPresenter?
    private static let directoryKey = "sharedDirectory"

    init() {
        if let path = UserDefaults.standard.string(forKey: Self.directoryKey) {
            setDirectory(URL(fileURLWithPath: path), remember: false)
        }
    }

    var isEnabled: Bool { directory != nil }

    /// 共有フォルダを設定する（nil で共有をやめる。ファイルは消さない）
    func setDirectory(_ url: URL?, remember: Bool = true) {
        if let presenter {
            NSFileCoordinator.removeFilePresenter(presenter)
            self.presenter = nil
        }
        directory = url
        if remember {
            UserDefaults.standard.set(url?.path, forKey: Self.directoryKey)
        }
        passwordVault?.close()
        passwordVault = url.map { PasswordVault(directory: $0, keyAccount: "shared-vault-key") }
        Passwords.sharedVault = passwordVault
        guard let url else {
            servers = []
            folders = []
            return
        }
        try? FileManager.default.createDirectory(at: url.appendingPathComponent("servers"),
                                                 withIntermediateDirectories: true)
        let marker = url.appendingPathComponent("ferry-shared.json")
        if !FileManager.default.fileExists(atPath: marker.path) {
            try? Data(#"{"version":1}"#.utf8).write(to: marker, options: .atomic)
        }
        load()

        // 他の人の変更が Dropbox から届いたら読み直す
        let presenter = DirectoryPresenter(url: url) { [weak self] in
            Task { @MainActor in self?.load() }
        }
        NSFileCoordinator.addFilePresenter(presenter)
        self.presenter = presenter
    }

    func load() {
        guard let directory else { return }
        let decoder = JSONDecoder()
        let serverFiles = (try? FileManager.default.contentsOfDirectory(
            at: directory.appendingPathComponent("servers"), includingPropertiesForKeys: nil)) ?? []
        // Dropbox の「競合コピー」などは、名前が <ID>.json の形でないので読まない
        let loaded = serverFiles
            .filter { $0.pathExtension == "json" && UUID(uuidString: $0.deletingPathExtension().lastPathComponent) != nil }
            .compactMap { try? decoder.decode(ServerConfig.self, from: Data(contentsOf: $0)) }
        let loadedFolders = (try? decoder.decode([ServerFolder].self, from: Data(
            contentsOf: directory.appendingPathComponent("folders.json")))) ?? []
        if loaded.sorted(by: { $0.id.uuidString < $1.id.uuidString })
            != servers.sorted(by: { $0.id.uuidString < $1.id.uuidString }) {
            servers = loaded
        }
        if loadedFolders != folders { folders = loadedFolders }
    }

    // MARK: - 書き込み

    /// PC ごとの設定を除いて保存する
    func save(_ server: ServerConfig) {
        guard let directory else { return }
        var shared = server
        shared.localPath = nil
        shared.bookmarks = nil
        shared.startBookmarkID = nil
        shared.privateKeyPath = nil
        write(shared, to: directory.appendingPathComponent("servers/\(server.id.uuidString).json"))
        if let index = servers.firstIndex(where: { $0.id == server.id }) {
            servers[index] = shared
        } else {
            servers.append(shared)
        }
    }

    func delete(_ id: UUID) {
        guard let directory else { return }
        try? FileManager.default.removeItem(at: directory.appendingPathComponent("servers/\(id.uuidString).json"))
        servers.removeAll { $0.id == id }
    }

    func saveFolders(_ newFolders: [ServerFolder]) {
        guard let directory else { return }
        folders = newFolders
        write(newFolders, to: directory.appendingPathComponent("folders.json"))
    }

    private func write<T: Encodable>(_ value: T, to url: URL) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(value) else { return }
        var coordinatorError: NSError?
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: url, options: .forReplacing,
                                                               error: &coordinatorError) { url in
            try? data.write(to: url, options: .atomic)
        }
    }
}

/// 共有の接続先のうち、PC ごとに違う設定（共有しない）
struct SharedServerOverride: Codable, Hashable {
    var localPath: String?
    var bookmarks: [PaneBookmark]?
    var startBookmarkID: UUID?
    var privateKeyPath: String?

    init(from server: ServerConfig) {
        localPath = server.localPath
        bookmarks = server.bookmarks
        startBookmarkID = server.startBookmarkID
        privateKeyPath = server.privateKeyPath
    }

    func apply(to server: inout ServerConfig) {
        server.localPath = localPath
        server.bookmarks = bookmarks
        server.startBookmarkID = startBookmarkID
        server.privateKeyPath = privateKeyPath
    }

    var isEmpty: Bool {
        localPath == nil && (bookmarks?.isEmpty ?? true) && startBookmarkID == nil && privateKeyPath == nil
    }
}

private final class DirectoryPresenter: NSObject, NSFilePresenter {
    let presentedItemURL: URL?
    let presentedItemOperationQueue = OperationQueue.main
    private let onChange: () -> Void

    init(url: URL, onChange: @escaping () -> Void) {
        presentedItemURL = url
        self.onChange = onChange
    }

    func presentedItemDidChange() { onChange() }
    func presentedSubitemDidChange(at url: URL) { onChange() }
    func presentedSubitemDidAppear(at url: URL) { onChange() }
    func accommodatePresentedSubitemDeletion(at url: URL, completionHandler: @escaping (Error?) -> Void) {
        completionHandler(nil)
        onChange()
    }
}
