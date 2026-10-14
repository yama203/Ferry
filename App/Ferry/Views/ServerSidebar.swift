import FerryKit
import SwiftUI

struct ServerSidebar: View {
    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @Binding var selection: UUID?
    @Binding var editor: ContentView.EditorRequest?
    @State private var pendingDelete: ServerConfig?

    var body: some View {
        List(selection: $selection) {
            Section("接続先") {
                ForEach(store.servers) { server in
                    ServerRow(server: server)
                        .tag(server.id)
                        .contextMenu {
                            Button("編集…") { editor = .init(server: server, isNew: false) }
                            Button("複製") { duplicate(server) }
                            Divider()
                            Button("削除…", role: .destructive) { pendingDelete = server }
                        }
                }
                .onMove { store.move(from: $0, to: $1) }
            }
        }
        .safeAreaInset(edge: .bottom) {
            HStack(spacing: 6) {
                Image(systemName: store.isICloud ? "icloud" : "internaldrive")
                Text(store.isICloud ? "iCloud Drive で同期" : "この Mac に保存")
                Spacer()
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
        .toolbar {
            ToolbarItem {
                Button {
                    editor = .init(server: .newDraft, isNew: true)
                } label: {
                    Label("接続先を追加", systemImage: "plus")
                }
            }
        }
        .confirmationDialog("「\(pendingDelete?.name ?? "")」を削除しますか？",
                            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
                            presenting: pendingDelete) { server in
            Button("削除", role: .destructive) {
                sessions.close(server.id)
                if selection == server.id { selection = nil }
                store.delete(server.id)
            }
        } message: { _ in
            Text("保存したパスワードも削除されます。")
        }
    }

    private func duplicate(_ server: ServerConfig) {
        var copy = server
        copy.id = UUID()
        copy.name += " のコピー"
        Keychain.setPassword(Keychain.password(for: server.id), for: copy.id)
        store.upsert(copy)
    }
}

private struct ServerRow: View {
    let server: ServerConfig

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(server.name.isEmpty ? server.host : server.name)
                .lineLimit(1)
            Text("\(server.serverProtocol.displayName)  \(server.host)")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .padding(.vertical, 2)
    }
}
