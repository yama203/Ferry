import FerryKit
import SwiftUI

@main
struct FerryApp: App {
    @State private var store: ServerStore
    @State private var sessions: SessionManager
    @State private var vault: PasswordVault?
    @State private var updater: Updater

    init() {
        let store = ServerStore()
        _store = State(initialValue: store)
        _sessions = State(initialValue: SessionManager(store: store))
        // 更新は会社の共有フォルダの App/Mac から受け取る
        let updater = Updater { [weak store] in store?.shared.directory }
        updater.start()
        _updater = State(initialValue: updater)
        // パスワードの同期は、自分の接続先を同期する場所（iCloud Drive・選んだフォルダ）に置いている場合だけ
        let vault = store.isSynced ? PasswordVault(directory: store.fileURL.deletingLastPathComponent()) : nil
        Passwords.vault = vault
        // 共有の接続先のパスワードは会社の保管庫（store.shared.passwordVault）に入れる
        Passwords.isShared = { [weak store] id in store?.isShared(id) ?? false }
        _vault = State(initialValue: vault)
        Notifier.setUp()
        ServerSession.removeLeftoverDownloads()
        // 同梱したルート証明書を使う（Homebrew の無い Mac でも FTPS の証明書を検証できるように）
        RemoteClient.caBundlePath = Bundle.main.path(forResource: "cacert", ofType: "pem")
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(store)
                .environment(sessions)
                .environment(updater)
                .frame(minWidth: 820, minHeight: 560)
        }
        .defaultSize(width: 1280, height: 800)
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("アップデートを確認…") {
                    Task { await updater.checkByHand() }
                }
            }
            CommandGroup(after: .importExport) {
                Button("Transmit から読み込む…") { store.chooseTransmitFile() }
            }
        }

        Settings {
            SettingsView(vault: vault)
                .environment(store)
        }
    }
}
