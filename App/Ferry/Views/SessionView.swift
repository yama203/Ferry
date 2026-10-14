import SwiftUI
import UniformTypeIdentifiers

/// 接続中の画面。左にこの Mac、右にサーバー、下に転送一覧。
struct SessionView: View {
    @Bindable var session: ServerSession
    let onEdit: () -> Void

    var body: some View {
        VSplitView {
            HSplitView {
                FileBrowserView(
                    model: session.local,
                    title: "この Mac",
                    systemImage: "laptopcomputer",
                    transferLabel: "アップロード",
                    onTransfer: { session.upload($0) },
                    dropTypes: [.ferryRemoteItem],
                    onDrop: handleRemoteItemsDropped
                )
                .frame(minWidth: 320, maxWidth: .infinity)

                remotePane
                    .frame(minWidth: 320, maxWidth: .infinity)
            }
            .frame(minHeight: 280, maxHeight: .infinity)

            if !session.transfers.items.isEmpty {
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
        .toolbar {
            ToolbarItemGroup {
                Button {
                    session.upload(session.local.selectedEntries)
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

                Button(action: onEdit) {
                    Label("接続設定", systemImage: "slider.horizontal.3")
                }
                .help("接続設定を編集")
            }
        }
    }

    private var subtitle: String {
        let server = session.server
        let place = "\(server.serverProtocol.displayName)  \(server.username)@\(server.host):\(server.effectivePort)"
        switch session.status {
        case .connecting: return "接続中…  " + place
        case .failed: return "未接続  " + place
        default: return place
        }
    }

    @ViewBuilder private var remotePane: some View {
        switch session.status {
        case .idle, .connecting where session.remote.entries.isEmpty:
            ProgressView("\(session.server.host) に接続中…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
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
                onDrop: handleLocalFilesDropped
            )
        }
    }

    /// 右のペインからドロップされたリモートの項目をダウンロード
    private func handleRemoteItemsDropped(_ providers: [NSItemProvider]) -> Bool {
        let matching = providers.filter { $0.hasItemConformingToTypeIdentifier(UTType.ferryRemoteItem.identifier) }
        guard !matching.isEmpty else { return false }
        for provider in matching {
            provider.loadDataRepresentation(forTypeIdentifier: UTType.ferryRemoteItem.identifier) { data, _ in
                guard let data, let entries = try? JSONDecoder().decode([FileEntry].self, from: data) else { return }
                Task { @MainActor in await session.download(entries) }
            }
        }
        return true
    }

    /// 左のペインや Finder からドロップされたファイルをアップロード
    private func handleLocalFilesDropped(_ providers: [NSItemProvider]) -> Bool {
        let matching = providers.filter { $0.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) }
        guard !matching.isEmpty, session.status == .connected else { return false }
        for provider in matching {
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                Task { @MainActor in session.upload(urls: [url]) }
            }
        }
        return true
    }
}
