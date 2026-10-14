import AppKit
import SwiftUI
import UniformTypeIdentifiers

extension NSPasteboard.PasteboardType {
    static let ferryRemoteItem = NSPasteboard.PasteboardType(UTType.ferryRemoteItem.identifier)
}

/// ファイル一覧の表。SwiftUI の Table は列幅を変えるとレイアウトが崩れるため、NSTableView を使う。
struct FileTableView: NSViewRepresentable {
    let entries: [FileEntry]
    @Binding var selection: Set<FileEntry.ID>
    @Binding var sortKey: BrowserModel.SortKey
    @Binding var ascending: Bool
    let isLocal: Bool
    /// 列幅を保存する名前
    let autosaveName: String
    let onOpen: (FileEntry) -> Void
    let onDelete: ([FileEntry]) -> Void
    let menuItems: ([FileEntry]) -> [MenuAction]
    let dragWriter: (FileEntry) -> NSPasteboardWriting?
    /// ドロップを受け付ける型と、受け取った時の処理（true なら受け付けた）
    let dropTypes: [NSPasteboard.PasteboardType]
    let onDrop: (NSPasteboard) -> Bool
    @Binding var isDropTargeted: Bool

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let table = FerryTableView()
        table.coordinator = context.coordinator
        table.style = .inset
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.allowsColumnReordering = false
        table.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        table.rowHeight = 22
        table.intercellSpacing = NSSize(width: 6, height: 2)

        addColumn(to: table, id: .name, title: "名前", width: 260, min: 120, sortKey: "name")
        addColumn(to: table, id: .size, title: "サイズ", width: 80, min: 50, sortKey: "size", alignment: .right)
        addColumn(to: table, id: .date, title: "更新日", width: 150, min: 90, sortKey: "date")
        if !isLocal {
            addColumn(to: table, id: .permissions, title: "権限", width: 90, min: 60, sortKey: nil)
        }
        table.autosaveName = autosaveName
        table.autosaveTableColumns = true
        table.sortDescriptors = [NSSortDescriptor(key: sortKey.rawValue, ascending: ascending)]

        table.dataSource = context.coordinator
        table.delegate = context.coordinator
        table.target = context.coordinator
        table.doubleAction = #selector(Coordinator.doubleClicked(_:))
        table.setDraggingSourceOperationMask(.copy, forLocal: true)
        table.setDraggingSourceOperationMask(.copy, forLocal: false)
        table.registerForDraggedTypes(dropTypes)

