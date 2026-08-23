import Foundation

/// Builds the small .app bundle that a shortcut turns into. The bundle holds a
/// copy of the launcher binary plus a JSON description of the profile, so it
/// keeps working without this app installed.
enum Installer {
    static let fm = FileManager.default

    /// Redirected by the test suite; also stops it registering junk bundles.
    static var installDirectoryOverride: URL?
    static var registersWithLaunchServices = true

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
        case writeFailed(String)

        var errorDescription: String? {
            switch self {
            case .missingLauncher:
                return "This copy of Claude Graft is missing its launcher binary."
            case .reservedName(let name):
                return "“\(name)” is the name of Claude itself. Pick something else."
            case .nameTaken(let path):
                return "There is already an application at \(path) that Claude Graft did not create. Rename this shortcut."
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

        let bundle = installedBundle(for: shortcut) ?? bundleURL(for: shortcut)
        // Something is already there and it is not a shortcut of ours. Checked
        // before anything is removed, so a refused rename leaves both intact.
        if fm.fileExists(atPath: bundle.path), !isGraftBundle(bundle) {
            throw InstallError.nameTaken(bundle.path)
        }

        // A rename leaves the old bundle behind, so clear it. Only ever one of
        // ours; installedBundle already refuses anything else.
        if let previousName, previousName != shortcut.name {
            var stale = shortcut
            stale.name = previousName
            if let old = installedBundle(for: stale) { try? fm.removeItem(at: old) }
        }
        let contents = bundle.appending(path: "Contents")
        let macos = contents.appending(path: "MacOS")
        let resources = contents.appending(path: "Resources")

        do {
            try fm.createDirectory(at: macos, withIntermediateDirectories: true)
            try fm.createDirectory(at: resources, withIntermediateDirectories: true)

            let binary = macos.appending(path: "launcher")
            if fm.fileExists(atPath: binary.path) { try fm.removeItem(at: binary) }
            try fm.copyItem(at: launcher, to: binary)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)

            let config = GraftConfig(profileDir: shortcut.profileDir.path, sourceDir: sourceDir?.path)
            let data = try JSONEncoder().encode(config)
            try data.write(to: resources.appending(path: "graft.json"))

            try infoPlist(for: shortcut).write(to: contents.appending(path: "Info.plist"),
                                               atomically: true, encoding: .utf8)
            copyIcon(into: resources)
            try fm.createDirectory(at: shortcut.profileDir, withIntermediateDirectories: true)
        } catch {
            throw InstallError.writeFailed(error.localizedDescription)
        }

        sign(bundle)
        touch(bundle)

        // Apply straight away when nothing holds the profile open; otherwise
        // the shortcut picks it up the next time it launches.
        if !Graft.isRunning(profile: shortcut.profileDir) {
            Graft.apply(GraftConfig(profileDir: shortcut.profileDir.path, sourceDir: sourceDir?.path))
        }
        return bundle
    }

    static func uninstall(_ shortcut: Shortcut) {
        guard let bundle = installedBundle(for: shortcut), isGraftBundle(bundle) else { return }
        try? fm.removeItem(at: bundle)
    }

    // MARK: - Bundle pieces

    private static func infoPlist(for shortcut: Shortcut) -> String {
        let identifier = "graft." + shortcut.folder.lowercased()
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key><string>\(shortcut.name)</string>
            <key>CFBundleDisplayName</key><string>\(shortcut.name)</string>
            <key>CFBundleIdentifier</key><string>\(identifier)</string>
            <key>CFBundleExecutable</key><string>launcher</string>
            <key>CFBundleIconFile</key><string>icon</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleShortVersionString</key><string>1.0</string>
            <key>CFBundleVersion</key><string>1</string>
            <key>LSUIElement</key><true/>
            <key>LSMinimumSystemVersion</key><string>13.0</string>
        </dict>
        </plist>
        """
    }

    /// Borrow Claude's own icon so the shortcut is recognisable in the Dock.
    private static func copyIcon(into resources: URL) {
        let candidates = ["electron.icns", "Claude.icns", "app.icns"]
        for name in candidates {
            let source = Graft.claudeApp.appending(path: "Contents/Resources/\(name)")
            guard fm.fileExists(atPath: source.path) else { continue }
            let destination = resources.appending(path: "icon.icns")
            if fm.fileExists(atPath: destination.path) { try? fm.removeItem(at: destination) }
            try? fm.copyItem(at: source, to: destination)
            return
        }
    }

    /// Ad-hoc signature, otherwise macOS refuses to launch a bundle whose
    /// contents changed after the first run.
    private static func sign(_ bundle: URL) {
        run("/usr/bin/codesign", ["--force", "--sign", "-", bundle.path])
    }

    /// Nudge Launch Services so the new name and icon show up straight away.
    private static func touch(_ bundle: URL) {
        run("/usr/bin/touch", [bundle.path])
        guard registersWithLaunchServices else { return }
        run("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
            ["-f", bundle.path])
    }

    private static func run(_ tool: String, _ arguments: [String]) {
        guard fm.isExecutableFile(atPath: tool) else { return }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: tool)
        task.arguments = arguments
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
    }
}
