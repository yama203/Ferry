import AppKit
import SwiftUI

struct SettingsView: View {
    let vault: PasswordVault?

    var body: some View {
        TabView {
            SharedSettings()
                .tabItem { Label("会社の共有", systemImage: "person.2") }
            PasswordSyncSettings(kind: .personal, vault: vault)
                .tabItem { Label("パスワード同期", systemImage: "key.icloud") }
            SharedPasswordSettings()
                .tabItem { Label("会社のパスワード", systemImage: "person.badge.key") }
            ExcludeSettings()
                .tabItem { Label("除外", systemImage: "line.3.horizontal.decrease.circle") }
            EditorSettings()
                .tabItem { Label("エディタ", systemImage: "pencil") }
            NotificationSettings()
                .tabItem { Label("通知", systemImage: "bell") }
        }
        .frame(width: 520, height: 440)
    }
}

/// 転送・同期で除外するファイル（iCloud で他の Mac と共有）
private struct ExcludeSettings: View {
    @Environment(ServerStore.self) private var store
    @State private var selection: Int?
    @State private var newPattern = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("フォルダをアップロード・ダウンロード・同期するとき、次の名前のファイルやフォルダは含めません。* と ? が使えます（例: *.log）。直接選んだ項目は除外されません。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            List(selection: $selection) {
                ForEach(Array(store.excludeRules.patterns.enumerated()), id: \.offset) { index, pattern in
                    Text(pattern)
                        .font(.system(.body, design: .monospaced))
                        .tag(index)
                }
            }
            .listStyle(.bordered)
            .onDeleteCommand { removeSelected() }

            HStack {
                TextField("追加するパターン", text: $newPattern)
                    .onSubmit(add)
                Button("追加", action: add)
                    .disabled(newPattern.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("削除", action: removeSelected)
                    .disabled(selection == nil)
                Spacer()
                Button("初期設定に戻す") { store.setExcludeRules(.defaults) }
            }
        }
        .padding()
    }

    private func add() {
        let pattern = newPattern.trimmingCharacters(in: .whitespaces)
        guard !pattern.isEmpty, !store.excludeRules.patterns.contains(pattern) else { return }
        var rules = store.excludeRules
        rules.patterns.append(pattern)
        store.setExcludeRules(rules)
        newPattern = ""
    }

    private func removeSelected() {
        guard let index = selection, store.excludeRules.patterns.indices.contains(index) else { return }
        var rules = store.excludeRules
        rules.patterns.remove(at: index)
        store.setExcludeRules(rules)
        selection = nil
    }
}

/// 「エディタで開く」に使うアプリ（Mac ごとの設定）
private struct EditorSettings: View {
    @State private var appURL = EditorApp.url

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("サーバーのファイルを「エディタで開く」と、このアプリで開きます。保存するたびに自動でサーバーへアップロードします。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 10) {
                if let appURL {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: appURL.path))
                        .resizable()
                        .frame(width: 32, height: 32)
                    Text(FileManager.default.displayName(atPath: appURL.path))
                } else {
                    Text("ファイルの種類ごとの既定のアプリ")
                }
                Spacer()
                Button("選択…", action: choose)
                Button("既定に戻す") {
                    EditorApp.url = nil
                    appURL = EditorApp.url
                }
            }
            Spacer()
        }
        .padding()
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        if panel.runModal() == .OK, let url = panel.url {
            EditorApp.url = url
            appURL = url
        }
    }
}

/// 通知を出す場面（Mac ごとの設定）
private struct NotificationSettings: View {
    @AppStorage("notifyTransfers") private var notifyTransfers = true
    @AppStorage("notifySync") private var notifySync = true

