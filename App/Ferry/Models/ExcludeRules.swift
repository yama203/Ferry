import Darwin
import Foundation

/// フォルダを転送・同期するときに含めないファイル名のパターン（* や ? が使える）。
/// 明示的に選んだ項目そのものには適用せず、フォルダの中身にだけ適用する。
struct ExcludeRules: Codable, Hashable {
    var patterns: [String]

    static let defaults = ExcludeRules(patterns: [
        ".git", ".svn", ".hg",
        "node_modules",
        ".DS_Store", "._*", "Thumbs.db", "desktop.ini",
        ".idea", ".sass-cache", "__pycache__",
        "*.swp", "*~",
        "*.ferrypart",
    ])

    func excludes(_ name: String) -> Bool {
        patterns.contains { pattern in
            !pattern.isEmpty && fnmatch(pattern, name, 0) == 0
        }
    }
}
