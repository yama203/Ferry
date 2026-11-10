import CryptoKit
import Foundation

/// SFTP のサーバーが出したホスト鍵（ssh の公開鍵の形式のバイト列）
public struct HostKey: Equatable, Hashable, Sendable {
    public let data: Data

    public init(data: Data) {
        self.data = data
    }

    /// 鍵の種類（"ssh-ed25519"、"ecdsa-sha2-nistp256"、"ssh-rsa" など）。鍵の先頭に名前が入っている
    public var algorithm: String {
        guard data.count >= 4 else { return "不明" }
        let bytes = [UInt8](data)
        let length = Int(bytes[0]) << 24 | Int(bytes[1]) << 16 | Int(bytes[2]) << 8 | Int(bytes[3])
        guard length > 0, length <= 64, bytes.count >= 4 + length,
              let name = String(bytes: bytes[4..<(4 + length)], encoding: .ascii) else { return "不明" }
        return name
    }

    /// `ssh-keygen -l` と同じ表示（"ED25519"・"ECDSA"・"RSA" など）
    public var typeName: String {
        switch algorithm {
        case "ssh-ed25519": "ED25519"
        case "ssh-ed448": "ED448"
        case "ssh-rsa": "RSA"
        case "ssh-dss": "DSA"
        case let name where name.hasPrefix("ecdsa-sha2-"): "ECDSA"
        case let name: name
        }
    }

    /// OpenSSH と同じフィンガープリント（"SHA256:" + SHA-256 の base64、末尾の = は付けない）
    public var fingerprint: String {
        let digest = Data(SHA256.hash(data: data)).base64EncodedString()
        return "SHA256:" + digest.replacingOccurrences(of: "=", with: "")
    }

    /// known_hosts の鍵の欄（base64）
    var base64: String { data.base64EncodedString() }
}

/// SFTP のホスト鍵を記録するファイル（OpenSSH の known_hosts と同じ形式。libcurl / libssh2 が読み書きする）
public enum KnownHosts {
    /// known_hosts でのホストの書き方。ポートが 22 以外なら `[host]:port`
    public static func hostPattern(host: String, port: Int) -> String {
        port == 22 ? host : "[\(host)]:\(port)"
    }

    /// そのホストの行を消した内容を返す。libcurl はポートが 22 以外でもポートを付けずにホスト名だけで書くので
    /// （libcurl 8.20 で確かめた）、`host` と `[host]:port` の両方を消す。同じホストの別のポートの `[host]:port` は残す。
    /// 1 行に複数の名前が並んでいる（`a,b ssh-ed25519 …`）ときは、そのホストの名前だけを除く。
    /// ハッシュ化された行（`|1|…`）はどのホストか分からないので残す。コメント・空行・`@` で始まる行・他のホストの行も残す。
    /// keepingKey … この鍵の行は消さない（新しい鍵を記録した後に、古い鍵の行だけを片付けるとき）
    public static func removingEntries(for host: String, port: Int, from contents: String,
                                       keepingKey: HostKey? = nil) -> String {
        let names = Set([host, hostPattern(host: host, port: port)].map { $0.lowercased() })
        var lines: [String] = []
        for line in contents.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#"), !trimmed.hasPrefix("@"), !trimmed.hasPrefix("|") else {
                lines.append(line)
                continue
            }
            let fields = trimmed.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true)
            guard fields.count == 2 else {
                lines.append(line)
                continue
            }
            let hosts = fields[0].split(separator: ",").map(String.init)
            let remaining = hosts.filter { !names.contains($0.lowercased()) }
            if remaining.count == hosts.count {
                lines.append(line)
                continue
            }
            if let keepingKey, keyField(of: String(fields[1])) == keepingKey.base64 {
                lines.append(line)
                continue
            }
            if !remaining.isEmpty {
                lines.append(remaining.joined(separator: ",") + " " + fields[1])
            }
        }
        return lines.joined(separator: "\n")
    }

    /// known_hosts のファイルから、そのホストの行を消す。ファイルが無ければ何もしない
    public static func removeEntries(for host: String, port: Int, in url: URL = RemoteClient.knownHostsURL,
                                     keepingKey: HostKey? = nil) throws {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let contents = try String(contentsOf: url, encoding: .utf8)
        let updated = removingEntries(for: host, port: port, from: contents, keepingKey: keepingKey)
        guard updated != contents else { return }
        try Data(updated.utf8).write(to: url, options: .atomic)
    }

    /// 「鍵の種類 base64 コメント」から base64 の部分
    private static func keyField(of rest: String) -> String? {
        let parts = rest.split(separator: " ", omittingEmptySubsequences: true)
        return parts.count >= 2 ? String(parts[1]) : nil
    }
}