        let menu = NSMenu()
        menu.delegate = context.coordinator
        table.menu = menu

        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        context.coordinator.table = table
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.update(self)
    }

    private func addColumn(to table: NSTableView, id: NSUserInterfaceItemIdentifier, title: String, width: CGFloat,
                           min: CGFloat, sortKey: String?, alignment: NSTextAlignment = .left) {
        let column = NSTableColumn(identifier: id)
        column.title = title
        column.width = width
        column.minWidth = min
        // 異常な幅が保存されても他の列が見えなくならないよう上限を設ける
        column.maxWidth = 1200
        column.headerCell.alignment = alignment
        if let sortKey {
            column.sortDescriptorPrototype = NSSortDescriptor(key: sortKey, ascending: true)
        }
        table.addTableColumn(column)
    }

    // MARK: - Coordinator

    @MainActor
    final class Coordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSMenuDelegate {
        var parent: FileTableView
        weak var table: NSTableView?
        private var entries: [FileEntry] = []
        private var isApplyingSelection = false

        init(_ parent: FileTableView) {
            self.parent = parent
        }

        func update(_ parent: FileTableView) {
            self.parent = parent
            guard let table else { return }
            if parent.entries != entries {
                entries = parent.entries
                table.reloadData()
            }
            let wanted = IndexSet(entries.indices.filter { parent.selection.contains(entries[$0].id) })
            if wanted != table.selectedRowIndexes {
                isApplyingSelection = true
                table.selectRowIndexes(wanted, byExtendingSelection: false)
                isApplyingSelection = false
            }
        }

        func entries(at rows: IndexSet) -> [FileEntry] {
            rows.compactMap { entries.indices.contains($0) ? entries[$0] : nil }
        }

        var selectedEntries: [FileEntry] {
            entries(at: table?.selectedRowIndexes ?? [])
        }

        // MARK: データ

        func numberOfRows(in tableView: NSTableView) -> Int {
            entries.count
        }

        func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
            guard let column = tableColumn, entries.indices.contains(row) else { return nil }
            let entry = entries[row]
            let cell = tableView.makeView(withIdentifier: column.identifier, owner: nil) as? FileCellView
                ?? FileCellView(identifier: column.identifier, withIcon: column.identifier == .name)
            switch column.identifier {
            case .name:
                cell.textField?.stringValue = entry.name
                cell.imageView?.image = FileIcons.icon(for: entry, isLocal: parent.isLocal)
                cell.tagDots?.colors = entry.tags.compactMap { FinderTagColor.color(for: $0.color) }
                let tagNames = entry.tags.map(\.name).joined(separator: ", ")
                cell.textField?.toolTip = [entry.isSymlink ? "シンボリックリンク" : nil, tagNames.isEmpty ? nil : "タグ: " + tagNames]
                    .compactMap { $0 }.joined(separator: "\n").nilIfEmpty
            case .size:
                cell.textField?.stringValue = Formatters.size(entry)
                cell.textField?.alignment = .right
            case .date:
                cell.textField?.stringValue = Formatters.date(entry.modified)
            default:
                cell.textField?.stringValue = entry.permissions ?? ""
                cell.textField?.font = .monospacedSystemFont(ofSize: NSFont.systemFontSize - 1, weight: .regular)
            }
            cell.textField?.textColor = column.identifier == .name ? .labelColor : .secondaryLabelColor
            cell.alphaValue = entry.isHidden ? 0.55 : 1
            return cell
        }

        func tableViewSelectionDidChange(_ notification: Notification) {
            guard !isApplyingSelection, let table else { return }
            let ids = Set(entries(at: table.selectedRowIndexes).map(\.id))
            // 描画中の状態変更を避ける
            DispatchQueue.main.async { self.parent.selection = ids }
        }

        func tableView(_ tableView: NSTableView, sortDescriptorsDidChange oldDescriptors: [NSSortDescriptor]) {
            guard let descriptor = tableView.sortDescriptors.first, let key = descriptor.key,
                  let sortKey = BrowserModel.SortKey(rawValue: key) else { return }
            DispatchQueue.main.async {
                self.parent.sortKey = sortKey
                self.parent.ascending = descriptor.ascending
            }
        }

        // MARK: 操作

        @objc func doubleClicked(_ sender: NSTableView) {
            let row = sender.clickedRow
            guard entries.indices.contains(row) else { return }
            parent.onOpen(entries[row])
        }

        func deleteSelected() {
            let targets = selectedEntries
            if !targets.isEmpty { parent.onDelete(targets) }
        }

        func openSelected() {
            let targets = selectedEntries
            if targets.count == 1 { parent.onOpen(targets[0]) }
        }

        // MARK: 右クリックメニュー

        func menuNeedsUpdate(_ menu: NSMenu) {
            menu.removeAllItems()
            guard let table else { return }
            let clicked = table.clickedRow
            // Finder と同じく、選択外の行を右クリックしたらその行だけが対象
            let targets: [FileEntry]
            if clicked < 0 {
                targets = []
            } else if table.selectedRowIndexes.contains(clicked) {
                targets = selectedEntries
            } else {
                targets = entries(at: [clicked])
            }
            for action in parent.menuItems(targets) {
                menu.addItem(action.menuItem())
            }
        }

        // MARK: ドラッグ

        func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
            guard entries.indices.contains(row) else { return nil }
            return parent.dragWriter(entries[row])
        }

        func tableView(_ tableView: NSTableView, validateDrop info: NSDraggingInfo, proposedRow row: Int,
                       proposedDropOperation dropOperation: NSTableView.DropOperation) -> NSDragOperation {
            // 自分自身からのドラッグは受け付けない
            if (info.draggingSource as? NSTableView) === tableView { return [] }
            guard info.draggingPasteboard.availableType(from: parent.dropTypes) != nil else { return [] }
            // 行ではなく表全体（今開いているフォルダ）へのドロップとして扱う
            tableView.setDropRow(-1, dropOperation: .on)
            setDropTargeted(true)
            return .copy
        }

        func tableView(_ tableView: NSTableView, acceptDrop info: NSDraggingInfo, row: Int,
                       dropOperation: NSTableView.DropOperation) -> Bool {
            setDropTargeted(false)
            return parent.onDrop(info.draggingPasteboard)
        }

        func setDropTargeted(_ value: Bool) {
            guard parent.isDropTargeted != value else { return }
            DispatchQueue.main.async { self.parent.isDropTargeted = value }
        }
    }
}

