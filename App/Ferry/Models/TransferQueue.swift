import FerryKit
import Foundation
import Observation

@MainActor @Observable
final class TransferItem: Identifiable {
    enum Direction { case upload, download }

    enum State: Equatable {
        case waiting, running, finished, cancelled
        case failed(String)
    }

    let id = UUID()
    let direction: Direction
    let localURL: URL
    let remotePath: String
    var total: Int64
    var done: Int64 = 0
    private(set) var state: State = .waiting

    /// 転送元の更新日時と、転送先に同名ファイルがある場合はその更新日時（上書き確認用）
    var sourceModified: Date?
    var destinationExists = false
    var destinationModified: Date?
    /// 転送先の更新日時の粗さ（秒）。サーバーの一覧は分単位・日単位のことがある。ローカルは 0
    var destinationModifiedPrecision: TimeInterval = 0
    /// アップロード先にある同名ファイルの権限（"rw-r--r--" 形式）。一時ファイルから置き換えた後に同じ権限に戻す
    var destinationPermissions: String?

    @ObservationIgnored var task: Task<Void, Error>?
    @ObservationIgnored private var waiters: [CheckedContinuation<Void, Never>] = []

    var name: String { localURL.lastPathComponent }
    var fraction: Double { total > 0 ? min(Double(done) / Double(total), 1) : 0 }
    var isActive: Bool { state == .waiting || state == .running }

    /// 転送元の方が新しいか（日時が分からない場合は新しいとみなす）
    var isSourceNewer: Bool {
        guard let source = sourceModified, let destination = destinationModified else { return true }
        return ModifiedDate.isSourceNewer(source, than: destination, destinationPrecision: destinationModifiedPrecision)
    }

    init(direction: Direction, localURL: URL, remotePath: String, total: Int64, sourceModified: Date? = nil) {
        self.direction = direction
        self.localURL = localURL
        self.remotePath = remotePath
        self.total = total
        self.sourceModified = sourceModified
    }

    func setState(_ newState: State) {
        state = newState
        if !isActive {
            waiters.forEach { $0.resume() }
            waiters = []
        }
    }

    func waitUntilDone() async {
        guard isActive else { return }
        await withCheckedContinuation { waiters.append($0) }
    }
}

/// 同名ファイルがあったときの扱い
enum ConflictPolicy {
    case overwrite, skip, newerOnly
}

/// 上書き確認待ちのまとまり（1 回のドラッグやボタン操作の単位）
@MainActor
struct PendingConflict: Identifiable {
    let id = UUID()
    let direction: TransferItem.Direction
    let items: [TransferItem]
    let notifies: Bool
    var conflictCount: Int { items.filter(\.destinationExists).count }
    var firstConflictName: String { items.first(where: \.destinationExists)?.name ?? "" }
}

/// 転送の待ち行列。転送は専用の接続（設定の「同時に転送する数」まで）で先頭から順に同時に処理し、
/// フォルダの中身の一覧は一覧表示用の接続で取る。
@MainActor @Observable
final class TransferQueue {
    private(set) var items: [TransferItem] = []
    private(set) var pendingConflicts: [PendingConflict] = []
    /// フォルダの中身を調べている最中か
    private(set) var isPreparing = false

    @ObservationIgnored private let server: ServerConfig
    @ObservationIgnored private var password: String?
    /// 転送用の接続。1 本目は最初から持ち、2 本目以降は同時に転送するときに作る
    @ObservationIgnored private var connections: [TransferConnection]
    /// 接続がつながらなかった（共用サーバーの同時接続数の上限など）ために減らした後の接続の数。このセッションの間は増やさない
    @ObservationIgnored private var connectionLimit: Int?
    @ObservationIgnored private let lister: RemoteClient
    @ObservationIgnored private let excludeRules: () -> ExcludeRules
    /// 待ち行列が空になったとき（両ペインの再読み込み用）
    @ObservationIgnored var onIdle: (() -> Void)?
    /// 完了通知に出す接続先の名前
    @ObservationIgnored var serverName = ""

    init(server: ServerConfig, password: String?, lister: RemoteClient, excludeRules: @escaping () -> ExcludeRules) {
        self.server = server
        self.password = password
        connections = [TransferConnection(client: RemoteClient(server: server, password: password))]
        self.lister = lister
        self.excludeRules = excludeRules
    }

    var activeCount: Int { items.filter(\.isActive).count }
    /// 転送を処理している最中か（全部の接続が空くと onIdle を呼ぶ）
    var isRunning: Bool { connections.contains(where: \.isBusy) }

    /// 設定「同時に転送する数」（1〜4）。次に始まる転送から効く
    static let connectionsKey = "transferConnections"
    static let defaultConnections = 3
    /// 設定「アップロードは一時ファイルに送ってから置き換える」
    static let safeUploadKey = "safeUpload"

