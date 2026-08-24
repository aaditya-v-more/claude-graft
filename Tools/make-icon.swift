import AppKit

// Draws the app icon and writes an .iconset for iconutil. Run through
// release.sh; there is no drawing program in the loop and no layered source
// file to keep in step with the code.
//
// The mark is a graft: two stems rising from below, meeting, and carrying on as
// one. The right-hand stem is the paler of the two because that is what the app
// actually does — a shortcut borrows the account beside it rather than becoming
// it. Everything is placed in fractions of the icon body, so 16 points and 1024
// are the same drawing rather than two that have to be kept looking alike.

let arguments = CommandLine.arguments
guard arguments.count > 1 else {
    FileHandle.standardError.write(Data("usage: make-icon <output.iconset>\n".utf8))
    exit(1)
}
let outputDirectory = URL(fileURLWithPath: arguments[1])
try? FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

/// The rounded square macOS uses, which is a superellipse and not a rectangle
/// with circular corners. Described, the difference sounds like nothing; in the
/// Dock it is the only icon on the shelf whose corners meet its edges at a
/// visible kink.
func squircle(in rect: CGRect, exponent: Double = 5) -> CGPath {
    let path = CGMutablePath()
    let a = rect.width / 2, b = rect.height / 2
    let centre = CGPoint(x: rect.midX, y: rect.midY)
    let steps = 512
    for step in 0...steps {
        let t = Double(step) / Double(steps) * 2 * .pi
        let power = 2 / exponent
        let x = copysign(pow(abs(cos(t)), power), cos(t))
        let y = copysign(pow(abs(sin(t)), power), sin(t))
        let point = CGPoint(x: centre.x + a * x, y: centre.y + b * y)
        step == 0 ? path.move(to: point) : path.addLine(to: point)
    }
    path.closeSubpath()
    return path
}

func colour(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255,
            green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255,
            alpha: alpha)
}

func draw(size: Int) -> Data? {
    let side = CGFloat(size)
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
        let context = NSGraphicsContext(bitmapImageRep: bitmap)?.cgContext
    else { return nil }

    // Apple's icon grid: a 1024 canvas holding an 824 body. An icon drawn edge
    // to edge stands proud of every other one in the Dock.
    let inset = side * (100.0 / 1024.0)
    let body = CGRect(x: inset, y: inset, width: side - inset * 2, height: side - inset * 2)

    // The shadow the macOS icon template bakes in. Left off below 128 points,
    // where it eats more contrast than it buys depth.
    if size >= 128 {
        context.setShadow(offset: CGSize(width: 0, height: -side * 0.014),
                          blur: side * 0.026,
                          color: CGColor(red: 0.08, green: 0.03, blue: 0, alpha: 0.24))
        context.addPath(squircle(in: body))
        context.setFillColor(colour(0xC85A34))
        context.fillPath()
        context.setShadow(offset: .zero, blur: 0, color: nil)
    }

    context.saveGState()
    context.addPath(squircle(in: body))
    context.clip()

    // Terracotta, lit from the top left the way macOS lights everything.
    if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                 colors: [colour(0xEE9366), colour(0xD1663C), colour(0xA43C19)] as CFArray,
                                 locations: [0, 0.52, 1]) {
        context.drawLinearGradient(gradient,
                                   start: CGPoint(x: body.minX, y: body.maxY),
                                   end: CGPoint(x: body.maxX, y: body.minY),
                                   options: [])
    }

    // The sheen off the top edge, which is what keeps a flat gradient from
    // reading as a printed sticker.
    if let sheen = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                              colors: [CGColor(red: 1, green: 1, blue: 1, alpha: 0.18),
                                       CGColor(red: 1, green: 1, blue: 1, alpha: 0)] as CFArray,
                              locations: [0, 1]) {
        context.drawRadialGradient(sheen,
                                   startCenter: CGPoint(x: body.midX, y: body.maxY),
                                   startRadius: 0,
                                   endCenter: CGPoint(x: body.midX, y: body.maxY),
                                   endRadius: body.width * 0.8,
                                   options: [])
    }

    // ---- the graft ----------------------------------------------------

    func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
        CGPoint(x: body.minX + body.width * x, y: body.minY + body.height * y)
    }

    let ink = CGColor(red: 1, green: 0.972, blue: 0.945, alpha: 0.97)
    let inkBorrowed = CGColor(red: 1, green: 0.972, blue: 0.945, alpha: 0.52)
    let union = point(0.5, 0.475)

    context.setLineWidth(body.width * 0.122)
    context.setLineCap(.round)
    context.setLineJoin(.round)

    // The borrowed stem stops at the union rather than crossing it. It feeds
    // the trunk; it is not the trunk.
    let scion = CGMutablePath()
    scion.move(to: point(0.765, 0.215))
    scion.addCurve(to: union, control1: point(0.745, 0.365), control2: point(0.64, 0.425))
    context.addPath(scion)
    context.setStrokeColor(inkBorrowed)
    context.strokePath()

    // Rootstock and trunk as one unbroken line, so the union is a join and not
    // a seam between two strokes that happen to touch.
    let trunk = CGMutablePath()
    trunk.move(to: point(0.235, 0.215))
    trunk.addCurve(to: union, control1: point(0.255, 0.365), control2: point(0.36, 0.425))
    trunk.addLine(to: point(0.5, 0.82))
    context.addPath(trunk)
    context.setStrokeColor(ink)
    context.strokePath()

    context.restoreGState()

    // A hairline of the light along the edge, kept inside the clip so it does
    // not soften the silhouette.
    context.addPath(squircle(in: body.insetBy(dx: side * 0.005, dy: side * 0.005)))
    context.setStrokeColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.20))
    context.setLineWidth(side * 0.006)
    context.strokePath()

    return bitmap.representation(using: .png, properties: [:])
}

// The sizes iconutil expects, each in its one-times and two-times form.
let sizes = [16, 32, 128, 256, 512]
for size in sizes {
    for scale in [1, 2] {
        guard let data = draw(size: size * scale) else { continue }
        let name = scale == 1 ? "icon_\(size)x\(size).png" : "icon_\(size)x\(size)@2x.png"
        try? data.write(to: outputDirectory.appending(path: name))
    }
}

// The site and the README want a plain PNG of the same drawing. Written beside
// the iconset when asked for, so the two can never drift apart.
if arguments.count > 2, let data = draw(size: 512) {
    try? data.write(to: URL(fileURLWithPath: arguments[2]))
}

print("wrote \(outputDirectory.path)")
