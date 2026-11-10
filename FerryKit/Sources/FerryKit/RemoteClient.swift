import CCurl
import Foundation

/// 1 つのサーバーへの接続。操作は内部のキューで 1 つずつ実行され、接続は操作間で再利用される。
/// 並行して転送したい場合はクライアントを複数作る。
public final class RemoteClient: @unchecked Sendable {
    public typealias Progress = @Sendable (_ done: Int64, _ total: Int64) -> Void

    public let server: ServerConfig
    private var password: String?
    private let queue: DispatchQueue
    private var handle: CurlHandle?
    /// MLSD 非対応のサーバーでは LIST に切り替える
    private var supportsMLSD = true
    /// LIST だけでは .htaccess などの隠しファイルを返さないサーバー（vsFTPd など）があるので、
    /// MLSD 非対応のときは LIST -a を使う。受け付けないサーバーでは LIST に戻す
    private var supportsListAll = true

    /// 通信ログを stderr に出す（PASS は伏せ字）
    public var debugLog = false
    /// ユーザーが承認した、変わった後のホスト鍵（SFTP）。この鍵のときだけ known_hosts の記録を置き換えて接続する。
    /// 置き換えたら消す（以後は known_hosts の記録どおりに照合する）
    private var approvedHostKey: HostKey?
    /// FTPS のサーバー証明書の名前を読む（FTPS に切り替えられるかを調べるとき）
    private var readsCertificateNames = false

    /// FTPS の証明書検証に使うルート証明書（アプリに同梱したもの）。nil なら libcurl の既定。
    public static var caBundlePath: String?

