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

public struct RemoteItem: Identifiable, Hashable, Sendable {
    public enum Kind: Sendable {
        case file, directory, symlink
    }

    public var name: String
    public var path: String
    public var kind: Kind
    public var size: Int64?
    public var modified: Date?
    /// "rwxr-xr-x" 形式
    public var permissions: String?
    public var symlinkTarget: String?

    public var id: String { path }
    public var isDirectory: Bool { kind == .directory }

    public init(name: String, path: String, kind: Kind, size: Int64? = nil, modified: Date? = nil,
                permissions: String? = nil, symlinkTarget: String? = nil) {
        self.name = name
        self.path = path
        self.kind = kind
        self.size = size
        self.modified = modified
        self.permissions = permissions
        self.symlinkTarget = symlinkTarget
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
}
