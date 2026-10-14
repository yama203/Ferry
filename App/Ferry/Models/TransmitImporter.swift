import FerryKit
import Foundation

/// Transmit の「サーバーを書き出す」で作った .transmitservers（JSON）を読む。
/// パスワードは含まれないので、接続時に入力するかキーチェーンから探す。
enum TransmitImporter {
    struct Result {
        struct Folder: Identifiable {
            let id = UUID()
            var name: String?
            var servers: [ServerConfig]
        }

        var folders: [Folder]
        /// 対応していないプロトコル（S3 など）で読み込めなかった数
        var unsupportedCount: Int

        var serverCount: Int { folders.reduce(0) { $0 + $1.servers.count } }
    }

    static func read(_ url: URL) throws -> Result {
        let data = try Data(contentsOf: url)
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw FerryError(kind: .other, message: "Transmit の書き出しファイルではありません", detail: nil)
        }
        var result = Result(folders: [], unsupportedCount: 0)
        var ungrouped: [ServerConfig] = []

        for element in archivedArray(root) {
            if let connections = element["connections"] as? [[String: Any]] {
                var folder = Result.Folder(name: element["name"] as? String, servers: [])
                for connection in connections {
                    if let server = server(from: connection) {
                        folder.servers.append(server)
                    } else {
                        result.unsupportedCount += 1
                    }
                }
                result.folders.append(folder)
            } else if element["address"] != nil {
                if let server = server(from: element) {
                    ungrouped.append(server)
                } else {
                    result.unsupportedCount += 1
                }
            }
        }
        if !ungrouped.isEmpty {
            result.folders.append(Result.Folder(name: nil, servers: ungrouped))
        }
        return result
    }

    /// {"__NSArrayM": {"NS.object.0": …, "NS.object.1": …}} を順番どおりの配列にする
    private static func archivedArray(_ root: [String: Any]) -> [[String: Any]] {
        let container = (root["__NSArrayM"] ?? root["__NSArrayI"] ?? root) as? [String: Any] ?? [:]
        return container
            .compactMap { key, value -> (Int, [String: Any])? in
                guard key.hasPrefix("NS.object."), let index = Int(key.dropFirst("NS.object.".count)),
                      let dictionary = value as? [String: Any] else { return nil }
                return (index, dictionary)
            }
            .sorted { $0.0 < $1.0 }
            .map(\.1)
    }

    private static func server(from connection: [String: Any]) -> ServerConfig? {
        guard let host = connection["address"] as? String, !host.isEmpty,
              let code = connection["protocol"] as? Int,
              let serverProtocol = serverProtocol(fourCharCode: code) else { return nil }
        let port = connection["port"] as? Int ?? 0
        return ServerConfig(
            name: (connection["name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? host,
            serverProtocol: serverProtocol,
            host: host,
            port: port > 0 ? port : nil,
            username: connection["username"] as? String ?? "",
            initialPath: nonEmpty(connection["remotePath"]),
            passiveMode: connection["usePassiveMode"] as? Bool ?? true,
            localPath: nonEmpty(connection["localPath"])
        )
    }

    /// Transmit はプロトコルを 4 文字コード（'FTP ' など）の数値で保存している
    private static func serverProtocol(fourCharCode code: Int) -> ServerProtocol? {
        let bytes = [24, 16, 8, 0].map { UInt8((code >> $0) & 0xFF) }
        switch String(decoding: bytes, as: UTF8.self) {
        case "FTP ": return .ftp
        case "FTPS": return .ftps
        case "FTPI": return .ftpsImplicit
        case "SFTP": return .sftp
        default: return nil
        }
    }

    private static func nonEmpty(_ value: Any?) -> String? {
        guard let string = value as? String, !string.isEmpty else { return nil }
        return string
    }
}
