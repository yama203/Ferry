import FerryKit
import SwiftUI

@main
struct FerryApp: App {
    @State private var store: ServerStore
    @State private var sessions: SessionManager

    init() {
        let store = ServerStore()
        _store = State(initialValue: store)
        _sessions = State(initialValue: SessionManager(store: store))
        Notifier.setUp()
        // 同梱したルート証明書を使う（Homebrew の無い Mac でも FTPS の証明書を検証できるように）
        RemoteClient.caBundlePath = Bundle.main.path(forResource: "cacert", ofType: "pem")
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(store)
                .environment(sessions)
                .frame(minWidth: 820, minHeight: 560)
        }
        .defaultSize(width: 1280, height: 800)
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(after: .importExport) {
                Button("Transmit から読み込む…") { store.chooseTransmitFile() }
            }
        }

        Settings {
            SettingsView()
                .environment(store)
        }
    }
}
