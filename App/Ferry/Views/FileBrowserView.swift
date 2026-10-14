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
    let dropTypes: [NSPasteboard.PasteboardType]
    let onDrop: (NSPasteboard) -> Bool
    /// ドラッグで渡すもの（リモートは Finder へのダウンロードに対応したもの）
    let dragWriter: (FileEntry) -> NSPasteboardWriting?
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
            VStack(spacing: 0) {
                header
                pathBar
            }
            .background(headerBackground)
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
                .foregroundStyle(model.side == .remote ? Color.accentColor : .secondary)
            Text(title)
                .font(.headline)
                .lineLimit(1)
            Spacer()
            if model.isLoading {
                ProgressView().controlSize(.small)
            }
            Group {
                if model.side == .local {
                    Menu {
                        Button("フォルダを選択…") { chooseLocalFolder() }
                        Button("Finder で開いているフォルダ") { openFinderFrontFolder() }
                        Divider()
                        Button("このフォルダを Finder で開く") {
                            NSWorkspace.shared.open(URL(fileURLWithPath: model.path))
                        }
                    } label: {
                        Image(systemName: "folder")
                    }
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .help("Finder からフォルダを選ぶ（Finder のフォルダをここへドラッグしても移動できます）")
                }
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

    /// サーバー側は Ferry の色をうっすら敷いて、この Mac 側と見分けやすくする
    @ViewBuilder private var headerBackground: some View {
        if model.side == .remote {
            LinearGradient(colors: [Color.accentColor.opacity(0.16), Color.accentColor.opacity(0.05)],
                           startPoint: .leading, endPoint: .trailing)
        }
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
        FileTableView(
            entries: model.visibleEntries,
            selection: $model.selection,
            sortKey: $model.sortKey,
            ascending: $model.ascending,
            isLocal: model.side == .local,
            autosaveName: model.side == .local ? "Ferry.LocalFiles" : "Ferry.RemoteFiles",
            onOpen: open,
            onDelete: requestDelete,
            menuItems: menuItems(for:),
            dragWriter: dragWriter,
            dropTypes: dropTypes,
            onDrop: onDrop,
            isDropTargeted: $isDropTargeted
        )
    }

    private func open(_ entry: FileEntry) {
        // リモートのファイルはダブルクリックでダウンロード
        if model.side == .remote && !entry.isDirectory && !entry.isSymlink {
            onTransfer([entry])
        } else {
            Task { await model.open(entry) }
        }
    }

    /// Finder と同じ並び（レッド〜グレイ）の標準タグと、このフォルダで使われている他のタグ
    private func tagMenu(for targets: [FileEntry]) -> [MenuAction] {
        let labels = NSWorkspace.shared.fileLabels
        let standard: [FinderTag] = [6, 7, 5, 2, 4, 3, 1].compactMap { number in
            labels.indices.contains(number) ? FinderTag(name: labels[number], color: number) : nil
        }
        var custom: [FinderTag] = []
        for tag in model.entries.flatMap(\.tags) where !standard.contains(where: { $0.name == tag.name })
            && !custom.contains(where: { $0.name == tag.name }) {
            custom.append(tag)
        }

        func action(for tag: FinderTag) -> MenuAction {
            let applied = targets.allSatisfy { $0.tags.contains { $0.name == tag.name } }
            return MenuAction(title: tag.name, image: TagMenuImage.make(colorNumber: tag.color), isChecked: applied) {
                Task { await model.setTag(tag.name, on: targets, enabled: !applied) }
            }
        }

        var items = standard.map(action(for:))
        if !custom.isEmpty {
            items.append(.separator)
            items += custom.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }.map(action(for:))
        }
        if targets.contains(where: { !$0.tags.isEmpty }) {
            items.append(.separator)
            items.append(MenuAction(title: "すべてのタグを外す") {
                Task { await model.clearTags(on: targets) }
            })
        }
        return items
    }

    private func menuItems(for targets: [FileEntry]) -> [MenuAction] {
        var items: [MenuAction] = []
        if !targets.isEmpty {
            items.append(MenuAction(title: transferLabel) { onTransfer(targets) })
            if let onEdit, targets.count == 1, !targets[0].isDirectory {
                items.append(MenuAction(title: "エディタで開く") { onEdit(targets[0]) })
            }
            items.append(.separator)
            if targets.count == 1 {
                let target = targets[0]
                items.append(MenuAction(title: "名前を変更…") {
                    nameText = target.name
                    renameTarget = target
                })
            }
            if model.side == .remote {
                items.append(MenuAction(title: "パーミッション…") {
                    permissionText = targets.count == 1 ? octalPermissions(targets[0]) : ""
                    permissionTargets = targets
                })
            } else {
                items.append(MenuAction(title: "タグ", submenu: tagMenu(for: targets)))
                items.append(MenuAction(title: "Finder で表示") {
                    NSWorkspace.shared.activateFileViewerSelecting(targets.map { URL(fileURLWithPath: $0.path) })
                })
            }
            items.append(MenuAction(title: "パスをコピー") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(targets.map(\.path).joined(separator: "\n"), forType: .string)
            })
            items.append(.separator)
            items.append(MenuAction(title: model.side == .local ? "ゴミ箱に入れる" : "削除…", isDestructive: true) {
                requestDelete(targets)
            })
            items.append(.separator)
        }
        items.append(MenuAction(title: "新規フォルダ…") { startCreatingFolder() })
        items.append(MenuAction(title: "再読み込み") { Task { await model.refresh() } })
        return items
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

    private func chooseLocalFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.directoryURL = URL(fileURLWithPath: model.path)
        panel.prompt = "開く"
        if panel.runModal() == .OK, let url = panel.url {
            Task { await model.load(url.path) }
        }
    }

    private func openFinderFrontFolder() {
        switch FinderBridge.frontWindowFolder() {
        case .success(let path):
            Task { await model.load(path) }
        case .failure(let error):
            model.errorMessage = error.localizedDescription
        }
    }

    private func startCreatingFolder() {
        nameText = ""
        isCreatingFolder = true
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

/// タグのメニュー項目に付ける色の丸
enum TagMenuImage {
    static func make(colorNumber: Int) -> NSImage {
        let color = FinderTagColor.color(for: colorNumber)
        return NSImage(size: NSSize(width: 12, height: 12), flipped: false) { rect in
            let circle = NSBezierPath(ovalIn: rect.insetBy(dx: 1, dy: 1))
            if let color {
                color.setFill()
                circle.fill()
            } else {
                NSColor.secondaryLabelColor.setStroke()
                circle.lineWidth = 1
                circle.stroke()
            }
            return true
        }
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
