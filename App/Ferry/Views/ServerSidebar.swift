import FerryKit
import SwiftUI

struct ServerSidebar: View {
    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @Binding var selection: UUID?
    @Binding var editor: ContentView.EditorRequest?

    @AppStorage("collapsedFolders") private var collapsedFolders = ""
    @State private var pendingDelete: ServerConfig?
    @State private var pendingFolderDelete: ServerFolder?
    @State private var folderNameTarget: FolderNameTarget?
    @State private var folderName = ""
    @State private var searchText = ""

    private enum FolderNameTarget: Identifiable {
        case new
        case rename(ServerFolder)
        var id: String {
            switch self {
            case .new: "new"
            case .rename(let folder): folder.id.uuidString
            }
        }
    }

    var body: some View {
        dialogs(list)
    }

    private var list: some View {
        List(selection: $selection) {
            if isSearching {
                Section("検索結果") {
                    rows(for: searchResults)
                }
            } else {
                folderSections
            }
        }
        .searchable(text: $searchText, placement: .sidebar, prompt: "接続先を検索")
        .onSubmit(of: .search) {
            // Return で最初の結果に接続
            if let first = sortedByName(searchResults).first {
                selection = first.id
            }
        }
        .overlay {
            if isSearching && searchResults.isEmpty {
                ContentUnavailableView.search(text: searchText)
            }
        }
        .safeAreaInset(edge: .bottom) {
            footer
        }
        .toolbar {
            ToolbarItem {
                Menu {
                    Button("接続先を追加…") { editor = .init(server: .newDraft, isNew: true) }
                    Button("フォルダを追加…") { startFolderName(.new) }
                    Divider()
                    Button("Transmit から読み込む…") { store.chooseTransmitFile() }
                } label: {
                    Label("追加", systemImage: "plus")
                }
            }
        }
    }

    @ViewBuilder private var folderSections: some View {
        ForEach(store.folders) { folder in
            Section(isExpanded: expansion(of: folder)) {
                rows(for: store.servers(inFolder: folder.id))
            } header: {
                folderHeader(folder)
            }
        }
        Section {
            rows(for: store.servers(inFolder: nil))
        } header: {
            Text(store.folders.isEmpty ? "接続先" : "フォルダなし")
                .dropDestination(for: String.self) { ids, _ in
                    moveServers(ids, to: nil)
                }
        }
    }

    private var footer: some View {
        HStack(spacing: 6) {
            BrandMark(size: 18)
            Text("Ferry")
                .font(.callout.weight(.semibold))
                .foregroundStyle(.primary)
            Spacer()
            Label(store.isICloud ? "iCloud で同期" : "この Mac に保存",
                  systemImage: store.isICloud ? "icloud" : "internaldrive")
                .labelStyle(.titleAndIcon)
                .font(.caption)
                .help(store.isICloud ? "接続先リストは iCloud Drive/Ferry に保存され、他の Mac と同期されます" : "")
        }
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
    }

    // MARK: - 検索

    private var isSearching: Bool {
        !searchText.trimmingCharacters(in: .whitespaces).isEmpty
    }

    /// 空白区切りの語をすべて含む接続先（名前・ホスト・ユーザー名・フォルダ名が対象）
    private var searchResults: [ServerConfig] {
        let words = searchText.split(whereSeparator: \.isWhitespace).map(String.init)
        let folderNames = Dictionary(uniqueKeysWithValues: store.folders.map { ($0.id, $0.name) })
        return store.servers.filter { server in
            let haystack = [server.name, server.host, server.username,
                            server.folderID.flatMap { folderNames[$0] } ?? ""].joined(separator: " ")
            return words.allSatisfy { haystack.localizedStandardContains($0) }
        }
    }

