import FerryKit
import SwiftUI

/// 暗号化なしの FTP の接続先（自分のもの・会社の共有）が FTPS（Explicit）でつながるかをまとめて調べ、選んだものを切り替える
struct FTPSCheckView: View {
    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @Environment(\.dismiss) private var dismiss

    /// 開いたときの FTP の接続先（調べている間に一覧が変わっても、行が入れ替わらないように）
    @State private var candidates: [ServerConfig] = []
    /// 調べた結果と、そのときの接続先（調べた後に設定が変わっていたら切り替えない）
    @State private var results: [UUID: (server: ServerConfig, result: FTPSCheckResult)] = [:]
    @State private var checking: Set<UUID> = []
    @State private var selected: Set<UUID> = []
    @State private var runTask: Task<Void, Never>?
    /// 切り替えた結果（件数と、切り替えられなかったもの）
    @State private var switchReport: String?

    /// 同時に調べる数（共用サーバーの同時接続の上限に当たらないよう少なめ）
    private static let concurrency = 3

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 6) {
                Text("FTPS に切り替えられる接続先を調べる")
                    .font(.headline)
                Text("暗号化なしの FTP では、パスワードもファイルの中身もそのまま流れます。同じホスト・ユーザー名・保存したパスワードで、FTPS（Explicit）でログインできるかを調べます（今の接続には影響しません）。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if candidates.contains(where: { store.isShared($0.id) }) {
                    Label("会社の共有の接続先を切り替えると、会社の全員の接続設定が変わります。", systemImage: "person.2")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
            .padding()

            Divider()
            if candidates.isEmpty {
                ContentUnavailableView("暗号化なしの FTP の接続先はありません", systemImage: "lock.fill")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(candidates) { server in
                        row(server)
                    }
                }
                .listStyle(.inset)
            }

            Divider()
            if let switchReport {
                Text(switchReport)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding([.horizontal, .top])
            }
            HStack {
                if runTask != nil {
                    Button("中止") { runTask?.cancel() }
                    ProgressView().controlSize(.small)
                    Text("\(results.count) / \(candidates.count) 件")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                } else {
                    Button(results.isEmpty ? "調べる" : "もう一度調べる") { start() }
                        .disabled(candidates.isEmpty)
                }
                Spacer()
                Button("閉じる", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("選んだ接続先を FTPS に切り替える") { switchSelected() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(switchable.isEmpty || runTask != nil)
            }
            .padding()
        }
        .frame(width: 640, height: 520)
        .onAppear {
            candidates = store.servers.filter { $0.serverProtocol == .ftp }
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
        .onDisappear { runTask?.cancel() }
    }

    /// 選んでいて、切り替えられると分かっているもの
    private var switchable: [UUID] {
        candidates.map(\.id).filter { id in
            selected.contains(id) && results[id].flatMap { FTPSUpgrade.verifiesCertificate(after: $0.result) } != nil
        }
    }

    @ViewBuilder private func row(_ server: ServerConfig) -> some View {
        let entry = results[server.id]
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text(server.name.isEmpty ? server.host : server.name)
                        .lineLimit(1)
                    if store.isShared(server.id) {
                        Image(systemName: "person.2.fill")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .help("会社で共有している接続先")
                    }
                }
                Text("\(server.username)@\(server.host):\(server.effectivePort)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                if let entry {
                    Text(entry.result.explanation(host: server.host))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                    if case .certificateUnverified = entry.result {
                        Toggle("証明書を検証せずに FTPS にする", isOn: selection(server.id))
                            .font(.caption)
                    }
                }
            }
            Spacer(minLength: 8)
            status(server, entry?.result)
        }
        .padding(.vertical, 3)
    }

    @ViewBuilder private func status(_ server: ServerConfig, _ result: FTPSCheckResult?) -> some View {
        if checking.contains(server.id) {
            ProgressView().controlSize(.small)
        } else if let result {
            HStack(spacing: 6) {
                Label(result.title, systemImage: result.systemImage)
                    .font(.callout)
                    .foregroundStyle(color(result))
                if case .available = result {
                    Toggle("切り替える", isOn: selection(server.id))
                        .labelsHidden()
                        .help("FTPS に切り替える")
                }
            }
        } else {
            Text(runTask == nil ? "" : "待機中")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    private func color(_ result: FTPSCheckResult) -> Color {
        switch result {
        case .available: .green
        case .certificateUnverified: .orange
        case .unsupported, .failed: .secondary
        }
    }

    private func selection(_ id: UUID) -> Binding<Bool> {
        Binding(get: { selected.contains(id) },
                set: { if $0 { selected.insert(id) } else { selected.remove(id) } })
    }

    // MARK: - 調べる

    private func start() {
        results = [:]
        selected = []
        switchReport = nil
        // 調べる直前の設定で調べる（パスワードは保存してあるものだけ。FTPSUpgrade.check が取る）
        let targets = candidates.compactMap { store.server(withID: $0.id) }.filter { $0.serverProtocol == .ftp }
        runTask = Task {
            await withTaskGroup(of: (ServerConfig, FTPSCheckResult).self) { group in
                var pending = targets[...]
                var running = 0
                while true {
                    while running < Self.concurrency, !Task.isCancelled, let server = pending.popFirst() {
                        checking.insert(server.id)
                        group.addTask { await (server, FTPSUpgrade.check(server)) }
                        running += 1
                    }
                    guard let (server, result) = await group.next() else { break }
                    running -= 1
                    checking.remove(server.id)
                    results[server.id] = (server, result)
                    // 証明書を検証できるものだけ最初から選ぶ（検証しないものは、説明を読んで選んでもらう）
                    if case .available = result { selected.insert(server.id) }
                }
            }
            checking = []
            runTask = nil
        }
    }

    // MARK: - 切り替える

    private func switchSelected() {
        var switched = 0
        var problems: [String] = []
        for id in switchable {
            guard let entry = results[id] else { continue }
            do {
                try FTPSUpgrade.switchToFTPS(entry.server, result: entry.result, store: store, sessions: sessions)
                switched += 1
                selected.remove(id)
                results[id] = nil
            } catch {
                problems.append("\(entry.server.name)：\(error.localizedDescription)")
            }
        }
        // 切り替えたものは一覧から外す（もう FTP ではないので）
        candidates.removeAll { store.server(withID: $0.id)?.serverProtocol != .ftp }
        switchReport = (["\(switched) 件を FTPS に切り替えました。"] + problems).joined(separator: "\n")
    }
}

/// 接続中の画面に出す「暗号化されていません」の印。押すと、この接続先が FTPS でつながるかを調べて切り替えられる
struct FTPSBadge: View {
    let session: ServerSession

    @Environment(ServerStore.self) private var store
    @Environment(SessionManager.self) private var sessions
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented = true
        } label: {
            Label("暗号化されていません", systemImage: "lock.open")
                .labelStyle(.titleAndIcon)
                .foregroundStyle(.orange)
        }
        .help("FTP の接続はパスワードもファイルの中身も暗号化されません。FTPS に切り替えられるかを調べます")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            FTPSCheckPopover(session: session, store: store, sessions: sessions) {
                isPresented = false
            }
        }
    }
}

