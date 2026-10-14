import FerryKit
import Foundation
import UniformTypeIdentifiers

/// ローカル・リモート共通のファイル表示用データ
struct FileEntry: Identifiable, Hashable, Codable {
    var name: String
    var path: String
    var isDirectory: Bool
    var isSymlink: Bool
    var size: Int64?
    var modified: Date?
    var permissions: String?
    /// Finder のタグ（ローカルのみ）
    var tags: [FinderTag] = []

    var id: String { path }
    var isHidden: Bool { name.hasPrefix(".") }

    // 表の並べ替え用
    var sortSize: Int64 { isDirectory ? -1 : (size ?? 0) }
    var sortDate: Date { modified ?? .distantPast }

    init(remote item: RemoteItem) {
        name = item.name
        path = item.path
        isDirectory = item.isDirectory
        isSymlink = item.kind == .symlink
        size = item.size
        modified = item.modified
        permissions = item.permissions
    }

    init?(local url: URL) {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey]
        guard let values = try? url.resourceValues(forKeys: keys) else { return nil }
        name = url.lastPathComponent
        path = url.path
        isSymlink = values.isSymbolicLink ?? false
        // シンボリックリンクはリンク先がフォルダかどうかで判定
        isDirectory = isSymlink
            ? (try? url.resolvingSymlinksInPath().resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
            : values.isDirectory ?? false
        size = values.fileSize.map(Int64.init)
        modified = values.contentModificationDate
        permissions = nil
        tags = FinderTag.read(atPath: url.path)
    }
}

/// Finder のタグ。color は Finder の色番号（0: なし, 1: グレイ, 2: グリーン, 3: パープル, 4: ブルー, 5: イエロー, 6: レッド, 7: オレンジ）
struct FinderTag: Hashable, Codable {
    var name: String
    var color: Int

    /// 拡張属性に保存されている「名前\n色番号」の一覧を読む（色はカスタムタグも含めてここに入っている）
    static func read(atPath path: String) -> [FinderTag] {
        let attribute = "com.apple.metadata:_kMDItemUserTags"
        let size = getxattr(path, attribute, nil, 0, 0, XATTR_NOFOLLOW)
        guard size > 0 else { return [] }
        var data = Data(count: size)
        let read = data.withUnsafeMutableBytes { getxattr(path, attribute, $0.baseAddress, size, 0, XATTR_NOFOLLOW) }
        guard read == size,
              let list = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String] else { return [] }
        return list.map { item in
            let parts = item.split(separator: "\n", maxSplits: 1)
            return FinderTag(name: String(parts.first ?? ""), color: parts.count > 1 ? Int(parts[1]) ?? 0 : 0)
        }
    }
}

extension UTType {
    static let ferryRemoteItem = UTType(exportedAs: "com.yama.ferry.remote-item")
}

enum Formatters {
    static func size(_ entry: FileEntry) -> String {
        guard !entry.isDirectory, let size = entry.size else { return "--" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    static let date: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        formatter.doesRelativeDateFormatting = true
        return formatter
    }()

    static func date(_ date: Date?) -> String {
        date.map(Self.date.string(from:)) ?? "--"
    }
}
