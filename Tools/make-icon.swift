import AppKit

// Draws the app icon and writes an .iconset for iconutil. Run through
// release.sh; there is no image asset to keep in the repo.

let arguments = CommandLine.arguments
guard arguments.count > 1 else {
    FileHandle.standardError.write(Data("usage: make-icon <output.iconset>\n".utf8))
    exit(1)
}
let outputDirectory = URL(fileURLWithPath: arguments[1])
try? FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

func draw(size: Int) -> Data? {
    let side = CGFloat(size)
    let image = NSImage(size: NSSize(width: side, height: side))
    image.lockFocus()
    defer { image.unlockFocus() }

    guard let context = NSGraphicsContext.current?.cgContext else { return nil }

    // Rounded square, in the proportions macOS uses for app icons.
    let inset = side * 0.06
    let rect = CGRect(x: inset, y: inset, width: side - inset * 2, height: side - inset * 2)
    let body = NSBezierPath(roundedRect: rect, xRadius: side * 0.22, yRadius: side * 0.22)
    body.addClip()

    let colors = [NSColor(calibratedRed: 0.85, green: 0.44, blue: 0.28, alpha: 1).cgColor,
                  NSColor(calibratedRed: 0.72, green: 0.30, blue: 0.22, alpha: 1).cgColor]
    if let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                 colors: colors as CFArray, locations: [0, 1]) {
        context.drawLinearGradient(gradient,
                                   start: CGPoint(x: 0, y: side),
                                   end: CGPoint(x: side, y: 0),
                                   options: [])
    }

    // Two rings, one growing out of the other: the graft.
    let centre = CGPoint(x: side / 2, y: side / 2)
    let radius = side * 0.20
    context.setStrokeColor(NSColor.white.withAlphaComponent(0.95).cgColor)
    context.setLineWidth(side * 0.055)
    context.setLineCap(.round)

    context.addArc(center: CGPoint(x: centre.x - radius * 0.55, y: centre.y),
                   radius: radius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
    context.strokePath()

    context.setStrokeColor(NSColor.white.withAlphaComponent(0.55).cgColor)
    context.addArc(center: CGPoint(x: centre.x + radius * 0.55, y: centre.y),
                   radius: radius, startAngle: 0, endAngle: .pi * 2, clockwise: false)
    context.strokePath()

    guard let representation = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0)
    else { return nil }
    NSGraphicsContext.current?.cgContext.flush()
    guard let cgImage = context.makeImage() else { return nil }
    let bitmap = NSBitmapImageRep(cgImage: cgImage)
    _ = representation
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
print("wrote \(outputDirectory.path)")
