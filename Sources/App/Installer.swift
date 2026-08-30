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
        case badFolder(String)
        case selfSource
        case writeFailed(String)

        var errorDescription: String? {
            switch self {
            case .missingLauncher:
                return "This copy of Claude Graft is missing its launcher binary."
            case .reservedName(let name):
                return "“\(name)” is the name of Claude itself. Pick something else."
            case .nameTaken(let path):
                return "There is already an application at \(path) that Claude Graft did not create. Rename this shortcut."
            case .badFolder(let reason):
                return reason
            case .selfSource:
                return "This shortcut is set to borrow chats from its own profile. Choose a different source."
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

        // A rename leaves the old bundle behind, so clear it. Only ever one of
        // ours; installedBundle already refuses anything else.
        if let previousName, previousName != shortcut.name {
            var stale = shortcut
            stale.name = previousName
            if let old = installedBundle(for: stale) { try? fm.removeItem(at: old) }
        }
        let config = GraftConfig(profileDir: shortcut.profileDir.path,
                                 sourceDir: sourceDir?.path)
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
        guard current?.profileDir != wanted.profileDir
                || current?.sourceDir != wanted.sourceDir
        else { return false }

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
              builtBy(bundle) != version
        else { return false }

        // Staged and swapped rather than removed and rewritten. This runs while
        // Graft starts, which on a login is exactly when a shortcut may be
        // starting too, and a shortcut that finds no executable where its
        // launcher was does not open anything.
        let binary = bundle.appending(path: "Contents/MacOS/launcher")
        let staged = bundle.appending(path: "Contents/MacOS/launcher.staged")
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

    /// A name is free text and lands inside an XML document; an ampersand in it
    /// would otherwise produce a plist macOS refuses to read.
    private static func escaped(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }

    /// The running app's own version. Nil only under the test binary, which is
    /// not a bundle and has no version to inherit.
    static var graftVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"
    }

    private static func infoPlist(for shortcut: Shortcut) -> String {
        let slug = shortcut.folder.lowercased()
            .map { $0.isLetter || $0.isNumber || $0 == "-" ? $0 : "-" }
        let identifier = "graft." + String(slug)
        let name = escaped(shortcut.name)
        // Stamped with whichever Graft built it, so a shortcut left behind by an
        // older one can be told apart from the current crop.
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
        Graft.runTool(tool, arguments)
    }
}
