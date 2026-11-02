import AppKit
import Foundation
import Observation

/// 自動更新。会社の共有フォルダの App/Mac に release.sh が置いた Ferry.zip と version.json を見て、
/// 新しければ展開しておき、再起動するとき（またはアプリを終了したとき）に今の Ferry.app と入れ替える。
/// GitHub のリポジトリは非公開なので、Windows 版と同じく Dropbox から受け取る。
@MainActor @Observable
final class Updater {
    /// 展開済みの新しいバージョン
    private(set) var pendingVersion: String?
    /// 新しいバージョンのお知らせを出す
    var showsPrompt = false
    /// 手で確かめたときの結果など
    var notice: Notice?

    struct Notice: Identifiable {
        let title: String
        let message: String
        var id: String { title + message }
    }

    /// 起動中に確かめる間隔
    static let checkInterval: Duration = .seconds(6 * 60 * 60)
    /// 入れ替える Ferry.app の署名のチーム ID（project.yml の DEVELOPMENT_TEAM）。共有フォルダに別のアプリを置かれても入れない
    nonisolated private static let teamID = "4AU22553B4"

    @ObservationIgnored private let sharedDirectory: @MainActor () -> URL?
    @ObservationIgnored private var pendingApp: URL?
    @ObservationIgnored private var pendingBuild: Int?
    @ObservationIgnored private var loop: Task<Void, Never>?
    @ObservationIgnored private var terminationObserver: NSObjectProtocol?

