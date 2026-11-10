import Foundation

/// ファイル一覧の名前での絞り込み（サイドバーの接続先の検索も同じ比べ方）。空白区切りの語をすべて含む名前だけを残す。
/// 大文字小文字・アクセントに加えて全角半角も区別しない
/// （localizedStandardContains だけでは「ＩＭＧ」と「img」、「ｶﾅ」と「カナ」が別物になる）。
public struct NameFilter: Sendable, Equatable {
    public let terms: [String]

    public init(_ query: String) {
        // 全角の空白も区切りとして扱う（isWhitespace に含まれる）
        terms = query.split(whereSeparator: \.isWhitespace).map(String.init)
    }

    /// 空・空白だけなら絞り込まない
    public var isActive: Bool { !terms.isEmpty }

    public func matches(_ name: String) -> Bool {
        terms.allSatisfy { term in
            name.range(of: term, options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive],
                       locale: .current) != nil
        }
    }
}
