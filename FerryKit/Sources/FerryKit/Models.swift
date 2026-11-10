import Foundation

public enum ServerProtocol: String, Codable, CaseIterable, Sendable {
    case ftp
    /// AUTH TLS で途中から暗号化する方式（一般的な FTPS）
    case ftps
    /// 接続直後から TLS の方式（通常ポート 990）
    case ftpsImplicit
    case sftp

    public var displayName: String {
        switch self {
        case .ftp: "FTP"
        case .ftps: "FTPS（Explicit）"
        case .ftpsImplicit: "FTPS（Implicit）"
        case .sftp: "SFTP"
        }
    }

    public var defaultPort: Int {
        switch self {
        case .ftp, .ftps: 21
        case .ftpsImplicit: 990
        case .sftp: 22
        }
    }

    var urlScheme: String {
        switch self {
        case .ftp, .ftps: "ftp"
        case .ftpsImplicit: "ftps"
        case .sftp: "sftp"
        }
    }

    var isFTPFamily: Bool { self != .sftp }
    var usesTLS: Bool { self == .ftps || self == .ftpsImplicit }
}

/// 接続先（ブックマーク）。パスワードはここに含めず Keychain に保存する。
public struct ServerConfig: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var serverProtocol: ServerProtocol
    public var host: String
    public var port: Int?
    public var username: String
    /// 接続直後に開くリモートのフォルダ。nil ならログイン時のフォルダ。
    public var initialPath: String?
    /// FTP/FTPS のパッシブモード（既定でオン）
    public var passiveMode: Bool
    /// FTPS のサーバー証明書を検証するか。共用サーバーで証明書のホスト名が合わない場合にオフにする。
    public var verifyCertificate: Bool
    /// SFTP の秘密鍵ファイル。指定時はパスワードを鍵のパスフレーズとして使う。
    public var privateKeyPath: String?
    /// 接続直後に開くローカルのフォルダ
    public var localPath: String?
    /// サイドバーのフォルダ。nil ならフォルダに入れない。
    public var folderID: UUID?
    /// 左右のペインで開く場所の組み合わせ
    public var bookmarks: [PaneBookmark]?
    /// 接続時に開くブックマーク
    public var startBookmarkID: UUID?

    public var startBookmark: PaneBookmark? {
        bookmarks?.first { $0.id == startBookmarkID }
    }

    public init(
        id: UUID = UUID(),
        name: String,
        serverProtocol: ServerProtocol,
        host: String,
        port: Int? = nil,
        username: String,
        initialPath: String? = nil,
        passiveMode: Bool = true,
        verifyCertificate: Bool = true,
        privateKeyPath: String? = nil,
        localPath: String? = nil,
        folderID: UUID? = nil
    ) {
        self.id = id
        self.name = name
        self.serverProtocol = serverProtocol
        self.host = host
        self.port = port
        self.username = username
        self.initialPath = initialPath
        self.passiveMode = passiveMode
        self.verifyCertificate = verifyCertificate
        self.privateKeyPath = privateKeyPath
        self.localPath = localPath
        self.folderID = folderID
    }

    public var effectivePort: Int { port ?? serverProtocol.defaultPort }
}

/// 「この Mac のフォルダ」と「サーバーのフォルダ」の組み合わせ
public struct PaneBookmark: Codable, Identifiable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var localPath: String
    public var remotePath: String

    public init(id: UUID = UUID(), name: String, localPath: String, remotePath: String) {
        self.id = id
        self.name = name
        self.localPath = localPath
        self.remotePath = remotePath
    }
}

public struct RemoteItem: Identifiable, Hashable, Sendable {
    public enum Kind: Sendable {
        case file, directory, symlink
    }

    public var name: String
    public var path: String
    public var kind: Kind
    public var size: Int64?
    public var modified: Date?
    /// 更新日時の粗さ（秒）。一覧の日時は切り捨てた値なので、本当の日時は modified から modified + これ未満の間にある。
    /// MLSD は 1、ls -l 形式の時刻付き（`Oct 14 10:00`）は 60、日付だけ（`Jan  2  2020`）は 86400
    public var modifiedPrecision: TimeInterval
    /// "rwxr-xr-x" 形式
    public var permissions: String?
    public var symlinkTarget: String?

    public var id: String { path }
    public var isDirectory: Bool { kind == .directory }