    private static var maxConnections: Int {
        min(max(UserDefaults.standard.object(forKey: connectionsKey) as? Int ?? defaultConnections, 1), 4)
    }

    /// パスワードを後から設定する（接続時に入力された場合など）。これから作る接続にも使う
    func setPassword(_ password: String?) {
        self.password = password
        connections.forEach { $0.client.setPassword(password) }
    }

    // MARK: - アップロード

    func enqueueUploads(_ urls: [URL], toRemoteDirectory directory: String) async {
        isPreparing = true
        // ファイルの多いフォルダをたどると時間がかかるので、メインスレッドの外でたどる
        let rules = excludeRules()
        let planned = await Task.detached { Self.collectUploads(urls, toRemoteDirectory: directory, rules: rules) }.value
        let batch = planned.map { upload in
            TransferItem(direction: .upload, localURL: upload.localURL, remotePath: upload.remotePath,
                         total: upload.size, sourceModified: upload.modified)
        }
        guard !batch.isEmpty else {
            isPreparing = false
            return
        }
        await markRemoteConflicts(batch)
        isPreparing = false
        submit(batch, direction: .upload)
    }

    /// エディタで保存されたファイルをそのままアップロードする（確認なし）。
    /// exists … サーバーにまだあるか（開いたファイルなので通常はある）。permissions … サーバーのファイルの権限
    @discardableResult
    func uploadEdited(_ url: URL, to remotePath: String, exists: Bool = true, permissions: String? = nil) -> TransferItem {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        let item = TransferItem(direction: .upload, localURL: url, remotePath: remotePath, total: Int64(size))
        item.destinationExists = exists
        item.destinationPermissions = permissions
        submit([item], direction: .upload, confirmOverwrite: false, notifies: false)
        return item
    }

    /// アップロードする 1 ファイル（メインスレッドの外で集めるので値型にする）
    private struct PlannedUpload: Sendable {
        let localURL: URL
        let remotePath: String
        let size: Int64
        let modified: Date?
    }

    /// フォルダは中のファイルに展開する。除外ルールはフォルダの中身にだけ適用する。
    private nonisolated static func collectUploads(_ urls: [URL], toRemoteDirectory directory: String,
                                                   rules: ExcludeRules) -> [PlannedUpload] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        var batch: [PlannedUpload] = []

        func item(for file: URL, remotePath: String) -> PlannedUpload? {
            guard let values = try? file.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { return nil }
            return PlannedUpload(localURL: file, remotePath: remotePath, size: Int64(values.fileSize ?? 0),
                                 modified: values.contentModificationDate)
        }

