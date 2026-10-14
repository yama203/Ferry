import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Transmit の書き出しファイルを選ぶ
@MainActor
enum TransmitImportPanel {
    static func choose() -> Result<TransmitImporter.Result, Error>? {
        let panel = NSOpenPanel()
        panel.message = "Transmit で書き出したサーバーのファイル（.transmitservers）を選んでください"
        panel.allowedContentTypes = [UTType(filenameExtension: "transmitservers") ?? .json, .json]
        panel.directoryURL = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        return Result { try TransmitImporter.read(url) }
    }
}

/// 読み込む内容の確認
struct ImportView: View {
    let result: TransmitImporter.Result

    @Environment(ServerStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var importedCount: Int?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Transmit から読み込む").font(.title2.bold())

            if let importedCount {
                Label("\(importedCount) 件の接続先を追加しました", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                Text("パスワードは書き出しファイルに含まれていないため、それぞれ最初に接続するときに入力します。「Transmit のパスワードを探す」でキーチェーンから読み込むこともできます。")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                summary
            }

            List(result.folders) { folder in
                HStack {
                    Image(systemName: folder.name == nil ? "server.rack" : "folder")
                        .foregroundStyle(.secondary)
                    Text(folder.name ?? "フォルダなし")
                    Spacer()
                    Text("\(folder.servers.count) 件").foregroundStyle(.secondary)
                }
            }
            .listStyle(.bordered)

            HStack {
                Spacer()
                if importedCount == nil {
                    Button("キャンセル", role: .cancel) { dismiss() }
                        .keyboardShortcut(.cancelAction)
                    Button("読み込む") { importedCount = store.importServers(result) }
                        .keyboardShortcut(.defaultAction)
                        .disabled(result.serverCount == 0)
                } else {
                    Button("閉じる") { dismiss() }
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(20)
        .frame(width: 460, height: 480)
    }

    private var summary: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("\(result.folders.filter { $0.name != nil }.count) 個のフォルダ、\(result.serverCount) 件の接続先を読み込みます。")
            let duplicates = store.duplicateCount(in: result)
            if duplicates > 0 {
                Text("すでに登録済みの \(duplicates) 件は飛ばします。")
                    .foregroundStyle(.secondary)
            }
            if result.unsupportedCount > 0 {
                Text("対応していないプロトコル（S3 など）の \(result.unsupportedCount) 件は読み込めません。")
                    .foregroundStyle(.secondary)
            }
        }
    }
}