    /// SFTP のホスト鍵を記録するファイル
    public static var knownHostsURL: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Ferry/known_hosts")
    }()

    public init(server: ServerConfig, password: String?) {
        self.server = server
        self.password = password
        queue = DispatchQueue(label: "Ferry.RemoteClient.\(server.host)")
    }

    /// パスワードを後から設定する（接続時に入力された場合など）。次の操作から使われる。
    public func setPassword(_ password: String?) {
        queue.async { self.password = password }
    }

    /// 画面で見て承認した、変わった後のホスト鍵。次の接続で、サーバーの鍵がこれと同じときだけ known_hosts の記録を置き換える
    /// （承認した後に別の鍵にすり替わっていたら接続しない）。nil で取り消す
    public func approveHostKey(_ key: HostKey?) {
        queue.async { self.approvedHostKey = key }
    }

    // MARK: - 公開 API

    /// ログイン直後のフォルダ（ホームディレクトリ）
    public func homeDirectory() async throws -> String {
        try await run { handle in
            handle.set(CURLOPT_URL, self.baseURL + "/")
            handle.set(CURLOPT_NOBODY, true)
            try handle.perform()
            return handle.entryPath ?? "/"
        }
    }

    /// ログインして、ホームディレクトリとサーバー証明書の名前（FTPS。CN と DNS 名）を返す
    func homeDirectoryAndCertificateNames() async throws -> (home: String, names: [String]) {
        try await run { handle in
            self.readsCertificateNames = true
            defer { self.readsCertificateNames = false }
            handle.reset()
            self.configure(handle)
            handle.set(CURLOPT_URL, self.baseURL + "/")
            handle.set(CURLOPT_NOBODY, true)
            try handle.perform()
            return (handle.entryPath ?? "/", handle.certificateNames)
        }
    }

    public func list(_ directory: String) async throws -> [RemoteItem] {
        try await run { handle in try self.list(directory, handle: handle) }
    }

    /// modified … サーバー側の更新日時。分かっていればダウンロードしたファイルの更新日時をそれに揃える
    /// （ダウンロードした時刻のままだと、同期や「新しい場合のみ上書き」でローカルの方が新しく見える）
    public func download(_ remotePath: String, to localURL: URL, expectedSize: Int64? = nil, modified: Date? = nil,
                         progress: Progress? = nil) async throws {
        let partURL = localURL.appendingPathExtension("ferrypart")
        let cancel = CancelFlag()
        try await withTaskCancellationHandler {
            try await run { handle in
                FileManager.default.createFile(atPath: partURL.path, contents: nil)
                let output = try FileHandle(forWritingTo: partURL)
                defer { try? output.close() }

                let context = TransferContext(cancel: cancel, progress: progress, expectedTotal: expectedSize ?? 0)
                context.output = output
                handle.set(CURLOPT_URL, self.url(for: remotePath, directory: false))
                let quote = self.attach(context, to: handle, isUpload: false)
                do {
                    try withExtendedLifetime((context, quote)) { try handle.perform() }
                } catch {
                    try? FileManager.default.removeItem(at: partURL)
                    throw error
                }
                if FileManager.default.fileExists(atPath: localURL.path) {
                    _ = try FileManager.default.replaceItemAt(localURL, withItemAt: partURL)
                } else {
                    try FileManager.default.moveItem(at: partURL, to: localURL)
                }
                if let modified {
                    try? FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: localURL.path)
                }
            }
        } onCancel: {
            cancel.cancel()
        }
    }

    /// アップロード先のフォルダが無ければ作成する。終わったらサーバー側の更新日時をローカルのファイルに揃える。
    /// replacingExisting … アップロード先に同名のファイルがあると分かっているとき true。同じフォルダの一時的な名前
    /// （`.<名前>.ferrypart`）へ送ってから置き換える（途中で切れても、書きかけのファイルで元のファイルを壊さない）。
    /// permissions … 置き換える前のファイルの権限（"rw-r--r--" 形式）。置き換えると既定の権限になるので、後で同じ権限に戻す
    public func upload(_ localURL: URL, to remotePath: String, replacingExisting: Bool = false,
                       permissions: String? = nil, progress: Progress? = nil) async throws {
        let cancel = CancelFlag()
        try await withTaskCancellationHandler {
            try await run { handle in
                let attributes = try FileManager.default.attributesOfItem(atPath: localURL.path)
                let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
                let modified = attributes[.modificationDate] as? Date
                func sendDirectly() throws {
                    try self.send(localURL, size: size, to: remotePath, handle: handle, cancel: cancel,
                                  progress: progress, filePermissions: 0o644,
                                  postQuote: modified.map { [self.setModifiedCommand(remotePath, to: $0)] } ?? [])
                }
                guard replacingExisting else { return try sendDirectly() }

                let temporaryPath = RemotePath.join(RemotePath.parent(of: remotePath),
                                                    RemotePath.temporaryName(for: RemotePath.name(of: remotePath)))
                let mode = permissions.flatMap(FilePermissions.octal)
                do {
                    // SFTP は作成時の権限を指定できる。600 の wp-config.php などを一時ファイルの間だけ 644 にしない
                    let filePermissions = mode.flatMap { Int($0, radix: 8) }.map { $0 & 0o777 | 0o600 } ?? 0o644
                    try self.send(localURL, size: size, to: temporaryPath, handle: handle, cancel: cancel,
                                  progress: progress, filePermissions: filePermissions, postQuote: [])
                } catch {
                    // 中止・失敗したら書きかけの一時ファイルを残さない（元のファイルには触れていない）
                    try? self.sendCommands([self.deleteCommand(temporaryPath, ignoringFailure: true)], handle: handle)
                    // フォルダに新しいファイルを作る権限は無いが、既存のファイルは書き換えられるサーバーがある。
                    // 権限エラーは送り始める前に出るので、今までどおり直接上書きする。途中で切れたなどの失敗では直接送らない
                    guard !cancel.isCancelled, (error as? FerryError)?.kind == .permissionDenied else { throw error }
                    return try sendDirectly()
                }
                try self.replace(remotePath, with: temporaryPath, size: size, handle: handle)

                // 権限と更新日時は置き換えた後の名前に付ける。どちらも失敗は無視する
                var after: [String] = []
                if let mode {
                    after.append(self.chmodCommand(remotePath, mode: mode, ignoringFailure: true))
                }
                if let modified {
                    after.append(self.setModifiedCommand(remotePath, to: modified))
                }
                if !after.isEmpty {
                    try? self.sendCommands(after, handle: handle)
                }
            }
        } onCancel: {
            cancel.cancel()
        }
    }

    public func makeDirectory(_ path: String) async throws {
        try await execute(server.serverProtocol.isFTPFamily ? ["MKD \(path)"] : ["mkdir \(sftpQuoted(path))"])
    }

    public func deleteFile(_ path: String) async throws {
        try await execute([deleteCommand(path, ignoringFailure: false)])
    }

    /// 空のフォルダを削除する。中身ごと消す場合は deleteRecursively を使う。
    public func removeDirectory(_ path: String) async throws {
        try await execute(server.serverProtocol.isFTPFamily ? ["RMD \(path)"] : ["rmdir \(sftpQuoted(path))"])
    }

    public func deleteRecursively(_ item: RemoteItem) async throws {
        guard item.isDirectory else { return try await deleteFile(item.path) }
        for child in try await list(item.path) {
            try await deleteRecursively(child)
        }
        try await removeDirectory(item.path)
    }

    public func rename(_ from: String, to: String) async throws {
        try await execute(renameCommands(from, to: to))
    }

    /// mode は "755" のような 8 進数の文字列
    public func changePermissions(_ path: String, mode: String) async throws {
        try await execute([chmodCommand(path, mode: mode, ignoringFailure: false)])
    }

    // MARK: - 内部処理

    private func run<T>(_ body: @escaping (CurlHandle) throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let handle = try self.handle ?? CurlHandle()
                    self.handle = handle
                    handle.reset()
                    self.configure(handle)
                    defer { self.forgetApprovedHostKeyIfRecorded(handle) }
                    continuation.resume(returning: try body(handle))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    /// 承認した鍵が known_hosts に記録されたら、承認を消す（キューの中から呼ぶ）
    private func forgetApprovedHostKeyIfRecorded(_ handle: CurlHandle) {
        guard handle.takeAcceptedApprovedKey(), approvedHostKey != nil else { return }
        approvedHostKey = nil
    }

    /// 一覧を取る（キューの中から、設定を初期化したハンドルで呼ぶ）。MLSD・LIST -a に対応していなければ LIST に切り替える
    private func list(_ directory: String, handle: CurlHandle) throws -> [RemoteItem] {
        if server.serverProtocol.isFTPFamily, supportsMLSD {
            do {
                return ListingParser.parseMLSD(try fetchListing(handle, directory, command: "MLSD"), directory: directory)
            } catch let error as FerryError where error.kind == .other {
                supportsMLSD = false
                handle.reset()
                configure(handle)
            }
        }
        if server.serverProtocol.isFTPFamily, supportsListAll {
            do {
                let items = ListingParser.parseUnixListing(
                    try fetchListing(handle, directory, command: "LIST -a"), directory: directory)
                // -a をパスとして解釈して空を返すサーバーもあるので、空のときは LIST で取り直す
                if !items.isEmpty { return items }
                handle.reset()
                configure(handle)
            } catch let error as FerryError where error.kind == .other {
                supportsListAll = false
                handle.reset()
                configure(handle)
            }
        }
        return ListingParser.parseUnixListing(try fetchListing(handle, directory, command: nil), directory: directory)
    }

    /// 1 ファイルを送る（キューの中から呼ぶ）。filePermissions は SFTP で新しく作るファイルの権限
    private func send(_ localURL: URL, size: Int64, to remotePath: String, handle: CurlHandle, cancel: CancelFlag,
                      progress: Progress?, filePermissions: Int, postQuote: [String]) throws {
        // 前のコマンドの設定（解放済みのコマンドの一覧）が残らないよう初期化する
        handle.reset()
        configure(handle)
        let input = try FileHandle(forReadingFrom: localURL)
        defer { try? input.close() }

        let context = TransferContext(cancel: cancel, progress: progress, expectedTotal: size)
        context.input = input
        handle.set(CURLOPT_URL, url(for: remotePath, directory: false))
        handle.set(CURLOPT_UPLOAD, true)
        handle.set(CURLOPT_INFILESIZE_LARGE, offset: curl_off_t(size))
        handle.set(CURLOPT_FTP_CREATE_MISSING_DIRS, Int(CURLFTP_CREATE_DIR_RETRY))
        // フルパスで 1 回だけ CWD すると、無いフォルダの MKD もフルパスで 1 回しか送らず、
        // 2 段以上の新しいフォルダを作れない。アップロードは階層ごとに CWD する
        handle.set(CURLOPT_FTP_FILEMETHOD, CURLFTPMETHOD_MULTICWD)
        handle.set(CURLOPT_NEW_FILE_PERMS, filePermissions)
        handle.set(CURLOPT_NEW_DIRECTORY_PERMS, 0o755)
        let quote = attach(context, to: handle, isUpload: true)
        let post = postQuote.isEmpty ? nil : CurlStringList(postQuote)
        if let post {
            handle.set(CURLOPT_POSTQUOTE, pointer: UnsafeMutableRawPointer(post.raw))
        }
        try withExtendedLifetime((context, quote, post)) { try handle.perform() }
    }

    /// 一時ファイルを元の名前に置き換える。置き換え先があると名前の変更が失敗するサーバー（OpenSSH の SFTP、IIS の FTP など）では、
    /// 一時ファイルが全部届いていることを一覧で確かめてから元のファイルを削除し、もう一度名前を変える。
    /// 元のファイルを消す前に失敗したら一時ファイルを消す。消した後に失敗したら、アップロードした内容は一時ファイルにしか無いので残す
    private func replace(_ remotePath: String, with temporaryPath: String, size: Int64, handle: CurlHandle) throws {
        let renameError: Error
        do {
            try sendCommands(renameCommands(temporaryPath, to: remotePath), handle: handle)
            return
        } catch {
            renameError = error
        }

        let temporaryName = RemotePath.name(of: temporaryPath)
        // 前のコマンドの設定（解放済みのコマンドの一覧）が残っていると libcurl が落ちるので、初期化してから一覧を取る
        handle.reset()
        configure(handle)
        let uploaded = (try? list(RemotePath.parent(of: remotePath), handle: handle))?
            .first { $0.name == temporaryName && $0.kind == .file }
        guard let uploaded, uploaded.size == nil || uploaded.size == size else {
            try? sendCommands([deleteCommand(temporaryPath, ignoringFailure: true)], handle: handle)
            throw Self.replaceError("アップロードしたファイルを元の名前に置き換えられませんでした（元のファイルはそのままです）",
                                    renameError)
        }
        do {
            try sendCommands([deleteCommand(remotePath, ignoringFailure: false)], handle: handle)
        } catch {
            try? sendCommands([deleteCommand(temporaryPath, ignoringFailure: true)], handle: handle)
            throw Self.replaceError("元のファイルを置き換えられませんでした（元のファイルはそのままです）", error)
        }
        do {
            try sendCommands(renameCommands(temporaryPath, to: remotePath), handle: handle)
        } catch {
            throw Self.replaceError("元のファイルを削除した後、名前を変えられませんでした。"
                                    + "アップロードした内容はサーバーの「\(temporaryName)」に残っています", error)
        }
    }

    private static func replaceError(_ message: String, _ underlying: Error) -> FerryError {
        let ferryError = underlying as? FerryError
        return FerryError(kind: ferryError?.kind ?? .other, message: message,
                          detail: ferryError?.errorDescription ?? underlying.localizedDescription)
    }

    // コマンドの組み立て。ignoringFailure なら先頭の * で失敗を無視させる。パスは絶対パス

    private func renameCommands(_ from: String, to: String) -> [String] {
        server.serverProtocol.isFTPFamily
            ? ["RNFR \(from)", "RNTO \(to)"]
            : ["rename \(sftpQuoted(from)) \(sftpQuoted(to))"]
    }

    private func deleteCommand(_ path: String, ignoringFailure: Bool) -> String {
        (ignoringFailure ? "*" : "") + (server.serverProtocol.isFTPFamily ? "DELE \(path)" : "rm \(sftpQuoted(path))")
    }

    private func chmodCommand(_ path: String, mode: String, ignoringFailure: Bool) -> String {
        (ignoringFailure ? "*" : "")
            + (server.serverProtocol.isFTPFamily ? "SITE CHMOD \(mode) \(path)" : "chmod \(mode) \(sftpQuoted(path))")
    }

    private func configure(_ handle: CurlHandle) {
        handle.set(CURLOPT_USERNAME, server.username)
        if let password {
            handle.set(server.privateKeyPath == nil ? CURLOPT_PASSWORD : CURLOPT_KEYPASSWD, password)
        }
        handle.set(CURLOPT_NOSIGNAL, true)
        handle.set(CURLOPT_CONNECTTIMEOUT, 20)
        // 60 秒間ほぼ通信が止まったら失敗とみなす
        handle.set(CURLOPT_LOW_SPEED_LIMIT, 1)
        handle.set(CURLOPT_LOW_SPEED_TIME, 60)
        handle.set(CURLOPT_TCP_KEEPALIVE, true)

        let proto = server.serverProtocol
        if proto.isFTPFamily {
            if proto == .ftps {
                handle.set(CURLOPT_USE_SSL, Int(CURLUSESSL_ALL))
            }
            // 既定では階層ごとに CWD を送るので、深いフォルダほど遅くなる。フルパスで 1 回だけ CWD する
            handle.set(CURLOPT_FTP_FILEMETHOD, CURLFTPMETHOD_SINGLECWD)
            if server.passiveMode {
                // EPSV を試し、非対応なら PASV に自動で切り替わる
                handle.set(CURLOPT_FTP_USE_EPSV, true)
                // PASV 応答の IP は無視して制御接続と同じホストへつなぐ（NAT 内のサーバー対策）
                handle.set(CURLOPT_FTP_SKIP_PASV_IP, true)
            } else {
                handle.set(CURLOPT_FTPPORT, "-")
            }
            if proto.usesTLS {
                if let caBundle = Self.caBundlePath {
                    handle.set(CURLOPT_CAINFO, caBundle)
                }
                handle.set(CURLOPT_SSL_VERIFYPEER, server.verifyCertificate)
                handle.set(CURLOPT_SSL_VERIFYHOST, server.verifyCertificate ? 2 : 0)
                if readsCertificateNames {
                    handle.set(CURLOPT_CERTINFO, true)
                }
            }
        } else {
            handle.set(CURLOPT_SSH_AUTH_TYPES,
                       Int(CURLSSH_AUTH_PASSWORD | CURLSSH_AUTH_PUBLICKEY | CURLSSH_AUTH_KEYBOARD))
            if let key = server.privateKeyPath {
                handle.set(CURLOPT_SSH_PRIVATE_KEYFILE, (key as NSString).expandingTildeInPath)
            }
            let knownHosts = Self.knownHostsURL
            try? FileManager.default.createDirectory(at: knownHosts.deletingLastPathComponent(),
                                                     withIntermediateDirectories: true)
            handle.useKnownHosts(at: knownHosts.path, approvedKey: approvedHostKey)
        }

        if debugLog {
            handle.enableDebugLog()
        }
    }

    private func fetchListing(_ handle: CurlHandle, _ directory: String, command: String?) throws -> String {
        let context = TransferContext(cancel: CancelFlag(), progress: nil, expectedTotal: 0)
        handle.set(CURLOPT_URL, url(for: directory, directory: true))
        if let command {
            handle.set(CURLOPT_CUSTOMREQUEST, command)
        }
        let quote = attach(context, to: handle, isUpload: false)
        try withExtendedLifetime((context, quote)) { try handle.perform() }
        return ListingParser.decode(context.buffer)
    }

    /// 転送用のコールバックを設定する。戻り値は perform が終わるまで保持すること。
    private func attach(_ context: TransferContext, to handle: CurlHandle, isUpload: Bool) -> CurlStringList? {
        let pointer = Unmanaged.passUnretained(context).toOpaque()
        context.isUpload = isUpload
        if isUpload {
            handle.set(CURLOPT_READFUNCTION, callback: readCallback)
            handle.set(CURLOPT_READDATA, pointer: pointer)
        } else {
            handle.set(CURLOPT_WRITEFUNCTION, callback: writeCallback)
            handle.set(CURLOPT_WRITEDATA, pointer: pointer)
        }
        handle.setProgress(progressCallback)
        handle.set(CURLOPT_XFERINFODATA, pointer: pointer)
        handle.set(CURLOPT_NOPROGRESS, false)
        return ftpPreamble(handle)
    }

    /// FTP ではファイル名を UTF-8 で扱うよう依頼する（非対応のサーバーでは無視される）
    private func ftpPreamble(_ handle: CurlHandle) -> CurlStringList? {
        guard server.serverProtocol.isFTPFamily else { return nil }
        let list = CurlStringList(["*OPTS UTF8 ON"])
        handle.set(CURLOPT_QUOTE, pointer: UnsafeMutableRawPointer(list.raw))
        return list
    }

    /// 更新日時を設定するコマンド。先頭の * で失敗を無視する（非対応のサーバーでは何も起きない）。
    /// アップロードは階層ごとに CWD した後なので、パスは絶対パスで渡す
    private func setModifiedCommand(_ remotePath: String, to date: Date) -> String {
        server.serverProtocol.isFTPFamily
            ? "*MFMT \(Self.mfmtFormatter.string(from: date)) \(remotePath)"
            // libcurl の SFTP の mtime。日時は curl_getdate が読める形で、空白を含むので引用符で囲む
            : "*mtime \"\(Self.sftpDateFormatter.string(from: date))\" \(sftpQuoted(remotePath))"
    }

    private static let mfmtFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyyMMddHHmmss"
        return formatter
    }()

    private static let sftpDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "dd MMM yyyy HH:mm:ss 'GMT'"
        return formatter
    }()

    private func execute(_ commands: [String]) async throws {
        try await run { handle in try self.sendCommands(commands, handle: handle) }
    }

    /// コマンドだけを送る（キューの中から呼ぶ）。前の転送の設定が残らないよう、設定を初期化してから送る
    private func sendCommands(_ commands: [String], handle: CurlHandle) throws {
        handle.reset()
        configure(handle)
        var all = commands
        if server.serverProtocol.isFTPFamily {
            all.insert("*OPTS UTF8 ON", at: 0)
        }
        let list = CurlStringList(all)
        handle.set(CURLOPT_URL, url(for: "/", directory: true))
        handle.set(CURLOPT_NOBODY, true)
        handle.set(CURLOPT_QUOTE, pointer: UnsafeMutableRawPointer(list.raw))
        try withExtendedLifetime(list) { try handle.perform() }
    }

    private var baseURL: String {
        let host = server.host.contains(":") ? "[\(server.host)]" : server.host
        return "\(server.serverProtocol.urlScheme)://\(host):\(server.effectivePort)"
    }

    /// 絶対パスから URL を作る。FTP の URL は既定でログイン先からの相対パスなので、先頭に %2F を付けて絶対指定にする。
    func url(for path: String, directory: Bool) -> String {
        let segments = path.split(separator: "/").map { Self.percentEncode(String($0)) }
        var urlPath = (server.serverProtocol.isFTPFamily ? "/%2F" : "/") + segments.joined(separator: "/")
        if directory, !urlPath.hasSuffix("/") {
            urlPath += "/"
        }
        return baseURL + urlPath
    }

    private static let pathSegmentAllowed: CharacterSet = {
        var set = CharacterSet.alphanumerics
        set.insert(charactersIn: "-._~!$&'()*+,=:@")
        return set
    }()

    private static func percentEncode(_ segment: String) -> String {
        // alphanumerics には日本語なども含まれるため ASCII 以外は必ずエンコードする
        segment.unicodeScalars.map { scalar in
            scalar.isASCII && pathSegmentAllowed.contains(scalar)
                ? String(scalar)
                : String(scalar).utf8.map { String(format: "%%%02X", $0) }.joined()
        }.joined()
    }

    private func sftpQuoted(_ path: String) -> String {
        "\"" + path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}

