import AppKit
import SwiftUI

struct SettingsView: View {
    let vault: PasswordVault?

    var body: some View {
        TabView {
            PasswordSyncSettings(vault: vault)
                .tabItem { Label("パスワード同期", systemImage: "key.icloud") }
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

/// パスワードを iCloud Drive で同期する設定
private struct PasswordSyncSettings: View {
    let vault: PasswordVault?

    @Environment(ServerStore.self) private var store
    @State private var master = ""
    @State private var confirm = ""
    @State private var message: String?
    @State private var confirmingDisable = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let vault {
                switch vault.state {
                case .off: setup(vault)
                case .locked: unlock(vault)
                case .unlocked: status(vault)
                }
            } else {
                Label("iCloud Drive が使えないため、パスワードの同期はできません", systemImage: "icloud.slash")
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

    private func setup(_ vault: PasswordVault) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("接続先のパスワードを暗号化して iCloud Drive に保存し、他の Mac と同期します。")
            Text("暗号化に使う「マスターパスワード」を決めてください。他の Mac では最初に 1 回だけ入力します。忘れると同期したパスワードは読み出せません（各 Mac に保存済みのパスワードは残ります）。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            SecureField("マスターパスワード", text: $master)
            SecureField("もう一度入力", text: $confirm)
            HStack {
                Spacer()
                Button("同期を始める") {
                    guard master.count >= 8 else { return message = "8 文字以上にしてください" }
                    guard master == confirm else { return message = "2 つの入力が一致しません" }
                    // この Mac に保存してあるパスワードを最初の中身にする
                    var initial: [UUID: String] = [:]
                    for server in store.servers {
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
            Label("他の Mac でパスワードの同期が設定されています", systemImage: "lock.icloud")
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
            Label("iCloud Drive で同期中", systemImage: "checkmark.icloud.fill")
                .font(.headline)
                .foregroundStyle(.green)
            Text("\(vault.count) 件のパスワードを同期しています。パスワードを保存・変更すると、他の Mac にも反映されます。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("同期をやめる…", role: .destructive) { confirmingDisable = true }
            }
        }
        .confirmationDialog("パスワードの同期をやめますか？", isPresented: $confirmingDisable) {
            Button("同期をやめる", role: .destructive) { vault.disable() }
        } message: {
            Text("すべての Mac で同期が止まります。iCloud Drive の暗号化したファイルは削除し、パスワードはこの Mac のキーチェーンに残します。")
        }
    }
}