extension NSUserInterfaceItemIdentifier {
    static let name = NSUserInterfaceItemIdentifier("name")
    static let size = NSUserInterfaceItemIdentifier("size")
    static let date = NSUserInterfaceItemIdentifier("date")
    static let permissions = NSUserInterfaceItemIdentifier("permissions")
}

/// キー操作（削除・Return）を受け取る表
final class FerryTableView: NSTableView {
    weak var coordinator: FileTableView.Coordinator?

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 51, 117: // delete, forward delete
            coordinator?.deleteSelected()
        case 36, 76: // return, enter
            coordinator?.openSelected()
        default:
            super.keyDown(with: event)
        }
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        super.draggingExited(sender)
        coordinator?.setDropTargeted(false)
    }

    override func draggingEnded(_ sender: NSDraggingInfo) {
        super.draggingEnded(sender)
        coordinator?.setDropTargeted(false)
    }
}

final class FileCellView: NSTableCellView {
    private(set) var tagDots: TagDotsView?

    init(identifier: NSUserInterfaceItemIdentifier, withIcon: Bool) {
        super.init(frame: .zero)
        self.identifier = identifier

        let text = NSTextField(labelWithString: "")
        text.lineBreakMode = .byTruncatingMiddle
        text.translatesAutoresizingMaskIntoConstraints = false
        addSubview(text)
        textField = text

        if withIcon {
            let image = NSImageView()
            image.translatesAutoresizingMaskIntoConstraints = false
            addSubview(image)
            imageView = image
            // Finder と同じく、名前のすぐ後ろにタグの色の丸を並べる
            let dots = TagDotsView()
            dots.translatesAutoresizingMaskIntoConstraints = false
            addSubview(dots)
            tagDots = dots
            text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            NSLayoutConstraint.activate([
                image.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
                image.centerYAnchor.constraint(equalTo: centerYAnchor),
                image.widthAnchor.constraint(equalToConstant: 16),
                image.heightAnchor.constraint(equalToConstant: 16),
                text.leadingAnchor.constraint(equalTo: image.trailingAnchor, constant: 6),
                dots.leadingAnchor.constraint(equalTo: text.trailingAnchor, constant: 4),
                dots.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -2),
                dots.centerYAnchor.constraint(equalTo: centerYAnchor),
                text.centerYAnchor.constraint(equalTo: centerYAnchor),
            ])
        } else {
            NSLayoutConstraint.activate([
                text.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
                text.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -2),
                text.centerYAnchor.constraint(equalTo: centerYAnchor),
            ])
        }
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
}

/// タグの色の丸（少しずつ重ねて並べる）
final class TagDotsView: NSView {
    var colors: [NSColor] = [] {
        didSet {
            guard colors != oldValue else { return }
            invalidateIntrinsicContentSize()
            needsDisplay = true
        }
    }

    private let diameter: CGFloat = 10
    private let step: CGFloat = 6

    override var intrinsicContentSize: NSSize {
        NSSize(width: colors.isEmpty ? 0 : diameter + CGFloat(colors.count - 1) * step, height: diameter)
    }

    override func draw(_ dirtyRect: NSRect) {
        for (index, color) in colors.enumerated() {
            let rect = NSRect(x: CGFloat(index) * step, y: (bounds.height - diameter) / 2, width: diameter, height: diameter)
            let circle = NSBezierPath(ovalIn: rect.insetBy(dx: 0.5, dy: 0.5))
            color.setFill()
            circle.fill()
            // 重なりが分かるよう縁取りする
            NSColor.controlBackgroundColor.setStroke()
            circle.lineWidth = 1
            circle.stroke()
        }
    }
}

enum FinderTagColor {
    static func color(for number: Int) -> NSColor? {
        switch number {
        case 1: .systemGray
        case 2: .systemGreen
        case 3: .systemPurple
        case 4: .systemBlue
        case 5: .systemYellow
        case 6: .systemRed
        case 7: .systemOrange
        default: nil
        }
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}

/// 右クリックメニューの項目
struct MenuAction {
    var title: String
    var isDestructive = false
    var isSeparator = false
    var action: () -> Void = {}

    static let separator = MenuAction(title: "", isSeparator: true)

    func menuItem() -> NSMenuItem {
        if isSeparator { return .separator() }
        return ClosureMenuItem(title: title, action: action)
    }
}

private final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, action: @escaping () -> Void) {
        handler = action
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
    }

    required init(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    @objc private func run() {
        handler()
    }
}
