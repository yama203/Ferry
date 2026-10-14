// アプリアイコンを描く。採用しているのは B3（波に浮かぶ折り紙の船）。
//   swift App/Tools/make-icon.swift appicon App/Ferry/Assets.xcassets/AppIcon.appiconset
//   swift App/Tools/make-icon.swift concepts <出力フォルダ> [boat]   … デザイン案の比較画像
import AppKit

let mode = CommandLine.arguments[1]
let outputDirectory = URL(fileURLWithPath: CommandLine.arguments[2])
let space = CGColorSpaceCreateDeviceRGB()

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

func gradient(_ colors: [CGColor], _ locations: [CGFloat]? = nil) -> CGGradient {
    CGGradient(colorsSpace: space, colors: colors as CFArray, locations: locations)!
}

func polygon(_ ctx: CGContext, _ points: [(CGFloat, CGFloat)], fill: CGColor) {
    ctx.beginPath()
    ctx.move(to: CGPoint(x: points[0].0, y: points[0].1))
    for point in points.dropFirst() { ctx.addLine(to: CGPoint(x: point.0, y: point.1)) }
    ctx.closePath()
    ctx.setFillColor(fill)
    ctx.fillPath()
}

let iconBody = CGRect(x: 100, y: 100, width: 824, height: 824)
let iconShape = CGPath(roundedRect: iconBody, cornerWidth: 185, cornerHeight: 185, transform: nil)

/// 角丸の土台・影・上部のつや・縁取りを描き、中身は draw に任せる
func render(size: CGFloat, background: (CGContext) -> Void, draw: (CGContext) -> Void) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.scaleBy(x: size / 1024, y: size / 1024)

    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: color(0x000000, 0.28))
    ctx.addPath(iconShape)
    ctx.setFillColor(color(0x000000))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(iconShape)
    ctx.clip()
    background(ctx)
    draw(ctx)
    // 上部のつや
    ctx.drawLinearGradient(gradient([color(0xFFFFFF, 0.22), color(0xFFFFFF, 0)]),
                           start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 560), options: [])
    ctx.restoreGState()

    // 縁の細いハイライト
    ctx.addPath(CGPath(roundedRect: iconBody.insetBy(dx: 2, dy: 2), cornerWidth: 183, cornerHeight: 183, transform: nil))
    ctx.setStrokeColor(color(0xFFFFFF, 0.18))
    ctx.setLineWidth(4)
    ctx.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep
}

func linear(_ ctx: CGContext, _ colors: [CGColor], from: CGPoint, to: CGPoint) {
    ctx.drawLinearGradient(gradient(colors), start: from, end: to, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
}

// MARK: - A: 上下の矢印（転送）

func conceptA(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0x5B4CF5), color(0x1FB6F2)], from: CGPoint(x: 150, y: 924), to: CGPoint(x: 874, y: 100))
    }, draw: { ctx in
        ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 18, color: color(0x1A1060, 0.35))
        ctx.setStrokeColor(color(0xFFFFFF))
        ctx.setLineWidth(78)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        // 上向き（左）
        ctx.move(to: CGPoint(x: 400, y: 280)); ctx.addLine(to: CGPoint(x: 400, y: 730))
        ctx.move(to: CGPoint(x: 280, y: 610)); ctx.addLine(to: CGPoint(x: 400, y: 730)); ctx.addLine(to: CGPoint(x: 520, y: 610))
        ctx.strokePath()
        // 下向き（右）は少し透明に
        ctx.setStrokeColor(color(0xFFFFFF, 0.72))
        ctx.move(to: CGPoint(x: 624, y: 744)); ctx.addLine(to: CGPoint(x: 624, y: 294))
        ctx.move(to: CGPoint(x: 504, y: 414)); ctx.addLine(to: CGPoint(x: 624, y: 294)); ctx.addLine(to: CGPoint(x: 744, y: 414))
        ctx.strokePath()
    })
}

// MARK: - B: 折り紙の船（濃紺）

