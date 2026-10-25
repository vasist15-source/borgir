// Draws the Borgir app icon, a burger whose buns are two controller halves, and writes Borgir.icns (or, given a .png
// path, one 1024-pixel PNG for previewing).
//
//   swift distribution/macos/make_icon.swift distribution/macos/Borgir.icns

import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Borgir.icns"

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> NSColor {
    NSColor(calibratedRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

let outline = color(0x2B1A12)

func render(size: Int) -> Data {
    let canvas = CGFloat(size)
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: canvas, height: canvas)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let context = NSGraphicsContext.current!.cgContext

    // Everything below is laid out on a 1024-point canvas, y up.
    let unit = canvas / 1024
    context.scaleBy(x: unit, y: unit)

    // macOS icon grid: an 824-point tile centred in 1024 with continuous corners.
    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = NSBezierPath(roundedRect: tile, xRadius: 185, yRadius: 185)

    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -10 * unit), blur: 28 * unit, color: NSColor.black.withAlphaComponent(0.3).cgColor)
    NSColor.black.setFill()
    tilePath.fill()
    context.restoreGState()

    context.saveGState()
    tilePath.addClip()

    NSGradient(colors: [color(0xFFF3DC), color(0xFFD9A0)])!.draw(in: tile, angle: -90)
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.55), NSColor.white.withAlphaComponent(0)])!
        .draw(fromCenter: NSPoint(x: 512, y: 600), radius: 0, toCenter: NSPoint(x: 512, y: 600), radius: 430, options: [])

    // Shadow under the burger.
    context.saveGState()
    context.setShadow(offset: .zero, blur: 30 * unit, color: color(0x8A4B12, 0.45).cgColor)
    color(0x8A4B12, 0.35).setFill()
    NSBezierPath(ovalIn: CGRect(x: 232, y: 228, width: 560, height: 56)).fill()
    context.restoreGState()

    drawBottomBun()
    drawPatty()
    drawCheese()
    drawLettuce()
    drawTopBun()

    context.restoreGState()

    // Rim.
    color(0xFFFFFF, 0.35).setStroke()
    tilePath.lineWidth = 6
    tilePath.stroke()

    NSGraphicsContext.restoreGraphicsState()

    return rep.representation(using: .png, properties: [:])!
}

/// Fills a shape with a vertical gradient, then outlines it.
func paint(_ path: NSBezierPath, top: NSColor, bottom: NSColor, lineWidth: CGFloat = 10) {
    NSGraphicsContext.saveGraphicsState()
    path.addClip()
    NSGradient(colors: [top, bottom])!.draw(in: path.bounds, angle: -90)
    NSGraphicsContext.restoreGraphicsState()

    outline.setStroke()
    path.lineWidth = lineWidth
    path.lineJoinStyle = .round
    path.stroke()
}

/// A controller half lying on its side: one long edge flat (the rail), the other rounded like a bun.
func controllerHalf(_ rect: CGRect, roundedTop: Bool) -> NSBezierPath {
    let big = rect.height * 0.5
    let small: CGFloat = 26
    let path = NSBezierPath()

    if roundedTop {
        path.move(to: NSPoint(x: rect.minX + small, y: rect.minY))
        path.line(to: NSPoint(x: rect.maxX - small, y: rect.minY))
        path.appendArc(withCenter: NSPoint(x: rect.maxX - small, y: rect.minY + small), radius: small, startAngle: 270, endAngle: 0)
        path.line(to: NSPoint(x: rect.maxX, y: rect.maxY - big))
        path.appendArc(withCenter: NSPoint(x: rect.maxX - big, y: rect.maxY - big), radius: big, startAngle: 0, endAngle: 90)
        path.line(to: NSPoint(x: rect.minX + big, y: rect.maxY))
        path.appendArc(withCenter: NSPoint(x: rect.minX + big, y: rect.maxY - big), radius: big, startAngle: 90, endAngle: 180)
        path.line(to: NSPoint(x: rect.minX, y: rect.minY + small))
        path.appendArc(withCenter: NSPoint(x: rect.minX + small, y: rect.minY + small), radius: small, startAngle: 180, endAngle: 270)
    } else {
        path.move(to: NSPoint(x: rect.minX + big, y: rect.minY))
        path.line(to: NSPoint(x: rect.maxX - big, y: rect.minY))
        path.appendArc(withCenter: NSPoint(x: rect.maxX - big, y: rect.minY + big), radius: big, startAngle: 270, endAngle: 0)
        path.line(to: NSPoint(x: rect.maxX, y: rect.maxY - small))
        path.appendArc(withCenter: NSPoint(x: rect.maxX - small, y: rect.maxY - small), radius: small, startAngle: 0, endAngle: 90)
        path.line(to: NSPoint(x: rect.minX + small, y: rect.maxY))
        path.appendArc(withCenter: NSPoint(x: rect.minX + small, y: rect.maxY - small), radius: small, startAngle: 90, endAngle: 180)
        path.line(to: NSPoint(x: rect.minX, y: rect.minY + big))
        path.appendArc(withCenter: NSPoint(x: rect.minX + big, y: rect.minY + big), radius: big, startAngle: 180, endAngle: 270)
    }

    path.close()
    return path
}

func stick(at center: NSPoint, radius: CGFloat) {
    let base = NSBezierPath(ovalIn: CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2))
    color(0x26201E).setFill()
    base.fill()
    outline.setStroke()
    base.lineWidth = 6
    base.stroke()

    let cap = radius * 0.68
    let top = NSBezierPath(ovalIn: CGRect(x: center.x - cap, y: center.y - cap + 4, width: cap * 2, height: cap * 2))
    NSGraphicsContext.saveGraphicsState()
    top.addClip()
    NSGradient(colors: [color(0x5A5250), color(0x34302E)])!.draw(in: top.bounds, angle: -90)
    NSGraphicsContext.restoreGraphicsState()
}

