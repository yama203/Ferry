import AppKit
import SwiftUI

/// 左右 2 ペインの分割。SwiftUI の HSplitView は初期幅を指定できないため NSSplitView を使う。
/// 初回は半分ずつ、以降は前回の位置を保存する。境界線をダブルクリックすると半分ずつに戻る。
struct PaneSplitView<Left: View, Right: View>: NSViewRepresentable {
    let autosaveName: String
    let minimumWidth: CGFloat
    @ViewBuilder let left: () -> Left
    @ViewBuilder let right: () -> Right

    func makeCoordinator() -> Coordinator { Coordinator(minimumWidth: minimumWidth) }

    func makeNSView(context: Context) -> EqualSplitView {
        let split = EqualSplitView()
        split.isVertical = true
        split.dividerStyle = .thin
        split.delegate = context.coordinator
        let leftHost = NSHostingView(rootView: AnyView(left()))
        let rightHost = NSHostingView(rootView: AnyView(right()))
        for host in [leftHost, rightHost] {
            // ホストの理想サイズを分割ビューに伝えない（幅が膨らむ原因になる）
            host.sizingOptions = []
            split.addArrangedSubview(host)
        }
        split.hasSavedPosition = UserDefaults.standard.object(forKey: "NSSplitView Subview Frames \(autosaveName)") != nil
        split.autosaveName = autosaveName
        context.coordinator.hosts = (leftHost, rightHost)
        return split
    }

    /// 中身は最初に作ったものを使い続ける。ペインの中身はそれぞれ自分でモデルの変更を追うので、
    /// ここで作り直すと選択を変えるたびに両方のペインを丸ごと描き直すことになり重い。
    /// （接続先が変わったときは SessionView ごと作り直される）
    func updateNSView(_ split: EqualSplitView, context: Context) {}

    final class Coordinator: NSObject, NSSplitViewDelegate {
        let minimumWidth: CGFloat
        var hosts: (left: NSHostingView<AnyView>, right: NSHostingView<AnyView>)?

        init(minimumWidth: CGFloat) {
            self.minimumWidth = minimumWidth
        }

        func splitView(_ splitView: NSSplitView, constrainMinCoordinate proposedMinimumPosition: CGFloat,
                       ofSubviewAt dividerIndex: Int) -> CGFloat {
            max(proposedMinimumPosition, minimumWidth)
        }

        func splitView(_ splitView: NSSplitView, constrainMaxCoordinate proposedMaximumPosition: CGFloat,
                       ofSubviewAt dividerIndex: Int) -> CGFloat {
            min(proposedMaximumPosition, splitView.bounds.width - minimumWidth)
        }

        // ウィンドウの幅を変えたときは左右の比率を保つ
        func splitView(_ splitView: NSSplitView, shouldAdjustSizeOfSubview view: NSView) -> Bool {
            true
        }
    }
}

final class EqualSplitView: NSSplitView {
    var hasSavedPosition = false
    private var didSetInitialPosition = false

    override func layout() {
        super.layout()
        // 保存された位置が無ければ、最初に幅が決まったところで半分ずつにする
        if !didSetInitialPosition, bounds.width > 0 {
            didSetInitialPosition = true
            if !hasSavedPosition {
                centerDivider()
            }
        }
    }

    func centerDivider() {
        setPosition((bounds.width - dividerThickness) / 2, ofDividerAt: 0)
    }

    override func mouseDown(with event: NSEvent) {
        // 境界線のダブルクリックで半分ずつに戻す
        if event.clickCount == 2, subviews.count > 1 {
            let point = convert(event.locationInWindow, from: nil)
            let dividerX = subviews[0].frame.maxX
            if abs(point.x - dividerX - dividerThickness / 2) <= 4 {
                centerDivider()
                return
            }
        }
        super.mouseDown(with: event)
    }
}