func origamiBoat(_ ctx: CGContext, light: CGColor, mid: CGColor, dark: CGColor, offsetY: CGFloat = 0) {
    let y = offsetY
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 22, color: color(0x000000, 0.25))
    // 船体（左右で明暗）
    polygon(ctx, [(220, 470 + y), (512, 470 + y), (512, 300 + y), (330, 300 + y)], fill: light)
    ctx.setShadow(offset: .zero, blur: 0, color: nil)
    polygon(ctx, [(512, 470 + y), (804, 470 + y), (694, 300 + y), (512, 300 + y)], fill: mid)
    // 中央の帆
    polygon(ctx, [(512, 800 + y), (370, 470 + y), (512, 470 + y)], fill: light)
    polygon(ctx, [(512, 800 + y), (512, 470 + y), (654, 470 + y)], fill: dark)
    // 左右の小さな三角
    polygon(ctx, [(220, 470 + y), (370, 470 + y), (300, 600 + y)], fill: mid)
    polygon(ctx, [(654, 470 + y), (804, 470 + y), (724, 600 + y)], fill: dark)
}

func conceptB(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0x2F7BF0), color(0x0A1F5C)], from: CGPoint(x: 512, y: 924), to: CGPoint(x: 512, y: 100))
    }, draw: { ctx in
        // 水面
        ctx.setFillColor(color(0x5FB0FF, 0.25))
        let water = CGMutablePath()
        water.move(to: CGPoint(x: 0, y: 330))
        water.addCurve(to: CGPoint(x: 1024, y: 330), control1: CGPoint(x: 340, y: 380), control2: CGPoint(x: 680, y: 280))
        water.addLine(to: CGPoint(x: 1024, y: 0)); water.addLine(to: CGPoint(x: 0, y: 0)); water.closeSubpath()
        ctx.addPath(water); ctx.fillPath()
        origamiBoat(ctx, light: color(0xFFFFFF), mid: color(0xD6E4F7), dark: color(0xAFC6EA), offsetY: 10)
    })
}

// MARK: - C: 折り紙の船（白地）

func conceptC(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0xFFFFFF), color(0xE4ECF7)], from: CGPoint(x: 512, y: 924), to: CGPoint(x: 512, y: 100))
    }, draw: { ctx in
        origamiBoat(ctx, light: color(0x3D8BFF), mid: color(0x2563D9), dark: color(0x163E9E), offsetY: 20)
        // 波
        ctx.setStrokeColor(color(0x3D8BFF, 0.45))
        ctx.setLineWidth(22)
        ctx.setLineCap(.round)
        let wave = CGMutablePath()
        wave.move(to: CGPoint(x: 250, y: 240))
        wave.addCurve(to: CGPoint(x: 774, y: 240), control1: CGPoint(x: 420, y: 290), control2: CGPoint(x: 600, y: 190))
        ctx.addPath(wave); ctx.strokePath()
    })
}

// MARK: - D: 帆（青緑のグラデーション）

func conceptD(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0x18D2B4), color(0x0866D8)], from: CGPoint(x: 150, y: 924), to: CGPoint(x: 874, y: 100))
    }, draw: { ctx in
        ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 20, color: color(0x003060, 0.3))
        // 主帆と前帆
        let main = CGMutablePath()
        main.move(to: CGPoint(x: 540, y: 820))
        main.addQuadCurve(to: CGPoint(x: 760, y: 400), control: CGPoint(x: 700, y: 640))
        main.addLine(to: CGPoint(x: 540, y: 400))
        main.closeSubpath()
        ctx.addPath(main); ctx.setFillColor(color(0xFFFFFF)); ctx.fillPath()
        let jib = CGMutablePath()
        jib.move(to: CGPoint(x: 500, y: 760))
        jib.addLine(to: CGPoint(x: 500, y: 400))
        jib.addQuadCurve(to: CGPoint(x: 290, y: 400), control: CGPoint(x: 380, y: 470))
        jib.closeSubpath()
        ctx.addPath(jib); ctx.setFillColor(color(0xFFFFFF, 0.82)); ctx.fillPath()
        // 船体
        let hull = CGMutablePath()
        hull.move(to: CGPoint(x: 250, y: 360))
        hull.addLine(to: CGPoint(x: 790, y: 360))
        hull.addQuadCurve(to: CGPoint(x: 660, y: 270), control: CGPoint(x: 740, y: 280))
        hull.addLine(to: CGPoint(x: 360, y: 270))
        hull.addQuadCurve(to: CGPoint(x: 250, y: 360), control: CGPoint(x: 290, y: 280))
        ctx.addPath(hull); ctx.setFillColor(color(0xFFFFFF)); ctx.fillPath()
        // 波
        ctx.setShadow(offset: .zero, blur: 0, color: nil)
        ctx.setStrokeColor(color(0xFFFFFF, 0.5))
        ctx.setLineWidth(20)
        ctx.setLineCap(.round)
        let wave = CGMutablePath()
        wave.move(to: CGPoint(x: 230, y: 210))
        for i in 0..<4 {
            let x = 230 + CGFloat(i) * 141
            wave.addQuadCurve(to: CGPoint(x: x + 141, y: 210), control: CGPoint(x: x + 70, y: i % 2 == 0 ? 245 : 175))
        }
        ctx.addPath(wave); ctx.strokePath()
    })
}

