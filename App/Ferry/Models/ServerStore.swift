import FerryKit
import Foundation
import Observation

/// 自分の接続先（servers.json）とパスワードの保管庫（passwords.vault）の保存場所。Windows 版の PersonalLocation と同じ決め方。
enum PersonalLocation: Equatable {
    /// iCloud Drive/Ferry（iCloud Drive があれば既定）
    case iCloud
    /// 選んだフォルダ（自分だけの Dropbox など）
    case folder(URL)
    /// この Mac だけ（Application Support/Ferry。同期しない）
    case local

    /// UserDefaults のキー。値が無ければ自動、"local" でこの Mac だけ、それ以外はフォルダのパス
    static let defaultsKey = "personalDirectory"

    static var iCloudDirectory: URL? {
        let iCloudDrive = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Mobile Documents/com~apple~CloudDocs")
        return FileManager.default.fileExists(atPath: iCloudDrive.path) ? iCloudDrive.appendingPathComponent("Ferry") : nil
    }

    static var localDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Ferry")
    }

    /// 設定から場所を決める。選んだフォルダが無くなっていたら自動に戻す
    static var current: PersonalLocation {
        let setting = UserDefaults.standard.string(forKey: defaultsKey)
        if setting == "local" { return .local }
        if let setting, FileManager.default.fileExists(atPath: setting) {
            let url = URL(fileURLWithPath: setting)
            return url.standardizedFileURL == iCloudDirectory?.standardizedFileURL ? .iCloud : .folder(url)
        }
        return iCloudDirectory != nil ? .iCloud : .local
    }

    var directory: URL {
        switch self {
        case .iCloud: Self.iCloudDirectory ?? Self.localDirectory
        case .folder(let url): url
        case .local: Self.localDirectory
        }
    }

    var isSynced: Bool { self != .local }

    /// 会社の共有フォルダ（またはその中）か。自分の一覧が会社の全員に見え、
    /// パスワードの保管庫も会社のもの（同じ passwords.vault）と重なるので、保存場所にはさせない
    func isInside(_ sharedDirectory: URL?) -> Bool {
        guard let sharedDirectory else { return false }
        let path = directory.standardizedFileURL.resolvingSymlinksInPath().path
        let shared = sharedDirectory.standardizedFileURL.resolvingSymlinksInPath().path
        return path == shared || path.hasPrefix(shared + "/")
    }

    var displayName: String {
        switch self {
        case .iCloud: "iCloud Drive（他の Mac・Windows と同期）"
        case .folder(let url): url.path
        case .local: "この Mac だけ（同期しない）"
        }
    }

    /// 次の起動からこの場所を使う。新しい場所にまだ servers.json が無ければ、今の servers.json と passwords.vault をコピーする。
    /// 戻り値は「すでにあったので、そちらを使う」か（2 台目の PC など）。
    @discardableResult
    func makeCurrent(movingFrom current: PersonalLocation) throws -> Bool {
        let fileManager = FileManager.default
        let target = directory
        let existing = fileManager.fileExists(atPath: target.appendingPathComponent("servers.json").path)
        if !existing {
            try fileManager.createDirectory(at: target, withIntermediateDirectories: true)
            for name in ["servers.json", "passwords.vault"] {
                let source = current.directory.appendingPathComponent(name)
                let destination = target.appendingPathComponent(name)
                if fileManager.fileExists(atPath: source.path), !fileManager.fileExists(atPath: destination.path) {
                    try fileManager.copyItem(at: source, to: destination)
                }
            }
        }
        switch self {
        case .iCloud: UserDefaults.standard.removeObject(forKey: Self.defaultsKey)
        case .folder(let url): UserDefaults.standard.set(url.path, forKey: Self.defaultsKey)
        case .local: UserDefaults.standard.set("local", forKey: Self.defaultsKey)
        }
        return existing
    }
}

/// 接続先リスト。自分の接続先は保存場所（iCloud Drive・選んだフォルダ・この Mac）の servers.json に保存し、
/// 会社で共有する接続先は SharedStore（Dropbox などの共有フォルダ）に保存する。
@MainActor @Observable
final class ServerStore {
    /// 自分だけの接続先とフォルダ
    private(set) var personalServers: [ServerConfig] = []
    private(set) var personalFolders: [ServerFolder] = []
    /// 共有の接続先のうち、この人の PC ごとの設定（ローカルのフォルダ・ブックマークなど）
    private(set) var sharedOverrides: [String: SharedServerOverride] = [:]
    /// 会社で共有する接続先
    let shared = SharedStore()
    /// 転送・同期で除外するファイル名のパターン（Mac 間で同期）
    private(set) var excludeRules = ExcludeRules.defaults
    let fileURL: URL
    /// 自分の接続先の保存場所（変えたら再起動で切り替える）
    let location: PersonalLocation
    var isICloud: Bool { location == .iCloud }
    /// 他の PC と同期する場所か（パスワードの保管庫もここに置く）
    var isSynced: Bool { location.isSynced }
    /// 読み込みの確認画面に渡す内容（メニューから開く）
    var pendingImport: TransmitImporter.Result?
    var importError: String?

