import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// 片側のペイン。ヘッダー（場所・移動ボタン）、パンくず、ファイル一覧、フッター。
struct FileBrowserView: View {
    @Bindable var model: BrowserModel
    let title: String
    let systemImage: String
    /// 反対側のペインへ転送する操作（「アップロード」「ダウンロード」）
    let transferLabel: String
    let onTransfer: ([FileEntry]) -> Void
    let dropTypes: [UTType]
    let onDrop: ([NSItemProvider]) -> Bool
    /// リモート側のみ: ドラッグ用（Finder へのダウンロードに対応）
    var remoteItemProvider: ((FileEntry) -> NSItemProvider)?
    /// リモート側のみ: エディタで開いて自動アップロード
    var onEdit: ((FileEntry) -> Void)?
    /// 右のペインとの同期
    var onSync: (() -> Void)?

    @State private var isDropTargeted = false
    @State private var renameTarget: FileEntry?
    @State private var isCreatingFolder = false
    @State private var nameText = ""
    @State private var deleteTargets: [FileEntry] = []
    @State private var permissionTargets: [FileEntry] = []
    @State private var permissionText = ""

    var body: some View {
        VStack(spacing: 0) {
            header
            pathBar
            Divider()
            table
                .overlay {
                    if model.isLoading && model.entries.isEmpty {
                        ProgressView()
                    }
                }
                .overlay(alignment: .bottom) { errorBanner }
                .overlay {
                    if isDropTargeted {
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(Color.accentColor, lineWidth: 3)
                            .allowsHitTesting(false)
                    }
                }
            Divider()
            footer
        }
        .alert("名前を変更", isPresented: isPresent($renameTarget)) {
            TextField("名前", text: $nameText)
            Button("変更") {
                if let target = renameTarget, !nameText.isEmpty, nameText != target.name {
                    Task { await model.rename(target, to: nameText) }
                }
            }
            Button("キャンセル", role: .cancel) {}
        }
        .alert("新規フォルダ", isPresented: $isCreatingFolder) {
            TextField("フォルダ名", text: $nameText)
            Button("作成") {
                if !nameText.isEmpty {
                    Task { await model.makeFolder(named: nameText) }
                }
            }
            Button("キャンセル", role: .cancel) {}
        }
        .alert("パーミッションを変更", isPresented: isPresent($permissionTargets)) {
            TextField("例: 755", text: $permissionText)
            Button("変更") {
                let targets = permissionTargets
                let mode = permissionText
                if mode.range(of: "^[0-7]{3,4}$", options: .regularExpression) != nil {
                    Task { await model.changePermissions(targets, mode: mode) }
                }
            }
            Button("キャンセル", role: .cancel) {}
        } message: {
            Text("ファイルは 644、フォルダは 755 が一般的です")
        }
        .confirmationDialog(deleteTitle, isPresented: isPresent($deleteTargets)) {
            Button("削除", role: .destructive) {
                let targets = deleteTargets
                Task { await model.delete(targets) }
            }
        } message: {
            Text("サーバー上のファイルは元に戻せません。フォルダは中身ごと削除されます。")
        }
    }

