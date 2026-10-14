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
    /// チェックを外した項目（相対パス）
    @State private var unchecked: Set<String> = []
    @State private var filter: Filter = .all
    @State private var scannedFolders = 0

    private enum Phase {
        case options
        case scanning(String)
        case review(SyncPlanner.Plan)
        case failed(String)
    }

    private enum Filter {
        case all, new, changed, deleted
    }

    private var localRoot: URL { URL(fileURLWithPath: session.local.path) }
    private var remoteRoot: String { session.remote.path }

    var body: some View {
        VStack(spacing: 0) {
            endpoints
                .padding(.horizontal, 24)
                .padding(.top, 22)
                .padding(.bottom, 14)
            options
                .padding(.horizontal, 24)
                .padding(.bottom, 14)
            Divider()
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            footer
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
        }
        .frame(width: 700, height: 660)
        .onChange(of: direction) { resetPlan() }
        .onChange(of: deleteExtraneous) { resetPlan() }
    }

    // MARK: - 上部: 送信元 → 送信先

    private var endpoints: some View {
        HStack(spacing: 16) {
            EndpointCard(icon: "laptopcomputer", place: "この Mac", path: localRoot.path,
                         role: direction == .upload ? .source : .destination)
            directionButton
            EndpointCard(icon: "server.rack", place: session.server.name, path: remoteRoot,
                         role: direction == .upload ? .destination : .source)
        }
    }

    private var directionButton: some View {
        VStack(spacing: 6) {
            Button {
                withAnimation(.spring(duration: 0.35)) {
                    direction = direction == .upload ? .download : .upload
                }
            } label: {
                Image(systemName: "arrow.right")
                    .font(.system(size: 22, weight: .bold))
                    .foregroundStyle(.white)
                    .rotationEffect(.degrees(direction == .upload ? 0 : 180))
                    .frame(width: 52, height: 52)
                    .background(Circle().fill(Color.accentColor.gradient))
                    .shadow(color: .accentColor.opacity(0.35), radius: 6, y: 3)
            }
            .buttonStyle(.plain)
            .disabled(isBusy)
            .help("クリックで方向を切り替え")

            Text(direction == .upload ? "アップロード" : "ダウンロード")
                .font(.caption.weight(.semibold))
                .foregroundStyle(Color.accentColor)
        }
    }

    // MARK: - オプション

    private var options: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Toggle(isOn: $deleteExtraneous) {
                    Label(direction == .upload ? "サーバーにしかないファイルを削除（ミラー）"
                                               : "この Mac にしかないファイルをゴミ箱へ（ミラー）",
                          systemImage: deleteExtraneous ? "exclamationmark.triangle.fill" : "square.on.square")
                        .foregroundStyle(deleteExtraneous ? .red : .primary)
                }
                .toggleStyle(.switch)
                .controlSize(.small)
                .disabled(isBusy)
                Spacer()
            }

            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("除外")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 4) {
                        ForEach(excludeRules.patterns, id: \.self) { pattern in
                            Text(pattern)
                                .font(.caption.monospaced())
                                .padding(.horizontal, 7)
                                .padding(.vertical, 2)
                                .background(Capsule().fill(.quaternary))
                        }
                    }
                }
            }
        }
    }

    // MARK: - 中央

    @ViewBuilder private var content: some View {
        switch phase {
        case .options:
            ContentUnavailableView {
                Label("比較して確認", systemImage: "arrow.left.arrow.right.circle")
            } description: {
                Text("「比較」を押すと、転送するファイルの一覧を確認できます。\nサイズが違うか、送信元の方が新しいファイルを転送します。")
            }
        case .scanning(let folder):
            VStack(spacing: 12) {
                ProgressView()
                Text("フォルダを調べています…")
                    .font(.headline)
                Text("\(scannedFolders) フォルダ")
                    .font(.title2.monospacedDigit().bold())
                    .foregroundStyle(Color.accentColor)
                    .contentTransition(.numericText())
                Text(folder)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 480)
            }
        case .failed(let message):
            ContentUnavailableView {
                Label("比較できませんでした", systemImage: "exclamationmark.triangle")
            } description: {
                Text(message)
            }
        case .review(let plan):
            if plan.isEmpty {
                ContentUnavailableView {
                    Label("同期済みです", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                } description: {
                    Text("転送するファイルはありません")
                }
            } else {
                review(plan)
            }
        }
    }

    private func review(_ plan: SyncPlanner.Plan) -> some View {
        let new = plan.changes.filter { $0.reason == .new }
        let changed = plan.changes.filter { $0.reason == .changed }
        return VStack(spacing: 12) {
            HStack(spacing: 10) {
                StatTile(icon: "plus.circle.fill", count: new.count, label: "新規", color: .green,
                         isSelected: filter == .new) { toggleFilter(.new) }
                StatTile(icon: "pencil.circle.fill", count: changed.count, label: "変更", color: .blue,
                         isSelected: filter == .changed) { toggleFilter(.changed) }
                StatTile(icon: "trash.circle.fill", count: plan.deletions.count, label: "削除", color: .red,
                         isSelected: filter == .deleted) { toggleFilter(.deleted) }
                StatTile(icon: "shippingbox.circle.fill",
                         value: ByteCountFormatter.string(fromByteCount: checkedBytes(plan), countStyle: .file),
                         label: "転送量", color: .accentColor, isSelected: false) {}
                    .allowsHitTesting(false)
            }
            .padding(.horizontal, 20)
            .padding(.top, 14)

            List {
                if filter == .all || filter == .new {
                    ForEach(new) { row(path: $0.relativePath, kind: .new, size: $0.size) }
                }
                if filter == .all || filter == .changed {
                    ForEach(changed) { row(path: $0.relativePath, kind: .changed, size: $0.size) }
                }
                if filter == .all || filter == .deleted {
                    ForEach(plan.deletions) { row(path: $0.relativePath + ($0.isDirectory ? "/" : ""), kind: .deleted, size: nil) }
                }
            }
            .listStyle(.inset(alternatesRowBackgrounds: true))

            HStack {
                Button("すべて選択") { unchecked = [] }
                Button("すべて解除") {
                    unchecked = Set(plan.changes.map(\.relativePath) + plan.deletions.map(\.relativePath))
                }
                Spacer()
                if filter != .all {
                    Button("絞り込みを解除") { filter = .all }
                }
            }
            .controlSize(.small)
            .padding(.horizontal, 20)
            .padding(.bottom, 10)
        }
    }

    private enum RowKind { case new, changed, deleted }

    private func row(path: String, kind: RowKind, size: Int64?) -> some View {
        let key = path.hasSuffix("/") ? String(path.dropLast()) : path
        let (icon, color): (String, Color) = switch kind {
        case .new: ("plus.circle.fill", .green)
        case .changed: ("pencil.circle.fill", .blue)
        case .deleted: ("trash.circle.fill", .red)
        }
        let folder = (key as NSString).deletingLastPathComponent
        let name = (key as NSString).lastPathComponent + (path.hasSuffix("/") ? "/" : "")
        return HStack(spacing: 8) {
            Toggle("", isOn: Binding(
                get: { !unchecked.contains(key) },
                set: { if $0 { unchecked.remove(key) } else { unchecked.insert(key) } }
            ))
            .labelsHidden()
            Image(systemName: icon)
                .foregroundStyle(color)
            HStack(spacing: 0) {
                if !folder.isEmpty {
                    Text(folder + "/").foregroundStyle(.secondary)
                }
                Text(name).fontWeight(.medium)
            }
            .lineLimit(1)
            .truncationMode(.head)
            Spacer()
            if let size {
                Text(ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
        }
        .opacity(unchecked.contains(key) ? 0.4 : 1)
    }

    // MARK: - 下部

    private var footer: some View {
        HStack {
            if case .review(let plan) = phase, !plan.isEmpty {
                Text("\(checkedCount(plan)) 件を実行します")
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("キャンセル", role: .cancel) { dismiss() }
                .keyboardShortcut(.cancelAction)
            if case .review(let plan) = phase {
                Button("比較し直す") { Task { await compare() } }
                Button {
                    Task { await execute(plan) }
                } label: {
                    Label("同期を実行", systemImage: "arrow.triangle.2.circlepath")
                }
                .keyboardShortcut(.defaultAction)
                .disabled(checkedCount(plan) == 0)
            } else {
                Button("比較") { Task { await compare() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(isBusy)
            }
        }
    }

    // MARK: - 状態

    private var isBusy: Bool {
        if case .scanning = phase { return true }
        return false
    }

    private func resetPlan() {
        phase = .options
        unchecked = []
        filter = .all
    }

    private func toggleFilter(_ value: Filter) {
        filter = filter == value ? .all : value
    }

    private func checkedCount(_ plan: SyncPlanner.Plan) -> Int {
        plan.changes.filter { !unchecked.contains($0.relativePath) }.count
            + plan.deletions.filter { !unchecked.contains($0.relativePath) }.count
    }

    private func checkedBytes(_ plan: SyncPlanner.Plan) -> Int64 {
        plan.changes.filter { !unchecked.contains($0.relativePath) }.reduce(0) { $0 + $1.size }
    }

    // MARK: - 処理

    private func compare() async {
        guard let client = session.remote.client else { return }
        unchecked = []
        filter = .all
        scannedFolders = 0
        phase = .scanning(localRoot.path)
        let rules = excludeRules
        let root = localRoot
        let local = await Task.detached { SyncPlanner.scanLocal(root, rules: rules) }.value
        do {
            let remote = try await SyncPlanner.scanRemote(remoteRoot, client: client, rules: rules) { folder in
                withAnimation { scannedFolders += 1 }
                phase = .scanning(folder)
            }
            let plan = direction == .upload
                ? SyncPlanner.plan(source: local, destination: remote, deleteExtraneous: deleteExtraneous)
                : SyncPlanner.plan(source: remote, destination: local, deleteExtraneous: deleteExtraneous)
            withAnimation { phase = .review(plan) }
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }

    private func execute(_ fullPlan: SyncPlanner.Plan) async {
        // チェックを外した項目は除く
        var plan = fullPlan
        plan.changes.removeAll { unchecked.contains($0.relativePath) }
        plan.deletions.removeAll { unchecked.contains($0.relativePath) }

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

// MARK: - 部品

/// 送信元・送信先のカード
private struct EndpointCard: View {
    enum Role { case source, destination }

    let icon: String
    let place: String
    let path: String
    let role: Role

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: icon)
                    .font(.title2)
                    .foregroundStyle(role == .source ? Color.accentColor : .secondary)
                    .frame(width: 30)
                Text(place)
                    .font(.headline)
                    .lineLimit(1)
                Spacer()
                Text(role == .source ? "送信元" : "送信先")
                    .font(.caption2.weight(.bold))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 2)
                    .foregroundStyle(role == .source ? .white : .secondary)
                    .background(Capsule().fill(role == .source ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.quaternary)))
            }
            Label((path as NSString).lastPathComponent.isEmpty ? "/" : (path as NSString).lastPathComponent,
                  systemImage: "folder.fill")
                .font(.title3.weight(.semibold))
                .lineLimit(1)
            Text(path)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12).fill(.background.secondary))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(role == .source ? Color.accentColor.opacity(0.6) : Color.secondary.opacity(0.2),
                              lineWidth: role == .source ? 2 : 1)
        )
    }
}

/// 件数のタイル（押すとその種類だけに絞り込む）
private struct StatTile: View {
    let icon: String
    let value: String
    let label: String
    let color: Color
    let isSelected: Bool
    let action: () -> Void

    init(icon: String, count: Int, label: String, color: Color, isSelected: Bool, action: @escaping () -> Void) {
        self.init(icon: icon, value: "\(count)", label: label, color: color, isSelected: isSelected, action: action)
    }

    init(icon: String, value: String, label: String, color: Color, isSelected: Bool, action: @escaping () -> Void) {
        self.icon = icon
        self.value = value
        self.label = label
        self.color = color
        self.isSelected = isSelected
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 4) {
                Label(label, systemImage: icon)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(color)
                Text(value)
                    .font(.title2.weight(.bold).monospacedDigit())
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 10).fill(color.opacity(isSelected ? 0.22 : 0.09)))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(color.opacity(isSelected ? 0.8 : 0), lineWidth: 2))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
