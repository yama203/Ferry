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
    var state: State = .waiting
    @ObservationIgnored var task: Task<Void, Error>?

    var name: String { localURL.lastPathComponent }
    var fraction: Double { total > 0 ? min(Double(done) / Double(total), 1) : 0 }
    var isActive: Bool { state == .waiting || state == .running }

    init(direction: Direction, localURL: URL, remotePath: String, total: Int64) {
        self.direction = direction
        self.localURL = localURL
        self.remotePath = remotePath
        self.total = total
    }
}

/// 転送の待ち行列。一覧表示と干渉しないよう、専用の接続で 1 件ずつ処理する。
@MainActor @Observable
final class TransferQueue {
    private(set) var items: [TransferItem] = []
    @ObservationIgnored private let client: RemoteClient
    @ObservationIgnored private var runner: Task<Void, Never>?
    /// 待ち行列が空になったとき（両ペインの再読み込み用）
    @ObservationIgnored var onIdle: (() -> Void)?

    init(client: RemoteClient) {
        self.client = client
    }

    var activeCount: Int { items.filter(\.isActive).count }

    // MARK: - 追加

    /// フォルダは中のファイルに展開して追加する
    func enqueueUploads(_ urls: [URL], toRemoteDirectory directory: String) {
        let fileManager = FileManager.default
        for url in urls {
            var isDirectory: ObjCBool = false
            guard fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory) else { continue }
            let remoteBase = RemotePath.join(directory, url.lastPathComponent)
            if isDirectory.boolValue {
                let enumerator = fileManager.enumerator(at: url, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey])
                while let file = enumerator?.nextObject() as? URL {
                    let values = try? file.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
                    guard values?.isRegularFile == true, file.lastPathComponent != ".DS_Store" else { continue }
                    let relative = file.path.dropFirst(url.path.count + 1)
                    add(TransferItem(direction: .upload, localURL: file,
                                     remotePath: RemotePath.join(remoteBase, String(relative)),
                                     total: Int64(values?.fileSize ?? 0)))
                }
            } else {
                let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                add(TransferItem(direction: .upload, localURL: url, remotePath: remoteBase, total: Int64(size)))
            }
        }
        pump()
    }

    /// リモートのフォルダは中身を一覧してから追加する
    func enqueueDownloads(_ entries: [FileEntry], toLocalDirectory directory: URL) async {
        for entry in entries {
            let localURL = directory.appendingPathComponent(entry.name)
            if entry.isDirectory {
                do {
                    try await addRemoteFolder(entry.path, into: localURL)
                } catch {
                    let item = TransferItem(direction: .download, localURL: localURL, remotePath: entry.path, total: 0)
                    item.state = .failed(error.localizedDescription)
                    items.append(item)
                }
            } else {
                add(TransferItem(direction: .download, localURL: localURL, remotePath: entry.path,
                                 total: entry.size ?? 0))
            }
            pump()
        }
    }

    private func addRemoteFolder(_ remotePath: String, into localURL: URL) async throws {
        try FileManager.default.createDirectory(at: localURL, withIntermediateDirectories: true)
        for child in try await client.list(remotePath) {
            let childURL = localURL.appendingPathComponent(child.name)
            if child.isDirectory {
                try await addRemoteFolder(child.path, into: childURL)
            } else {
                add(TransferItem(direction: .download, localURL: childURL, remotePath: child.path,
                                 total: child.size ?? 0))
            }
        }
    }

    private func add(_ item: TransferItem) {
        items.append(item)
    }

    // MARK: - 操作

    func cancel(_ item: TransferItem) {
        switch item.state {
        case .waiting: item.state = .cancelled
        case .running: item.task?.cancel()
        default: break
        }
    }

    func cancelAll() {
        items.forEach(cancel)
    }

    func retry(_ item: TransferItem) {
        item.done = 0
        item.state = .waiting
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
        item.state = .running
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
            item.state = .finished
        } catch let error as FerryError where error.kind == .cancelled {
            item.state = .cancelled
        } catch {
            item.state = task.isCancelled ? .cancelled : .failed(error.localizedDescription)
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
