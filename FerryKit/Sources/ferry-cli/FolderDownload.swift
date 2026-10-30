import Foundation
import FerryKit

/// フォルダの中身をまるごとローカルにダウンロードする（get -r）。
/// 先にフォルダをたどってファイルの一覧を作り、複数の接続で同時にダウンロードする。
/// 手元に同じサイズ・同じ更新日時のファイルがあれば飛ばすので、途中で止まってもやり直せる。
/// 1 件失敗しても残りは続け、最後にまとめて報告する。
struct FolderDownload {
    private(set) var downloaded = 0
    private(set) var skipped = 0
    private(set) var failures: [String] = []

    private struct Job: Sendable {
        let item: RemoteItem
        let target: URL
    }

    private enum Outcome: Sendable {
        case downloaded
        case failed(String)
    }

    /// connections … 同時に使う接続の数。共用サーバーは同時接続数に上限があることが多いので控えめにする
    mutating func run(server: ServerConfig, password: String?, verbose: Bool, _ remote: String, to local: URL,
                      connections: Int) async throws {
        let lister = RemoteClient(server: server, password: password)
        lister.debugLog = verbose
        var jobs: [Job] = []
        try await collect(lister, remote, to: local, into: &jobs)
        print("📋 \(jobs.count) 件をダウンロードします（\(skipped) 件は変更なし、接続 \(connections) 本）")

        let clients = (0..<max(1, connections)).map { index -> RemoteClient in
            // 一覧に使った接続も 1 本目として使い回す
            let client = index == 0 ? lister : RemoteClient(server: server, password: password)
            client.debugLog = verbose
            return client
        }
        let queue = JobQueue(jobs)
        await withTaskGroup(of: [Outcome].self) { group in
            for client in clients {
                group.addTask {
                    var outcomes: [Outcome] = []
                    while let job = await queue.next() {
                        do {
                            try await client.download(job.item.path, to: job.target, expectedSize: job.item.size)
                            if let modified = job.item.modified {
                                try? FileManager.default.setAttributes([.modificationDate: modified],
                                                                       ofItemAtPath: job.target.path)
                            }
                            print("⬇️  \(job.item.path)")
                            outcomes.append(.downloaded)
                        } catch {
                            outcomes.append(.failed("\(job.item.path): \(error.localizedDescription)"))
                        }
                    }
                    return outcomes
                }
            }
            for await outcomes in group {
                for outcome in outcomes {
                    switch outcome {
                    case .downloaded: downloaded += 1
                    case .failed(let message): failures.append(message)
                    }
                }
            }
        }
    }

    /// フォルダをたどり、ダウンロードが必要なファイルを集める（ローカルのフォルダもここで作る）
    private mutating func collect(_ client: RemoteClient, _ remote: String, to local: URL,
                                  into jobs: inout [Job]) async throws {
        try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
        let items: [RemoteItem]
        do {
            items = try await client.list(remote)
        } catch {
            failures.append("\(remote)/: \(error.localizedDescription)")
            return
        }
        for item in items.sorted(by: { $0.name < $1.name }) {
            let target = local.appendingPathComponent(item.name)
            switch item.kind {
            case .directory:
                try await collect(client, item.path, to: target, into: &jobs)
            case .symlink:
                // リンク先がフォルダかどうか一覧からは分からないので、リンクは取らずに知らせる
                print("🔗 飛ばしました（リンク）: \(item.path) -> \(item.symlinkTarget ?? "?")")
            case .file:
                if isUnchanged(target, item) {
                    skipped += 1
                } else {
                    jobs.append(Job(item: item, target: target))
                }
            }
        }
    }

    private func isUnchanged(_ local: URL, _ item: RemoteItem) -> Bool {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: local.path),
              let size = attributes[.size] as? Int64, size == item.size,
              let localDate = attributes[.modificationDate] as? Date, let remoteDate = item.modified else { return false }
        // LIST の日時は分単位（古いものは日単位）なので、1 日以内の差は同じとみなす
        return abs(localDate.timeIntervalSince(remoteDate)) < 86_400
    }
}

/// 接続ごとの作業が、次のファイルを 1 件ずつ取り出す
private actor JobQueue<Job: Sendable> {
    private var jobs: [Job]
    private var index = 0

    init(_ jobs: [Job]) {
        self.jobs = jobs
    }

    func next() -> Job? {
        guard index < jobs.count else { return nil }
        defer { index += 1 }
        return jobs[index]
    }
}