private struct FTPSCheckPopover: View {
    let session: ServerSession
    let store: ServerStore
    let sessions: SessionManager
    let close: () -> Void

    @State private var result: FTPSCheckResult?
    @State private var isChecking = false
    @State private var switchError: String?
    /// 閉じたときに印を消す理由（開いている間に印を消すと、ポップオーバーごと消えて結果を読めないので）
    @State private var hideOnClose: FTPSNotice.Reason?

    private var server: ServerConfig { session.server }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("この接続は暗号化されていません", systemImage: "lock.open")
                .font(.headline)
            Text("FTP ではパスワードもファイルの中身もそのまま流れます。サーバーが FTPS（Explicit）に対応していれば、切り替えをおすすめします。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if isChecking {
                HStack {
                    ProgressView().controlSize(.small)
                    Text("FTPS でつながるか調べています…")
                        .font(.callout)
                }
            } else if let result {
                Label(result.title, systemImage: result.systemImage)
                    .font(.callout.weight(.semibold))
                Text(result.explanation(host: server.host))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if result.flatMap(FTPSUpgrade.verifiesCertificate(after:)) != nil, store.isShared(server.id) {
                Label("会社の共有の接続先なので、切り替えると会社の全員の接続設定が変わります。", systemImage: "person.2")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let switchError {
                Text(switchError)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Button("今後表示しない") {
                    hideOnClose = .dismissed
                    close()
                }
                Spacer()
                actions
            }
        }
        .padding()
        .frame(width: 380)
        .onDisappear {
            if let hideOnClose {
                FTPSNotice.shared.hide(server.id, because: hideOnClose)
            }
        }
    }

    @ViewBuilder private var actions: some View {
        switch result {
        case nil:
            Button("FTPS でつながるか調べる") { Task { await check() } }
                .buttonStyle(.borderedProminent)
                .disabled(isChecking)
        case .available?:
            Button("FTPS に切り替える") { switchToFTPS() }
                .buttonStyle(.borderedProminent)
        case .certificateUnverified?:
            Button("証明書を検証せずに FTPS にする") { switchToFTPS() }
        case .unsupported?:
            Button("閉じる", action: close)
        case .failed?:
            Button("もう一度調べる") { Task { await check() } }
                .disabled(isChecking)
        }
    }

    private func check() async {
        isChecking = true
        switchError = nil
        let checked = await FTPSCheck.check(server, password: Passwords.storedPassword(for: server.id))
        isChecking = false
        result = checked
        if case .unsupported = checked {
            hideOnClose = .unsupported
        }
    }

    private func switchToFTPS() {
        guard let result else { return }
        do {
            // 保存すると、表示中の接続は閉じて FTPS でつなぎ直す
            try FTPSUpgrade.switchToFTPS(server, result: result, store: store, sessions: sessions)
            close()
        } catch {
            switchError = error.localizedDescription
        }
    }
}
