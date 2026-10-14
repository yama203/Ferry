import FerryKit
import Foundation
import Observation

/// 接続先リスト。iCloud Drive が使えれば iCloud Drive/Ferry/servers.json に保存し、Mac 間で同期する。
@MainActor @Observable
final class ServerStore {
    private(set) var servers: [ServerConfig] = []
    private(set) var folders: [ServerFolder] = []
    /// 転送・同期で除外するファイル名のパターン（Mac 間で同期）
    private(set) var excludeRules = ExcludeRules.defaults
    let fileURL: URL
    let isICloud: Bool

    @ObservationIgnored private var presenter: FilePresenter?

    private struct FileContents: Codable {
        var version = 2
        var servers: [ServerConfig] = []
        var folders: [ServerFolder] = []
        var excludePatterns: [String]?

        init(servers: [ServerConfig], folders: [ServerFolder], excludePatterns: [String]) {
            self.servers = servers
            self.folders = folders
            self.excludePatterns = excludePatterns
        }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            servers = try container.decodeIfPresent([ServerConfig].self, forKey: .servers) ?? []
            folders = try container.decodeIfPresent([ServerFolder].self, forKey: .folders) ?? []
            excludePatterns = try container.decodeIfPresent([String].self, forKey: .excludePatterns)
        }
    }

    init() {
        let fileManager = FileManager.default
        let iCloudDrive = fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Mobile Documents/com~apple~CloudDocs")
        let directory: URL
        if fileManager.fileExists(atPath: iCloudDrive.path) {
            directory = iCloudDrive.appendingPathComponent("Ferry")
            isICloud = true
        } else {
            directory = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Ferry")
            isICloud = false
        }
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("servers.json")

        load()

        // 他の Mac での変更が iCloud から届いたら読み直す
        let presenter = FilePresenter(url: fileURL) { [weak self] in
            Task { @MainActor in self?.load() }
        }
        NSFileCoordinator.addFilePresenter(presenter)
        self.presenter = presenter
    }

    func server(withID id: UUID) -> ServerConfig? {
        servers.first { $0.id == id }
    }

    func load() {
        try? FileManager.default.startDownloadingUbiquitousItem(at: fileURL)
        var coordinatorError: NSError?
        var loaded: FileContents?
        NSFileCoordinator(filePresenter: presenter).coordinate(readingItemAt: fileURL, options: [],
                                                               error: &coordinatorError) { url in
            guard let data = try? Data(contentsOf: url) else { return }
            loaded = try? JSONDecoder().decode(FileContents.self, from: data)
        }
        guard let loaded else { return }
        if loaded.servers != servers { servers = loaded.servers }
        if loaded.folders != folders { folders = loaded.folders }
        let rules = loaded.excludePatterns.map(ExcludeRules.init(patterns:)) ?? .defaults
        if rules != excludeRules { excludeRules = rules }
    }

    func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(FileContents(servers: servers, folders: folders,
                                                             excludePatterns: excludeRules.patterns)) else { return }
        var coordinatorError: NSError?
        NSFileCoordinator(filePresenter: presenter).coordinate(writingItemAt: fileURL, options: .forReplacing,
                                                               error: &coordinatorError) { url in
            try? data.write(to: url, options: .atomic)
        }
    }

    func upsert(_ server: ServerConfig) {
        if let index = servers.firstIndex(where: { $0.id == server.id }) {
            servers[index] = server
        } else {
            servers.append(server)
        }
        save()
    }

    func delete(_ id: UUID) {
        servers.removeAll { $0.id == id }
        Keychain.setPassword(nil, for: id)
        save()
    }

    func servers(inFolder folderID: UUID?) -> [ServerConfig] {
        servers.filter { $0.folderID == folderID }
    }

    /// フォルダ内での並べ替え（フォルダ内の位置を全体の配列の位置に変換する）
    func move(inFolder folderID: UUID?, from source: IndexSet, to destination: Int) {
        var group = servers(inFolder: folderID)
        group.move(fromOffsets: source, toOffset: destination)
        let others = servers.filter { $0.folderID != folderID }
        servers = others + group
        save()
    }

    func moveServer(_ id: UUID, toFolder folderID: UUID?) {
        guard let index = servers.firstIndex(where: { $0.id == id }) else { return }
        servers[index].folderID = folderID
        save()
    }

    // MARK: - フォルダ

    @discardableResult
    func addFolder(named name: String) -> ServerFolder {
        let folder = ServerFolder(id: UUID(), name: name)
        folders.append(folder)
        save()
        return folder
    }

    func renameFolder(_ id: UUID, to name: String) {
        guard let index = folders.firstIndex(where: { $0.id == id }) else { return }
        folders[index].name = name
        save()
    }

    /// フォルダを削除する。中の接続先はフォルダの外に出す。
    func deleteFolder(_ id: UUID) {
        folders.removeAll { $0.id == id }
        for index in servers.indices where servers[index].folderID == id {
            servers[index].folderID = nil
        }
        save()
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