// MARK: - B2 / B3: 船らしくした折り紙の船

/// 実際の折り紙の船に近い形: 幅の広い船体（両端がとがる）と中央の帆 1 枚
func paperBoat(_ ctx: CGContext, x: CGFloat = 0, y: CGFloat = 0) {
    func p(_ px: CGFloat, _ py: CGFloat) -> (CGFloat, CGFloat) { (px + x, py + y) }
    let white = color(0xFFFFFF), light = color(0xE3ECF9), mid = color(0xC3D4EE), dark = color(0x9DB6E0)

    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000820, 0.35))
    // 帆（中央の大きな三角。折り目で左右の明暗を分ける）
    polygon(ctx, [p(512, 800), p(352, 500), p(512, 500)], fill: white)
    ctx.setShadow(offset: .zero, blur: 0, color: nil)
    polygon(ctx, [p(512, 800), p(512, 500), p(672, 500)], fill: mid)

    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000820, 0.35))
    // 船体（上辺が広く底が狭い台形。両端は外へとがる）
    polygon(ctx, [p(170, 520), p(512, 470), p(512, 290), p(330, 290)], fill: white)
    ctx.setShadow(offset: .zero, blur: 0, color: nil)
    polygon(ctx, [p(512, 470), p(854, 520), p(694, 290), p(512, 290)], fill: light)
    // 船べりの折り返し（両端から中央へ細くなる帯）
    polygon(ctx, [p(170, 520), p(512, 470), p(512, 430), p(260, 470)], fill: mid)
    polygon(ctx, [p(854, 520), p(512, 470), p(512, 430), p(764, 470)], fill: dark)
}

func water(_ ctx: CGContext, level: CGFloat) {
    ctx.setFillColor(color(0x5FB0FF, 0.22))
    let back = CGMutablePath()
    back.move(to: CGPoint(x: 0, y: level + 20))
    back.addCurve(to: CGPoint(x: 1024, y: level + 20), control1: CGPoint(x: 340, y: level + 70),
                  control2: CGPoint(x: 680, y: level - 30))
    back.addLine(to: CGPoint(x: 1024, y: 0)); back.addLine(to: CGPoint(x: 0, y: 0)); back.closeSubpath()
    ctx.addPath(back); ctx.fillPath()
}

func frontWater(_ ctx: CGContext, level: CGFloat) {
    // 船体の下側を隠す手前の波
    ctx.setFillColor(color(0x123A8C, 0.92))
    let front = CGMutablePath()
    front.move(to: CGPoint(x: 0, y: level))
    for i in 0..<4 {
        let x = CGFloat(i) * 256
        front.addQuadCurve(to: CGPoint(x: x + 256, y: level), control: CGPoint(x: x + 128, y: level + (i % 2 == 0 ? 36 : -36)))
    }
    front.addLine(to: CGPoint(x: 1024, y: 0)); front.addLine(to: CGPoint(x: 0, y: 0)); front.closeSubpath()
    ctx.addPath(front); ctx.fillPath()
    // 波の縁を明るくする
    let edge = CGMutablePath()
    edge.move(to: CGPoint(x: 0, y: level))
    for i in 0..<4 {
        let x = CGFloat(i) * 256
        edge.addQuadCurve(to: CGPoint(x: x + 256, y: level), control: CGPoint(x: x + 128, y: level + (i % 2 == 0 ? 36 : -36)))
    }
    ctx.setStrokeColor(color(0x8CC8FF, 0.55))
    ctx.setLineWidth(10)
    ctx.setLineCap(.round)
    ctx.addPath(edge)
    ctx.strokePath()
}

