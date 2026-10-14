import AppKit
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

        var name: String { localURL.lastPathComponent }
    }

    private(set) var files: [EditedFile] = []
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private let onSave: (EditedFile) -> Void

    init(onSave: @escaping (EditedFile) -> Void) {
        self.onSave = onSave
    }

    func watch(_ localURL: URL, remotePath: String) {
        files.removeAll { $0.remotePath == remotePath }
        files.append(EditedFile(remotePath: remotePath, localURL: localURL, lastModified: modificationDate(localURL)))
        startTimerIfNeeded()
    }

    func stop(_ id: EditedFile.ID) {
        files.removeAll { $0.id == id }
        if files.isEmpty {
            timer?.invalidate()
            timer = nil
        }
    }

    func stopAll() {
        files.removeAll()
        timer?.invalidate()
        timer = nil
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
                    files[index].lastUploaded = Date()
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
