import Foundation
import FerryKit

/// フォルダの中身をまるごとアップロードする（put -r）。
/// 先にフォルダをたどってアップロードするファイルの一覧を作り（無いフォルダはここで作る）、複数の接続で同時にアップロードする。
/// サーバーに同じサイズで、手元より新しいファイルがあれば飛ばすので、途中で止まってもやり直せる。
/// 1 件失敗しても残りは続け、最後にまとめて報告する。
struct FolderUpload {
    private(set) var uploaded = 0
    private(set) var skipped = 0
    private(set) var failures: [String] = []

    private struct Job: Sendable {
        let local: URL
        let remote: String
    }

    private enum Outcome: Sendable {
        case uploaded
        case failed(String)
    }

    /// 手元のファイルの情報（一覧を作るときに読む）
    private static let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey,
                                                 .contentModificationDateKey]

    /// connections … 同時に使う接続の数。共用サーバーは同時接続数に上限があることが多いので控えめにする
    mutating func run(server: ServerConfig, password: String?, verbose: Bool, _ local: URL, to remote: String,
                      connections: Int) async throws {
        let clients = (0..<max(1, connections)).map { _ -> RemoteClient in
            let client = RemoteClient(server: server, password: password)
            client.debugLog = verbose
            return client
        }
        let jobs = try await collect(clients, local, to: remote)
        print("📋 \(jobs.count) 件をアップロードします（\(skipped) 件は変更なし、接続 \(connections) 本）")

        let queue = JobQueue(jobs)
        await withTaskGroup(of: [Outcome].self) { group in
            for client in clients {
                group.addTask {
                    var outcomes: [Outcome] = []
                    while let job = await queue.next() {
                        do {
                            try await client.upload(job.local, to: job.remote)
                            print("⬆️  \(job.remote)")
                            outcomes.append(.uploaded)
                        } catch {
                            outcomes.append(.failed("\(job.remote): \(error.localizedDescription)"))
                        }
                    }
                    return outcomes
                }
            }
            for await outcomes in group {
                for outcome in outcomes {
                    switch outcome {
                    case .uploaded: uploaded += 1
                    case .failed(let message): failures.append(message)
                    }
                }
            }
        }
    }

    /// フォルダをたどり、アップロードが必要なファイルを集める（サーバーに無いフォルダもここで作る）。
    /// 同じ階層のフォルダは複数の接続で同時に一覧を取る。親のフォルダは前の階層で作り終えている。
    private mutating func collect(_ clients: [RemoteClient], _ local: URL, to remote: String) async throws -> [Job] {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: local.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw CocoaError(.fileReadNoSuchFile, userInfo: [NSFilePathErrorKey: local.path])
        }

        var jobs: [Job] = []
        var level = [Job(local: local, remote: remote)]
        while !level.isEmpty {
            let queue = JobQueue(level)
            var listings: [(folder: Job, items: [String: RemoteItem])] = []
            await withTaskGroup(of: [(folder: Job, items: [String: RemoteItem])].self) { group in
                for client in clients {
                    group.addTask {
                        var results: [(folder: Job, items: [String: RemoteItem])] = []
                        while let folder = await queue.next() {
                            if let listed = try? await client.list(folder.remote) {
                                results.append((folder, Dictionary(listed.map { ($0.name, $0) },
                                                                   uniquingKeysWith: { first, _ in first })))
                            } else {
                                // 一覧を取れないのはフォルダが無いとき。作れなかった場合は、中のファイルのアップロードで失敗として報告される
                                try? await client.makeDirectory(folder.remote)
                                results.append((folder, [:]))
                            }
                        }
                        return results
                    }
                }
                for await results in group {
                    listings += results
                }
            }

            var next: [Job] = []
            for (folder, remoteItems) in listings.sorted(by: { $0.folder.remote < $1.folder.remote }) {
                let contents: [URL]
                do {
                    contents = try FileManager.default.contentsOfDirectory(at: folder.local,
                                                                           includingPropertiesForKeys: Self.keys)
                } catch {
                    failures.append("\(folder.local.path)/: \(error.localizedDescription)")
                    continue
                }
                for url in contents.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                    let name = url.lastPathComponent
                    guard name != ".DS_Store" else { continue }
                    let values = try url.resourceValues(forKeys: Set(Self.keys))
                    let target = RemotePath.join(folder.remote, name)
                    if values.isSymbolicLink == true {
                        print("🔗 飛ばしました（リンク）: \(url.path)")
                    } else if values.isDirectory == true {
                        next.append(Job(local: url, remote: target))
                    } else if isUnchanged(values, remoteItems[name]) {
                        skipped += 1
                    } else {
                        jobs.append(Job(local: url, remote: target))
                    }
                }
            }
            level = next
        }
        return jobs
    }

    private func isUnchanged(_ local: URLResourceValues, _ remote: RemoteItem?) -> Bool {
        guard let remote, remote.kind == .file, let size = local.fileSize, Int64(size) == remote.size,
              let localDate = local.contentModificationDate, let remoteDate = remote.modified else { return false }
        // サーバーの日時はアップロードした時刻になる。LIST の日時は分単位なので 2 分の余裕を見る
        return remoteDate >= localDate.addingTimeInterval(-120)
    }
}
