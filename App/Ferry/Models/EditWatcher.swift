import AppKit
import FerryKit
import Foundation
import Observation

/// サーバーのファイルをエディタで開いている間、保存を検知してアップロードする
@MainActor @Observable
final class EditWatcher {
    struct EditedFile: Identifiable {
        let id = UUID()
        let remotePath: String
        let localURL: URL
        var lastModified: Date?
        var lastUploaded: Date?
        /// 変更を検知したが、書き込み途中かもしれないので次の確認まで待っている
        var pendingSince: Date?
        /// 開いたとき（自分がアップロードした後は、その後）のサーバーのサイズと更新日時。
        /// 保存したときに比べて、他の人がサーバー側で変えていないかを調べる。nil なら分からない（調べずにアップロードする）
        var baseline: RemoteFileStamp?
        /// サーバーのファイルの権限（一時ファイルから置き換えた後に戻す）
        var permissions: String?

        var name: String { localURL.lastPathComponent }
    }

    private(set) var files: [EditedFile] = []
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private let onSave: (EditedFile) -> Void
    /// 監視をやめたとき（そのファイルの上書きの確認を取り下げる）。引数はサーバーのパス
    @ObservationIgnored var onStop: ((String) -> Void)?

    init(onSave: @escaping (EditedFile) -> Void) {
        self.onSave = onSave
    }

    func watch(_ localURL: URL, remotePath: String, baseline: RemoteFileStamp?, permissions: String?) {
        files.removeAll { $0.remotePath == remotePath }
        files.append(EditedFile(remotePath: remotePath, localURL: localURL, lastModified: modificationDate(localURL),
                                baseline: baseline, permissions: permissions))
        startTimerIfNeeded()
    }

    func file(for remotePath: String) -> EditedFile? {
        files.first { $0.remotePath == remotePath }
    }

    /// アップロードできたら、基準をアップロードした後のサーバーの値にする（自分のアップロードを次の保存で「変更された」とみなさない）
    func uploaded(_ remotePath: String, baseline: RemoteFileStamp?, permissions: String?) {
        guard let index = files.firstIndex(where: { $0.remotePath == remotePath }) else { return }
        files[index].lastUploaded = Date()
        files[index].baseline = baseline
        if let permissions {
            files[index].permissions = permissions
        }
    }

    func stop(_ id: EditedFile.ID) {
        let stopped = files.filter { $0.id == id }
        files.removeAll { $0.id == id }
        if files.isEmpty {
            timer?.invalidate()
            timer = nil
        }
        stopped.forEach { onStop?($0.remotePath) }
    }

    func stopAll() {
        let stopped = files
        files.removeAll()
        timer?.invalidate()
        timer = nil
        stopped.forEach { onStop?($0.remotePath) }
    }

    private func startTimerIfNeeded() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.poll() }
        }
    }

    /// エディタは一時ファイルに書いて置き換えることが多いので、ファイル監視ではなく更新日時を定期的に見る
    private func poll() {
        for index in files.indices {
            guard let modified = modificationDate(files[index].localURL) else { continue }
            if let pending = files[index].pendingSince {
                // 前回から更新日時が変わっていなければ書き込み完了とみなす
                if modified == pending {
                    files[index].pendingSince = nil
                    files[index].lastModified = modified
                    onSave(files[index])
                } else {
                    files[index].pendingSince = modified
                }
            } else if modified != files[index].lastModified {
                files[index].pendingSince = modified
            }
        }
    }

    private func modificationDate(_ url: URL) -> Date? {
        try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
    }
}

enum EditorApp {
    private static let key = "editorAppPath"

    /// 設定されたエディタ。未設定なら VS Code、無ければファイルの種類ごとの既定のアプリ。
    static var url: URL? {
        get {
            if let path = UserDefaults.standard.string(forKey: key), FileManager.default.fileExists(atPath: path) {
                return URL(fileURLWithPath: path)
            }
            return NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.VSCode")
        }
        set {
            UserDefaults.standard.set(newValue?.path, forKey: key)
        }
    }

    static func open(_ file: URL) {
        if let app = url {
            NSWorkspace.shared.open([file], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
        } else {
            NSWorkspace.shared.open(file)
        }
    }
}
