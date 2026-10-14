import SwiftUI
import UniformTypeIdentifiers

/// 接続中の画面。左にこの Mac、右にサーバー、下に転送一覧。
struct SessionView: View {
    @Bindable var session: ServerSession
    let onEdit: () -> Void

    @Environment(ServerStore.self) private var store
    @State private var isSyncing = false

    var body: some View {
        VSplitView {
            PaneSplitView(autosaveName: "Ferry.Panes", minimumWidth: 260) {
                FileBrowserView(
                    model: session.local,
                    title: "この Mac",
                    systemImage: "laptopcomputer",
                    transferLabel: "アップロード",
                    onTransfer: { entries in Task { await session.upload(entries) } },
                    dropTypes: [.ferryRemoteItem],
                    onDrop: handleRemoteItemsDropped,
                    dragWriter: { NSURL(fileURLWithPath: $0.path) },
                    onSync: session.status == .connected ? { isSyncing = true } : nil
                )
            } right: {
                remotePane
            }
            .frame(minHeight: 280, maxHeight: .infinity)

            if !session.transfers.items.isEmpty || session.transfers.isPreparing {
                TransferListView(queue: session.transfers)
                    .frame(minHeight: 100, idealHeight: 160, maxHeight: 400)
            }
        }
        .navigationTitle(session.server.name)
        .navigationSubtitle(subtitle)
        .task {
            if session.status == .idle {
                await session.connect()
            }
        }
        .toolbar { toolbar }
        .sheet(isPresented: $isSyncing) {
            SyncView(session: session, excludeRules: store.excludeRules)
        }
        .confirmationDialog(conflictTitle, isPresented: hasConflict, presenting: session.transfers.pendingConflicts.first) { conflict in
            Button("新しい場合のみ上書き") { session.transfers.resolve(conflict, with: .newerOnly) }
            Button("すべて上書き") { session.transfers.resolve(conflict, with: .overwrite) }
            Button("既存のファイルはスキップ") { session.transfers.resolve(conflict, with: .skip) }
            Button("転送をやめる", role: .cancel) { session.transfers.resolve(conflict, with: nil) }
        } message: { conflict in
            Text(conflict.direction == .upload ? "サーバーに同じ名前のファイルがあります。" : "この Mac に同じ名前のファイルがあります。")
        }
    }

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItemGroup {
            if let name = session.openingForEdit {
                ProgressView().controlSize(.small)
                    .help("\(name) を開いています")
            }
            if !session.editWatcher.files.isEmpty {
                EditingMenu(watcher: session.editWatcher)
            }

            Button {
                Task { await session.upload(session.local.selectedEntries) }
            } label: {
                Label("アップロード", systemImage: "arrow.up.doc")
            }
            .help("左で選択した項目をサーバーへアップロード")
            .disabled(session.local.selection.isEmpty || session.status != .connected)

            Button {
                Task { await session.download(session.remote.selectedEntries) }
            } label: {
                Label("ダウンロード", systemImage: "arrow.down.doc")
            }
            .help("右で選択した項目をこの Mac へダウンロード")
            .disabled(session.remote.selection.isEmpty || session.status != .connected)

            Button {
                isSyncing = true
            } label: {
                Label("同期", systemImage: "arrow.triangle.2.circlepath")
            }
            .help("左右で開いているフォルダを同期")
            .disabled(session.status != .connected)

            Button(action: onEdit) {
                Label("接続設定", systemImage: "slider.horizontal.3")
            }
            .help("接続設定を編集")
        }
    }

    private var hasConflict: Binding<Bool> {
        Binding(get: { !session.transfers.pendingConflicts.isEmpty }, set: { _ in })
    }

    private var conflictTitle: String {
        guard let conflict = session.transfers.pendingConflicts.first else { return "" }
        return conflict.conflictCount == 1
            ? "「\(conflict.firstConflictName)」はすでにあります"
            : "\(conflict.conflictCount) 個のファイルがすでにあります"
    }

    private var subtitle: String {
        let server = session.server
        let place = "\(server.serverProtocol.displayName)  \(server.username)@\(server.host):\(server.effectivePort)"
        switch session.status {
        case .connecting: return "接続中…  " + place
        case .failed, .needsPassword, .untrustedCertificate: return "未接続  " + place
        default: return place
        }
    }

    @ViewBuilder private var remotePane: some View {
        switch session.status {
        case .idle, .connecting where session.remote.entries.isEmpty:
            ProgressView("\(session.server.host) に接続中…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .untrustedCertificate(let message):
            ContentUnavailableView {
                Label("証明書を確認できません", systemImage: "lock.trianglebadge.exclamationmark")
            } description: {
                Text(message + "\n\nレンタルサーバーの FTPS ではよくあることで、Transmit でも最初に「信頼する」を選んでいた可能性があります。接続先が正しいことが分かっていれば、検証せずに接続できます。通信の暗号化はそのまま行われます。")
                    .textSelection(.enabled)
            } actions: {
                Button("証明書を検証せずに接続") {
                    var server = session.server
                    server.verifyCertificate = false
                    store.upsert(server)
                }
                .buttonStyle(.borderedProminent)
                Button("接続設定を編集…", action: onEdit)
            }
        case .needsPassword(let message):
            PasswordPrompt(session: session, message: message)
                .id(message)
        case .failed(let message):
            ContentUnavailableView {
                Label("接続できませんでした", systemImage: "exclamationmark.triangle")
            } description: {
                Text(message)
                    .textSelection(.enabled)
            } actions: {
                Button("再接続") { Task { await session.connect() } }
                    .buttonStyle(.borderedProminent)
                Button("接続設定を編集…", action: onEdit)
            }
        default:
            FileBrowserView(
                model: session.remote,
                title: session.server.name,
                systemImage: "server.rack",
                transferLabel: "ダウンロード",
                onTransfer: { entries in Task { await session.download(entries) } },
                dropTypes: [.fileURL],
                onDrop: handleLocalFilesDropped,
                dragWriter: { session.dragWriter(forRemote: $0) },
                onEdit: { entry in Task { await session.edit(entry) } },
                onSync: { isSyncing = true }
            )
        }
    }

    /// 右のペインからドロップされたリモートの項目をダウンロード
    private func handleRemoteItemsDropped(_ pasteboard: NSPasteboard) -> Bool {
        let entries = (pasteboard.pasteboardItems ?? []).flatMap { item -> [FileEntry] in
            guard let data = item.data(forType: .ferryRemoteItem) else { return [] }
            return (try? JSONDecoder().decode([FileEntry].self, from: data)) ?? []
        }
        guard !entries.isEmpty else { return false }
        Task { await session.download(entries) }
        return true
    }

    /// 左のペインや Finder からドロップされたファイルをアップロード
    private func handleLocalFilesDropped(_ pasteboard: NSPasteboard) -> Bool {
        // 右のペイン自身からのドラッグは無視する（同じ場所へ再アップロードしないように）
        guard session.status == .connected, pasteboard.availableType(from: [.ferryRemoteItem]) == nil,
              let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                                options: [.urlReadingFileURLsOnly: true]) as? [URL],
              !urls.isEmpty else { return false }
        // まとめて追加すると上書き確認が 1 回で済む
        Task { await session.upload(urls: urls) }
        return true
    }
}

/// エディタで編集中のファイル一覧（保存すると自動でアップロード）
private struct EditingMenu: View {
    let watcher: EditWatcher

    var body: some View {
        Menu {
            Section("保存すると自動でアップロードします") {
                ForEach(watcher.files) { file in
                    Menu(file.name) {
                        Text(file.remotePath)
                        if let uploaded = file.lastUploaded {
                            Text("最終アップロード: \(uploaded.formatted(date: .omitted, time: .shortened))")
                        }
                        Button("もう一度エディタで開く") { EditorApp.open(file.localURL) }
                        Button("監視をやめる") { watcher.stop(file.id) }
                    }
                }
            }
            Divider()
            Button("すべての監視をやめる") { watcher.stopAll() }
        } label: {
            Label("編集中 \(watcher.files.count)", systemImage: "pencil.circle")
        }
        .help("エディタで編集中のファイル")
    }
}
