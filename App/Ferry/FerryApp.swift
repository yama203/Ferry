import SwiftUI

@main
struct FerryApp: App {
    @State private var store = ServerStore()
    @State private var sessions = SessionManager()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(store)
                .environment(sessions)
                .frame(minWidth: 960, minHeight: 600)
        }
        .defaultSize(width: 1280, height: 800)
        .windowToolbarStyle(.unified)
    }
}