        for url in urls {
            let remoteBase = RemotePath.join(directory, url.lastPathComponent)
            guard (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else {
                item(for: url, remotePath: remoteBase).map { batch.append($0) }
                continue
            }
            guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: keys) else { continue }
            while let file = enumerator.nextObject() as? URL {
                if rules.excludes(file.lastPathComponent) {
                    enumerator.skipDescendants()
                    continue
                }
                let relative = String(file.standardizedFileURL.path.dropFirst(url.standardizedFileURL.path.count + 1))
                item(for: file, remotePath: RemotePath.join(remoteBase, relative)).map { batch.append($0) }
            }
        }
        return batch
    }

    private func markRemoteConflicts(_ batch: [TransferItem]) async {
        var existing: [String: RemoteItem] = [:]
        for directory in Set(batch.map { RemotePath.parent(of: $0.remotePath) }) {
            // 存在しないフォルダはエラーになるが、その場合は衝突なし
            guard let listing = try? await lister.list(directory) else { continue }
            for item in listing { existing[item.path] = item }
        }
        for item in batch {
            if let remote = existing[item.remotePath], !remote.isDirectory {
                item.destinationExists = true
                item.destinationModified = remote.modified
                item.destinationModifiedPrecision = remote.modifiedPrecision
                item.destinationPermissions = remote.permissions
            }
        }
    }

    // MARK: - ダウンロード

    func enqueueDownloads(_ entries: [FileEntry], toLocalDirectory directory: URL) async {
        isPreparing = true
        let batch = await collectDownloads(entries, into: directory)
        isPreparing = false
        for item in batch {
            let values = try? item.localURL.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey])
            if values?.isRegularFile == true {
                item.destinationExists = true
                item.destinationModified = values?.contentModificationDate
            }
        }
        submit(batch, direction: .download)
    }

    /// Finder へのドラッグ用。一時フォルダへダウンロードし、終わるまで待つ。
    func downloadAndWait(_ entries: [FileEntry], into directory: URL) async throws {
        let batch = await collectDownloads(entries, into: directory)
        submit(batch, direction: .download, confirmOverwrite: false, notifies: false)
        for item in batch {
            await item.waitUntilDone()
        }
        if let failed = batch.first(where: { $0.state != .finished }) {
            if case .failed(let message) = failed.state {
                throw FerryError(kind: .other, message: message, detail: nil)
            }
            throw FerryError(kind: .cancelled, message: "キャンセルしました", detail: nil)
        }
    }

    private func collectDownloads(_ entries: [FileEntry], into directory: URL) async -> [TransferItem] {
        var batch: [TransferItem] = []
        for entry in entries {
            let localURL = directory.appendingPathComponent(entry.name)
            if entry.isDirectory {
                do {
                    try FileManager.default.createDirectory(at: localURL, withIntermediateDirectories: true)
                    try await collectRemoteFolder(entry.path, into: localURL, batch: &batch)
                } catch {
                    let item = TransferItem(direction: .download, localURL: localURL, remotePath: entry.path, total: 0)
                    item.setState(.failed(error.localizedDescription))
                    items.append(item)
                }
            } else {
                batch.append(TransferItem(direction: .download, localURL: localURL, remotePath: entry.path,
                                          total: entry.size ?? 0, sourceModified: entry.modified))
            }
        }
        return batch
    }

    private func collectRemoteFolder(_ remotePath: String, into localURL: URL, batch: inout [TransferItem]) async throws {
        let rules = excludeRules()
        for child in try await lister.list(remotePath) where !rules.excludes(child.name) {
            let childURL = localURL.appendingPathComponent(child.name)
            if child.isDirectory {
                try FileManager.default.createDirectory(at: childURL, withIntermediateDirectories: true)
                try await collectRemoteFolder(child.path, into: childURL, batch: &batch)
            } else {
                batch.append(TransferItem(direction: .download, localURL: childURL, remotePath: child.path,
                                          total: child.size ?? 0, sourceModified: child.modified))
            }
        }
    }

    // MARK: - 上書き確認

    /// notifies: まとまりが全部終わったら通知する（編集の自動アップロードなどでは出さない）
    private func submit(_ batch: [TransferItem], direction: TransferItem.Direction, confirmOverwrite: Bool = true,
                        notifies: Bool = true) {
        guard !batch.isEmpty else { return }
        if confirmOverwrite, batch.contains(where: \.destinationExists) {
            pendingConflicts.append(PendingConflict(direction: direction, items: batch, notifies: notifies))
        } else {
            start(batch, direction: direction, notifies: notifies)
        }
    }

    private func start(_ batch: [TransferItem], direction: TransferItem.Direction, notifies: Bool) {
        guard !batch.isEmpty else { return }
        items.append(contentsOf: batch)
        pump()
        guard notifies, UserDefaults.standard.object(forKey: "notifyTransfers") as? Bool ?? true else { return }
        let serverName = serverName
        Task {
            await Notifier.requestAuthorization()
            for item in batch {
                await item.waitUntilDone()
            }
            Self.notifyFinished(batch, direction: direction, serverName: serverName)
        }
    }

    private static func notifyFinished(_ batch: [TransferItem], direction: TransferItem.Direction, serverName: String) {
        let finished = batch.filter { $0.state == .finished }
        let failed = batch.filter { if case .failed = $0.state { true } else { false } }.count
        // すべて中止した場合は知らせない
        guard !finished.isEmpty || failed > 0 else { return }

        let action = direction == .upload ? "アップロード" : "ダウンロード"
        let title = failed > 0 ? "\(action)が終わりました（失敗 \(failed) 件）" : "\(action)が完了しました"
        var body = finished.first.map { $0.name } ?? ""
        if finished.count > 1 {
            body += " ほか \(finished.count - 1) 件"
        }
        Notifier.post(title: title, body: serverName.isEmpty ? body : "\(serverName)：\(body)")
    }

    /// policy が nil ならまとまりごと取りやめる
    func resolve(_ conflict: PendingConflict, with policy: ConflictPolicy?) {
        pendingConflicts.removeAll { $0.id == conflict.id }
        guard let policy else { return }
        let accepted = conflict.items.filter { item in
            guard item.destinationExists else { return true }
            switch policy {
            case .overwrite: return true
            case .skip: return false
            case .newerOnly: return item.isSourceNewer
            }
        }
        start(accepted, direction: conflict.direction, notifies: conflict.notifies)
    }

    /// 同期など、転送するものが確定しているまとまりを追加する（通知は同期側で出す）
    func enqueuePlanned(_ batch: [TransferItem]) {
        submit(batch, direction: batch.first?.direction ?? .upload, confirmOverwrite: false, notifies: false)
    }

    // MARK: - 操作

    func cancel(_ item: TransferItem) {
        switch item.state {
        case .waiting: item.setState(.cancelled)
        case .running: item.task?.cancel()
        default: break
        }
    }

    func cancelAll() {
        items.forEach(cancel)
    }

    func retry(_ item: TransferItem) {
        item.done = 0
        item.setState(.waiting)
        pump()
    }

    func clearFinished() {
        items.removeAll { !$0.isActive }
    }

    // MARK: - 実行

    /// 待っている項目を先頭から順に、空いている接続（足りなければ作る）に割り当てる
    private func pump() {
        while let next = items.first(where: { $0.state == .waiting }), let connection = idleConnection() {
            run(next, on: connection)
        }
    }

    private func idleConnection() -> TransferConnection? {
        let limit = min(Self.maxConnections, connectionLimit ?? .max)
        guard connections.filter(\.isBusy).count < limit else { return nil }
        if let idle = connections.first(where: { !$0.isBusy }) {
            return idle
        }
        let connection = TransferConnection(client: RemoteClient(server: server, password: password))
        connections.append(connection)
        return connection
    }

    /// 中止に備えて、状態と転送のタスクはここで（割り当てたその場で）設定する
    private func run(_ item: TransferItem, on connection: TransferConnection) {
        connection.isBusy = true
        item.setState(.running)
        let throttle = ProgressThrottle()
        let progress: RemoteClient.Progress = { done, total in
            // 間引く前に記録する（画面用の item.done は間引いていて、メインスレッドへの反映も遅れる）
            if done > 0 { throttle.markStarted() }
            guard throttle.shouldReport() else { return }
            Task { @MainActor in
                item.done = done
                if total > 0 { item.total = total }
            }
        }

        let client = connection.client
        // 同名のファイルがあると分かっているときだけ一時ファイルから置き換える。新しいファイル・分からないときは直接送る
        let replacing = item.destinationExists
            && (UserDefaults.standard.object(forKey: Self.safeUploadKey) as? Bool ?? true)
        let permissions = item.destinationPermissions
        let task = Task {
            switch item.direction {
            case .upload:
                try await client.upload(item.localURL, to: item.remotePath, replacingExisting: replacing,
                                        permissions: permissions, progress: progress)
            case .download:
                try FileManager.default.createDirectory(at: item.localURL.deletingLastPathComponent(),
                                                        withIntermediateDirectories: true)
                try await client.download(item.remotePath, to: item.localURL, expectedSize: item.total,
                                          modified: item.sourceModified, progress: progress)
            }
        }
        item.task = task
        Task { await finish(item, task: task, on: connection, progress: throttle) }
    }

    private func finish(_ item: TransferItem, task: Task<Void, Error>, on connection: TransferConnection,
                        progress: ProgressThrottle) async {
        do {
            try await task.value
            item.done = item.total
            item.setState(.finished)
        } catch let error as FerryError where error.kind == .cancelled {
            item.setState(.cancelled)
        } catch let error as FerryError where connections.count > 1 && !task.isCancelled && !progress.hasStarted
            && (error.kind == .connectionFailed || error.kind == .loginFailed) {
            // 複数の接続のうち 1 本が、まだ 1 バイトも送れないうちにつながらないのは、同時接続数の上限のことが多い。
            // 項目は待ちに戻し、この接続は使うのをやめて残りで続ける。同時につなぎに行くので、上限に当たるのは 1 本目のこともある。
            // 最後の 1 本なら今までどおり失敗にする。少しでも進んだ後の失敗（一時的に回線が切れたなど）は接続を減らさず、その項目の失敗にする
            connections.removeAll { $0 === connection }
            connectionLimit = connections.count
            item.done = 0
            item.setState(.waiting)
        } catch {
            item.setState(task.isCancelled ? .cancelled : .failed(error.localizedDescription))
        }
        item.task = nil
        connection.isBusy = false
        pump()
        if !isRunning {
            onIdle?()
        }
    }
}

/// 転送用の接続 1 本と、転送中かどうか
@MainActor
private final class TransferConnection {
    let client: RemoteClient
    var isBusy = false

    init(client: RemoteClient) {
        self.client = client
    }
}

/// curl の進捗コールバックは頻繁に呼ばれるので、画面更新は 0.1 秒ごとに間引く。
/// 1 バイトでも進んだかは、間引かずに記録する（つなぐ段階の失敗と、途中の失敗を分けるため）
private final class ProgressThrottle: @unchecked Sendable {
    private let lock = NSLock()
    private var last = Date.distantPast
    private var started = false

    var hasStarted: Bool { lock.withLock { started } }
    func markStarted() { lock.withLock { started = true } }

    func shouldReport() -> Bool {
        lock.withLock {
            let now = Date()
            guard now.timeIntervalSince(last) >= 0.1 else { return false }
            last = now
            return true
        }
    }
}