    var body: some View {
        Form {
            Toggle("アップロード・ダウンロードが完了したとき", isOn: $notifyTransfers)
            Toggle("同期が完了したとき", isOn: $notifySync)
            Text("1 回のドラッグやボタン操作で始めた転送ごとにまとめて通知します。エディタで保存したときの自動アップロードでは通知しません。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding()
    }
}

/// 会社の共有の接続先のパスワードを、共有フォルダで同期する設定（store.shared.passwordVault を見る）
private struct SharedPasswordSettings: View {
    @Environment(ServerStore.self) private var store

    var body: some View {
        PasswordSyncSettings(kind: .shared, vault: store.shared.passwordVault)
    }
}

/// パスワードを同期する設定（自分: iCloud Drive、会社: 共有フォルダ）
private struct PasswordSyncSettings: View {
    enum Kind { case personal, shared }

    let kind: Kind
    let vault: PasswordVault?

    @Environment(ServerStore.self) private var store
    @State private var master = ""
    @State private var confirm = ""
    @State private var message: String?
    @State private var confirmingDisable = false
    @State private var addResult: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let vault {
                switch vault.state {
                case .off: setup(vault)
                case .locked: unlock(vault)
                case .unlocked: status(vault)
                }
            } else if kind == .personal {
                Label("iCloud Drive が使えないため、パスワードの同期はできません", systemImage: "icloud.slash")
            } else {
                Label("会社の共有フォルダを設定すると使えます（「会社の共有」のタブ）", systemImage: "person.2.slash")
            }
            if let message {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.red)
            }
            Spacer()
        }
        .padding()
    }

    /// この保管庫に入れる接続先
    private var serverIDs: [UUID] {
        kind == .personal ? store.servers.map(\.id) : store.shared.servers.map(\.id)
    }

    private func setup(_ vault: PasswordVault) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            if kind == .personal {
                Text("接続先のパスワードを暗号化して iCloud Drive に保存し、他の Mac や Windows 版と同期します。")
                Text("暗号化に使う「マスターパスワード」を決めてください。他の PC では最初に 1 回だけ入力します。忘れると同期したパスワードは読み出せません（各 PC に保存済みのパスワードは残ります）。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("会社の共有の接続先のパスワードを暗号化して共有フォルダに保存し、会社の人と同期します（Windows 版とも同じ）。")
                Text("暗号化に使う「会社のマスターパスワード」を決めて、会社の人に伝えてください。各自が最初に 1 回だけ入力します。会社のマスターパスワードを知っている人は、共有の接続先のパスワードをすべて使えます。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            SecureField(kind == .personal ? "マスターパスワード" : "会社のマスターパスワード", text: $master)
            SecureField("もう一度入力", text: $confirm)
            HStack {
                Spacer()
                Button("同期を始める") {
                    guard master.count >= 8 else { return message = "8 文字以上にしてください" }
                    guard master == confirm else { return message = "2 つの入力が一致しません" }
                    // この Mac に保存してあるパスワードを最初の中身にする
                    var initial: [UUID: String] = [:]
                    for server in store.servers where serverIDs.contains(server.id) {
                        if let password = Keychain.password(for: server.id) { initial[server.id] = password }
                    }
                    do {
                        try vault.enable(masterPassword: master, initial: initial)
                        message = nil
                        master = ""
                        confirm = ""
                    } catch {
                        message = error.localizedDescription
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(master.isEmpty)
            }
        }
    }

    private func unlock(_ vault: PasswordVault) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(kind == .personal ? "他の PC でパスワードの同期が設定されています" : "会社でパスワードの同期が設定されています",
                  systemImage: "lock.icloud")
                .font(.headline)
            Text("マスターパスワードを入力すると、この Mac でも同期したパスワードが使えるようになります。")
                .font(.callout)
                .foregroundStyle(.secondary)
            SecureField("マスターパスワード", text: $master)
                .onSubmit { unlockVault(vault) }
            HStack {
                Spacer()
                Button("この Mac で使う") { unlockVault(vault) }
                    .buttonStyle(.borderedProminent)
                    .disabled(master.isEmpty)
            }
        }
    }

    private func unlockVault(_ vault: PasswordVault) {
        do {
            try vault.unlock(masterPassword: master)
            message = nil
            master = ""
        } catch {
            message = error.localizedDescription
        }
    }

    private func status(_ vault: PasswordVault) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(kind == .personal ? "iCloud Drive で同期中" : "共有フォルダで同期中", systemImage: "checkmark.icloud.fill")
                .font(.headline)
                .foregroundStyle(.green)
            Text("\(vault.count) 件のパスワードを同期しています。パスワードを保存・変更すると、\(kind == .personal ? "他の PC" : "会社の人の Ferry")にも反映されます。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("この Mac のパスワードを同期に追加") {
                    let result = Passwords.addLocalPasswordsToVault(vault, serverIDs: serverIDs)
                    var text = "\(result.added) 件を追加しました。"
                    if !result.failures.isEmpty {
                        let detail = result.failures.map { "コード \($0.key)：\($0.value) 件" }.joined(separator: "、")
                        text += "読めなかったもの（\(detail)）があります。"
                    }
                    addResult = text
                }
                Spacer()
                Button("同期をやめる…", role: .destructive) { confirmingDisable = true }
            }
            if let addResult {
                Text(addResult)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
        }
        .confirmationDialog("パスワードの同期をやめますか？", isPresented: $confirmingDisable) {
            Button("同期をやめる", role: .destructive) { vault.disable() }
        } message: {
            Text(kind == .personal
                 ? "すべての PC で同期が止まります。iCloud Drive の暗号化したファイルは削除し、パスワードはこの Mac のキーチェーンに残します。"
                 : "会社の全員の同期が止まります。共有フォルダの暗号化したファイルは削除し、パスワードはこの Mac のキーチェーンに残します。")
        }
    }
}

/// 会社で共有する接続先（Dropbox などの共有フォルダ）
private struct SharedSettings: View {
    @Environment(ServerStore.self) private var store
    @State private var confirmingStop = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Dropbox などの共有フォルダを選ぶと、そこに保存した接続先が会社の全員の一覧に表示されます。Windows 版の Ferry とも同じフォルダを使えます。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text("パスワードは「会社のパスワード」のタブで会社のマスターパスワードを決めると、暗号化して共有できます。決めない場合は、それぞれが最初に接続するときに入力します。ローカルのフォルダやブックマークは PC ごとの設定です。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if let directory = store.shared.directory {
                Label("\(store.shared.servers.count) 件の接続先を共有しています", systemImage: "person.2.fill")
                    .font(.headline)
                Text(directory.path)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .lineLimit(2)
                    .truncationMode(.middle)
                HStack {
                    Button("フォルダを変更…", action: choose)
                    Button("Finder で開く") { NSWorkspace.shared.open(directory) }
                    Spacer()
                    Button("この Mac で共有をやめる…", role: .destructive) { confirmingStop = true }
                }
            } else {
                HStack {
                    Spacer()
                    Button("共有フォルダを選ぶ…", action: choose)
                        .buttonStyle(.borderedProminent)
                }
            }
            Spacer()
        }
        .padding()
        .confirmationDialog("この Mac で共有の接続先を使うのをやめますか？", isPresented: $confirmingStop) {
            Button("やめる", role: .destructive) { store.shared.setDirectory(nil) }
        } message: {
            Text("共有フォルダのファイルは消えないので、他の人には影響しません。")
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.message = "会社で共有するフォルダを選んでください（例: Dropbox の中の「Ferry共有」フォルダ）"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "このフォルダを使う"
        if panel.runModal() == .OK, let url = panel.url {
            store.shared.setDirectory(url)
        }
    }
}
