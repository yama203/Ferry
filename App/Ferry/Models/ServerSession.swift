import AppKit
import FerryKit
import Foundation
import Observation
import UniformTypeIdentifiers

/// 1 つの接続先を開いている間の状態（左右のペイン、転送、編集中のファイル）
@MainActor @Observable
final class ServerSession {
    enum Status: Equatable {
        case idle, connecting, connected
        case failed(String)
        /// パスワードが未保存、またはログインに失敗した（メッセージ付き）
        case needsPassword(String?)
    }

    let server: ServerConfig
    let local: BrowserModel
    let remote: BrowserModel
    let transfers: TransferQueue
    @ObservationIgnored private(set) var editWatcher: EditWatcher!
    private(set) var status: Status = .idle
    /// 編集用にダウンロード中のファイル名
    private(set) var openingForEdit: String?
    @ObservationIgnored private let transferClient: RemoteClient
    @ObservationIgnored private var hasPassword: Bool

    init(server: ServerConfig, password: String?, excludeRules: @escaping () -> ExcludeRules) {
        self.server = server
        local = BrowserModel(side: .local)
        // 一覧用と転送用で接続を分け、転送中でもフォルダを移動できるようにする
        let browseClient = RemoteClient(server: server, password: password)
        remote = BrowserModel(side: .remote, client: browseClient)
        transferClient = RemoteClient(server: server, password: password)
        transfers = TransferQueue(client: transferClient, lister: browseClient, excludeRules: excludeRules)
        // 秘密鍵はパスフレーズ無しの場合もあるので、パスワード無しでも接続を試す
        hasPassword = password != nil || server.privateKeyPath != nil || server.username == "anonymous"
        transfers.onIdle = { [weak self] in
            guard let self else { return }
            Task {
                await self.local.refresh()
                await self.remote.refresh()
            }
        }
        editWatcher = EditWatcher { [weak self] file in
            self?.transfers.uploadEdited(file.localURL, to: file.remotePath)
        }
    }

    func connect() async {
        guard status != .connecting, let client = remote.client else { return }
        guard hasPassword else {
            status = .needsPassword(nil)
            return
        }
        status = .connecting

        if local.path.isEmpty {
            let localPath = server.localPath.map { ($0 as NSString).expandingTildeInPath }
                ?? FileManager.default.homeDirectoryForCurrentUser.path
            await local.load(localPath, recordHistory: false)
        }

        do {
            // ログインの確認を兼ねてホームを取得する
            let home = try await client.homeDirectory()
            let start = server.initialPath.flatMap { $0.isEmpty ? nil : $0 } ?? home
            await remote.load(start, recordHistory: false)
            if let message = remote.errorMessage, remote.entries.isEmpty {
                status = .failed(message)
            } else {
                status = .connected
            }
        } catch let error as FerryError where error.kind == .loginFailed {
            status = .needsPassword(error.localizedDescription)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    /// 接続画面で入力されたパスワードで接続する
    func connect(withPassword password: String, save: Bool) async {
        remote.client?.setPassword(password)
        transferClient.setPassword(password)
        if save {
            Keychain.setPassword(password, for: server.id)
        }
        hasPassword = true
        status = .idle
        await connect()
    }

    func close() {
        transfers.cancelAll()
        editWatcher.stopAll()
    }

    // MARK: - 転送

    func upload(_ entries: [FileEntry]) async {
        await upload(urls: entries.map { URL(fileURLWithPath: $0.path) })
    }

    func upload(urls: [URL]) async {
        await transfers.enqueueUploads(urls, toRemoteDirectory: remote.path)
    }

    func download(_ entries: [FileEntry]) async {
        await transfers.enqueueDownloads(entries, toLocalDirectory: URL(fileURLWithPath: local.path))
    }

    // MARK: - 編集

    /// ダウンロードしてエディタで開き、保存されたら自動でアップロードする
    func edit(_ entry: FileEntry) async {
        guard !entry.isDirectory else { return }
        let directory = Self.editCacheDirectory
            .appendingPathComponent(server.id.uuidString)
            .appendingPathComponent(String(RemotePath.parent(of: entry.path).dropFirst()))
        openingForEdit = entry.name
        defer { openingForEdit = nil }
        do {
            try await transfers.downloadAndWait([entry], into: directory)
            let file = directory.appendingPathComponent(entry.name)
            editWatcher.watch(file, remotePath: entry.path)
            EditorApp.open(file)
        } catch {
            remote.errorMessage = "編集用のダウンロードに失敗しました: \(error.localizedDescription)"
        }
    }

    private static let editCacheDirectory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.yama.Ferry/Edit")

    // MARK: - ドラッグ

    /// リモートの項目のドラッグ。アプリ内（左のペイン）には項目の情報を、Finder にはダウンロードしたファイルを渡す。
    func itemProvider(forRemote entry: FileEntry) -> NSItemProvider {
        let provider = NSItemProvider()
        provider.suggestedName = entry.name

        let data = try? JSONEncoder().encode([entry])
        provider.registerDataRepresentation(forTypeIdentifier: UTType.ferryRemoteItem.identifier,
                                            visibility: .ownProcess) { completion in
            completion(data, nil)
            return nil
        }

        let type = entry.isDirectory ? UTType.folder
            : UTType(filenameExtension: (entry.name as NSString).pathExtension) ?? .data
        provider.registerFileRepresentation(forTypeIdentifier: type.identifier, fileOptions: [],
                                            visibility: .all) { [weak self] completion in
            let progress = Progress(totalUnitCount: 1)
            Task { @MainActor in
                guard let self else { return completion(nil, false, nil) }
                let directory = FileManager.default.temporaryDirectory
                    .appendingPathComponent("Ferry-Drag/\(UUID().uuidString)")
                do {
                    try await self.transfers.downloadAndWait([entry], into: directory)
                    completion(directory.appendingPathComponent(entry.name), false, nil)
                } catch {
                    completion(nil, false, error)
                }
                progress.completedUnitCount = 1
            }
            return progress
        }
        return provider
    }
}

/// 開いている接続をサーバーごとに保持する
@MainActor @Observable
final class SessionManager {
    @ObservationIgnored private let store: ServerStore
    // 画面描画中に作成するので変更通知の対象外にする
    @ObservationIgnored private var sessions: [UUID: ServerSession] = [:]

    init(store: ServerStore) {
        self.store = store
    }

    func session(for server: ServerConfig) -> ServerSession {
        // フォルダの移動だけなら接続し直さない
        if let existing = sessions[server.id], existing.server.sameConnection(as: server) {
            return existing
        }
        sessions[server.id]?.close()
        let session = ServerSession(server: server, password: Keychain.password(for: server.id)) { [weak store] in
            store?.excludeRules ?? .defaults
        }
        sessions[server.id] = session
        return session
    }

    func close(_ id: UUID) {
        sessions[id]?.close()
        sessions[id] = nil
    }
}

extension ServerConfig {
    func sameConnection(as other: ServerConfig) -> Bool {
        var a = self, b = other
        a.folderID = nil
        b.folderID = nil
        a.name = ""
        b.name = ""
        return a == b
    }
}
