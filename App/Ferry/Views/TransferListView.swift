import SwiftUI

struct TransferListView: View {
    let queue: TransferQueue

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("転送")
                    .font(.headline)
                let active = queue.activeCount
                if active > 0 {
                    Text("残り \(active) 件")
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("完了した項目を消去") { queue.clearFinished() }
                    .disabled(queue.items.allSatisfy(\.isActive))
                Button("すべて中止") { queue.cancelAll() }
                    .disabled(active == 0)
            }
            .controlSize(.small)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            Divider()
            List(queue.items) { item in
                TransferRow(item: item, queue: queue)
            }
            .listStyle(.plain)
        }
    }
}

private struct TransferRow: View {
    let item: TransferItem
    let queue: TransferQueue

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: item.direction == .upload ? "arrow.up.circle.fill" : "arrow.down.circle.fill")
                .font(.title3)
                .foregroundStyle(item.direction == .upload ? .blue : .green)

            VStack(alignment: .leading, spacing: 2) {
                Text(item.name)
                    .lineLimit(1)
                Text(item.direction == .upload ? "→ \(item.remotePath)" : "← \(item.remotePath)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            Spacer()

            status
                .frame(width: 200, alignment: .trailing)

            actionButton
                .frame(width: 20)
        }
        .padding(.vertical, 2)
    }

    @ViewBuilder private var status: some View {
        switch item.state {
        case .waiting:
            Text("待機中").foregroundStyle(.secondary)
        case .running:
            VStack(alignment: .trailing, spacing: 2) {
                ProgressView(value: item.fraction)
                Text("\(bytes(item.done)) / \(bytes(item.total))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
        case .finished:
            Label("完了", systemImage: "checkmark").foregroundStyle(.secondary)
        case .cancelled:
            Text("中止").foregroundStyle(.secondary)
        case .failed(let message):
            Label("失敗", systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.red)
                .help(message)
        }
    }

    @ViewBuilder private var actionButton: some View {
        if item.isActive {
            Button { queue.cancel(item) } label: { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                .help("中止")
        } else if case .failed = item.state {
            Button { queue.retry(item) } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.borderless)
                .help("再試行")
        } else if item.state == .cancelled {
            Button { queue.retry(item) } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.borderless)
                .help("再試行")
        }
    }

    private func bytes(_ value: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: value, countStyle: .file)
    }
}