    init(sharedDirectory: @escaping @MainActor () -> URL?) {
        self.sharedDirectory = sharedDirectory
    }

    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
    }

    static var currentBuild: Int {
        Int(Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "") ?? 0
    }

    enum Result {
        case downloaded(String)
        case upToDate
        /// 共有フォルダを設定していない、または App/Mac が無い
        case noSource
        case failed(String)
    }

    /// 起動したときと、その後 6 時間ごとに確かめる。終了するときに、用意した更新があれば入れる
    func start() {
        guard loop == nil else { return }
        loop = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                let before = pendingBuild
                if case .downloaded = await check(), pendingBuild != before {
                    showsPrompt = true
                }
                try? await Task.sleep(for: Self.checkInterval)
            }
        }
        terminationObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { _ = self?.launchInstaller(relaunch: false) }
        }
    }

    /// 「アップデートを確認…」
    func checkByHand() async {
        switch await check() {
        case .downloaded:
            showsPrompt = true
        case .upToDate:
            notice = Notice(title: "最新です", message: "Ferry \(Self.currentVersion) は最新のバージョンです。")
        case .noSource:
            notice = Notice(title: "更新を確かめられません",
                            message: "設定で会社の共有フォルダを選ぶと、そこ（App/Mac）から更新を受け取れます。")
        case .failed(let message):
            notice = Notice(title: "更新を確かめられませんでした", message: message)
        }
    }

    func check() async -> Result {
        guard let shared = sharedDirectory() else { return .noSource }
        let folder = shared.appendingPathComponent("App/Mac")
        let info = folder.appendingPathComponent("version.json")
        guard FileManager.default.fileExists(atPath: info.path) else { return .noSource }
        do {
            let remote = try JSONDecoder().decode(RemoteVersion.self, from: Data(contentsOf: info))
            guard remote.build > Self.currentBuild else { return .upToDate }
            if pendingBuild == remote.build, let pendingVersion { return .downloaded(pendingVersion) }
            let zip = folder.appendingPathComponent("Ferry.zip")
            let app = try await Task.detached { try Self.extract(zip, expectedBuild: remote.build) }.value
            pendingApp = app
            pendingBuild = remote.build
            pendingVersion = remote.version
            return .downloaded(remote.version)
        } catch {
            NSLog("Ferry: 更新の確認に失敗しました: %@", error.localizedDescription)
            return .failed(error.localizedDescription)
        }
    }

    /// 用意した更新を入れて開き直す
    func installAndRelaunch() {
        if let problem = installProblem {
            notice = Notice(title: "更新できません", message: problem)
            return
        }
        guard launchInstaller(relaunch: true) else {
            notice = Notice(title: "更新できません", message: "入れ替えの準備に失敗しました。")
            return
        }
        NSApp.terminate(nil)
    }

    /// 今の場所の Ferry.app を入れ替えられない理由
    private var installProblem: String? {
        let target = Bundle.main.bundleURL
        // ダウンロードしたまま開くと、macOS が別の場所（App Translocation）から動かしている
        if target.path.contains("/AppTranslocation/") {
            return "Ferry.app を「アプリケーション」フォルダへ移してから開き直してください。"
        }
        if !FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) {
            return "\(target.deletingLastPathComponent().path) に書き込めません。共有フォルダの App/Mac にある Ferry.zip から入れ直してください。"
        }
        return nil
    }

    /// アプリが終わるのを待って Ferry.app を入れ替える小さなスクリプトを動かす（アプリより後まで動き続ける）
    private func launchInstaller(relaunch: Bool) -> Bool {
        guard let pendingApp, installProblem == nil else { return false }
        let script = """
            while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done
            old="$(mktemp -d)/Ferry.app"
            mv "$TARGET" "$old" || exit 1
            if mv "$NEW" "$TARGET"; then rm -rf "$old"; else mv "$old" "$TARGET"; fi
            \(relaunch ? #"open "$TARGET""# : "")
            """
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", script]
        process.environment = ["TARGET": Bundle.main.bundleURL.path, "NEW": pendingApp.path, "PATH": "/usr/bin:/bin"]
        do {
            try process.run()
        } catch {
            NSLog("Ferry: 更新を入れられませんでした: %@", error.localizedDescription)
            return false
        }
        // 終了の通知でもう一度動かさない
        self.pendingApp = nil
        return true
    }

    // MARK: - 展開（メインスレッドの外で）

    nonisolated private static func extract(_ zip: URL, expectedBuild: Int) throws -> URL {
        let fileManager = FileManager.default
        let work = fileManager.temporaryDirectory.appendingPathComponent("FerryUpdate-\(expectedBuild)")
        try? fileManager.removeItem(at: work)
        try fileManager.createDirectory(at: work, withIntermediateDirectories: true)
        // 同期中に書き換わっても困らないよう、先に手元へコピーしてから展開する
        let copy = work.appendingPathComponent("Ferry.zip")
        try fileManager.copyItem(at: zip, to: copy)
        try run("/usr/bin/ditto", ["-x", "-k", copy.path, work.path])
        try? fileManager.removeItem(at: copy)

        let app = work.appendingPathComponent("Ferry.app")
        // Bundle は読んだ内容を覚えているので、Info.plist を直接読む
        let plist = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
        guard Int(plist?["CFBundleVersion"] as? String ?? "") == expectedBuild else {
            throw UpdateError("共有フォルダの Ferry.zip がまだ新しいバージョンになっていません（同期の途中かもしれません）。")
        }
        do {
            try run("/usr/bin/codesign", ["--verify", "--deep", "--strict",
                                          #"-R=anchor apple generic and certificate leaf[subject.OU] = "\#(teamID)""#,
                                          app.path])
        } catch {
            throw UpdateError("共有フォルダの Ferry.app の署名を確かめられませんでした。\n\(error.localizedDescription)")
        }
        // ダウンロードした印が付いていると、開くときに止められるので外す
        try? run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
        return app
    }

    @discardableResult
    nonisolated private static func run(_ path: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw UpdateError("\(URL(fileURLWithPath: path).lastPathComponent): \(output)")
        }
        return output
    }
}

/// 共有フォルダの App/Mac/version.json（release.sh が書く）
private struct RemoteVersion: Decodable {
    let build: Int
    let version: String
}

private struct UpdateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
