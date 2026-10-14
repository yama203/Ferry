import FerryKit
import SwiftUI

struct ContentView: View {
    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @State private var selection: UUID?
    /// 前回開いていた接続先を次回起動時に開く
    @AppStorage("lastServerID") private var lastServerID = ""
    @State private var editor: EditorRequest?

    struct EditorRequest: Identifiable {
        var server: ServerConfig
        var isNew: Bool
        var id: UUID { server.id }
    }

    var body: some View {
        NavigationSplitView {
            ServerSidebar(selection: $selection, editor: $editor)
                .navigationSplitViewColumnWidth(min: 180, ideal: 220, max: 320)
        } detail: {
            if let id = selection, let server = store.server(withID: id) {
                SessionView(session: sessions.session(for: server), onEdit: {
                    editor = EditorRequest(server: server, isNew: false)
                })
                .id(server)
            } else {
                WelcomeView(hasServers: !store.servers.isEmpty) {
                    editor = EditorRequest(server: .newDraft, isNew: true)
                }
            }
        }
        .onAppear {
            if selection == nil, let id = UUID(uuidString: lastServerID), store.server(withID: id) != nil {
                selection = id
            }
        }
        .onChange(of: selection) {
            lastServerID = selection?.uuidString ?? ""
        }
        .sheet(item: $editor) { request in
            ServerEditView(server: request.server, isNew: request.isNew) { server, password in
                Keychain.setPassword(password, for: server.id)
                sessions.close(server.id)
                store.upsert(server)
                selection = server.id
            }
        }
    }
}

extension ServerConfig {
    static var newDraft: ServerConfig {
        ServerConfig(name: "", serverProtocol: .ftps, host: "", username: "")
    }
}

private struct WelcomeView: View {
    let hasServers: Bool
    let onAdd: () -> Void

    var body: some View {
        ContentUnavailableView {
            Label("Ferry", systemImage: "ferry")
        } description: {
            Text(hasServers ? "左のリストから接続先を選んでください" : "まずは接続先を追加しましょう")
        } actions: {
            Button("接続先を追加…", action: onAdd)
                .buttonStyle(.borderedProminent)
        }
    }
}
