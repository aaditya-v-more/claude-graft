import AppKit
import CoreImage
import Foundation

/// Builds the small .app bundle that a shortcut turns into. The bundle holds a
/// copy of the launcher binary plus a JSON description of the profile, so it
/// keeps working without this app installed.
enum Installer {
    static let fm = FileManager.default

    /// Redirected by the test suite; also stops it registering junk bundles.
    static var installDirectoryOverride: URL?
    static var registersWithLaunchServices = true
    /// Lets the icon integration test use a self-made image instead of Claude.
    static var iconSourceOverride: URL?

    private static let imageContext = CIContext()

    /// Preferred install directory, falling back to the user's own when
    /// /Applications is not writable.
    static var installDirectory: URL {
        if let installDirectoryOverride {
            try? fm.createDirectory(at: installDirectoryOverride, withIntermediateDirectories: true)
            return installDirectoryOverride
        }
        let system = URL(fileURLWithPath: "/Applications")
        if fm.isWritableFile(atPath: system.path) { return system }
        let user = fm.homeDirectoryForCurrentUser.appending(path: "Applications")
        try? fm.createDirectory(at: user, withIntermediateDirectories: true)
        return user
    }

    static func bundleURL(for shortcut: Shortcut, in directory: URL? = nil) -> URL {
        (directory ?? installDirectory).appending(path: "\(shortcut.name).app")
    }

    /// Names that belong to Claude itself and must never be written over.
    static let reservedNames = ["Claude", "Claude Graft"]

    /// True only for a bundle this app built. Every destructive step checks it,
    /// so an unrelated application that happens to share a name is left alone.
    static func isGraftBundle(_ url: URL) -> Bool {
        fm.fileExists(atPath: url.appending(path: "Contents/Resources/graft.json").path)
    }

    /// An installed shortcut bundle, wherever it ended up. Deliberately blind
    /// to anything Graft did not create.
    /// Where an already-installed shortcut might be sitting.
    static var searchDirectories: [URL] {
        if let installDirectoryOverride { return [installDirectoryOverride] }
        return [URL(fileURLWithPath: "/Applications"),
                fm.homeDirectoryForCurrentUser.appending(path: "Applications")]
    }

    static func installedBundle(for shortcut: Shortcut) -> URL? {
        for directory in searchDirectories {
            let candidate = bundleURL(for: shortcut, in: directory)
            if fm.fileExists(atPath: candidate.path), isGraftBundle(candidate) { return candidate }
        }
        return nil
    }

    enum InstallError: LocalizedError {
        case missingLauncher
        case reservedName(String)
        case nameTaken(String)
        case badFolder(String)
        case selfSource
        case missingIcon
        case iconCreationFailed
        case writeFailed(String)

        var errorDescription: String? {
            switch self {
            case .missingLauncher:
                return L10n.text("This copy of Claude Graft is missing its launcher binary.")
            case .reservedName(let name):
                return L10n.format("“%@” is the name of Claude itself. Pick something else.", name)
            case .nameTaken(let path):
                return L10n.format("There is already an application at %@ that Claude Graft did not create. Rename this shortcut.", path)
            case .badFolder(let reason):
                return reason
            case .selfSource:
                return L10n.text("This shortcut is set to borrow chats from its own profile. Choose a different source.")
            case .missingIcon:
                return L10n.text("Claude's application icon could not be read.")
            case .iconCreationFailed:
                return L10n.text("The selected shortcut icon could not be created.")
            case .writeFailed(let detail):
                return detail
            }
        }
    }