    // MARK: - 部品

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: systemImage)
                .foregroundStyle(.secondary)
            Text(title)
                .font(.headline)
                .lineLimit(1)
            Spacer()
            if model.isLoading {
                ProgressView().controlSize(.small)
            }
            Group {
                Button { Task { await model.goBack() } } label: { Image(systemName: "chevron.left") }
                    .disabled(!model.canGoBack)
                    .help("戻る")
                Button { Task { await model.goUp() } } label: { Image(systemName: "arrow.up") }
                    .disabled(!model.canGoUp)
                    .help("上の階層へ")
                Button { Task { await model.refresh() } } label: { Image(systemName: "arrow.clockwise") }
                    .help("再読み込み")
                Menu {
                    Button("新規フォルダ…") { startCreatingFolder() }
                    if let onSync {
                        Button("同期…", action: onSync)
                    }
                    Divider()
                    Toggle("隠しファイルを表示", isOn: $model.showHidden)
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuIndicator(.hidden)
                .fixedSize()
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
    }

    private var pathBar: some View {
        ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 2) {
                    ForEach(Array(model.pathComponents.enumerated()), id: \.offset) { index, component in
                        if index > 0 {
                            Image(systemName: "chevron.compact.right")
                                .foregroundStyle(.tertiary)
                        }
                        Button(component.name) {
                            Task { await model.load(component.path) }
                        }
                        .buttonStyle(.borderless)
                        .foregroundStyle(index == model.pathComponents.count - 1 ? .primary : .secondary)
                        .id(index)
                    }
                }
                .font(.callout)
                .padding(.horizontal, 10)
                .padding(.bottom, 6)
            }
            .onChange(of: model.path) {
                proxy.scrollTo(model.pathComponents.count - 1, anchor: .trailing)
            }
        }
    }

    private var table: some View {
        Table(of: FileEntry.self, selection: $model.selection, sortOrder: $model.sortOrder) {
            TableColumn("名前", value: \.name, comparator: .localizedStandard) { entry in
                HStack(spacing: 6) {
                    Image(nsImage: FileIcons.icon(for: entry, isLocal: model.side == .local))
                        .resizable()
                        .frame(width: 16, height: 16)
                    Text(entry.name)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if entry.isSymlink {
                        Image(systemName: "arrow.turn.up.right")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                }
                .opacity(entry.isHidden ? 0.6 : 1)
            }
            .width(min: 140, ideal: 260)

            TableColumn("サイズ", value: \.sortSize) { entry in
                Text(Formatters.size(entry))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            .width(min: 60, ideal: 80)

            TableColumn("更新日", value: \.sortDate) { entry in
                Text(Formatters.date(entry.modified))
                    .foregroundStyle(.secondary)
            }
            .width(min: 100, ideal: 150)

            if model.side == .remote {
                TableColumn("権限") { entry in
                    Text(entry.permissions ?? "")
                        .font(.system(.body, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
                .width(min: 70, ideal: 90)
            }
        } rows: {
            ForEach(model.visibleEntries) { entry in
                TableRow(entry)
                    .itemProvider { itemProvider(for: entry) }
            }
        }
        .contextMenu(forSelectionType: FileEntry.ID.self) { ids in
            contextMenu(for: model.entries.filter { ids.contains($0.id) })
        } primaryAction: { ids in
            guard ids.count == 1, let entry = model.entries.first(where: { ids.contains($0.id) }) else { return }
            // リモートのファイルはダブルクリックでダウンロード
            if model.side == .remote && !entry.isDirectory && !entry.isSymlink {
                onTransfer([entry])
            } else {
                Task { await model.open(entry) }
            }
        }
        .onDeleteCommand { requestDelete(model.selectedEntries) }
        .onDrop(of: dropTypes, isTargeted: $isDropTargeted, perform: onDrop)
    }

    @ViewBuilder
    private func contextMenu(for targets: [FileEntry]) -> some View {
        if !targets.isEmpty {
            Button(transferLabel) { onTransfer(targets) }
            if let onEdit, targets.count == 1, let target = targets.first, !target.isDirectory {
                Button("エディタで開く") { onEdit(target) }
            }
            Divider()
            if targets.count == 1, let target = targets.first {
                Button("名前を変更…") {
                    nameText = target.name
                    renameTarget = target
                }
            }
            if model.side == .remote {
                Button("パーミッション…") {
                    permissionText = targets.count == 1 ? octalPermissions(targets[0]) : ""
                    permissionTargets = targets
                }
            } else {
                Button("Finder で表示") {
                    NSWorkspace.shared.activateFileViewerSelecting(targets.map { URL(fileURLWithPath: $0.path) })
                }
            }
            Button("パスをコピー") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(targets.map(\.path).joined(separator: "\n"), forType: .string)
            }
            Divider()
            Button(model.side == .local ? "ゴミ箱に入れる" : "削除…", role: .destructive) {
                requestDelete(targets)
            }
            Divider()
        }
        Button("新規フォルダ…") { startCreatingFolder() }
        Button("再読み込み") { Task { await model.refresh() } }
    }

    @ViewBuilder private var errorBanner: some View {
        if let message = model.errorMessage {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.yellow)
                Text(message)
                    .font(.callout)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button {
                    model.errorMessage = nil
                } label: {
                    Image(systemName: "xmark")
                }
                .buttonStyle(.borderless)
            }
            .padding(10)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8))
            .shadow(radius: 4)
            .padding(10)
        }
    }

    private var footer: some View {
        HStack {
            let count = model.visibleEntries.count
            Text(model.selection.isEmpty ? "\(count) 項目" : "\(count) 項目中 \(model.selection.count) 項目を選択")
            Spacer()
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
    }

    // MARK: - 操作

    private var deleteTitle: String {
        deleteTargets.count == 1 ? "「\(deleteTargets[0].name)」を削除しますか？" : "\(deleteTargets.count) 項目を削除しますか？"
    }

    private func requestDelete(_ targets: [FileEntry]) {
        guard !targets.isEmpty else { return }
        if model.side == .local {
            // ゴミ箱から戻せるので確認しない
            Task { await model.delete(targets) }
        } else {
            deleteTargets = targets
        }
    }

    private func startCreatingFolder() {
        nameText = ""
        isCreatingFolder = true
    }

    private func itemProvider(for entry: FileEntry) -> NSItemProvider {
        if let remoteItemProvider {
            return remoteItemProvider(entry)
        }
        return NSItemProvider(contentsOf: URL(fileURLWithPath: entry.path)) ?? NSItemProvider()
    }

    private func octalPermissions(_ entry: FileEntry) -> String {
        guard let permissions = entry.permissions, permissions.count == 9 else { return "" }
        let chars = Array(permissions)
        return (0..<3).map { group in
            let r = chars[group * 3] == "r" ? 4 : 0
            let w = chars[group * 3 + 1] == "w" ? 2 : 0
            let x = chars[group * 3 + 2] == "x" || chars[group * 3 + 2] == "s" ? 1 : 0
            return String(r + w + x)
        }.joined()
    }

    private func isPresent<T>(_ binding: Binding<T?>) -> Binding<Bool> {
        Binding(get: { binding.wrappedValue != nil }, set: { if !$0 { binding.wrappedValue = nil } })
    }

    private func isPresent<T>(_ binding: Binding<[T]>) -> Binding<Bool> {
        Binding(get: { !binding.wrappedValue.isEmpty }, set: { if !$0 { binding.wrappedValue = [] } })
    }
}

@MainActor
enum FileIcons {
    private static var cache: [String: NSImage] = [:]

    static func icon(for entry: FileEntry, isLocal: Bool) -> NSImage {
        let key = isLocal ? entry.path : (entry.isDirectory ? "/folder" : (entry.name as NSString).pathExtension.lowercased())
        if let cached = cache[key] {
            return cached
        }
        let icon: NSImage
        if isLocal {
            icon = NSWorkspace.shared.icon(forFile: entry.path)
        } else if entry.isDirectory {
            icon = NSWorkspace.shared.icon(for: .folder)
        } else {
            icon = NSWorkspace.shared.icon(for: UTType(filenameExtension: key) ?? .data)
        }
        cache[key] = icon
        return icon
    }
}