    @ObservationIgnored private var presenter: FilePresenter?
    /// servers.json はあるのに読めなかった（壊れた JSON、他の版が足した知らない値、iCloud から落とせない など）。
    /// このまま保存すると一覧が空に近い内容で上書きされて元の接続先が消えるので、保存の前に元のファイルを退避する
    @ObservationIgnored private var hasUnreadableFile = false

    private struct FileContents: Codable {
        var version = 2
        var servers: [ServerConfig] = []
        var folders: [ServerFolder] = []
        var excludePatterns: [String]?
        var sharedOverrides: [String: SharedServerOverride]?

        init(servers: [ServerConfig], folders: [ServerFolder], excludePatterns: [String],
             sharedOverrides: [String: SharedServerOverride]) {
            self.servers = servers
            self.folders = folders
            self.excludePatterns = excludePatterns
            self.sharedOverrides = sharedOverrides
        }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            servers = try container.decodeIfPresent([ServerConfig].self, forKey: .servers) ?? []
            folders = try container.decodeIfPresent([ServerFolder].self, forKey: .folders) ?? []
            excludePatterns = try container.decodeIfPresent([String].self, forKey: .excludePatterns)
            sharedOverrides = try container.decodeIfPresent([String: SharedServerOverride].self, forKey: .sharedOverrides)
        }
    }

    // MARK: - 一覧（自分の接続先 + 共有の接続先）

    /// 共有の接続先には、この PC の設定を重ねて返す
    var sharedServers: [ServerConfig] {
        shared.servers.map { server in
            var merged = server
            sharedOverrides[server.id.uuidString]?.apply(to: &merged)
            return merged
        }
    }

    var servers: [ServerConfig] { sharedServers + personalServers }
    var folders: [ServerFolder] { shared.folders + personalFolders }

    func isShared(_ id: UUID) -> Bool {
        shared.servers.contains { $0.id == id }
    }

    func isSharedFolder(_ id: UUID) -> Bool {
        shared.folders.contains { $0.id == id }
    }

    /// フォルダの中の接続先。folderID が nil の場合は、shared で共有か自分のどちらの「フォルダなし」かを選ぶ。
    func servers(inFolder folderID: UUID?, shared isShared: Bool) -> [ServerConfig] {
        (isShared ? sharedServers : personalServers).filter { $0.folderID == folderID }
    }

    init() {
        location = PersonalLocation.current
        let directory = location.directory
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("servers.json")

        load()

        // 他の PC での変更が iCloud・Dropbox などから届いたら読み直す
        let presenter = FilePresenter(url: fileURL) { [weak self] in
            Task { @MainActor in self?.load() }
        }
        NSFileCoordinator.addFilePresenter(presenter)
        self.presenter = presenter
    }

    func server(withID id: UUID) -> ServerConfig? {
        servers.first { $0.id == id }
    }

    func reloadShared() {
        shared.load()
    }

    func load() {
        try? FileManager.default.startDownloadingUbiquitousItem(at: fileURL)
        var coordinatorError: NSError?
        var loaded: FileContents?
        var readError: Error?
        NSFileCoordinator(filePresenter: presenter).coordinate(readingItemAt: fileURL, options: [],
                                                               error: &coordinatorError) { url in
            do {
                loaded = try JSONDecoder().decode(FileContents.self, from: try Data(contentsOf: url))
            } catch {
                readError = error
            }
        }
        guard let loaded else {
            // ファイルが無いだけ（初回起動）なら今までどおり
            if fileExistsOrIsInICloud {
                hasUnreadableFile = true
                let reason = (readError ?? coordinatorError).map { String(describing: $0) } ?? "不明"
                NSLog("Ferry: %@ を読めませんでした（保存する前に退避します）: %@", fileURL.path, reason)
            }
            return
        }
        hasUnreadableFile = false
        if loaded.servers != personalServers { personalServers = loaded.servers }
        if loaded.folders != personalFolders { personalFolders = loaded.folders }
        let overrides = loaded.sharedOverrides ?? [:]
        if overrides != sharedOverrides { sharedOverrides = overrides }
        let rules = loaded.excludePatterns.map(ExcludeRules.init(patterns:)) ?? .defaults
        if rules != excludeRules { excludeRules = rules }
    }

    /// まだ iCloud から落としていないファイルは .servers.json.icloud という名前の代わりのファイルになっている
    private var fileExistsOrIsInICloud: Bool {
        let placeholder = fileURL.deletingLastPathComponent().appendingPathComponent(".\(fileURL.lastPathComponent).icloud")
        return FileManager.default.fileExists(atPath: fileURL.path) || FileManager.default.fileExists(atPath: placeholder.path)
    }

    /// 読めなかったファイルを同じフォルダに退避する名前
    private var unreadableBackupURL: URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HHmmss"
        return fileURL.deletingLastPathComponent()
            .appendingPathComponent("servers（読めなかったもの \(formatter.string(from: Date()))）.json")
    }

    func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(FileContents(servers: personalServers, folders: personalFolders,
                                                             excludePatterns: excludeRules.patterns,
                                                             sharedOverrides: sharedOverrides)) else { return }
        var coordinatorError: NSError?
        let backupURL = hasUnreadableFile ? unreadableBackupURL : nil
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: fileURL, options: .forReplacing,
                                                               error: &coordinatorError) { url in
            if backupURL != nil, !FileManager.default.fileExists(atPath: url.path), fileExistsOrIsInICloud {
                // iCloud から落とせていないだけで、退避できるものが手元に無い。上書きすると元の接続先が消えるかもしれない
                NSLog("Ferry: %@ を iCloud から落とせていないので保存しません", url.path)
                return
            }
            if let backupURL, FileManager.default.fileExists(atPath: url.path) {
                do {
                    try FileManager.default.copyItem(at: url, to: backupURL)
                    NSLog("Ferry: 読めなかった %@ を %@ に退避しました", url.path, backupURL.lastPathComponent)
                } catch {
                    // 退避できないまま上書きすると元の接続先が消えるので、保存しない
                    NSLog("Ferry: 読めなかった %@ を退避できなかったので保存しません: %@", url.path, String(describing: error))
                    return
                }
            }
            hasUnreadableFile = false
            try? data.write(to: url, options: .atomic)
        }
    }

    /// 追加・変更。新しい接続先は shared で共有か自分のどちらに入れるかを選ぶ（既存のものは今の場所のまま）。
    func upsert(_ server: ServerConfig, shared newIsShared: Bool = false) {
        let toShared = isShared(server.id) || (!personalServers.contains { $0.id == server.id } && newIsShared)
        if toShared {
            shared.save(server)
            setOverride(SharedServerOverride(from: server), for: server.id)
        } else if let index = personalServers.firstIndex(where: { $0.id == server.id }) {
            personalServers[index] = server
        } else {
            personalServers.append(server)
        }
        save()
    }

    /// 共有の接続先を消すと、会社の全員の一覧から消える
    func delete(_ id: UUID) {
        // 共有の一覧から消す前に消す（後だと自分の保管庫が対象になり、会社の保管庫に残る）
        Passwords.setPassword(nil, for: id)
        if isShared(id) {
            shared.delete(id)
            sharedOverrides[id.uuidString] = nil
        } else {
            personalServers.removeAll { $0.id == id }
        }
        save()
    }

    /// フォルダへ移す。共有のフォルダと自分のフォルダの間で移すと、共有する・しないも切り替わる。
    func moveServer(_ id: UUID, toFolder folderID: UUID?, shared toShared: Bool) {
        guard var server = server(withID: id) else { return }
        server.folderID = folderID
        if toShared == isShared(id) {
            upsert(server)
            return
        }
        // 共有 ⇄ 自分の間を移す（ID は変えないので、キーチェーンに保存済みのパスワードもそのまま使える）
        // 移す前の保管庫から消す（自分だけの接続先にしたのに、会社の保管庫に残らないように）
        // password(for:) は後で元の保管庫へ追加することがあるので使わない
        let password = Passwords.storedPassword(for: id)
        Passwords.setPassword(nil, for: id)
        if toShared {
            personalServers.removeAll { $0.id == id }
            shared.save(server)
            setOverride(SharedServerOverride(from: server), for: id)
        } else {
            shared.delete(id)
            sharedOverrides[id.uuidString] = nil
            personalServers.append(server)
        }
        save()
        // 同期しているパスワードは、移った先の保管庫（会社・自分）に入れ直す
        if let password {
            Passwords.setPassword(password, for: id)
        }
    }

    private func setOverride(_ override: SharedServerOverride, for id: UUID) {
        sharedOverrides[id.uuidString] = override.isEmpty ? nil : override
    }

    private func update(_ id: UUID, _ change: (inout ServerConfig) -> Void) {
        guard var server = server(withID: id) else { return }
        change(&server)
        upsert(server)
    }

    // MARK: - ブックマーク（左右の場所の組み合わせ）

    func addBookmark(_ bookmark: PaneBookmark, to serverID: UUID) {
        update(serverID) { $0.bookmarks = ($0.bookmarks ?? []) + [bookmark] }
    }

    func renameBookmark(_ bookmarkID: UUID, of serverID: UUID, to name: String) {
        update(serverID) { server in
            guard let index = server.bookmarks?.firstIndex(where: { $0.id == bookmarkID }) else { return }
            server.bookmarks?[index].name = name
        }
    }

    /// 接続時に開くブックマークを設定する（nil で解除）
    func setStartBookmark(_ bookmarkID: UUID?, of serverID: UUID) {
        update(serverID) { $0.startBookmarkID = bookmarkID }
    }

    func removeBookmark(_ bookmarkID: UUID, of serverID: UUID) {
        update(serverID) { server in
            if server.startBookmarkID == bookmarkID {
                server.startBookmarkID = nil
            }
            server.bookmarks?.removeAll { $0.id == bookmarkID }
            if server.bookmarks?.isEmpty == true {
                server.bookmarks = nil
            }
        }
    }

    // MARK: - フォルダ

    @discardableResult
    func addFolder(named name: String, shared toShared: Bool = false) -> ServerFolder {
        let folder = ServerFolder(id: UUID(), name: name)
        if toShared {
            shared.saveFolders(shared.folders + [folder])
        } else {
            personalFolders.append(folder)
            save()
        }
        return folder
    }

    func renameFolder(_ id: UUID, to name: String) {
        if isSharedFolder(id) {
            var folders = shared.folders
            guard let index = folders.firstIndex(where: { $0.id == id }) else { return }
            folders[index].name = name
            shared.saveFolders(folders)
        } else {
            guard let index = personalFolders.firstIndex(where: { $0.id == id }) else { return }
            personalFolders[index].name = name
            save()
        }
    }

    /// フォルダを削除する。中の接続先はフォルダの外に出す。
    func deleteFolder(_ id: UUID) {
        if isSharedFolder(id) {
            for server in sharedServers where server.folderID == id {
                var moved = server
                moved.folderID = nil
                shared.save(moved)
            }
            shared.saveFolders(shared.folders.filter { $0.id != id })
        } else {
            personalFolders.removeAll { $0.id == id }
            for index in personalServers.indices where personalServers[index].folderID == id {
                personalServers[index].folderID = nil
            }
            save()
        }
    }

    // MARK: - 読み込み

    func chooseTransmitFile() {
        switch TransmitImportPanel.choose() {
        case .success(let result)?: pendingImport = result
        case .failure(let error)?: importError = error.localizedDescription
        case nil: break
        }
    }

    /// Transmit から読み込んだ接続先を追加する。同じ接続先（ホスト・ユーザー・ポート・プロトコル）は飛ばす。
    /// フォルダは同名のものがあればそこに入れる。戻り値は追加した件数。
    @discardableResult
    func importServers(_ result: TransmitImporter.Result) -> Int {
        var added = 0
        for importedFolder in result.folders {
            var folderID: UUID?
            if let name = importedFolder.name {
                folderID = personalFolders.first(where: { $0.name == name })?.id
                if folderID == nil {
                    let folder = ServerFolder(id: UUID(), name: name)
                    personalFolders.append(folder)
                    folderID = folder.id
                }
            }
            for var server in importedFolder.servers {
                let exists = servers.contains {
                    $0.host == server.host && $0.username == server.username
                        && $0.effectivePort == server.effectivePort && $0.serverProtocol == server.serverProtocol
                }
                guard !exists else { continue }
                server.folderID = folderID
                personalServers.append(server)
                added += 1
            }
        }
        save()
        return added
    }

    func duplicateCount(in result: TransmitImporter.Result) -> Int {
        result.folders.flatMap(\.servers).filter { server in
            servers.contains {
                $0.host == server.host && $0.username == server.username
                    && $0.effectivePort == server.effectivePort && $0.serverProtocol == server.serverProtocol
            }
        }.count
    }

    // MARK: - 除外ルール

    func setExcludeRules(_ rules: ExcludeRules) {
        excludeRules = rules
        save()
    }
}

struct ServerFolder: Codable, Identifiable, Hashable {
    var id: UUID
    var name: String
}

private final class FilePresenter: NSObject, NSFilePresenter {
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
}
