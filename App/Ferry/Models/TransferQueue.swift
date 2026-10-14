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

    @ObservationIgnored var task: Task<Void, Error>?
    @ObservationIgnored private var waiters: [CheckedContinuation<Void, Never>] = []

    var name: String { localURL.lastPathComponent }
    var fraction: Double { total > 0 ? min(Double(done) / Double(total), 1) : 0 }
    var isActive: Bool { state == .waiting || state == .running }

    /// 転送元の方が新しいか（日時が分からない場合は新しいとみなす）
    var isSourceNewer: Bool {
        guard let source = sourceModified, let destination = destinationModified else { return true }
        return source.timeIntervalSince(destination) > 1
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

/// 転送の待ち行列。転送は専用の接続で 1 件ずつ処理し、フォルダの中身の一覧は一覧表示用の接続で取る。
@MainActor @Observable
final class TransferQueue {
    private(set) var items: [TransferItem] = []
    private(set) var pendingConflicts: [PendingConflict] = []
    /// フォルダの中身を調べている最中か
    private(set) var isPreparing = false

    @ObservationIgnored private let client: RemoteClient
    @ObservationIgnored private let lister: RemoteClient
    @ObservationIgnored private let excludeRules: () -> ExcludeRules
    @ObservationIgnored private var runner: Task<Void, Never>?
    /// 待ち行列が空になったとき（両ペインの再読み込み用）
    @ObservationIgnored var onIdle: (() -> Void)?
    /// 完了通知に出す接続先の名前
    @ObservationIgnored var serverName = ""

    init(client: RemoteClient, lister: RemoteClient, excludeRules: @escaping () -> ExcludeRules) {
        self.client = client
        self.lister = lister
        self.excludeRules = excludeRules
    }

    var activeCount: Int { items.filter(\.isActive).count }

    // MARK: - アップロード

    func enqueueUploads(_ urls: [URL], toRemoteDirectory directory: String) async {
        let batch = collectUploads(urls, toRemoteDirectory: directory)
        guard !batch.isEmpty else { return }
        isPreparing = true
        await markRemoteConflicts(batch)
        isPreparing = false
        submit(batch, direction: .upload)
    }

    /// エディタで保存されたファイルをそのままアップロードする（確認なし）
    func uploadEdited(_ url: URL, to remotePath: String) {
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        submit([TransferItem(direction: .upload, localURL: url, remotePath: remotePath, total: Int64(size))],
               direction: .upload, confirmOverwrite: false, notifies: false)
    }

    /// フォルダは中のファイルに展開する。除外ルールはフォルダの中身にだけ適用する。
    private func collectUploads(_ urls: [URL], toRemoteDirectory directory: String) -> [TransferItem] {
        let rules = excludeRules()
        let keys: [URLResourceKey] = [.isRegularFileKey, .isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        var batch: [TransferItem] = []

        func item(for file: URL, remotePath: String) -> TransferItem? {
            guard let values = try? file.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { return nil }
            return TransferItem(direction: .upload, localURL: file, remotePath: remotePath,
                                total: Int64(values.fileSize ?? 0), sourceModified: values.contentModificationDate)
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

    private func pump() {
        guard runner == nil else { return }
        runner = Task { [weak self] in
            while let self, let next = self.items.first(where: { $0.state == .waiting }) {
                await self.run(next)
            }
            self?.runner = nil
            self?.onIdle?()
        }
    }

    private func run(_ item: TransferItem) async {
        item.setState(.running)
        let throttle = ProgressThrottle()
        let progress: RemoteClient.Progress = { done, total in
            guard throttle.shouldReport() else { return }
            Task { @MainActor in
                item.done = done
                if total > 0 { item.total = total }
            }
        }

        let client = self.client
        let task = Task {
            switch item.direction {
            case .upload:
                try await client.upload(item.localURL, to: item.remotePath, progress: progress)
            case .download:
                try FileManager.default.createDirectory(at: item.localURL.deletingLastPathComponent(),
                                                        withIntermediateDirectories: true)
                try await client.download(item.remotePath, to: item.localURL, expectedSize: item.total,
                                          progress: progress)
            }
        }
        item.task = task

        do {
            try await task.value
            item.done = item.total
            item.setState(.finished)
        } catch let error as FerryError where error.kind == .cancelled {
            item.setState(.cancelled)
        } catch {
            item.setState(task.isCancelled ? .cancelled : .failed(error.localizedDescription))
        }
        item.task = nil
    }
}

/// curl の進捗コールバックは頻繁に呼ばれるので、画面更新は 0.1 秒ごとに間引く
private final class ProgressThrottle: @unchecked Sendable {
    private let lock = NSLock()
    private var last = Date.distantPast

    func shouldReport() -> Bool {
        lock.withLock {
            let now = Date()
            guard now.timeIntervalSince(last) >= 0.1 else { return false }
            last = now
            return true
        }
    }
}
