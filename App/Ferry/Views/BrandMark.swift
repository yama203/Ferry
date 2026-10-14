import SwiftUI

/// アプリアイコンと同じ折り紙の船を小さく描いたマーク
struct BrandMark: View {
    var size: CGFloat = 18

    var body: some View {
        Canvas { context, canvas in
            let scale = canvas.width / 1024
            func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
                // アイコンの座標は下が 0 なので上下を反転する
                CGPoint(x: x * scale, y: (1024 - y) * scale)
            }
            func fill(_ points: [(CGFloat, CGFloat)], _ color: Color) {
                var path = Path()
                path.addLines(points.map { point($0.0, $0.1) })
                path.closeSubpath()
                context.fill(path, with: .color(color))
            }

            let body = Path(roundedRect: CGRect(x: 0, y: 0, width: canvas.width, height: canvas.height),
                            cornerRadius: canvas.width * 0.225, style: .continuous)
            context.fill(body, with: .linearGradient(
                Gradient(colors: [Color(red: 0.23, green: 0.55, blue: 1.0), Color(red: 0.04, green: 0.13, blue: 0.40)]),
                startPoint: .zero, endPoint: CGPoint(x: 0, y: canvas.height)))

            // 小さいサイズでも形が分かるよう、アイコンより船を大きめに描く
            context.translateBy(x: -canvas.width * 0.04, y: canvas.height * 0.06)
            context.scaleBy(x: 1.08, y: 1.08)
            fill([(512, 800), (352, 500), (512, 500)], .white)
            fill([(512, 800), (512, 500), (672, 500)], Color(red: 0.76, green: 0.83, blue: 0.93))
            fill([(170, 520), (512, 470), (512, 290), (330, 290)], .white)
            fill([(512, 470), (854, 520), (694, 290), (512, 290)], Color(red: 0.89, green: 0.93, blue: 0.98))
        }
        .frame(width: size, height: size)
        .accessibilityLabel("Ferry")
    }
}