func conceptB2(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0x2F7BF0), color(0x0A1F5C)], from: CGPoint(x: 512, y: 924), to: CGPoint(x: 512, y: 100))
    }, draw: { ctx in
        water(ctx, level: 330)
        paperBoat(ctx, y: 10)
        // 水面の反射
        ctx.setStrokeColor(color(0xFFFFFF, 0.35))
        ctx.setLineWidth(16)
        ctx.setLineCap(.round)
        for (y, inset) in [(250.0, 330.0), (200.0, 420.0)] {
            ctx.move(to: CGPoint(x: inset, y: y)); ctx.addLine(to: CGPoint(x: 1024 - inset, y: y))
        }
        ctx.strokePath()
    })
}

func conceptB3(_ size: CGFloat) -> NSBitmapImageRep {
    render(size: size, background: { ctx in
        linear(ctx, [color(0x3B8CFF), color(0x0B2266)], from: CGPoint(x: 512, y: 924), to: CGPoint(x: 512, y: 100))
    }, draw: { ctx in
        // 少し傾けて、波に浮かんでいる感じにする
        ctx.saveGState()
        ctx.translateBy(x: 512, y: 400)
        ctx.rotate(by: -0.08)
        ctx.translateBy(x: -512, y: -400)
        paperBoat(ctx, y: 20)
        ctx.restoreGState()
        frontWater(ctx, level: 330)
    })
}

// MARK: - 書き出し

if mode == "appicon" {
    let sizes: [(points: Int, scale: Int)] = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2),
                                              (256, 1), (256, 2), (512, 1), (512, 2)]
    var images: [[String: String]] = []
    try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
    for (points, scale) in sizes {
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try conceptB3(CGFloat(points * scale)).representation(using: .png, properties: [:])!
            .write(to: outputDirectory.appendingPathComponent(name))
        images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
    }
    try JSONSerialization.data(withJSONObject: ["images": images, "info": ["version": 1, "author": "xcode"]],
                               options: [.prettyPrinted, .sortedKeys])
        .write(to: outputDirectory.appendingPathComponent("Contents.json"))
    print("✅ \(outputDirectory.path)")
    exit(0)
}

let concepts: [(String, (CGFloat) -> NSBitmapImageRep)] = CommandLine.arguments.count > 3 && CommandLine.arguments[3] == "boat"
    ? [("B", conceptB), ("B2", conceptB2), ("B3", conceptB3)]
    : [("A", conceptA), ("B", conceptB), ("C", conceptC), ("D", conceptD)]
try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
for (name, draw) in concepts {
    try draw(1024).representation(using: .png, properties: [:])!
        .write(to: outputDirectory.appendingPathComponent("concept-\(name).png"))
}

// 比較用: 大きいサイズ（512）と Dock 程度（128）、Finder の一覧程度（32）を並べる
let cell: CGFloat = 560
let sheet = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(cell * CGFloat(concepts.count)), pixelsHigh: 820, bitsPerSample: 8,
                             samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                             bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: sheet)
NSColor(white: 0.96, alpha: 1).setFill()
NSRect(x: 0, y: 0, width: cell * CGFloat(concepts.count), height: 820).fill()
for (index, (name, draw)) in concepts.enumerated() {
    let x = CGFloat(index) * cell
    NSImage(cgImage: draw(1024).cgImage!, size: .zero).draw(in: NSRect(x: x + 24, y: 260, width: 512, height: 512))
    NSImage(cgImage: draw(256).cgImage!, size: .zero).draw(in: NSRect(x: x + 150, y: 90, width: 128, height: 128))
    NSImage(cgImage: draw(64).cgImage!, size: .zero).draw(in: NSRect(x: x + 330, y: 138, width: 32, height: 32))
    let label = NSAttributedString(string: name, attributes: [.font: NSFont.boldSystemFont(ofSize: 44),
                                                               .foregroundColor: NSColor.darkGray])
    label.draw(at: NSPoint(x: x + 30, y: 120))
}
NSGraphicsContext.restoreGraphicsState()
let sheetName = concepts.count == 3 ? "boats.png" : "concepts.png"
try sheet.representation(using: .png, properties: [:])!.write(to: outputDirectory.appendingPathComponent(sheetName))
print("✅ \(outputDirectory.path)")
