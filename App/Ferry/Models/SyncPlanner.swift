import FerryKit
import Foundation

/// ローカルとリモートのフォルダを比べて、転送・削除する項目を決める
enum SyncPlanner {
    struct Entry {
        var isDirectory: Bool
        var size: Int64?
        var modified: Date?
    }

    struct Change: Identifiable {
        enum Reason { case new, changed }
        let relativePath: String
        let reason: Reason
        let size: Int64
        var id: String { relativePath }
    }

    struct Deletion: Identifiable {
        let relativePath: String
        let isDirectory: Bool
        var id: String { relativePath }
    }

    struct Plan {
        var changes: [Change] = []
        var deletions: [Deletion] = []
        var totalBytes: Int64 { changes.reduce(0) { $0 + $1.size } }
        var isEmpty: Bool { changes.isEmpty && deletions.isEmpty }
    }

    // MARK: - 走査（キーはルートからの相対パス）

    static func scanLocal(_ root: URL, rules: ExcludeRules) -> [String: Entry] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        var result: [String: Entry] = [:]
        guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: keys) else {
            return result
        }
        let rootPath = root.standardizedFileURL.path
        while let url = enumerator.nextObject() as? URL {
            if rules.excludes(url.lastPathComponent) {
                enumerator.skipDescendants()
                continue
            }
            guard let values = try? url.resourceValues(forKeys: Set(keys)) else { continue }
            let relative = String(url.standardizedFileURL.path.dropFirst(rootPath.count + 1))
            result[relative] = Entry(isDirectory: values.isDirectory ?? false,
                                     size: values.fileSize.map(Int64.init),
                                     modified: values.contentModificationDate)
        }
        return result
    }

    static func scanRemote(_ root: String, client: RemoteClient, rules: ExcludeRules,
                           onFolder: @MainActor (String) -> Void) async throws -> [String: Entry] {
        var result: [String: Entry] = [:]
        var pending = [""]
        while let relativeDirectory = pending.popLast() {
            let directory = relativeDirectory.isEmpty ? root : RemotePath.join(root, relativeDirectory)
            await onFolder(directory)
            for item in try await client.list(directory) where !rules.excludes(item.name) {
                let relative = relativeDirectory.isEmpty ? item.name : relativeDirectory + "/" + item.name
                result[relative] = Entry(isDirectory: item.isDirectory, size: item.size, modified: item.modified)
                if item.isDirectory {
                    pending.append(relative)
                }
            }
        }
        return result
    }

    // MARK: - 比較

    /// サイズが違うか、転送元の方が新しいファイルを転送する
    static func plan(source: [String: Entry], destination: [String: Entry], deleteExtraneous: Bool) -> Plan {
        var plan = Plan()
        for (path, entry) in source where !entry.isDirectory {
            guard let existing = destination[path], !existing.isDirectory else {
                plan.changes.append(Change(relativePath: path, reason: .new, size: entry.size ?? 0))
                continue
            }
            let sizeChanged = entry.size != nil && existing.size != nil && entry.size != existing.size
            let isNewer: Bool
            if let sourceDate = entry.modified, let destinationDate = existing.modified {
                isNewer = sourceDate.timeIntervalSince(destinationDate) > 2
            } else {
                isNewer = false
            }
            if sizeChanged || isNewer {
                plan.changes.append(Change(relativePath: path, reason: .changed, size: entry.size ?? 0))
            }
        }

        if deleteExtraneous {
            // 転送元に無いものを削除。フォルダごと消すものは中身を個別に挙げない。
            let extraneous = destination.keys.filter { source[$0] == nil }.sorted()
            var removedFolders: [String] = []
            for path in extraneous where !removedFolders.contains(where: { path.hasPrefix($0 + "/") }) {
                let isDirectory = destination[path]?.isDirectory ?? false
                plan.deletions.append(Deletion(relativePath: path, isDirectory: isDirectory))
                if isDirectory {
                    removedFolders.append(path)
                }
            }
        }

        plan.changes.sort { $0.relativePath.localizedStandardCompare($1.relativePath) == .orderedAscending }
        return plan
    }
}
