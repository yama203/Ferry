import Foundation
import FerryKit

// 接続部分の動作確認用ツール。
//   ferry-cli ls   sftp://user@host[:port] [/path]
//   ferry-cli get  ftps://user@host:10021 /remote/file ./local
//   ferry-cli get -r <url> /remote/folder ./local   … フォルダごと（同じサイズ・日時のファイルは飛ばす）
//                                                    --parallel <数> で同時に使う接続の数（既定は 4）
//   ferry-cli put  ftp://user@host ./local /remote/file
//   ferry-cli put -r <url> ./local/folder /remote/folder … フォルダごと（同じサイズでサーバーの方が新しいファイルは飛ばす）
//   ferry-cli mkdir|rm|rmdir <url> /path
//   ferry-cli mv   <url> /from /to
//   ferry-cli ls --server <接続先の名前> [/path]   … アプリに保存した接続先とパスワードを使う
//   ferry-cli servers                              … アプリに保存した接続先の一覧
// ftps:// は Explicit。Implicit は --implicit を付ける。
// オプション: --implicit --insecure（証明書を検証しない） --active --key <秘密鍵> --verbose
// パスワードは環境変数 FERRY_PASSWORD か、プロンプトで入力する（--server のときは保存したものを使う）。

let usage = """
使い方: ferry-cli <ls|pwd|get|put|mkdir|rm|rmdir|mv> <ftp|ftps|sftp>://user@host[:port] [引数...]
　　　 ferry-cli <ls|pwd|get|put|mkdir|rm|rmdir|mv> --server <接続先の名前> [引数...]
　　　 ferry-cli servers
オプション: --implicit --insecure --active --key <秘密鍵> --verbose -r（get・put でフォルダごと） --parallel <接続数>
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
var implicit = false, insecure = false, active = false, verbose = false, recursive = false
var keyPath: String?
var savedServerName: String?
var parallel = 4

var positional: [String] = []
var iterator = args.makeIterator()
while let arg = iterator.next() {
    switch arg {
    case "--implicit": implicit = true
    case "--insecure": insecure = true
    case "--active": active = true
    case "--verbose": verbose = true
    case "-r", "--recursive": recursive = true
    case "--key":
        guard let value = iterator.next() else { fail("--key には秘密鍵のパスを指定してください") }
        keyPath = value
    case "--parallel":
        guard let value = iterator.next().flatMap(Int.init), value >= 1 else { fail("--parallel には 1 以上の数を指定してください") }
        parallel = value
    case "--server":
        guard let value = iterator.next() else { fail("--server には接続先の名前を指定してください") }
        savedServerName = value
    default: positional.append(arg)
    }
}
args = positional

if args.first == "servers" {
    for server in SavedServers.all().sorted(by: { $0.name.localizedStandardCompare($1.name) == .orderedAscending }) {
        print("\(server.name)\t\(server.serverProtocol.rawValue)://\(server.username)@\(server.host):\(server.effectivePort)")
    }
    exit(0)
}

let server: ServerConfig
let password: String
let rest: [String]
/// URL で指定したパス（ls でパスを省いたときに使う）
var urlPath: String?

if let savedServerName {
    guard !args.isEmpty else { fail(usage) }
    switch SavedServers.find(savedServerName) {
    case .success(let found): server = found
    case .failure(let error): fail(error.localizedDescription)
    }
    guard let saved = SavedServers.password(for: server.id) else {
        fail("「\(server.name)」のパスワードを読めませんでした（アプリで保存しているか、キーチェーンの確認で許可したかを確かめてください）")
    }
    password = saved
    urlPath = server.initialPath
    rest = Array(args.dropFirst(1))
} else {
    guard args.count >= 2 else { fail(usage) }
    guard let components = URLComponents(string: args[1]), let scheme = components.scheme?.lowercased(),
          let host = components.host else { fail("URL を解釈できません: \(args[1])") }

    let serverProtocol: ServerProtocol
    switch scheme {
    case "ftp": serverProtocol = .ftp
    case "ftps": serverProtocol = implicit ? .ftpsImplicit : .ftps
    case "sftp": serverProtocol = .sftp
    default: fail("対応していないプロトコルです: \(scheme)")
    }

    server = ServerConfig(
        name: host,
        serverProtocol: serverProtocol,
        host: host,
        port: components.port,
        username: components.user ?? "anonymous",
        passiveMode: !active,
        verifyCertificate: !insecure,
        privateKeyPath: keyPath
    )

    password = ProcessInfo.processInfo.environment["FERRY_PASSWORD"]
        ?? String(cString: getpass(keyPath == nil ? "パスワード: " : "鍵のパスフレーズ: "))
    urlPath = components.path.isEmpty ? nil : components.path
    rest = Array(args.dropFirst(2))
}
let command = args[0]

// アプリに同梱したルート証明書で検証する場合に指定する
RemoteClient.caBundlePath = ProcessInfo.processInfo.environment["FERRY_CA_BUNDLE"]
let client = RemoteClient(server: server, password: password)
client.debugLog = verbose

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
        } else if let urlPath {
            path = urlPath
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
        if recursive {
            var result = FolderDownload()
            try await result.run(server: server, password: password, verbose: verbose, remote, to: local,
                                 connections: parallel)
            print("✅ ダウンロード完了: \(local.path)（\(result.downloaded) 件ダウンロード、\(result.skipped) 件は変更なし）")
            if !result.failures.isEmpty {
                fail("❌ \(result.failures.count) 件ダウンロードできませんでした:\n" + result.failures.joined(separator: "\n"))
            }
        } else {
            try await client.download(remote, to: local, progress: printProgress)
            print("\n✅ ダウンロード完了: \(local.path)")
        }

    case "put":
        let local = URL(fileURLWithPath: argument(0, "ローカルのファイル"))
        let remote = argument(1, "アップロード先のパス")
        if recursive {
            var result = FolderUpload()
            try await result.run(server: server, password: password, verbose: verbose, local, to: remote,
                                 connections: parallel)
            print("✅ アップロード完了: \(remote)（\(result.uploaded) 件アップロード、\(result.skipped) 件は変更なし）")
            if !result.failures.isEmpty {
                fail("❌ \(result.failures.count) 件アップロードできませんでした:\n" + result.failures.joined(separator: "\n"))
            }
        } else {
            try await client.upload(local, to: remote, progress: printProgress)
            print("\n✅ アップロード完了: \(remote)")
        }

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
