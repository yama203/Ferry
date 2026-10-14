// アプリアイコンを描いて AppIcon.appiconset に書き出す。
//   swift App/Tools/make-icon.swift App/Ferry/Assets.xcassets/AppIcon.appiconset
import AppKit

let outputDirectory = URL(fileURLWithPath: CommandLine.arguments[1])

func drawIcon(size: CGFloat) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.scaleBy(x: size / 1024, y: size / 1024)

    // macOS のアイコングリッド: 1024 の中に 824 の角丸四角形
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

    // 影
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: NSColor.black.withAlphaComponent(0.3).cgColor)
    ctx.addPath(shape)
    ctx.setFillColor(NSColor.black.cgColor)
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()

    // 空: 夕方の海のような青緑のグラデーション
    let sky = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
        NSColor(red: 0.30, green: 0.78, blue: 0.92, alpha: 1).cgColor,
        NSColor(red: 0.10, green: 0.40, blue: 0.80, alpha: 1).cgColor,
    ] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(sky, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])

    // 海
    let sea = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
        NSColor(red: 0.05, green: 0.26, blue: 0.58, alpha: 1).cgColor,
        NSColor(red: 0.03, green: 0.15, blue: 0.40, alpha: 1).cgColor,
    ] as CFArray, locations: [0, 1])!
    ctx.saveGState()
    let seaPath = CGMutablePath()
    seaPath.move(to: CGPoint(x: 0, y: 380))
    for i in 0...8 {
        let x = CGFloat(i) * 128
        seaPath.addQuadCurve(to: CGPoint(x: x + 128, y: 380), control: CGPoint(x: x + 64, y: i % 2 == 0 ? 410 : 350))
    }
    seaPath.addLine(to: CGPoint(x: 1024, y: 0))
    seaPath.addLine(to: CGPoint(x: 0, y: 0))
    seaPath.closeSubpath()
    ctx.addPath(seaPath)
    ctx.clip()
    ctx.drawLinearGradient(sea, start: CGPoint(x: 512, y: 400), end: CGPoint(x: 512, y: 100), options: [])
    ctx.restoreGState()

    // 船体
    let white = NSColor.white.cgColor
    let hull = CGMutablePath()
    hull.move(to: CGPoint(x: 210, y: 470))
    hull.addLine(to: CGPoint(x: 830, y: 470))
    hull.addLine(to: CGPoint(x: 740, y: 330))
    hull.addLine(to: CGPoint(x: 290, y: 330))
    hull.closeSubpath()
    ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 14, color: NSColor.black.withAlphaComponent(0.25).cgColor)
    ctx.addPath(hull)
    ctx.setFillColor(white)
    ctx.fillPath()

    // 船体の帯
    ctx.setShadow(offset: .zero, blur: 0, color: nil)
    ctx.setFillColor(NSColor(red: 0.95, green: 0.42, blue: 0.30, alpha: 1).cgColor)
    let stripe = CGMutablePath()
    stripe.move(to: CGPoint(x: 232, y: 436))
    stripe.addLine(to: CGPoint(x: 808, y: 436))
    stripe.addLine(to: CGPoint(x: 795, y: 416))
    stripe.addLine(to: CGPoint(x: 245, y: 416))
    stripe.closeSubpath()
    ctx.addPath(stripe)
    ctx.fillPath()

    // 船室（上下 2 段）
    ctx.setShadow(offset: CGSize(width: 0, height: -4), blur: 10, color: NSColor.black.withAlphaComponent(0.2).cgColor)
    ctx.setFillColor(white)
    ctx.addPath(CGPath(roundedRect: CGRect(x: 300, y: 470, width: 420, height: 120), cornerWidth: 22, cornerHeight: 22, transform: nil))
    ctx.fillPath()
    ctx.addPath(CGPath(roundedRect: CGRect(x: 380, y: 590, width: 260, height: 100), cornerWidth: 20, cornerHeight: 20, transform: nil))
    ctx.fillPath()

    // 窓
    ctx.setShadow(offset: .zero, blur: 0, color: nil)
    ctx.setFillColor(NSColor(red: 0.12, green: 0.42, blue: 0.80, alpha: 1).cgColor)
    for i in 0..<5 {
        ctx.addPath(CGPath(roundedRect: CGRect(x: 335 + CGFloat(i) * 74, y: 512, width: 52, height: 44),
                           cornerWidth: 10, cornerHeight: 10, transform: nil))
    }
    for i in 0..<3 {
        ctx.addPath(CGPath(roundedRect: CGRect(x: 412 + CGFloat(i) * 74, y: 625, width: 52, height: 40),
                           cornerWidth: 10, cornerHeight: 10, transform: nil))
    }
    ctx.fillPath()

    // 煙突
    ctx.setFillColor(NSColor(red: 0.95, green: 0.42, blue: 0.30, alpha: 1).cgColor)
    ctx.addPath(CGPath(roundedRect: CGRect(x: 545, y: 690, width: 60, height: 80), cornerWidth: 10, cornerHeight: 10, transform: nil))
    ctx.fillPath()

    // 波しぶき
    ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.55).cgColor)
    ctx.setLineWidth(18)
    ctx.setLineCap(.round)
    for (y, inset) in [(270.0, 220.0), (200.0, 300.0)] {
        let wave = CGMutablePath()
        wave.move(to: CGPoint(x: inset, y: y))
        var x = inset
        var up = true
        while x < 1024 - inset {
            wave.addQuadCurve(to: CGPoint(x: x + 70, y: y), control: CGPoint(x: x + 35, y: y + (up ? 22 : -22)))
            x += 70
            up.toggle()
        }
        ctx.addPath(wave)
    }
    ctx.strokePath()

    ctx.restoreGState()
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

let sizes: [(points: Int, scale: Int)] = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)]
var images: [[String: String]] = []
try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
for (points, scale) in sizes {
    let pixels = points * scale
    let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
    let data = drawIcon(size: CGFloat(pixels)).representation(using: .png, properties: [:])!
    try data.write(to: outputDirectory.appendingPathComponent(name))
    images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
}
let contents: [String: Any] = ["images": images, "info": ["version": 1, "author": "xcode"]]
try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
    .write(to: outputDirectory.appendingPathComponent("Contents.json"))
print("✅ \(outputDirectory.path)")