    private func sortedByName(_ servers: [ServerConfig]) -> [ServerConfig] {
        servers.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    // MARK: - ダイアログ

    private func dialogs<Content: View>(_ content: Content) -> some View {
        content
        .confirmationDialog("「\(pendingDelete?.name ?? "")」を削除しますか？",
                            isPresented: isPresent($pendingDelete), presenting: pendingDelete) { server in
            Button("削除", role: .destructive) {
                sessions.close(server.id)
                if selection == server.id { selection = nil }
                store.delete(server.id)
            }
        } message: { _ in
            Text("保存したパスワードも削除されます。")
        }
        .confirmationDialog("フォルダ「\(pendingFolderDelete?.name ?? "")」を削除しますか？",
                            isPresented: isPresent($pendingFolderDelete), presenting: pendingFolderDelete) { folder in
            Button("削除", role: .destructive) { store.deleteFolder(folder.id) }
        } message: { _ in
            Text("中の接続先は削除されず、フォルダの外に移動します。")
        }
        .alert(folderNameTarget.map { if case .new = $0 { "新規フォルダ" } else { "フォルダ名を変更" } } ?? "",
               isPresented: isPresent($folderNameTarget)) {
            TextField("フォルダ名", text: $folderName)
            Button("OK") { commitFolderName() }
            Button("キャンセル", role: .cancel) {}
        }
    }

    // MARK: - 部品

    private func rows(for servers: [ServerConfig]) -> some View {
        ForEach(sortedByName(servers)) { server in
            ServerRow(server: server)
                .tag(server.id)
                .draggable(server.id.uuidString)
                .contextMenu { serverMenu(server) }
        }
    }

    @ViewBuilder private func serverMenu(_ server: ServerConfig) -> some View {
        if let bookmarks = server.bookmarks, !bookmarks.isEmpty {
            Menu("ブックマークで開く") {
                ForEach(bookmarks) { bookmark in
                    Button(bookmark.name) {
                        let session = sessions.session(for: server)
                        Task { await session.open(bookmark) }
                        selection = server.id
                    }
                }
            }
            Divider()
        }
        Button("編集…") { editor = .init(server: server, isNew: false) }
        Button("複製") { duplicate(server) }
        if !store.folders.isEmpty {
            Menu("フォルダへ移動") {
                Button("フォルダなし") { store.moveServer(server.id, toFolder: nil) }
                    .disabled(server.folderID == nil)
                Divider()
                ForEach(store.folders) { folder in
                    Button(folder.name) { store.moveServer(server.id, toFolder: folder.id) }
                        .disabled(server.folderID == folder.id)
                }
            }
        }
        Divider()
        Button("削除…", role: .destructive) { pendingDelete = server }
    }

    private func folderHeader(_ folder: ServerFolder) -> some View {
        Label(folder.name, systemImage: "folder")
            .contentShape(Rectangle())
            .dropDestination(for: String.self) { ids, _ in
                moveServers(ids, to: folder.id)
            }
            .contextMenu {
                Button("名前を変更…") { startFolderName(.rename(folder)) }
                Button("接続先を追加…") {
                    var draft = ServerConfig.newDraft
                    draft.folderID = folder.id
                    editor = .init(server: draft, isNew: true)
                }
                Divider()
                Button("フォルダを削除…", role: .destructive) { pendingFolderDelete = folder }
            }
    }

    // MARK: - 操作

    private func expansion(of folder: ServerFolder) -> Binding<Bool> {
        Binding(
            get: { !collapsedFolders.contains(folder.id.uuidString) },
            set: { expanded in
                var ids = Set(collapsedFolders.split(separator: ",").map(String.init))
                if expanded { ids.remove(folder.id.uuidString) } else { ids.insert(folder.id.uuidString) }
                collapsedFolders = ids.joined(separator: ",")
            }
        )
    }

    private func moveServers(_ ids: [String], to folderID: UUID?) -> Bool {
        let uuids = ids.compactMap(UUID.init(uuidString:))
        uuids.forEach { store.moveServer($0, toFolder: folderID) }
        return !uuids.isEmpty
    }

    private func startFolderName(_ target: FolderNameTarget) {
        if case .rename(let folder) = target {
            folderName = folder.name
        } else {
            folderName = ""
        }
        folderNameTarget = target
    }

    private func commitFolderName() {
        let name = folderName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty, let target = folderNameTarget else { return }
        switch target {
        case .new: store.addFolder(named: name)
        case .rename(let folder): store.renameFolder(folder.id, to: name)
        }
    }

    private func duplicate(_ server: ServerConfig) {
        var copy = server
        copy.id = UUID()
        copy.name += " のコピー"
        Keychain.setPassword(Keychain.password(for: server.id), for: copy.id)
        store.upsert(copy)
    }

    private func isPresent<T>(_ binding: Binding<T?>) -> Binding<Bool> {
        Binding(get: { binding.wrappedValue != nil }, set: { if !$0 { binding.wrappedValue = nil } })
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
