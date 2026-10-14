import SwiftUI

/// パスワードが保存されていない接続先を開いたときの入力画面
struct PasswordPrompt: View {
    let session: ServerSession
    let message: String?

    @State private var password = ""
    @State private var save = true
    @State private var isSearching = false
    @State private var searchFailed = false
    @FocusState private var focused: Bool
    @State private var master = ""
    @State private var unlockError: String?

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "key.fill")
                .font(.system(size: 32))
                .foregroundStyle(.secondary)
            Text("\(session.server.username)@\(session.server.host) のパスワード")
                .font(.headline)
                .multilineTextAlignment(.center)
            if let message {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
            }

            SecureField("パスワード", text: $password)
                .textFieldStyle(.roundedBorder)
                .frame(width: 260)
                .focused($focused)
                .onSubmit(connect)
            Toggle("キーチェーンに保存", isOn: $save)

            HStack {
                Button {
                    Task { await searchKeychain() }
                } label: {
                    if isSearching {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Transmit のパスワードを探す")
                    }
                }
                .help("Transmit などがキーチェーンに保存したパスワードを探します（macOS が許可を求めます）")
                .disabled(isSearching)

                Button("接続", action: connect)
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(password.isEmpty)
            }
            if searchFailed {
                Text("キーチェーンに見つかりませんでした")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            // 他の Mac でパスワード同期を始めていて、この Mac ではまだ解除していない場合
            if Passwords.vault?.state == .locked {
                Divider().frame(width: 300).padding(.vertical, 6)
                Label("他の Mac で同期したパスワードを使う", systemImage: "key.icloud")
                    .font(.headline)
                HStack {
                    SecureField("マスターパスワード", text: $master)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 200)
                        .onSubmit(unlockVault)
                    Button("解除", action: unlockVault)
                        .disabled(master.isEmpty)
                }
            }
            // 解除後も結果を表示するため、上の if の外に置く
            if let unlockError {
                Text(unlockError).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { focused = true }
    }

    private func unlockVault() {
        guard let vault = Passwords.vault else { return }
        do {
            try vault.unlock(masterPassword: master)
            unlockError = nil
            if let synced = vault.password(for: session.server.id) {
                Task { await session.connect(withPassword: synced, save: false) }
            } else {
                unlockError = "解除しました。この接続先のパスワードはまだ同期されていないので、上に入力してください"
            }
        } catch {
            unlockError = error.localizedDescription
        }
    }

    private func connect() {
        guard !password.isEmpty else { return }
        let password = password
        let save = save
        Task { await session.connect(withPassword: password, save: save) }
    }

    private func searchKeychain() async {
        isSearching = true
        searchFailed = false
        let host = session.server.host
        let account = session.server.username
        // macOS の確認ダイアログが出ている間、画面が固まらないようにする
        let found = await Task.detached { Keychain.findInternetPassword(host: host, account: account) }.value
        isSearching = false
        if let found {
            password = found
            connect()
        } else {
            searchFailed = true
        }
    }
}
