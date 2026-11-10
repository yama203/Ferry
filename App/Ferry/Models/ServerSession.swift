import AppKit
import FerryKit
import Foundation
import Observation
import UniformTypeIdentifiers

/// エディタで開いた後にサーバー側で変わっていた（または削除されていた）ファイルの、上書きの確認待ち
struct EditConflict: Identifiable {
    let id = UUID()
    let remotePath: String
    let isDeleted: Bool
    /// 今のサーバーのファイルの権限（上書きするとき、一時ファイルから置き換えた後に戻す）
    let permissions: String?
    var name: String { RemotePath.name(of: remotePath) }
}

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
    private(set) var status: Status = .idle {
        didSet { recordProblem() }
    }
    /// 接続後に開くブックマーク（サイドバーから「〜で接続」を選んだときなど）
    @ObservationIgnored var pendingBookmark: PaneBookmark?
    /// 編集用にダウンロード中のファイル名
    private(set) var openingForEdit: String?
    /// エディタで保存したが、サーバー側で変わっていたのでアップロードせずに確認を待っているもの
    private(set) var editConflicts: [EditConflict] = []
    /// サーバー側の確認かアップロードの途中のファイル（サーバーのパス）。この間の保存は終わってからもう一度処理する
    @ObservationIgnored private var editUploadsInFlight: Set<String> = []
    @ObservationIgnored private var savedAgainDuringUpload: Set<String> = []
    /// Quick Look のプレビュー用（一覧表示や転送を待たせないよう接続を分ける）
    @ObservationIgnored private let previewClient: RemoteClient
    @ObservationIgnored private var hasPassword: Bool
    /// 編集したファイルの自動アップロードの失敗。待ち行列が空になったときの再読み込みが成功すると
    /// エラー表示が消えるので、覚えておいて再読み込みの後に入れ直す
    @ObservationIgnored private var editUploadFailure: String?
    /// 待ち行列が空になったときの再読み込みのうち、終わっていないものの数
    @ObservationIgnored private var idleRefreshCount = 0

    init(server: ServerConfig, password: String?, excludeRules: @escaping () -> ExcludeRules) {
        self.server = server
        local = BrowserModel(side: .local)
        // 一覧用と転送用で接続を分け、転送中でもフォルダを移動できるようにする
        let browseClient = RemoteClient(server: server, password: password)
        remote = BrowserModel(side: .remote, client: browseClient)
        previewClient = RemoteClient(server: server, password: password)
        transfers = TransferQueue(server: server, password: password, lister: browseClient, excludeRules: excludeRules)
        transfers.serverName = server.name
        // 秘密鍵はパスフレーズ無しの場合もあるので、パスワード無しでも接続を試す
        hasPassword = password != nil || server.privateKeyPath != nil || server.username == "anonymous"
        transfers.onIdle = { [weak self] in
            guard let self else { return }
            idleRefreshCount += 1
            Task {
                await self.local.refresh()
                await self.remote.refresh()
                self.idleRefreshCount -= 1
                if self.idleRefreshCount == 0, let failure = self.editUploadFailure {
                    self.remote.errorMessage = failure
                    self.editUploadFailure = nil
                }
            }
        }
        editWatcher = EditWatcher { [weak self] file in
            self?.editedFileSaved(file)
        }
        editWatcher.onStop = { [weak self] remotePath in
            self?.editConflicts.removeAll { $0.remotePath == remotePath }
        }
    }

    func connect() async {
        guard status != .connecting, let client = remote.client else { return }
        // 画面を開いた後にパスワードが同期されてきた場合に備えて、もう一度探す
        if !hasPassword, let password = Passwords.password(for: server.id) {
            client.setPassword(password)
            transfers.setPassword(password)
            previewClient.setPassword(password)
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

    /// 接続できたか・できなかったかをサイドバーの印に反映する
    private func recordProblem() {
        switch status {
        case .connected:
            ConnectionProblems.shared.set(nil, for: server.id)
        case .failed(let message):
            ConnectionProblems.shared.set(message, for: server.id)
        // パスワードを保存していないだけのときは印を出さない（ログインに失敗したときだけ理由がある）
        case .needsPassword(let message?):
            ConnectionProblems.shared.set("ログインできませんでした（パスワードが変わったかもしれません）。\(message)", for: server.id)
        default:
            break
        }
    }

    /// 接続画面で入力されたパスワードで接続する
    func connect(withPassword password: String, save: Bool) async {
        remote.client?.setPassword(password)
        transfers.setPassword(password)
        previewClient.setPassword(password)
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
        editConflicts = []
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
        await Self.removingLeftovers?.value
        let directory = Self.editCacheDirectory
            .appendingPathComponent(server.id.uuidString)
            .appendingPathComponent(String(RemotePath.parent(of: entry.path).dropFirst()))
        openingForEdit = entry.name
        defer { openingForEdit = nil }
        do {
            try await transfers.downloadAndWait([entry], into: directory)
            let file = directory.appendingPathComponent(entry.name)
            // 一覧に出ていたサイズと更新日時を、保存したときにサーバー側で変わっていないかを調べる基準にする
            editConflicts.removeAll { $0.remotePath == entry.path }
            editWatcher.watch(file, remotePath: entry.path,
                              baseline: RemoteFileStamp(size: entry.size, modified: entry.modified),
                              permissions: entry.permissions)
            EditorApp.open(file)
        } catch {
            remote.errorMessage = "編集用のダウンロードに失敗しました: \(error.localizedDescription)"
        }
    }

    /// エディタで保存された。開いた後に他の人がサーバー側で変えていないかを確かめてからアップロードする
    private func editedFileSaved(_ file: EditWatcher.EditedFile) {
        let path = file.remotePath
        // 確認を出している間の保存は、その答えに任せる（上書きするなら、そのときの最新の中身を送る）。確認を重ねて出さない
        guard !editConflicts.contains(where: { $0.remotePath == path }) else { return }
        // 確認やアップロードの途中なら、終わってからもう一度。基準が古いまま比べると、自分のアップロードを「変更」とみなしてしまう
        guard !editUploadsInFlight.contains(path) else {
            savedAgainDuringUpload.insert(path)
            return
        }
        editUploadsInFlight.insert(path)
        Task { await processEditedFile(path, overwriting: nil) }
    }

    /// 上書きの確認の答え
    func resolve(_ conflict: EditConflict, overwrite: Bool) {
        editConflicts.removeAll { $0.id == conflict.id }
        // 「アップロードしない」なら基準はそのまま。次に保存したときにまた確認する
        guard overwrite, editWatcher.file(for: conflict.remotePath) != nil,
              !editUploadsInFlight.contains(conflict.remotePath) else { return }
        editUploadsInFlight.insert(conflict.remotePath)
        Task { await processEditedFile(conflict.remotePath, overwriting: conflict) }
    }

    /// overwriting … 確認で「上書きする」を選んだもの（サーバー側を確かめずにアップロードする）
    private func processEditedFile(_ path: String, overwriting conflict: EditConflict?) async {
        defer { editUploadsInFlight.remove(path) }
        var conflict = conflict
        repeat {
            savedAgainDuringUpload.remove(path)
            guard let file = editWatcher.file(for: path) else { return }
            // 削除されていたものを上書きするときは、新しく作る（一時ファイルを使わず直接送る）
            let exists = conflict.map { !$0.isDeleted } ?? true
            var permissions = conflict.map(\.permissions) ?? file.permissions
            // 一覧を取れなかった（通信エラーなど）ときは、確かめずにアップロードする（確認のために保存を止めない）
            if conflict == nil, let baseline = file.baseline, let client = remote.client,
               let listing = try? await client.list(RemotePath.parent(of: path)) {
                let current = listing.first { $0.name == RemotePath.name(of: path) && !$0.isDirectory }
                switch baseline.change(of: path, in: listing) {
                case .unchanged:
                    permissions = current?.permissions ?? permissions
                case .modified:
                    askBeforeOverwriting(path, isDeleted: false, permissions: current?.permissions)
                    return
                case .deleted:
                    askBeforeOverwriting(path, isDeleted: true, permissions: nil)
                    return
                }
            }
            conflict = nil
            // 失敗したときは基準をそのままにする（安全なアップロードなら、サーバーのファイルは元のまま）
            if await uploadEdited(file, exists: exists, permissions: permissions) {
                await relearnBaseline(path)
            }
        } while savedAgainDuringUpload.contains(path)
    }

    private func askBeforeOverwriting(_ path: String, isDeleted: Bool, permissions: String?) {
        let conflict = EditConflict(remotePath: path, isDeleted: isDeleted, permissions: permissions)
        editConflicts.append(conflict)
        // エディタを見ていて確認に気づかないことがあるので、通知の設定に関係なく知らせる
        let serverName = server.name
        let body = "アップロードしていません。Ferry で上書きするかを選んでください。"
        Task {
            await Notifier.requestAuthorization()
            Notifier.post(title: "『\(conflict.name)』はサーバーで\(isDeleted ? "削除" : "変更")されています",
                          body: serverName.isEmpty ? body : "\(serverName)：\(body)")
        }
    }

    /// アップロードした後のサーバーのサイズと更新日時を、次の保存の基準にする。一覧を取れなければ分からないものとする
    private func relearnBaseline(_ path: String) async {
        let listing = try? await remote.client?.list(RemotePath.parent(of: path))
        let current = listing?.first { $0.name == RemotePath.name(of: path) && !$0.isDirectory }
        editWatcher.uploaded(path, baseline: current.map(RemoteFileStamp.init), permissions: current?.permissions)
    }

    /// 保存されたファイルを自動でアップロードし、終わるまで待つ。転送一覧に「失敗」と出るだけでは保存できたと思い込むので、
    /// 失敗したら通知の設定に関係なく通知し、リモート側にも出す（中止したときは出さない）
    private func uploadEdited(_ file: EditWatcher.EditedFile, exists: Bool, permissions: String?) async -> Bool {
        let item = transfers.uploadEdited(file.localURL, to: file.remotePath, exists: exists, permissions: permissions)
        await item.waitUntilDone()
        guard case .failed(let reason) = item.state else { return item.state == .finished }
        let message = "『\(item.name)』をアップロードできませんでした: \(reason)"
        showEditUploadFailure(message)
        await Notifier.requestAuthorization()
        Notifier.post(title: "『\(item.name)』をアップロードできませんでした",
                      body: server.name.isEmpty ? reason : "\(server.name)：\(reason)")
        return false
    }

    /// 再読み込みがこれから（転送の処理中）か途中なら、終わった後にも入れ直す。どちらでもなければ消されることはない
    private func showEditUploadFailure(_ message: String) {
        remote.errorMessage = message
        if transfers.isRunning || idleRefreshCount > 0 {
            editUploadFailure = message
        }
    }

    // MARK: - Quick Look

    /// これより大きいファイルはダウンロードに時間がかかるのでプレビューしない
    private static let previewSizeLimit: Int64 = 50 * 1024 * 1024

    /// 選んだだけで先にダウンロードしておくファイルの大きさの上限
    private static let prefetchSizeLimit: Int64 = 5 * 1024 * 1024

    /// ダウンロード中のプレビュー（同じファイルを二重にダウンロードしないため）
    @ObservationIgnored private var previewDownloads: [String: Task<URL?, Never>] = [:]

    private func previewCacheURL(for entry: FileEntry) -> URL {
        Self.previewCacheDirectory
            .appendingPathComponent(server.id.uuidString)
            .appendingPathComponent(String(entry.path.dropFirst()))
    }

    /// ダウンロード済みで、すぐに表示できるプレビュー。サイズだけではサイズの変わらない編集の後に古い中身が出るので、
    /// 更新日時（ダウンロード時にサーバーのものに揃えている）も同じときだけ使い回す。サーバーの日時が分からなければ使い回さない
    func cachedPreviewURL(for entry: FileEntry) -> URL? {
        guard !entry.isDirectory, Self.removingLeftovers == nil, let modified = entry.modified else { return nil }
        let url = previewCacheURL(for: entry)
        guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]),
              let cachedSize = values.fileSize, Int64(cachedSize) == entry.size,
              let cachedModified = values.contentModificationDate,
              abs(cachedModified.timeIntervalSince(modified)) < 1 else { return nil }
        return url
    }

    /// Quick Look 用にダウンロードしたファイル。転送一覧には出さず、プレビュー専用の接続で取得する。
    func previewURL(for entry: FileEntry) async -> URL? {
        guard !entry.isDirectory else { return nil }
        if let size = entry.size, size > Self.previewSizeLimit {
            remote.errorMessage = "\(entry.name) は大きいため（\(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))）プレビューしません"
            return nil
        }
        if let cached = cachedPreviewURL(for: entry) {
            return cached
        }
        if let running = previewDownloads[entry.path] {
            return await running.value
        }
        await Self.removingLeftovers?.value
        let url = previewCacheURL(for: entry)
        let client = previewClient
        let task = Task<URL?, Never> { @MainActor in
            defer { previewDownloads[entry.path] = nil }
            do {
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try await client.download(entry.path, to: url, expectedSize: entry.size, modified: entry.modified)
                return url
            } catch {
                remote.errorMessage = "プレビュー用のダウンロードに失敗しました: \(error.localizedDescription)"
                return nil
            }
        }
        previewDownloads[entry.path] = task
        return await task.value
    }

    /// サーバー側で小さいファイルを 1 つ選んだら、スペースを押す前に裏でダウンロードしておく
    func prefetchPreview(for entry: FileEntry) {
        guard status == .connected, !entry.isDirectory, let size = entry.size, size <= Self.prefetchSizeLimit,
              cachedPreviewURL(for: entry) == nil, previewDownloads[entry.path] == nil else { return }
        Task { _ = await previewURL(for: entry) }
    }

    private static let previewCacheDirectory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.yama.Ferry/Preview")

    private static let editCacheDirectory = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.yama.Ferry/Edit")

    /// Finder へのドラッグでいったんダウンロードする場所
    private static let dragDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("Ferry-Drag")

    /// 前回までに残ったファイルを消している最中か（消し終わるまで新しくダウンロードしない）
    private static var removingLeftovers: Task<Void, Never>?

    /// プレビュー・編集・ドラッグ用に落としたファイル（wp-config.php などパスワードを含むものもある）を残し続けないよう、
    /// 起動時（まだ何も編集・監視していない時点）に丸ごと消す。起動を遅くしないようメインスレッドの外で消す
    static func removeLeftoverDownloads() {
        let directories = [previewCacheDirectory, editCacheDirectory, dragDirectory]
        let task = Task.detached(priority: .utility) {
            for directory in directories {
                try? FileManager.default.removeItem(at: directory)
            }
        }
        removingLeftovers = task
        Task {
            await task.value
            removingLeftovers = nil
        }
    }

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
        await Self.removingLeftovers?.value
        let directory = Self.dragDirectory.appendingPathComponent(UUID().uuidString)
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

    /// 終わっていない転送の数（更新のための再起動の前に確かめる）
    var activeTransferCount: Int {
        sessions.values.reduce(0) { $0 + $1.transfers.activeCount }
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
