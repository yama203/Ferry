import FerryKit
import Foundation
import Observation

/// 1 つの接続先を開いている間の状態（左右のペインと転送）
@MainActor @Observable
final class ServerSession {
    enum Status: Equatable {
        case idle, connecting, connected
        case failed(String)
    }

    let server: ServerConfig
    let local: BrowserModel
    let remote: BrowserModel
    let transfers: TransferQueue
    private(set) var status: Status = .idle

    init(server: ServerConfig, password: String?) {
        self.server = server
        local = BrowserModel(side: .local)
        // 一覧用と転送用で接続を分け、転送中でもフォルダを移動できるようにする
        remote = BrowserModel(side: .remote, client: RemoteClient(server: server, password: password))
        transfers = TransferQueue(client: RemoteClient(server: server, password: password))
        transfers.onIdle = { [weak self] in
            guard let self else { return }
            Task {
                await self.local.refresh()
                await self.remote.refresh()
            }
        }
    }

    func connect() async {
        guard status != .connecting, let client = remote.client else { return }
        status = .connecting

        if local.path.isEmpty {
            let localPath = server.localPath.map { ($0 as NSString).expandingTildeInPath }
                ?? FileManager.default.homeDirectoryForCurrentUser.path
            await local.load(localPath, recordHistory: false)
        }

        do {
            let start: String
            if let initial = server.initialPath, !initial.isEmpty {
                start = initial
            } else {
                start = try await client.homeDirectory()
            }
            await remote.load(start, recordHistory: false)
            if let message = remote.errorMessage, remote.entries.isEmpty {
                status = .failed(message)
            } else {
                status = .connected
            }
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    func upload(_ entries: [FileEntry]) {
        transfers.enqueueUploads(entries.map { URL(fileURLWithPath: $0.path) }, toRemoteDirectory: remote.path)
    }

    func upload(urls: [URL]) {
        transfers.enqueueUploads(urls, toRemoteDirectory: remote.path)
    }

    func download(_ entries: [FileEntry]) async {
        await transfers.enqueueDownloads(entries, toLocalDirectory: URL(fileURLWithPath: local.path))
    }
}

/// 開いている接続をサーバーごとに保持する
@MainActor @Observable
final class SessionManager {
    // 画面描画中に作成するので変更通知の対象外にする
    @ObservationIgnored private var sessions: [UUID: ServerSession] = [:]

    func session(for server: ServerConfig) -> ServerSession {
        if let existing = sessions[server.id], existing.server == server {
            return existing
        }
        sessions[server.id]?.transfers.cancelAll()
        let session = ServerSession(server: server, password: Keychain.password(for: server.id))
        sessions[server.id] = session
        return session
    }

    func close(_ id: UUID) {
        sessions[id]?.transfers.cancelAll()
        sessions[id] = nil
    }
}
