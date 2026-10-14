import AppKit
import SwiftUI

struct SettingsView: View {
    var body: some View {
        TabView {
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
