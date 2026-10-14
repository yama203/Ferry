import FerryKit
import Foundation
import Observation

/// 接続先リスト。iCloud Drive が使えれば iCloud Drive/Ferry/servers.json に保存し、Mac 間で同期する。
@MainActor @Observable
final class ServerStore {
    private(set) var servers: [ServerConfig] = []
    let fileURL: URL
    let isICloud: Bool

    @ObservationIgnored private var presenter: FilePresenter?

    private struct FileContents: Codable {
        var version = 1
        var servers: [ServerConfig]
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
        var loaded: [ServerConfig]?
        NSFileCoordinator(filePresenter: presenter).coordinate(readingItemAt: fileURL, options: [],
                                                               error: &coordinatorError) { url in
            guard let data = try? Data(contentsOf: url) else { return }
            loaded = try? JSONDecoder().decode(FileContents.self, from: data).servers
        }
        if let loaded, loaded != servers {
            servers = loaded
        }
    }

    func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(FileContents(servers: servers)) else { return }
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

    func move(from source: IndexSet, to destination: Int) {
        servers.move(fromOffsets: source, toOffset: destination)
        save()
    }
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
