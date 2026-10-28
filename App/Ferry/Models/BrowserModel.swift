import AppKit
import FerryKit
import Foundation
import Observation

/// 片側のペイン（ローカルまたはリモート）の状態
@MainActor @Observable
final class BrowserModel {
    enum Side { case local, remote }
    enum SortKey: String { case name, size, date }

    let side: Side
    /// リモート側の一覧表示・ファイル操作用。転送は TransferQueue が別の接続で行う。
    let client: RemoteClient?

    private(set) var path = ""
    private(set) var entries: [FileEntry] = [] {
        didSet { updateVisibleEntries() }
    }
    private(set) var isLoading = false
    var errorMessage: String?
    var selection = Set<FileEntry.ID>()
    var sortKey = SortKey.name {
        didSet { if sortKey != oldValue { updateVisibleEntries() } }
    }
    var ascending = true {
        didSet { if ascending != oldValue { updateVisibleEntries() } }
    }
    var showHidden: Bool {
        didSet { if showHidden != oldValue { updateVisibleEntries() } }
    }
    /// 表示する一覧（フォルダが先、その中で選択中の並び順）。
    /// 選択を変えるたびに並べ替えると重いので、中身や並び順が変わったときだけ作り直す。
    private(set) var visibleEntries: [FileEntry] = []

    private var history: [String] = []

    init(side: Side, client: RemoteClient? = nil) {
        self.side = side
        self.client = client
        // WordPress などでは .htaccess を触るのでリモートは隠しファイルも表示する
        showHidden = side == .remote
    }

    var canGoBack: Bool { !history.isEmpty }
    var canGoUp: Bool { path != "/" && !path.isEmpty }

    private func updateVisibleEntries() {
        let shown = showHidden ? entries : entries.filter { !$0.isHidden }
        let folders = shown.filter(\.isDirectory).sorted(by: isOrderedBefore)
        let files = shown.filter { !$0.isDirectory }.sorted(by: isOrderedBefore)
        visibleEntries = folders + files
    }

    private func isOrderedBefore(_ a: FileEntry, _ b: FileEntry) -> Bool {
        let result: ComparisonResult
        switch sortKey {
        case .name: result = a.name.localizedStandardCompare(b.name)
        case .size: result = a.sortSize == b.sortSize ? .orderedSame : (a.sortSize < b.sortSize ? .orderedAscending : .orderedDescending)
        case .date: result = a.sortDate.compare(b.sortDate)
        }
        // 同じ値なら名前順
        if result == .orderedSame {
            return a.name.localizedStandardCompare(b.name) == .orderedAscending
        }
        return ascending ? result == .orderedAscending : result == .orderedDescending
    }

    var selectedEntries: [FileEntry] {
        entries.filter { selection.contains($0.id) }
    }

    /// パンくずリスト用
    var pathComponents: [(name: String, path: String)] {
        var result: [(String, String)] = [("/", "/")]
        var current = ""
        for part in path.split(separator: "/") {
            current += "/" + part
            result.append((String(part), current))
        }
        return result
    }

    // MARK: - 移動

    func load(_ newPath: String, recordHistory: Bool = true) async {
        isLoading = true
        defer { isLoading = false }
        do {
            let loaded = try await fetch(newPath)
            if recordHistory, !path.isEmpty, path != newPath {
                history.append(path)
            }
            if path != newPath {
                selection = []
            }
            path = newPath
            entries = loaded
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func refresh() async {
        guard !path.isEmpty else { return }
        await load(path, recordHistory: false)
        // 消えた項目は選択から外す
        selection.formIntersection(entries.map(\.id))
    }

    func goBack() async {
        guard let previous = history.popLast() else { return }
        await load(previous, recordHistory: false)
    }

    func goUp() async {
        await load(RemotePath.parent(of: path))
    }

    func open(_ entry: FileEntry) async {
        if entry.isDirectory || (side == .remote && entry.isSymlink) {
            await load(entry.path)
        } else if side == .local {
            NSWorkspace.shared.open(URL(fileURLWithPath: entry.path))
        }
    }

    private func fetch(_ directory: String) async throws -> [FileEntry] {
        switch side {
        case .remote:
            guard let client else { return [] }
            return try await client.list(directory).map(FileEntry.init(remote:))
        case .local:
            return try await Task.detached {
                try FileManager.default.contentsOfDirectory(
                    at: URL(fileURLWithPath: directory),
                    includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey,
                                                 .contentModificationDateKey]
                ).compactMap(FileEntry.init(local:))
            }.value
        }
    }

    // MARK: - ファイル操作

    func makeFolder(named name: String) async {
        let target = RemotePath.join(path, name)
        await perform {
            if let client = self.client {
                try await client.makeDirectory(target)
            } else {
                try FileManager.default.createDirectory(atPath: target, withIntermediateDirectories: false)
            }
        }
    }

    func rename(_ entry: FileEntry, to name: String) async {
        let target = RemotePath.join(RemotePath.parent(of: entry.path), name)
        await perform {
            if let client = self.client {
                try await client.rename(entry.path, to: target)
            } else {
                try FileManager.default.moveItem(atPath: entry.path, toPath: target)
            }
        }
    }

    /// リモートは完全に削除、ローカルはゴミ箱に入れる
    func delete(_ targets: [FileEntry]) async {
        await perform {
            for entry in targets {
                if let client = self.client {
                    let item = RemoteItem(name: entry.name, path: entry.path,
                                          kind: entry.isDirectory ? .directory : .file)
                    try await client.deleteRecursively(item)
                } else {
                    try FileManager.default.trashItem(at: URL(fileURLWithPath: entry.path), resultingItemURL: nil)
                }
            }
        }
    }

    /// Finder のタグを付ける・外す（ローカルのみ）。標準の色名なら Finder が色を割り当てる。
    func setTag(_ name: String, on targets: [FileEntry], enabled: Bool) async {
        await perform {
            for entry in targets {
                var names = entry.tags.map(\.name).filter { $0 != name }
                if enabled { names.append(name) }
                try (URL(fileURLWithPath: entry.path) as NSURL).setResourceValue(names, forKey: .tagNamesKey)
            }
        }
    }

    func clearTags(on targets: [FileEntry]) async {
        await perform {
            for entry in targets {
                try (URL(fileURLWithPath: entry.path) as NSURL).setResourceValue([String](), forKey: .tagNamesKey)
            }
        }
    }

    func changePermissions(_ targets: [FileEntry], mode: String) async {
        guard let client else { return }
        await perform {
            for entry in targets {
                try await client.changePermissions(entry.path, mode: mode)
            }
        }
    }

    private func perform(_ operation: @escaping () async throws -> Void) async {
        var failure: String?
        do {
            try await operation()
        } catch {
            failure = error.localizedDescription
        }
        await refresh()
        // refresh が成功するとエラー表示が消えるので、操作のエラーは後から設定する
        if let failure {
            errorMessage = failure
        }
    }
}
