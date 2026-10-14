import FerryKit
import SwiftUI

/// 左右のペインで開いているフォルダを同期する
struct SyncView: View {
    let session: ServerSession
    let excludeRules: ExcludeRules

    @Environment(\.dismiss) private var dismiss
    @State private var direction: TransferItem.Direction = .upload
    @State private var deleteExtraneous = false
    @State private var phase: Phase = .options

    private enum Phase {
        case options
        case scanning(String)
        case review(SyncPlanner.Plan)
        case failed(String)
    }

    private var localRoot: URL { URL(fileURLWithPath: session.local.path) }
    private var remoteRoot: String { session.remote.path }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("同期").font(.title2.bold())

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Label("この Mac", systemImage: "laptopcomputer").foregroundStyle(.secondary)
                    Text(localRoot.path).lineLimit(1).truncationMode(.middle)
                }
                GridRow {
                    Label("サーバー", systemImage: "server.rack").foregroundStyle(.secondary)
                    Text(remoteRoot).lineLimit(1).truncationMode(.middle)
                }
            }

            Picker("方向", selection: $direction) {
                Text("この Mac → サーバー（アップロード）").tag(TransferItem.Direction.upload)
                Text("サーバー → この Mac（ダウンロード）").tag(TransferItem.Direction.download)
            }
            .pickerStyle(.radioGroup)
            .disabled(isBusy)

            Toggle(isOn: $deleteExtraneous) {
                Text(direction == .upload ? "サーバーにしかないファイルを削除する" : "この Mac にしかないファイルをゴミ箱に入れる")
                Text("転送先を転送元とまったく同じ状態にします")
            }
            .disabled(isBusy)

            Text("除外: " + excludeRules.patterns.joined(separator: "  "))
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(2)

            Divider()
            content
                .frame(maxWidth: .infinity, minHeight: 220, maxHeight: .infinity, alignment: .top)

            HStack {
                Spacer()
                Button("キャンセル", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                if case .review(let plan) = phase {
                    Button("比較し直す") { Task { await compare() } }
                    Button("同期を実行") { Task { await execute(plan) } }
                        .keyboardShortcut(.defaultAction)
                        .disabled(plan.isEmpty)
                } else {
                    Button("比較") { Task { await compare() } }
                        .keyboardShortcut(.defaultAction)
                        .disabled(isBusy)
                }
            }
        }
        .padding(20)
        .frame(width: 600, height: 620)
        .onChange(of: direction) { phase = .options }
        .onChange(of: deleteExtraneous) { phase = .options }
    }

    private var isBusy: Bool {
        if case .scanning = phase { return true }
        return false
    }

    @ViewBuilder private var content: some View {
        switch phase {
        case .options:
            Text("「比較」を押すと、転送するファイルの一覧を確認できます。サイズが違うか、転送元の方が新しいファイルを転送します。")
                .foregroundStyle(.secondary)
        case .scanning(let folder):
            VStack(alignment: .leading, spacing: 8) {
                ProgressView().controlSize(.small)
                Text("フォルダを調べています…")
                Text(folder).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
        case .failed(let message):
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
        case .review(let plan):
            if plan.isEmpty {
                ContentUnavailableView("違いはありません", systemImage: "checkmark.circle",
                                       description: Text("転送するファイルはありません"))
            } else {
                review(plan)
            }
        }
    }

    private func review(_ plan: SyncPlanner.Plan) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("転送 \(plan.changes.count) 件（\(ByteCountFormatter.string(fromByteCount: plan.totalBytes, countStyle: .file))）"
                + (plan.deletions.isEmpty ? "" : "、削除 \(plan.deletions.count) 件"))
                .font(.headline)
            List {
                ForEach(plan.changes) { change in
                    HStack {
                        Text(change.reason == .new ? "新規" : "変更")
                            .font(.caption.bold())
                            .foregroundStyle(change.reason == .new ? .green : .blue)
                            .frame(width: 34)
                        Text(change.relativePath).lineLimit(1).truncationMode(.middle)
                    }
                }
                ForEach(plan.deletions) { deletion in
                    HStack {
                        Text("削除")
                            .font(.caption.bold())
                            .foregroundStyle(.red)
                            .frame(width: 34)
                        Text(deletion.relativePath + (deletion.isDirectory ? "/" : ""))
                            .lineLimit(1).truncationMode(.middle)
                    }
                }
            }
            .listStyle(.bordered)
        }
    }

    // MARK: - 処理

    private func compare() async {
        guard let client = session.remote.client else { return }
        phase = .scanning(localRoot.path)
        let rules = excludeRules
        let root = localRoot
        let local = await Task.detached { SyncPlanner.scanLocal(root, rules: rules) }.value
        do {
            let remote = try await SyncPlanner.scanRemote(remoteRoot, client: client, rules: rules) { folder in
                phase = .scanning(folder)
            }
            let plan = direction == .upload
                ? SyncPlanner.plan(source: local, destination: remote, deleteExtraneous: deleteExtraneous)
                : SyncPlanner.plan(source: remote, destination: local, deleteExtraneous: deleteExtraneous)
            phase = .review(plan)
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }

    private func execute(_ plan: SyncPlanner.Plan) async {
        await Notifier.requestAuthorization()
        // 削除を先に済ませてから転送を待ち行列に入れる
        var deleteFailures = 0
        for deletion in plan.deletions {
            if direction == .upload, let client = session.remote.client {
                let path = RemotePath.join(remoteRoot, deletion.relativePath)
                let item = RemoteItem(name: RemotePath.name(of: path), path: path,
                                      kind: deletion.isDirectory ? .directory : .file)
                do { try await client.deleteRecursively(item) } catch { deleteFailures += 1 }
            } else if direction == .download {
                do {
                    try FileManager.default.trashItem(at: localRoot.appendingPathComponent(deletion.relativePath),
                                                      resultingItemURL: nil)
                } catch {
                    deleteFailures += 1
                }
            }
        }

        let items = plan.changes.map { change in
            TransferItem(direction: direction,
                         localURL: localRoot.appendingPathComponent(change.relativePath),
                         remotePath: RemotePath.join(remoteRoot, change.relativePath),
                         total: change.size)
        }
        session.transfers.enqueuePlanned(items)
        if items.isEmpty {
            await session.local.refresh()
            await session.remote.refresh()
        }
        dismiss()

        // 画面を閉じた後も転送の完了を待って通知する
        let serverName = session.server.name
        let direction = direction
        let deletionCount = plan.deletions.count - deleteFailures
        guard UserDefaults.standard.object(forKey: "notifySync") as? Bool ?? true else { return }
        Task { @MainActor in
            for item in items {
                await item.waitUntilDone()
            }
            notifyFinished(serverName: serverName, direction: direction, items: items,
                           deletionCount: deletionCount, deleteFailures: deleteFailures)
        }
    }

    private func notifyFinished(serverName: String, direction: TransferItem.Direction, items: [TransferItem],
                                deletionCount: Int, deleteFailures: Int) {
        let finished = items.filter { $0.state == .finished }.count
        let failed = items.filter { if case .failed = $0.state { true } else { false } }.count + deleteFailures
        let cancelled = items.filter { $0.state == .cancelled }.count

        var parts = ["\(direction == .upload ? "アップロード" : "ダウンロード") \(finished) 件"]
        if deletionCount > 0 { parts.append("削除 \(deletionCount) 件") }
        if failed > 0 { parts.append("失敗 \(failed) 件") }
        if cancelled > 0 { parts.append("中止 \(cancelled) 件") }

        let title = failed > 0 ? "同期が終わりました（失敗あり）" : "同期が完了しました"
        Notifier.post(title: title, body: "\(serverName)：" + parts.joined(separator: "、"))
    }
}