    public init(name: String, path: String, kind: Kind, size: Int64? = nil, modified: Date? = nil,
                modifiedPrecision: TimeInterval = 1, permissions: String? = nil, symlinkTarget: String? = nil) {
        self.name = name
        self.path = path
        self.kind = kind
        self.size = size
        self.modified = modified
        self.modifiedPrecision = modifiedPrecision
        self.permissions = permissions
        self.symlinkTarget = symlinkTarget
    }
}

/// 転送元と転送先の更新日時の比較（同期・「新しい場合のみ上書き」で使う）
public enum ModifiedDate {
    /// これ以下のずれは同じとみなす
    public static let tolerance: TimeInterval = 2

    /// 転送元の方が新しいか。destinationPrecision は転送先の日時の粗さ（サーバーの一覧なら RemoteItem.modifiedPrecision、ローカルなら 0）。
    /// 転送先がサーバーのとき、一覧の値は本当の日時を切り捨てたものなので、粗さの分だけ遅い方に寄せて比べる
    /// （更新日時をローカルに揃えてアップロードしたファイルが、毎回「ローカルの方が新しい」と見えないように）。
    /// 転送元がサーバーのときは、切り捨てた値は本当の日時以下なので、そのまま比べても誤って新しいとはみなさない。
    public static func isSourceNewer(_ source: Date, than destination: Date, destinationPrecision: TimeInterval) -> Bool {
        source.timeIntervalSince(destination) > destinationPrecision + tolerance
    }
}

public enum RemotePath {
    public static func join(_ directory: String, _ name: String) -> String {
        directory.hasSuffix("/") ? directory + name : directory + "/" + name
    }

    public static func parent(of path: String) -> String {
        let trimmed = path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
        guard let slash = trimmed.lastIndex(of: "/") else { return "/" }
        let parent = String(trimmed[..<slash])
        return parent.isEmpty ? "/" : parent
    }

    public static func name(of path: String) -> String {
        path.split(separator: "/").last.map(String.init) ?? "/"
    }

    /// 転送の途中のファイルに付ける名前の終わり（ダウンロードは「名前.ferrypart」、安全なアップロードは「.名前.ferrypart」）
    public static let partialSuffix = ".ferrypart"

    /// 安全なアップロードで、置き換える前に送る一時的な名前（同じフォルダに置く）
    public static func temporaryName(for name: String) -> String {
        "." + name + partialSuffix
    }

    /// 転送の途中で残ったファイルか（同期で運ばない）
    public static func isPartialFile(_ name: String) -> Bool {
        name.hasSuffix(partialSuffix)
    }
}

public enum FilePermissions {
    /// "rwxr-xr-x" 形式を "755" のような 8 進数の文字列にする。setuid・setgid・sticky（s / S / t / T）があれば
    /// "4755" のように 4 桁にする。形式が違えば nil
    public static func octal(_ permissions: String) -> String? {
        let chars = Array(permissions)
        guard chars.count == 9 else { return nil }
        var special = 0
        var digits = ""
        for group in 0..<3 {
            let (r, w, x) = (chars[group * 3], chars[group * 3 + 1], chars[group * 3 + 2])
            guard r == "r" || r == "-", w == "w" || w == "-" else { return nil }
            let specialChar: Set<Character> = group == 2 ? ["t", "T"] : ["s", "S"]
            guard x == "x" || x == "-" || specialChar.contains(x) else { return nil }
            if specialChar.contains(x) {
                special |= 4 >> group
            }
            digits += String((r == "r" ? 4 : 0) + (w == "w" ? 2 : 0) + (x == "x" || x == "s" || x == "t" ? 1 : 0))
        }
        return special == 0 ? digits : String(special) + digits
    }
}

/// サーバーのファイルの「サイズと更新日時」（一覧に出た値）。エディタで開いた後に、他の人がサーバー側で変えていないかを調べる
public struct RemoteFileStamp: Equatable, Sendable {
    public enum Change: Equatable, Sendable {
        case unchanged, modified, deleted
    }

    public var size: Int64?
    public var modified: Date?

    public init(size: Int64?, modified: Date?) {
        self.size = size
        self.modified = modified
    }

    public init(_ item: RemoteItem) {
        self.init(size: item.size, modified: item.modified)
    }

    /// そのファイルのあるフォルダの今の一覧と比べる。どちらも同じ形式の一覧の値なので、粗さは考えずにそのまま比べる
    public func change(of path: String, in listing: [RemoteItem]) -> Change {
        let name = RemotePath.name(of: path)
        guard let current = listing.first(where: { $0.name == name }), !current.isDirectory else { return .deleted }
        return RemoteFileStamp(current) == self ? .unchanged : .modified
    }
}
