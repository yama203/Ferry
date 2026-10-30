import FerryKit
import SwiftUI

struct ServerSidebar: View {
    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @Binding var selection: UUID?
    @Binding var editor: ContentView.EditorRequest?

    @AppStorage("collapsedFolders") private var collapsedFolders = ""
    /// 閉じているフォルダ。@AppStorage を直接使うとアニメーションしないため、画面内の状態として持つ。
    @State private var collapsedIDs: Set<String> = []
    @State private var pendingDelete: ServerConfig?
    @State private var pendingFolderDelete: ServerFolder?
    @State private var folderNameTarget: FolderNameTarget?
    @State private var folderName = ""
    @State private var searchText = ""

    private enum FolderNameTarget: Identifiable {
        case new(shared: Bool)
        case rename(ServerFolder)
        var id: String {
            switch self {
            case .new(let shared): "new-\(shared)"
            case .rename(let folder): folder.id.uuidString
            }
        }
    }

    var body: some View {
        dialogs(list)
            .onAppear {
                collapsedIDs = Set(collapsedFolders.split(separator: ",").map(String.init))
            }
            .onChange(of: collapsedIDs) {
                collapsedFolders = collapsedIDs.sorted().joined(separator: ",")
            }
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
                    Button("フォルダを追加…") { startFolderName(.new(shared: false)) }
                    if store.shared.isEnabled {
                        Divider()
                        Button("共有フォルダを追加…") { startFolderName(.new(shared: true)) }
                    }
                    Divider()
                    Button("Transmit から読み込む…") { store.chooseTransmitFile() }
                } label: {
                    Label("追加", systemImage: "plus")
                }
            }
        }
    }

    /// 会社の共有（Dropbox）を先に、その下に自分の接続先
    @ViewBuilder private var folderSections: some View {
        if store.shared.isEnabled {
            ForEach(store.shared.folders) { folder in
                Section(isExpanded: expansion(of: folder)) {
                    rows(for: store.servers(inFolder: folder.id, shared: true))
                } header: {
                    folderHeader(folder, shared: true)
                }
            }
            Section {
                rows(for: store.servers(inFolder: nil, shared: true))
            } header: {
                Label(store.shared.folders.isEmpty ? "会社の共有" : "会社の共有・フォルダなし", systemImage: "person.2")
                    .dropDestination(for: String.self) { ids, _ in
                        moveServers(ids, to: nil, shared: true)
                    }
            }
        }
        ForEach(store.personalFolders) { folder in
            Section(isExpanded: expansion(of: folder)) {
                rows(for: store.servers(inFolder: folder.id, shared: false))
            } header: {
                folderHeader(folder, shared: false)
            }
        }
        Section {
            rows(for: store.servers(inFolder: nil, shared: false))
        } header: {
            Text(store.personalFolders.isEmpty ? (store.shared.isEnabled ? "自分の接続先" : "接続先") : "フォルダなし")
                .dropDestination(for: String.self) { ids, _ in
                    moveServers(ids, to: nil, shared: false)
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
        } message: { server in
            Text(store.isShared(server.id)
                 ? "会社で共有している接続先なので、全員の一覧から消えます。"
                 : "保存したパスワードも削除されます。")
        }
        .confirmationDialog("フォルダ「\(pendingFolderDelete?.name ?? "")」を削除しますか？",
                            isPresented: isPresent($pendingFolderDelete), presenting: pendingFolderDelete) { folder in
            Button("削除", role: .destructive) { store.deleteFolder(folder.id) }
        } message: { _ in
            Text("中の接続先は削除されず、フォルダの外に移動します。")
        }
        .alert(folderNameTarget.map {
            switch $0 {
            case .new(let shared): shared ? "新規共有フォルダ" : "新規フォルダ"
            case .rename: "フォルダ名を変更"
            }
        } ?? "",
               isPresented: isPresent($folderNameTarget)) {
            TextField("フォルダ名", text: $folderName)
            Button("OK") { commitFolderName() }
            Button("キャンセル", role: .cancel) {}
        }
    }

    // MARK: - 部品

    private func rows(for servers: [ServerConfig]) -> some View {
        ForEach(sortedByName(servers)) { server in
            ServerRow(server: server, isShared: store.isShared(server.id))
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
        let isShared = store.isShared(server.id)
        if !store.folders.isEmpty {
            Menu("フォルダへ移動") {
                Button("フォルダなし") { store.moveServer(server.id, toFolder: nil, shared: isShared) }
                    .disabled(server.folderID == nil)
                Divider()
                ForEach(isShared ? store.shared.folders : store.personalFolders) { folder in
                    Button(folder.name) { store.moveServer(server.id, toFolder: folder.id, shared: isShared) }
                        .disabled(server.folderID == folder.id)
                }
            }
        }
        if store.shared.isEnabled {
            Divider()
            if isShared {
                Button("自分だけの接続先にする") { store.moveServer(server.id, toFolder: nil, shared: false) }
            } else {
                Menu("会社で共有する") {
                    Button("フォルダなし") { store.moveServer(server.id, toFolder: nil, shared: true) }
                    if !store.shared.folders.isEmpty {
                        Divider()
                        ForEach(store.shared.folders) { folder in
                            Button(folder.name) { store.moveServer(server.id, toFolder: folder.id, shared: true) }
                        }
                    }
                }
            }
        }
        Divider()
        Button("削除…", role: .destructive) { pendingDelete = server }
    }

    private func folderHeader(_ folder: ServerFolder, shared: Bool) -> some View {
        Label(folder.name, systemImage: shared ? "person.2" : "folder")
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            // 矢印だけでなくフォルダ名を押しても開閉する
            .onTapGesture {
                expansion(of: folder).wrappedValue.toggle()
            }
            .dropDestination(for: String.self) { ids, _ in
                moveServers(ids, to: folder.id, shared: shared)
            }
            .contextMenu {
                Button("名前を変更…") { startFolderName(.rename(folder)) }
                Button("接続先を追加…") {
                    var draft = ServerConfig.newDraft
                    draft.folderID = folder.id
                    editor = .init(server: draft, isNew: true, shared: shared)
                }
                Divider()
                Button("フォルダを削除…", role: .destructive) { pendingFolderDelete = folder }
            }
    }

    // MARK: - 操作

    private func expansion(of folder: ServerFolder) -> Binding<Bool> {
        Binding(
            get: { !collapsedIDs.contains(folder.id.uuidString) },
            set: { expanded in
                // 一度に多くの行を挿入すると List がアニメーションを省き、段階的に出すと不自然になるので、
                // 行の多いフォルダはアニメーションなしで開く
                let animates = store.servers.filter { $0.folderID == folder.id }.count <= Self.animatedRowLimit
                withAnimation(animates ? .smooth(duration: 0.25) : nil) {
                    if expanded {
                        collapsedIDs.remove(folder.id.uuidString)
                    } else {
                        collapsedIDs.insert(folder.id.uuidString)
                    }
                }
            }
        )
    }

    /// これより行の多いフォルダはアニメーションしない（List が開くアニメーションを省くため。録画で確認した値）
    private static let animatedRowLimit = 6


    private func moveServers(_ ids: [String], to folderID: UUID?, shared: Bool) -> Bool {
        let uuids = ids.compactMap(UUID.init(uuidString:))
        uuids.forEach { store.moveServer($0, toFolder: folderID, shared: shared) }
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
        case .new(let shared): store.addFolder(named: name, shared: shared)
        case .rename(let folder): store.renameFolder(folder.id, to: name)
        }
    }

    private func duplicate(_ server: ServerConfig) {
        var copy = server
        copy.id = UUID()
        copy.name += " のコピー"
        Passwords.setPassword(Passwords.password(for: server.id), for: copy.id)
        store.upsert(copy, shared: store.isShared(server.id))
    }

    private func isPresent<T>(_ binding: Binding<T?>) -> Binding<Bool> {
        Binding(get: { binding.wrappedValue != nil }, set: { if !$0 { binding.wrappedValue = nil } })
    }
}

private struct ServerRow: View {
    let server: ServerConfig
    let isShared: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Text(server.name.isEmpty ? server.host : server.name)
                    .lineLimit(1)
                if isShared {
                    Image(systemName: "person.2.fill")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .help("会社で共有している接続先")
                }
            }
            Text("\(server.serverProtocol.displayName)  \(server.host)")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .padding(.vertical, 2)
    }
}