    @discardableResult
    static func install(_ shortcut: Shortcut, sourceDir: URL?, previousName: String? = nil) throws -> URL {
        guard let launcher = Bundle.main.url(forResource: "graft-launch", withExtension: nil) else {
            throw InstallError.missingLauncher
        }

        guard !reservedNames.contains(shortcut.name) else {
            throw InstallError.reservedName(shortcut.name)
        }

        if let reason = Graft.validateFolder(shortcut.folder) {
            throw InstallError.badFolder(reason)
        }

        if let sourceDir, Graft.samePath(sourceDir, shortcut.profileDir) {
            throw InstallError.selfSource
        }

        let bundle = installedBundle(for: shortcut) ?? bundleURL(for: shortcut)
        // Something is already there and it is not a shortcut of ours. Checked
        // before anything is removed, so a refused rename leaves both intact.
        if fm.fileExists(atPath: bundle.path), !isGraftBundle(bundle) {
            throw InstallError.nameTaken(bundle.path)
        }
        let config = GraftConfig(profileDir: shortcut.profileDir.path,
                                 sourceDir: sourceDir?.path)
        // Render and sign beside the destination before replacing anything.
        // A missing icon or failed conversion must leave a working shortcut,
        // including its old name and profile description, exactly as it was.
        let staging = bundle.deletingLastPathComponent()
            .appending(path: ".graft-install-\(UUID().uuidString).noindex")
        let replacement = staging.appending(path: bundle.lastPathComponent)
        defer { try? fm.removeItem(at: staging) }
        let contents = replacement.appending(path: "Contents")
        let macos = contents.appending(path: "MacOS")
        let resources = contents.appending(path: "Resources")

        do {
            try fm.createDirectory(at: macos, withIntermediateDirectories: true)
            try fm.createDirectory(at: resources, withIntermediateDirectories: true)

            let binary = macos.appending(path: "launcher")
            if fm.fileExists(atPath: binary.path) { try fm.removeItem(at: binary) }
            try fm.copyItem(at: launcher, to: binary)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)

            let data = try JSONEncoder().encode(config)
            try data.write(to: resources.appending(path: "graft.json"))

            try infoPlist(for: shortcut).write(to: contents.appending(path: "Info.plist"),
                                               atomically: true, encoding: .utf8)
            try writeIcon(shortcut.iconPreset, into: resources)
            guard sign(replacement),
                  Graft.runTool("/usr/bin/codesign", ["--verify", "--strict", replacement.path]) == 0
            else { throw InstallError.writeFailed(L10n.text("The shortcut could not be signed. Its existing app was kept.")) }
            try ManualUpdates.synchronize([Graft.mainProfile, shortcut.profileDir])
            try fm.createDirectory(at: shortcut.profileDir, withIntermediateDirectories: true)
            if fm.fileExists(atPath: bundle.path) {
                _ = try fm.replaceItemAt(bundle, withItemAt: replacement)
            } else {
                try fm.moveItem(at: replacement, to: bundle)
            }
        } catch {
            throw InstallError.writeFailed(error.localizedDescription)
        }

        // A failed rename keeps its old app until the new one is usable.
        if let previousName, previousName != shortcut.name {
            var stale = shortcut
            stale.name = previousName
            if let old = installedBundle(for: stale), !Graft.samePath(old, bundle) {
                try? fm.removeItem(at: old)
            }
        }
        touch(bundle)

