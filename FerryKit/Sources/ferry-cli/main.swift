import Foundation
import FerryKit

// 接続部分の動作確認用ツール。
//   ferry-cli ls   sftp://user@host[:port] [/path]
//   ferry-cli get  ftps://user@host:10021 /remote/file ./local
//   ferry-cli put  ftp://user@host ./local /remote/file
//   ferry-cli mkdir|rm|rmdir <url> /path
//   ferry-cli mv   <url> /from /to
// ftps:// は Explicit。Implicit は --implicit を付ける。
// オプション: --implicit --insecure（証明書を検証しない） --active --key <秘密鍵> --verbose
// パスワードは環境変数 FERRY_PASSWORD か、プロンプトで入力する。

let usage = """
使い方: ferry-cli <ls|pwd|get|put|mkdir|rm|rmdir|mv> <ftp|ftps|sftp>://user@host[:port] [引数...]
オプション: --implicit --insecure --active --key <秘密鍵> --verbose
"""

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

func formatSize(_ size: Int64?) -> String {
    guard let size else { return "-" }
    return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
}

@Sendable func printProgress(_ done: Int64, _ total: Int64) {
    let percent = total > 0 ? Int(Double(done) / Double(total) * 100) : 0
    FileHandle.standardError.write(Data("\r\(formatSize(done)) / \(formatSize(total))  \(percent)%   ".utf8))
}

var args = Array(CommandLine.arguments.dropFirst())
var implicit = false, insecure = false, active = false, verbose = false
var keyPath: String?

var positional: [String] = []
var iterator = args.makeIterator()
while let arg = iterator.next() {
    switch arg {
    case "--implicit": implicit = true
    case "--insecure": insecure = true
    case "--active": active = true
    case "--verbose": verbose = true
    case "--key":
        guard let value = iterator.next() else { fail("--key には秘密鍵のパスを指定してください") }
        keyPath = value
    default: positional.append(arg)
    }
}
args = positional

guard args.count >= 2 else { fail(usage) }
let command = args[0]
guard let components = URLComponents(string: args[1]), let scheme = components.scheme?.lowercased(),
      let host = components.host else { fail("URL を解釈できません: \(args[1])") }

let serverProtocol: ServerProtocol
switch scheme {
case "ftp": serverProtocol = .ftp
case "ftps": serverProtocol = implicit ? .ftpsImplicit : .ftps
case "sftp": serverProtocol = .sftp
default: fail("対応していないプロトコルです: \(scheme)")
}

let server = ServerConfig(
    name: host,
    serverProtocol: serverProtocol,
    host: host,
    port: components.port,
    username: components.user ?? "anonymous",
    passiveMode: !active,
    verifyCertificate: !insecure,
    privateKeyPath: keyPath
)

let password: String = ProcessInfo.processInfo.environment["FERRY_PASSWORD"]
    ?? String(cString: getpass(keyPath == nil ? "パスワード: " : "鍵のパスフレーズ: "))

// アプリに同梱したルート証明書で検証する場合に指定する
RemoteClient.caBundlePath = ProcessInfo.processInfo.environment["FERRY_CA_BUNDLE"]
let client = RemoteClient(server: server, password: password)
client.debugLog = verbose
let rest = Array(args.dropFirst(2))

func argument(_ index: Int, _ name: String) -> String {
    guard index < rest.count else { fail("\(name) を指定してください\n\(usage)") }
    return rest[index]
}

let dateFormatter = DateFormatter()
dateFormatter.dateFormat = "yyyy-MM-dd HH:mm"

do {
    switch command {
    case "pwd":
        print(try await client.homeDirectory())

    case "ls":
        let path: String
        if let first = rest.first {
            path = first
        } else if !components.path.isEmpty {
            path = components.path
        } else {
            path = try await client.homeDirectory()
        }
        let items = try await client.list(path)
        print("📂 \(path)  (\(items.count) 件)")
        for item in items.sorted(by: { ($0.isDirectory ? 0 : 1, $0.name) < ($1.isDirectory ? 0 : 1, $1.name) }) {
            let icon = switch item.kind { case .directory: "📁"; case .symlink: "🔗"; case .file: "  " }
            let date = item.modified.map(dateFormatter.string(from:)) ?? "-"
            let name = item.symlinkTarget.map { "\(item.name) -> \($0)" } ?? item.name
            print("\(icon) \(item.permissions ?? "---------")  \(formatSize(item.size).padding(toLength: 10, withPad: " ", startingAt: 0))  \(date)  \(name)")
        }

    case "get":
        let remote = argument(0, "リモートのパス")
        let local = URL(fileURLWithPath: rest.count > 1 ? rest[1] : RemotePath.name(of: remote))
        try await client.download(remote, to: local, progress: printProgress)
        print("\n✅ ダウンロード完了: \(local.path)")

    case "put":
        let local = URL(fileURLWithPath: argument(0, "ローカルのファイル"))
        let remote = argument(1, "アップロード先のパス")
        try await client.upload(local, to: remote, progress: printProgress)
        print("\n✅ アップロード完了: \(remote)")

    case "mkdir":
        try await client.makeDirectory(argument(0, "フォルダのパス"))
        print("✅ 作成しました")

    case "rm":
        try await client.deleteFile(argument(0, "ファイルのパス"))
        print("✅ 削除しました")

    case "rmdir":
        try await client.removeDirectory(argument(0, "フォルダのパス"))
        print("✅ 削除しました")

    case "mv":
        try await client.rename(argument(0, "元のパス"), to: argument(1, "新しいパス"))
        print("✅ 名前を変更しました")

    default:
        fail(usage)
    }
} catch {
    fail("\n❌ \(error.localizedDescription)")
}