// MARK: - コールバック

final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool { lock.withLock { cancelled } }
    func cancel() { lock.withLock { cancelled = true } }
}

final class TransferContext {
    let cancel: CancelFlag
    let progress: RemoteClient.Progress?
    let expectedTotal: Int64
    var isUpload = false
    var output: FileHandle?
    var input: FileHandle?
    var buffer = Data()

    init(cancel: CancelFlag, progress: RemoteClient.Progress?, expectedTotal: Int64) {
        self.cancel = cancel
        self.progress = progress
        self.expectedTotal = expectedTotal
    }
}

private let writeCallback: ccurl_io_cb = { pointer, size, count, userdata in
    guard let pointer, let userdata else { return 0 }
    let context = Unmanaged<TransferContext>.fromOpaque(userdata).takeUnretainedValue()
    let data = Data(bytes: pointer, count: size * count)
    if let output = context.output {
        do { try output.write(contentsOf: data) } catch { return 0 }
    } else {
        context.buffer.append(data)
    }
    return data.count
}

private let readCallback: ccurl_io_cb = { pointer, size, count, userdata in
    guard let pointer, let userdata else { return 0 }
    let context = Unmanaged<TransferContext>.fromOpaque(userdata).takeUnretainedValue()
    guard let input = context.input else { return 0 }
    do {
        guard let data = try input.read(upToCount: size * count), !data.isEmpty else { return 0 }
        data.copyBytes(to: UnsafeMutableRawPointer(pointer).assumingMemoryBound(to: UInt8.self), count: data.count)
        return data.count
    } catch {
        return Int(CURL_READFUNC_ABORT)
    }
}

private let progressCallback: ccurl_xferinfo_cb = { userdata, downloadTotal, downloaded, uploadTotal, uploaded in
    guard let userdata else { return 0 }
    let context = Unmanaged<TransferContext>.fromOpaque(userdata).takeUnretainedValue()
    if context.cancel.isCancelled { return 1 }
    let (done, total) = context.isUpload ? (Int64(uploaded), Int64(uploadTotal))
                                         : (Int64(downloaded), Int64(downloadTotal))
    context.progress?(done, total > 0 ? total : context.expectedTotal)
    return 0
}
