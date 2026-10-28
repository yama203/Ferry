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
        /// FTPS の証明書を検証できなかった（自己署名など）
        case untrustedCertificate(String)
    }

    let server: ServerConfig
    let local: BrowserModel
    let remote: BrowserModel
    let transfers: TransferQueue
    @ObservationIgnored private(set) var editWatcher: EditWatcher!
    private(set) var status: Status = .idle
    /// 接続後に開くブックマーク（サイドバーから「〜で接続」を選んだときなど）
    @ObservationIgnored var pendingBookmark: PaneBookmark?
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
        transfers.serverName = server.name
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
        // 画面を開いた後にパスワードが同期されてきた場合に備えて、もう一度探す
        if !hasPassword, let password = Passwords.password(for: server.id) {
            client.setPassword(password)
            transferClient.setPassword(password)
            hasPassword = true
        }
        guard hasPassword else {
            status = .needsPassword(nil)
            return
        }
        status = .connecting

        // 接続時に開くブックマーク（サイドバーから選んだもの、なければ接続先の設定）
        let bookmark = pendingBookmark ?? (local.path.isEmpty ? server.startBookmark : nil)
        pendingBookmark = nil
        if let bookmark {
            await local.load(bookmark.localPath, recordHistory: false)
        } else if local.path.isEmpty {
            let localPath = server.localPath.map { ($0 as NSString).expandingTildeInPath }
                ?? FileManager.default.homeDirectoryForCurrentUser.path
            await local.load(localPath, recordHistory: false)
        }

        do {
            // ログインの確認を兼ねてホームを取得する
            let home = try await client.homeDirectory()
            let start = bookmark?.remotePath ?? server.initialPath.flatMap { $0.isEmpty ? nil : $0 } ?? home
            await remote.load(start, recordHistory: false)
            if let message = remote.errorMessage, remote.entries.isEmpty {
                status = .failed(message)
            } else {
                status = .connected
            }
        } catch let error as FerryError where error.kind == .loginFailed {
            status = .needsPassword(error.localizedDescription)
        } catch let error as FerryError where error.kind == .untrustedCertificate {
            status = .untrustedCertificate(error.localizedDescription)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    /// 接続画面で入力されたパスワードで接続する
    func connect(withPassword password: String, save: Bool) async {
        remote.client?.setPassword(password)
        transferClient.setPassword(password)
        if save {
            Passwords.setPassword(password, for: server.id)
        }
        hasPassword = true
        status = .idle
        await connect()
    }

    /// ブックマークした組み合わせを左右のペインで開く。未接続なら接続後に開く。
    func open(_ bookmark: PaneBookmark) async {
        guard status == .connected else {
            pendingBookmark = bookmark
            return
        }
        await local.load(bookmark.localPath)
        await remote.load(bookmark.remotePath)
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

    // MARK: - Quick Look

    /// これより大きいファイルはダウンロードに時間がかかるのでプレビューしない
    private static let previewSizeLimit: Int64 = 50 * 1024 * 1024

    /// Quick Look 用にダウンロードしたファイル。転送一覧には出さず、一覧表示用の接続で取得する。
    /// 前回と同じサイズのファイルがあればダウンロードし直さない。
    func previewURL(for entry: FileEntry) async -> URL? {
        guard !entry.isDirectory, let client = remote.client else { return nil }
        if let size = entry.size, size > Self.previewSizeLimit {
            remote.errorMessage = "\(entry.name) は大きいため（\(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))）プレビューしません"
            return nil
        }
        let url = Self.previewCacheDirectory
            .appendingPathComponent(server.id.uuidString)
            .appendingPathComponent(String(entry.path.dropFirst()))
        let cachedSize = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init)
        if let cachedSize, cachedSize == entry.size {
            return url
        }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try await client.download(entry.path, to: url, expectedSize: entry.size)
            return url
        } catch {
            remote.errorMessage = "プレビュー用のダウンロードに失敗しました: \(error.localizedDescription)"
            return nil
        }
    }

    private static let previewCacheDirectory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.yama.Ferry/Preview")

    private static let editCacheDirectory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.yama.Ferry/Edit")

    // MARK: - ドラッグ

    /// リモートの項目のドラッグ。アプリ内（左のペイン）には項目の情報を、Finder にはダウンロードしたファイルを渡す。
    func dragWriter(forRemote entry: FileEntry) -> NSPasteboardWriting {
        let type = entry.isDirectory ? UTType.folder
            : UTType(filenameExtension: (entry.name as NSString).pathExtension) ?? .data
        let provider = RemoteFilePromiseProvider(fileType: type.identifier, delegate: promiseDelegate)
        provider.userInfo = entry
        return provider
    }

    @ObservationIgnored private lazy var promiseDelegate = RemoteFilePromiseDelegate(session: self)

    /// Finder へのドロップ先にダウンロードする
    fileprivate func downloadForPromise(_ entry: FileEntry, to destination: URL) async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("Ferry-Drag/\(UUID().uuidString)")
        try await transfers.downloadAndWait([entry], into: directory)
        try FileManager.default.moveItem(at: directory.appendingPathComponent(entry.name), to: destination)
        try? FileManager.default.removeItem(at: directory)
    }
}

/// Finder にはファイルプロミス、Ferry の左のペインには項目の情報（JSON）を渡す
final class RemoteFilePromiseProvider: NSFilePromiseProvider {
    override func writableTypes(for pasteboard: NSPasteboard) -> [NSPasteboard.PasteboardType] {
        super.writableTypes(for: pasteboard) + [.ferryRemoteItem]
    }

    override func pasteboardPropertyList(forType type: NSPasteboard.PasteboardType) -> Any? {
        if type == .ferryRemoteItem, let entry = userInfo as? FileEntry {
            return try? JSONEncoder().encode([entry])
        }
        return super.pasteboardPropertyList(forType: type)
    }
}

final class RemoteFilePromiseDelegate: NSObject, NSFilePromiseProviderDelegate {
    private weak var session: ServerSession?
    private let queue = OperationQueue()

    init(session: ServerSession) {
        self.session = session
    }

    func filePromiseProvider(_ provider: NSFilePromiseProvider, fileNameForType fileType: String) -> String {
        (provider.userInfo as? FileEntry)?.name ?? "download"
    }

    func operationQueue(for provider: NSFilePromiseProvider) -> OperationQueue {
        queue
    }

    func filePromiseProvider(_ provider: NSFilePromiseProvider, writePromiseTo url: URL,
                             completionHandler: @escaping (Error?) -> Void) {
        guard let entry = provider.userInfo as? FileEntry else { return completionHandler(nil) }
        Task { @MainActor [weak session] in
            guard let session else { return completionHandler(nil) }
            do {
                try await session.downloadForPromise(entry, to: url)
                completionHandler(nil)
            } catch {
                completionHandler(error)
            }
        }
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
        let session = ServerSession(server: server, password: Passwords.password(for: server.id)) { [weak store] in
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
        a.bookmarks = nil
        b.bookmarks = nil
        a.startBookmarkID = nil
        b.startBookmarkID = nil
        a.name = ""
        b.name = ""
        return a == b
    }
}