        // Apply straight away when nothing holds the profile open; otherwise
        // the shortcut picks it up the next time it launches. The very
        // description the bundle was given rather than a second one built to
        // match: what runs now and what runs from the Dock later cannot drift
        // if there is only one of them, and their drifting is the thing
        // `refreshConfig` exists to repair after the fact.
        if !Graft.isRunning(profile: shortcut.profileDir) {
            Graft.apply(config)
        }
        return bundle
    }

    /// Bring the launchers inside already-installed shortcuts up to date.
    ///
    /// A bundle carries the launcher it was built with, and nothing re-saves a
    /// shortcut on its own, so an update to how a shortcut behaves reaches
    /// nobody until each one is opened and saved by hand. The routes where that
    /// matters most never touch this app at all: the Dock, Finder and Spotlight
    /// run the binary in the bundle and ask nothing of Graft. So every stale
    /// launcher is replaced at launch — the binary and the version stamp that
    /// says which Graft wrote it, and nothing else. The name, the icon and the
    /// profile it points at are the person's.
    @discardableResult
    static func refreshLaunchers(in shortcuts: [(shortcut: Shortcut, sourceDir: URL?)]) -> Int {
        shortcuts.reduce(0) {
            let launcher = refreshLauncher(for: $1.shortcut)
            let config = refreshConfig(for: $1.shortcut, sourceDir: $1.sourceDir)
            return $0 + (launcher || config ? 1 : 0)
        }
    }

    /// Bring a bundle's `graft.json` back into line with the list.
    ///
    /// What a shortcut does is written down twice: in `shortcuts.json`, which
    /// the window edits, and in the bundle's own `graft.json`, which is what
    /// actually runs. The Dock, Finder and Spotlight start the binary in the
    /// bundle and ask Graft nothing, so where the two disagree the bundle wins
    /// and the window is describing a shortcut that does not exist.
    ///
    /// They disagreed on this machine. The list said a profile was back on its
    /// own chats; the bundle still named a source and asked for copies. Opening
    /// it from the Dock therefore grafted it again, and the first mirror pass
    /// put all 152 of the profile's own chats into the stash where a first pass
    /// puts them — leaving a sidebar holding one record, a stash holding the
    /// rest, and nothing anywhere saying why.
    ///
    /// Unlike the launcher this is not gated on the version, because a bundle
    /// can fall out of step with the list at any version, and it was the
    /// current one that did. Only the two paths are written: the name, the icon
    /// and the version stamp are somebody else's business, and rewriting the
    /// plist here would stamp a renamed shortcut's new name into a bundle still
    /// sitting under the old one.
    @discardableResult
    static func refreshConfig(for shortcut: Shortcut, sourceDir: URL?) -> Bool {
        guard let bundle = installedBundle(for: shortcut) else { return false }
        let file = bundle.appending(path: "Contents/Resources/graft.json")
        let wanted = GraftConfig(profileDir: shortcut.profileDir.path,
                                 sourceDir: sourceDir?.path)
        let current = (try? Data(contentsOf: file))
            .flatMap { try? JSONDecoder().decode(GraftConfig.self, from: $0) }
        guard current != wanted else { return false }

        guard let data = try? JSONEncoder().encode(wanted),
              (try? data.write(to: file, options: .atomic)) != nil
        else { return false }
        // The bundle is signed, and writing into it breaks that.
        sign(bundle)
        touch(bundle)
        return true
    }

    static func refreshLauncher(for shortcut: Shortcut) -> Bool {
        let version = graftVersion
        guard let launcher = Bundle.main.url(forResource: "graft-launch", withExtension: nil),
              let bundle = installedBundle(for: shortcut),
              builtBy(bundle) != version,
              let binary = executableURL(in: bundle)
        else { return false }

        // Staged and swapped rather than removed and rewritten. This runs while
        // Graft starts, which on a login is exactly when a shortcut may be
        // starting too, and a shortcut that finds no executable where its
        // launcher was does not open anything.
        let staged = binary.deletingLastPathComponent()
            .appending(path: "\(binary.lastPathComponent).staged")
        do {
            try? fm.removeItem(at: staged)
            try fm.copyItem(at: launcher, to: staged)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: staged.path)
            if fm.fileExists(atPath: binary.path) {
                _ = try fm.replaceItemAt(binary, withItemAt: staged)
            } else {
                try fm.moveItem(at: staged, to: binary)
            }
            try stampVersion(version, into: bundle)
        } catch {
            try? fm.removeItem(at: staged)
            return false
        }
        sign(bundle)
        touch(bundle)
        return true
    }

    /// The version of Graft that wrote this bundle, which is the only thing
    /// that says whether its launcher is the current one.
    static func builtBy(_ bundle: URL) -> String? {
        guard isGraftBundle(bundle),
              let data = try? Data(contentsOf: bundle.appending(path: "Contents/Info.plist")),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any]
        else { return nil }
        return plist["CFBundleShortVersionString"] as? String
    }

    /// Only the two version keys are rewritten. Regenerating the whole file
    /// would stamp the shortcut's current name into a bundle that may still be
    /// sitting under its old one, waiting for a save that renames it.
    private static func stampVersion(_ version: String, into bundle: URL) throws {
        let url = bundle.appending(path: "Contents/Info.plist")
        let data = try Data(contentsOf: url)
        guard var plist = try PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any] else { return }
        plist["CFBundleShortVersionString"] = version
        plist["CFBundleVersion"] = version
        let updated = try PropertyListSerialization.data(fromPropertyList: plist,
                                                         format: .xml, options: 0)
        try updated.write(to: url)
    }

    static func uninstall(_ shortcut: Shortcut) {
        guard let bundle = installedBundle(for: shortcut), isGraftBundle(bundle) else { return }
        try? fm.removeItem(at: bundle)
    }

    // MARK: - Bundle pieces

    /// The running app's own version. Nil only under the test binary, which is
    /// not a bundle and has no version to inherit.
    static var graftVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"
    }

    private static func executableURL(in bundle: URL) -> URL? {
        guard let info = plist(at: bundle.appending(path: "Contents/Info.plist")),
              let executable = info["CFBundleExecutable"] as? String,
              !executable.isEmpty
        else { return nil }
        return bundle.appending(path: "Contents/MacOS").appending(path: executable)
    }

    private static func plist(at url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any]
        else { return nil }
        return plist
    }

    /// A name is free text and lands inside an XML document; an ampersand in it
    /// would otherwise produce a plist macOS refuses to read.
    private static func escaped(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }

    private static func infoPlist(for shortcut: Shortcut) -> String {
        let slug = shortcut.folder.lowercased()
            .map { $0.isLetter || $0.isNumber || $0 == "-" ? $0 : "-" }
        let identifier = "graft." + String(slug)
        let name = escaped(shortcut.name)
        let version = graftVersion
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key><string>\(name)</string>
            <key>CFBundleDisplayName</key><string>\(name)</string>
            <key>CFBundleIdentifier</key><string>\(identifier)</string>
            <key>CFBundleExecutable</key><string>launcher</string>
            <key>CFBundleIconFile</key><string>icon</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleShortVersionString</key><string>\(version)</string>
            <key>CFBundleVersion</key><string>\(version)</string>
            <key>LSUIElement</key><true/>
            <key>LSMinimumSystemVersion</key><string>13.0</string>
        </dict>
        </plist>
        """
    }

    /// Borrow Claude's own icon and optionally recolour or badge it for the
    /// generated shortcut bundle.
    private static func writeIcon(_ preset: Shortcut.IconPreset, into resources: URL) throws {
        guard let source = iconSource else { throw InstallError.missingIcon }
        let destination = resources.appending(path: "icon.icns")
        let staged = resources.appending(path: "icon.staged.icns")
        try? fm.removeItem(at: staged)
        defer { try? fm.removeItem(at: staged) }

        if preset == .original, source.pathExtension.lowercased() == "icns" {
            try fm.copyItem(at: source, to: staged)
        } else {
            let scratch = fm.temporaryDirectory
                .appending(path: "claude-graft-icon-\(UUID().uuidString).png")
            defer { try? fm.removeItem(at: scratch) }
            guard let image = renderedIcon(from: source, preset: preset, pixels: 1024),
                  let data = image.tiffRepresentation
                    .flatMap(NSBitmapImageRep.init(data:))?
                    .representation(using: .png, properties: [:])
            else { throw InstallError.iconCreationFailed }
            try data.write(to: scratch)
            guard Graft.runTool("/usr/bin/sips",
                                ["-s", "format", "icns", scratch.path,
                                 "--out", staged.path]) == 0
            else { throw InstallError.iconCreationFailed }
        }

        if fm.fileExists(atPath: destination.path) {
            _ = try fm.replaceItemAt(destination, withItemAt: staged)
        } else {
            try fm.moveItem(at: staged, to: destination)
        }
    }

    private static var iconSource: URL? {
        if let iconSourceOverride { return iconSourceOverride }
        let candidates = ["electron.icns", "Claude.icns", "app.icns"]
        for name in candidates {
            let source = Graft.claudeApp.appending(path: "Contents/Resources/\(name)")
            if fm.fileExists(atPath: source.path) { return source }
        }
        return nil
    }

    static func previewIcon(for preset: Shortcut.IconPreset) -> NSImage? {
        guard let source = iconSource else { return nil }
        return renderedIcon(from: source, preset: preset, pixels: 96)
    }

    private static func renderedIcon(from source: URL, preset: Shortcut.IconPreset,
                                     pixels: Int) -> NSImage? {
        guard let stock = NSImage(contentsOf: source),
              let base = bitmapImage(pixels: pixels, drawing: {
                  stock.draw(in: $0, from: .zero, operation: .copy, fraction: 1)
              })
        else { return nil }

        var icon = base
        if (preset.hueAngle != nil || preset.saturation != 1),
           let input = base.representations.compactMap({ ($0 as? NSBitmapImageRep)?.cgImage }).first {
            var filtered = CIImage(cgImage: input)
            if let angle = preset.hueAngle {
                filtered = filtered.applyingFilter(
                    "CIHueAdjust", parameters: [kCIInputAngleKey: angle])
            }
            if preset.saturation != 1 {
                filtered = filtered.applyingFilter(
                    "CIColorControls", parameters: [kCIInputSaturationKey: preset.saturation])
            }
            guard let output = imageContext.createCGImage(filtered, from: filtered.extent) else {
                return nil
            }
            icon = NSImage(cgImage: output, size: base.size)
        }

        guard let symbolName = preset.badgeSymbol else { return icon }
        return bitmapImage(pixels: pixels) { rect in
            icon.draw(in: rect, from: .zero, operation: .copy, fraction: 1)

            let side = rect.width
            let badge = NSRect(x: side * 0.63, y: side * 0.075,
                               width: side * 0.29, height: side * 0.29)
            NSColor.white.withAlphaComponent(0.96).setFill()
            NSBezierPath(ovalIn: badge).fill()

            let disk = badge.insetBy(dx: side * 0.016, dy: side * 0.016)
            preset.accentColor.setFill()
            NSBezierPath(ovalIn: disk).fill()

            guard let symbol = NSImage(systemSymbolName: symbolName,
                                       accessibilityDescription: nil),
                  let glyph = whiteSymbol(symbol)
            else { return }
            let glyphSide = side * 0.135
            glyph.draw(in: NSRect(x: disk.midX - glyphSide / 2,
                                  y: disk.midY - glyphSide / 2,
                                  width: glyphSide, height: glyphSide),
                       from: .zero, operation: .sourceOver, fraction: 1)
        }
    }

    private static func bitmapImage(pixels: Int,
                                    drawing: (NSRect) -> Void) -> NSImage? {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil,
                                            pixelsWide: pixels,
                                            pixelsHigh: pixels,
                                            bitsPerSample: 8,
                                            samplesPerPixel: 4,
                                            hasAlpha: true,
                                            isPlanar: false,
                                            colorSpaceName: .deviceRGB,
                                            bytesPerRow: 0,
                                            bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: bitmap)
        else { return nil }

        let pixelSize = NSSize(width: pixels, height: pixels)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        context.imageInterpolation = .high
        drawing(NSRect(origin: .zero, size: pixelSize))
        NSGraphicsContext.restoreGraphicsState()

        // ICNS treats a 1024 px representation as a 512 pt @2x image. Set that
        // metadata after drawing, while the context still uses pixel units.
        let pointSize = NSSize(width: CGFloat(pixels) / 2, height: CGFloat(pixels) / 2)
        bitmap.size = pointSize
        let image = NSImage(size: pointSize)
        image.addRepresentation(bitmap)
        return image
    }

    private static func whiteSymbol(_ symbol: NSImage) -> NSImage? {
        let image = NSImage(size: symbol.size)
        image.lockFocus()
        symbol.draw(at: .zero, from: .zero, operation: .sourceOver, fraction: 1)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: symbol.size).fill(using: .sourceAtop)
        image.unlockFocus()
        return image
    }

    /// Ad-hoc signature, otherwise macOS refuses to launch a bundle whose
    /// contents changed after the first run.
    @discardableResult
    private static func sign(_ bundle: URL) -> Bool {
        Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", bundle.path]) == 0
    }

    /// Nudge Launch Services so the new name and icon show up straight away.
    private static func touch(_ bundle: URL) {
        run("/usr/bin/touch", [bundle.path])
        guard registersWithLaunchServices else { return }
        run("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
            ["-f", bundle.path])
    }

    private static func run(_ tool: String, _ arguments: [String]) {
        Graft.runTool(tool, arguments)
    }
}
