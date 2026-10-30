import Foundation
import FerryKit

/// フォルダの中身をまるごとローカルにダウンロードする（get -r）。
/// 手元に同じサイズ・同じ更新日時のファイルがあれば飛ばすので、途中で止まってもやり直せる。
/// 1 件失敗しても残りは続け、最後にまとめて報告する。
struct FolderDownload {
    private(set) var downloaded = 0
    private(set) var skipped = 0
    private(set) var failures: [String] = []

    mutating func run(_ client: RemoteClient, _ remote: String, to local: URL) async throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: local, withIntermediateDirectories: true)
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
                try await run(client, item.path, to: target)
            case .symlink:
                // リンク先がフォルダかどうか一覧からは分からないので、リンクは取らずに知らせる
                print("🔗 飛ばしました（リンク）: \(item.path) -> \(item.symlinkTarget ?? "?")")
            case .file:
                if isUnchanged(target, item) {
                    skipped += 1
                    continue
                }
                do {
                    try await client.download(item.path, to: target, expectedSize: item.size)
                    if let modified = item.modified {
                        try? fileManager.setAttributes([.modificationDate: modified], ofItemAtPath: target.path)
                    }
                    downloaded += 1
                    print("⬇️  \(item.path)")
                } catch {
                    failures.append("\(item.path): \(error.localizedDescription)")
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