func buttons(around center: NSPoint, spacing: CGFloat, radius: CGFloat) {
    for (dx, dy) in [(0, 1), (1, 0), (0, -1), (-1, 0)] as [(CGFloat, CGFloat)] {
        let button = NSBezierPath(ovalIn: CGRect(x: center.x + dx * spacing - radius, y: center.y + dy * spacing - radius,
                                                 width: radius * 2, height: radius * 2))
        color(0x2E2826).setFill()
        button.fill()
        color(0xFFFFFF, 0.28).setFill()
        NSBezierPath(ovalIn: CGRect(x: center.x + dx * spacing - radius * 0.45, y: center.y + dy * spacing + radius * 0.1,
                                    width: radius * 0.8, height: radius * 0.6)).fill()
    }
}

func pill(_ rect: CGRect) {
    let path = NSBezierPath(roundedRect: rect, xRadius: rect.height / 2, yRadius: rect.height / 2)
    color(0x2E2826).setFill()
    path.fill()
}

func drawTopBun() {
    let rect = CGRect(x: 206, y: 528, width: 612, height: 222)
    let bun = controllerHalf(rect, roundedTop: true)
    paint(bun, top: color(0xFF6B6B), bottom: color(0xE8343F), lineWidth: 12)

    // Gloss on the dome.
    color(0xFFFFFF, 0.35).setFill()
    NSBezierPath(roundedRect: CGRect(x: 300, y: 688, width: 250, height: 30), xRadius: 15, yRadius: 15).fill()

    stick(at: NSPoint(x: 346, y: 618), radius: 54)
    buttons(around: NSPoint(x: 660, y: 616), spacing: 42, radius: 17)
    pill(CGRect(x: 494, y: 660, width: 40, height: 12))
}

func drawLettuce() {
    let left: CGFloat = 186, right: CGFloat = 838, top: CGFloat = 540, bottom: CGFloat = 508
    let path = NSBezierPath()
    path.move(to: NSPoint(x: left, y: top))
    path.line(to: NSPoint(x: right, y: top))
    path.line(to: NSPoint(x: right, y: bottom))

    // Scalloped lower edge, right to left.
    let waves = 9
    let step = (right - left) / CGFloat(waves)
    var x = right

    for _ in 0..<waves {
        path.curve(to: NSPoint(x: x - step, y: bottom),
                   controlPoint1: NSPoint(x: x - step * 0.2, y: bottom - 34),
                   controlPoint2: NSPoint(x: x - step * 0.8, y: bottom - 34))
        x -= step
    }

    path.close()
    paint(path, top: color(0x8EE04C), bottom: color(0x4FAE2A), lineWidth: 10)
}

func drawCheese() {
    let left: CGFloat = 214, right: CGFloat = 812, top: CGFloat = 508, edge: CGFloat = 478
    let path = NSBezierPath()
    path.move(to: NSPoint(x: left, y: top))
    path.line(to: NSPoint(x: right, y: top))
    path.line(to: NSPoint(x: right, y: edge))

    // Two drips hanging over the patty.
    path.line(to: NSPoint(x: 700, y: edge))
    path.line(to: NSPoint(x: 662, y: 428))
    path.line(to: NSPoint(x: 624, y: edge))
    path.line(to: NSPoint(x: 400, y: edge))
    path.line(to: NSPoint(x: 360, y: 440))
    path.line(to: NSPoint(x: 320, y: edge))
    path.line(to: NSPoint(x: left, y: edge))
    path.close()

    paint(path, top: color(0xFFD84D), bottom: color(0xFFB81C), lineWidth: 10)
}

func drawPatty() {
    let rect = CGRect(x: 196, y: 398, width: 632, height: 88)
    let patty = NSBezierPath(roundedRect: rect, xRadius: 44, yRadius: 44)
    paint(patty, top: color(0x8A4A22), bottom: color(0x5C2C12), lineWidth: 12)

    color(0x3E1E0C, 0.6).setFill()
    for (x, y) in [(270, 424), (452, 418), (560, 430), (744, 424), (520, 412)] as [(CGFloat, CGFloat)] {
        NSBezierPath(ovalIn: CGRect(x: x, y: y, width: 18, height: 10)).fill()
    }
}

func drawBottomBun() {
    let rect = CGRect(x: 214, y: 262, width: 596, height: 160)
    let bun = controllerHalf(rect, roundedTop: false)
    paint(bun, top: color(0x4CC3FF), bottom: color(0x1592E6), lineWidth: 12)

    stick(at: NSPoint(x: 676, y: 334), radius: 42)
    buttons(around: NSPoint(x: 362, y: 336), spacing: 34, radius: 14)
    pill(CGRect(x: 498, y: 364, width: 30, height: 12))
    pill(CGRect(x: 507, y: 355, width: 12, height: 30))
}

if output.hasSuffix(".png") {
    try render(size: 1024).write(to: URL(fileURLWithPath: output))
    print("Wrote \(output)")
    exit(0)
}

let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("Borgir-\(ProcessInfo.processInfo.processIdentifier).iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

for points in [16, 32, 128, 256, 512] {
    try render(size: points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try render(size: points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", output]
try iconutil.run()
iconutil.waitUntilExit()

try? FileManager.default.removeItem(at: iconset)

if iconutil.terminationStatus != 0 {
    FileHandle.standardError.write(Data("iconutil failed\n".utf8))
    exit(1)
}

print("Wrote \(output)")
