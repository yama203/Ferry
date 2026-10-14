import AppKit
import FerryKit
import SwiftUI

struct ServerEditView: View {
    let isNew: Bool
    let onSave: (ServerConfig, String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var draft: ServerConfig
    @State private var password: String
    @State private var portText: String
    @State private var test: TestState = .idle

    private enum TestState: Equatable {
        case idle, running
        case success(String)
        case failure(String)
    }

    init(server: ServerConfig, isNew: Bool, onSave: @escaping (ServerConfig, String) -> Void) {
        self.isNew = isNew
        self.onSave = onSave
        _draft = State(initialValue: server)
        _password = State(initialValue: isNew ? "" : Keychain.password(for: server.id) ?? "")
        _portText = State(initialValue: server.port.map(String.init) ?? "")
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("名前", text: $draft.name, prompt: Text(draft.host.isEmpty ? "例: 本番サーバー" : draft.host))
                    Picker("プロトコル", selection: $draft.serverProtocol) {
                        ForEach(ServerProtocol.allCases, id: \.self) { proto in
                            Text(proto.displayName).tag(proto)
                        }
                    }
                    TextField("ホスト", text: $draft.host, prompt: Text("ftp.example.com"))
                    TextField("ポート", text: $portText, prompt: Text(String(draft.serverProtocol.defaultPort)))
                    TextField("ユーザー名", text: $draft.username)
                    SecureField(draft.privateKeyPath == nil ? "パスワード" : "鍵のパスフレーズ", text: $password)
                }

                Section("詳細") {
                    TextField("リモートの初期フォルダ", text: optional(\.initialPath), prompt: Text("ログイン時のフォルダ"))
                    HStack {
                        TextField("ローカルの初期フォルダ", text: optional(\.localPath), prompt: Text("ホームフォルダ"))
                        Button("選択…") { chooseLocalFolder() }
                    }
                    if draft.serverProtocol != .sftp {
                        Toggle("パッシブモード", isOn: $draft.passiveMode)
                    }
                    if draft.serverProtocol == .ftps || draft.serverProtocol == .ftpsImplicit {
                        Toggle(isOn: $draft.verifyCertificate) {
                            Text("サーバー証明書を検証")
                            Text("共用サーバーで証明書エラーになる場合はオフにします")
                        }
                    }
                    if draft.serverProtocol == .sftp {
                        HStack {
                            TextField("秘密鍵", text: optional(\.privateKeyPath), prompt: Text("使わない（パスワード認証）"))
                            Button("選択…") { chooseKeyFile() }
                        }
                    }
                }
            }
            .formStyle(.grouped)

            Divider()
            if case .failure(let message) = test {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding([.horizontal, .top])
            }
            HStack {
                Button("接続テスト") { Task { await runTest() } }
                    .disabled(draft.host.isEmpty || test == .running)
                testResult
                Spacer()
                Button("キャンセル", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(isNew ? "追加" : "保存") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(draft.host.isEmpty)
            }
            .padding()
        }
        .frame(width: 520)
    }

    @ViewBuilder private var testResult: some View {
        switch test {
        case .idle:
            EmptyView()
        case .running:
            ProgressView().controlSize(.small)
        case .success(let message):
            Label(message, systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .lineLimit(1)
        case .failure:
            Label("接続できませんでした", systemImage: "xmark.circle.fill")
                .foregroundStyle(.red)
                .lineLimit(1)
        }
    }

    private func optional(_ keyPath: WritableKeyPath<ServerConfig, String?>) -> Binding<String> {
        Binding(
            get: { draft[keyPath: keyPath] ?? "" },
            set: { draft[keyPath: keyPath] = $0.isEmpty ? nil : $0 }
        )
    }

    private func normalized() -> ServerConfig {
        var server = draft
        server.host = server.host.trimmingCharacters(in: .whitespaces)
        server.username = server.username.trimmingCharacters(in: .whitespaces)
        server.port = Int(portText.trimmingCharacters(in: .whitespaces))
        if server.name.trimmingCharacters(in: .whitespaces).isEmpty {
            server.name = server.host
        }
        return server
    }

    private func runTest() async {
        test = .running
        let client = RemoteClient(server: normalized(), password: password.isEmpty ? nil : password)
        do {
            let home = try await client.homeDirectory()
            test = .success("接続できました（\(home)）")
        } catch {
            test = .failure(error.localizedDescription)
        }
    }

    private func save() {
        onSave(normalized(), password)
        dismiss()
    }

    private func chooseLocalFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        if panel.runModal() == .OK, let url = panel.url {
            draft.localPath = url.path
        }
    }

    private func chooseKeyFile() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.showsHiddenFiles = true
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        if panel.runModal() == .OK, let url = panel.url {
            draft.privateKeyPath = url.path
        }
    }
}
