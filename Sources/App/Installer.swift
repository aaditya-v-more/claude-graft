import AppKit
import CoreImage
import CryptoKit
import Foundation

/// Builds a profile-specific copy of Claude.app. APFS clones make the copy
/// cheap, while running Claude from that bundle gives Dock and Force Quit the
/// shortcut's own name and icon instead of the stock Claude identity.
enum Installer {
    static let fm = FileManager.default

    /// Redirected by the test suite; also stops it registering junk bundles.
    static var installDirectoryOverride: URL?
    static var registersWithLaunchServices = true
    /// Lets the icon integration test use a self-made image instead of Claude.
    static var iconSourceOverride: URL? { didSet { iconPreviews.removeAll() } }

    private static let graftVersionKey = "ClaudeGraftVersion"
    private static let sourceVersionKey = "ClaudeGraftSourceVersion"
    private static let runtimeFormatKey = "ClaudeGraftRuntimeFormat"
    private static let runtimeFormat = 1

    private static let imageContext = CIContext()
    private static var iconPreviews: [String: NSImage] = [:]

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
        case missingClaude
        case incompatibleClaude
        case signingFailed
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
            case .missingClaude:
                return L10n.text("Claude.app could not be found or is incomplete.")
            case .incompatibleClaude:
                return L10n.text("This Claude version cannot yet be used as a profile app. Update Claude Graft and try again.")
            case .signingFailed:
                return L10n.text("The profile app could not be signed for this Mac.")
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
                return L10n.text("The selected application icon could not be created.")
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

        var previousBundle: URL?
        if let previousName, previousName != shortcut.name {
            var stale = shortcut
            stale.name = previousName
            previousBundle = installedBundle(for: stale)
            if let previousBundle, runtimeIsRunning(in: previousBundle) {
                throw InstallError.writeFailed(
                    L10n.text("Close this profile before renaming its application."))
            }
        }
        let config = GraftConfig(profileDir: shortcut.profileDir.path,
                                 sourceDir: sourceDir?.path,
                                 themeMode: shortcut.themePreset.value)
        do {
            let source = Graft.claudeApp
            guard validClaude(at: source) else { throw InstallError.missingClaude }
            let needsRuntime = !isRuntimeBundle(bundle)
                || runtimeFormatOf(bundle) != runtimeFormat
                || builtFrom(bundle) != sourceVersion(of: source)
            if needsRuntime && !runtimeIsRunning(in: bundle) {
                try buildBundle(at: bundle, from: source, shortcut: shortcut,
                                config: config, launcher: launcher)
            } else {
                try updateBundle(at: bundle, shortcut: shortcut,
                                 config: config, launcher: launcher)
            }
            try fm.createDirectory(at: shortcut.profileDir, withIntermediateDirectories: true)
        } catch let error as InstallError { throw error }
          catch { throw InstallError.writeFailed(error.localizedDescription) }

        if let previousBundle, previousBundle != bundle { try? fm.removeItem(at: previousBundle) }
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
            let config = refreshConfig(for: $1.shortcut, sourceDir: $1.sourceDir)
            let launcher = refreshLauncher(for: $1.shortcut)
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
                                 sourceDir: sourceDir?.path,
                                 themeMode: shortcut.themePreset.value)
        let current = (try? Data(contentsOf: file))
            .flatMap { try? JSONDecoder().decode(GraftConfig.self, from: $0) }
        guard current != wanted else { return false }

        guard let data = try? JSONEncoder().encode(wanted),
              (try? data.write(to: file, options: .atomic)) != nil
        else { return false }
        // The bundle is signed, and writing into it breaks that.
        guard sign(bundle) else { return false }
        touch(bundle)
        return true
    }

    static func refreshLauncher(for shortcut: Shortcut) -> Bool {
        let version = graftVersion
        guard let launcher = Bundle.main.url(forResource: "graft-launch", withExtension: nil),
              let bundle = installedBundle(for: shortcut)
        else { return false }

        let source = Graft.claudeApp
        if validClaude(at: source),
           (!isRuntimeBundle(bundle) || runtimeFormatOf(bundle) != runtimeFormat
                || builtFrom(bundle) != sourceVersion(of: source)),
           !runtimeIsRunning(in: bundle),
           let data = try? Data(contentsOf: bundle.appending(path: "Contents/Resources/graft.json")),
           let config = try? JSONDecoder().decode(GraftConfig.self, from: data) {
            do {
                try buildBundle(at: bundle, from: source, shortcut: shortcut,
                                config: config, launcher: launcher)
                touch(bundle)
                return true
            } catch {
                Diagnostics.note("installer.refresh.failed", [
                    "profile": shortcut.folder,
                    "error": error.localizedDescription
                ])
                return false
            }
        }
        guard isRuntimeBundle(bundle), builtBy(bundle) != version else { return false }

        // Staged and swapped rather than removed and rewritten. This runs while
        // Graft starts, which on a login is exactly when a shortcut may be
        // starting too, and a shortcut that finds no executable where its
        // launcher was does not open anything.
        let binary = bundle.appending(path: "Contents/MacOS/Claude")
        let staged = bundle.appending(path: "Contents/MacOS/Claude.staged")
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
            Diagnostics.note("installer.refresh.failed", [
                "profile": shortcut.folder,
                "error": error.localizedDescription
            ])
            return false
        }
        guard sign(bundle) else {
            Diagnostics.note("installer.refresh.failed", [
                "profile": shortcut.folder,
                "error": InstallError.signingFailed.localizedDescription
            ])
            return false
        }
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
        return plist[graftVersionKey] as? String
            ?? plist["CFBundleShortVersionString"] as? String
    }

    static func builtFrom(_ bundle: URL) -> String? {
        guard isGraftBundle(bundle),
              let plist = plist(at: bundle.appending(path: "Contents/Info.plist"))
        else { return nil }
        return plist[sourceVersionKey] as? String
    }

    private static func runtimeFormatOf(_ bundle: URL) -> Int? {
        plist(at: bundle.appending(path: "Contents/Info.plist"))?[runtimeFormatKey] as? Int
    }

    /// Only the two version keys are rewritten. Regenerating the whole file
    /// would stamp the shortcut's current name into a bundle that may still be
    /// sitting under its old one, waiting for a save that renames it.
    private static func stampVersion(_ version: String, into bundle: URL) throws {
        let url = bundle.appending(path: "Contents/Info.plist")
        let data = try Data(contentsOf: url)
        guard var plist = try PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any] else { return }
        plist[graftVersionKey] = version
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

    private static func identifier(for shortcut: Shortcut) -> String {
        let slug = shortcut.folder.lowercased()
            .map { $0.isLetter || $0.isNumber || $0 == "-" ? $0 : "-" }
        return "graft." + String(slug)
    }

    private static func validClaude(at bundle: URL) -> Bool {
        fm.isExecutableFile(atPath: bundle.appending(path: "Contents/MacOS/Claude").path)
            && fm.fileExists(atPath: bundle.appending(path: "Contents/Resources/app.asar").path)
            && plist(at: bundle.appending(path: "Contents/Info.plist")) != nil
    }

    private static func isRuntimeBundle(_ bundle: URL) -> Bool {
        fm.isExecutableFile(atPath: bundle.appending(path: "Contents/MacOS/ClaudeRuntime").path)
            && fm.isExecutableFile(atPath: bundle.appending(path: "Contents/MacOS/Claude").path)
    }

    private static func runtimeIsRunning(in bundle: URL) -> Bool {
        let runtime = bundle.appending(path: "Contents/MacOS/ClaudeRuntime").path
        return Graft.processes().contains { $0.command.contains(runtime) }
    }

    private static func sourceVersion(of bundle: URL) -> String? {
        guard let plist = plist(at: bundle.appending(path: "Contents/Info.plist")),
              let version = plist["CFBundleShortVersionString"] as? String
        else { return nil }
        return version + "|" + (plist["CFBundleVersion"] as? String ?? version)
    }

    private static func plist(at url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any]
        else { return nil }
        return plist
    }

    private static func writeInfo(for shortcut: Shortcut, into bundle: URL,
                                  sourceVersion: String? = nil) throws {
        let url = bundle.appending(path: "Contents/Info.plist")
        guard var plist = plist(at: url) else { throw InstallError.missingClaude }
        // Electron derives "Claude Helper.app" from CFBundleName. The display
        // name and bundle path can still carry the profile identity.
        plist["CFBundleName"] = "Claude"
        plist["CFBundleDisplayName"] = shortcut.name
        plist["CFBundleIdentifier"] = identifier(for: shortcut)
        plist["CFBundleExecutable"] = "Claude"
        plist["CFBundleIconFile"] = "icon.icns"
        plist.removeValue(forKey: "CFBundleIconName")
        plist.removeValue(forKey: "LSUIElement")
        plist[graftVersionKey] = graftVersion
        if let sourceVersion {
            plist[sourceVersionKey] = sourceVersion
            plist[runtimeFormatKey] = runtimeFormat
        }
        let data = try PropertyListSerialization.data(fromPropertyList: plist,
                                                       format: .xml, options: 0)
        try data.write(to: url, options: .atomic)
    }

    private static func writeConfig(_ config: GraftConfig, into bundle: URL) throws {
        let data = try JSONEncoder().encode(config)
        try data.write(to: bundle.appending(path: "Contents/Resources/graft.json"),
                       options: .atomic)
    }

    private static func installLauncher(_ launcher: URL, into bundle: URL) throws {
        let binary = bundle.appending(path: "Contents/MacOS/Claude")
        if fm.fileExists(atPath: binary.path) { try fm.removeItem(at: binary) }
        try fm.copyItem(at: launcher, to: binary)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
    }

    private static func buildBundle(at destination: URL, from source: URL,
                                    shortcut: Shortcut, config: GraftConfig,
                                    launcher: URL) throws {
        let staged = destination.deletingLastPathComponent()
            .appending(path: ".claude-graft-\(UUID().uuidString).app")
        defer { try? fm.removeItem(at: staged) }
        guard Graft.runTool("/bin/cp", ["-cR", source.path, staged.path]) == 0 else {
            throw InstallError.writeFailed(
                L10n.text("Claude.app could not be copied for this profile."))
        }

        let original = staged.appending(path: "Contents/MacOS/Claude")
        let runtime = staged.appending(path: "Contents/MacOS/ClaudeRuntime")
        try fm.moveItem(at: original, to: runtime)
        try installLauncher(launcher, into: staged)
        try writeConfig(config, into: staged)
        try writeInfo(for: shortcut, into: staged, sourceVersion: sourceVersion(of: source))
        try writeIcon(shortcut.iconPreset, into: staged.appending(path: "Contents/Resources"))
        try disableAutoUpdates(in: staged.appending(path: "Contents/Resources/app.asar"))
        try signRuntime(in: staged)
        guard sign(staged) else {
            Diagnostics.note("installer.sign.failed", ["target": "bundle"])
            throw InstallError.signingFailed
        }
        guard verify(staged) else {
            Diagnostics.note("installer.sign.failed", ["target": "verification"])
            throw InstallError.signingFailed
        }

        if fm.fileExists(atPath: destination.path) {
            _ = try fm.replaceItemAt(destination, withItemAt: staged)
        } else {
            try fm.moveItem(at: staged, to: destination)
        }
    }

    private static func updateBundle(at bundle: URL, shortcut: Shortcut,
                                     config: GraftConfig, launcher: URL) throws {
        try installLauncher(launcher, into: bundle)
        try writeConfig(config, into: bundle)
        try writeInfo(for: shortcut, into: bundle)
        try writeIcon(shortcut.iconPreset, into: bundle.appending(path: "Contents/Resources"))
        guard sign(bundle) else { throw InstallError.signingFailed }
    }

    /// Make only Claude's installed-app test return false. The replacement has
    /// identical length, so the ASAR header and every file offset stay intact;
    /// APFS copies only the touched block instead of duplicating the archive.
    private static func disableAutoUpdates(in asar: URL) throws {
        let data = try Data(contentsOf: asar, options: .mappedIfSafe)
        let replacement = Data("return!1/*graft-off__*/".utf8)
        var match: (range: Range<Data.Index>, returnOffset: Int)?
        for symbol in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ$_" {
            let prefix = "if(process.platform!==`win32`)"
            let old = "return \(symbol).app.isPackaged"
            let needle = Data((prefix + old + ";if(!\(symbol).app.isPackaged)").utf8)
            guard let range = data.range(of: needle) else { continue }
            guard match == nil, data.range(of: needle, in: range.upperBound..<data.endIndex) == nil
            else { throw InstallError.incompatibleClaude }
            match = (range, prefix.utf8.count)
        }
        guard let match, replacement.count == 23 else {
            throw InstallError.incompatibleClaude
        }

        // The tiny fixture is deliberately not an ASAR. Production archives
        // continue below so their per-file and embedded header hashes stay valid.
        if installDirectoryOverride != nil {
            let handle = try FileHandle(forWritingTo: asar)
            defer { try? handle.close() }
            try handle.seek(toOffset: UInt64(match.range.lowerBound + match.returnOffset))
            try handle.write(contentsOf: replacement)
            return
        }

        let patchOffset = match.range.lowerBound + match.returnOffset
        guard data.count >= 16 else { throw InstallError.incompatibleClaude }
        func uint32(at offset: Int) -> Int {
            data[offset..<offset + 4].enumerated().reduce(0) {
                $0 | Int($1.element) << ($1.offset * 8)
            }
        }
        let headerSize = uint32(at: 4)
        let jsonSize = uint32(at: 12)
        let jsonRange = 16..<16 + jsonSize
        let filesStart = 8 + headerSize
        guard jsonRange.upperBound <= data.count,
              let headerObject = try JSONSerialization.jsonObject(
                with: data.subdata(in: jsonRange)) as? [String: Any],
              let files = headerObject["files"] as? [String: Any],
              let entry = asarEntry(in: files, containing: patchOffset - filesStart)
        else { throw InstallError.incompatibleClaude }

        let fileRange = (filesStart + entry.offset)..<(filesStart + entry.offset + entry.size)
        guard fileRange.contains(patchOffset), fileRange.upperBound <= data.count,
              let blockSize = entry.integrity["blockSize"] as? Int,
              blockSize > 0
        else { throw InstallError.incompatibleClaude }

        var integrity = entry.integrity
        var file = data.subdata(in: fileRange)
        let relativePatch = patchOffset - fileRange.lowerBound
        file.replaceSubrange(relativePatch..<relativePatch + replacement.count, with: replacement)
        integrity["hash"] = sha256(file)
        integrity["blocks"] = stride(from: 0, to: file.count, by: blockSize).map {
            sha256(file.subdata(in: $0..<min($0 + blockSize, file.count)))
        }

        var updatedHeader = headerObject
        guard setAsarIntegrity(integrity, at: entry.path[...], node: &updatedHeader) else {
            throw InstallError.incompatibleClaude
        }
        let header = try JSONSerialization.data(withJSONObject: updatedHeader)
        guard header.count == jsonSize else { throw InstallError.incompatibleClaude }

        let handle = try FileHandle(forWritingTo: asar)
        defer { try? handle.close() }
        try handle.seek(toOffset: UInt64(patchOffset))
        try handle.write(contentsOf: replacement)
        try handle.seek(toOffset: UInt64(jsonRange.lowerBound))
        try handle.write(contentsOf: header)
        try handle.synchronize()

        try updateEmbeddedAsarHash(sha256(header), in: asar
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent())
    }

    private struct AsarEntry {
        let path: [String]
        let offset: Int
        let size: Int
        let integrity: [String: Any]
    }

    private static func asarEntry(in files: [String: Any], path: [String] = [],
                                  containing offset: Int) -> AsarEntry? {
        for (name, value) in files {
            guard let item = value as? [String: Any] else { continue }
            if let children = item["files"] as? [String: Any],
               let found = asarEntry(in: children, path: path + [name], containing: offset) {
                return found
            }
            guard let rawOffset = item["offset"] as? String,
                  let start = Int(rawOffset),
                  let size = item["size"] as? Int,
                  offset >= start, offset < start + size,
                  let integrity = item["integrity"] as? [String: Any]
            else { continue }
            return AsarEntry(path: path + [name], offset: start,
                             size: size, integrity: integrity)
        }
        return nil
    }

    private static func setAsarIntegrity(_ integrity: [String: Any],
                                         at path: ArraySlice<String>,
                                         node: inout [String: Any]) -> Bool {
        guard let name = path.first,
              var files = node["files"] as? [String: Any],
              var item = files[name] as? [String: Any]
        else { return false }
        if path.count == 1 {
            item["integrity"] = integrity
        } else if !setAsarIntegrity(integrity, at: path.dropFirst(), node: &item) {
            return false
        }
        files[name] = item
        node["files"] = files
        return true
    }

    private static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private static func updateEmbeddedAsarHash(_ hash: String, in bundle: URL) throws {
        guard let enumerator = fm.enumerator(at: bundle,
                                             includingPropertiesForKeys: [.isRegularFileKey])
        else { throw InstallError.incompatibleClaude }
        let infoFiles = enumerator.compactMap { $0 as? URL }.filter {
            $0.lastPathComponent == "Info.plist"
                && plist(at: $0)?["ElectronAsarIntegrity"] != nil
        }
        let roots = Set(infoFiles.compactMap { codeRoot(for: $0, inside: bundle) })
        let processEntitlements: [URL: Data] = Dictionary(
            uniqueKeysWithValues: try roots.compactMap { root -> (URL, Data)? in
            guard isProcessBundle(root) else { return nil }
            return (root, try entitlementsForResigning(root))
        })

        for url in infoFiles {
            guard var info = plist(at: url),
                  var archives = info["ElectronAsarIntegrity"] as? [String: Any],
                  var app = archives["Resources/app.asar"] as? [String: Any]
            else { continue }
            app["hash"] = hash
            archives["Resources/app.asar"] = app
            info["ElectronAsarIntegrity"] = archives
            let data = try PropertyListSerialization.data(fromPropertyList: info,
                                                           format: .xml, options: 0)
            try data.write(to: url, options: .atomic)
        }

        for root in roots.sorted(by: { $0.pathComponents.count > $1.pathComponents.count }) {
            let arguments: [String]
            let entitlementFile: URL?
            if let entitlements = processEntitlements[root] {
                let file = fm.temporaryDirectory
                    .appending(path: "claude-graft-entitlements-\(UUID().uuidString).plist")
                try entitlements.write(to: file, options: Data.WritingOptions.atomic)
                entitlementFile = file
                arguments = ["--force", "--options", "runtime", "--entitlements", file.path,
                             "--sign", "-", root.path]
            } else {
                entitlementFile = nil
                arguments = ["--force", "--sign", "-", root.path]
            }
            defer { if let entitlementFile { try? fm.removeItem(at: entitlementFile) } }
            guard Graft.runTool("/usr/bin/codesign", arguments) == 0 else {
                Diagnostics.note("installer.sign.failed", ["target": root.path])
                throw InstallError.signingFailed
            }
        }
    }

    private static func codeRoot(for info: URL, inside bundle: URL) -> URL? {
        var candidate = info.deletingLastPathComponent()
        while candidate.path.hasPrefix(bundle.path), candidate != bundle {
            if ["app", "xpc", "appex", "framework"].contains(candidate.pathExtension) {
                return candidate
            }
            candidate.deleteLastPathComponent()
        }
        return nil
    }

    private static func isProcessBundle(_ url: URL) -> Bool {
        ["app", "xpc", "appex"].contains(url.pathExtension)
    }

    private static func entitlementsForResigning(_ code: URL) throws -> Data {
        let file = fm.temporaryDirectory
            .appending(path: "claude-graft-original-entitlements-\(UUID().uuidString).plist")
        defer { try? fm.removeItem(at: file) }
        var entitlements: [String: Any] = [:]
        if Graft.runTool("/usr/bin/codesign",
                         ["--display", "--entitlements", file.path, "--xml", code.path]) == 0,
           let existing = plist(at: file) {
            entitlements = existing
        }
        entitlements.removeValue(forKey: "com.apple.application-identifier")
        entitlements.removeValue(forKey: "com.apple.developer.team-identifier")
        entitlements.removeValue(forKey: "keychain-access-groups")
        entitlements["com.apple.security.cs.disable-library-validation"] = true
        return try PropertyListSerialization.data(fromPropertyList: entitlements,
                                                   format: .xml, options: 0)
    }

    private static func signRuntime(in bundle: URL) throws {
        guard installDirectoryOverride == nil else { return }
        guard let entitlements = Bundle.main.url(forResource: "ClaudeRuntime",
                                                 withExtension: "entitlements") else {
            throw InstallError.signingFailed
        }
        let runtime = bundle.appending(path: "Contents/MacOS/ClaudeRuntime")
        guard Graft.runTool("/usr/bin/codesign",
                            ["--force", "--options", "runtime", "--entitlements",
                             entitlements.path, "--sign", "-", runtime.path]) == 0
        else {
            Diagnostics.note("installer.sign.failed", ["target": "runtime"])
            throw InstallError.signingFailed
        }
    }

    /// Borrow Claude's own icon and optionally recolour/badge it. The result is
    /// a real bundle icon, so Finder, Spotlight and the Dock all see it.
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
        let key = source.path + "|" + preset.rawValue
        if let cached = iconPreviews[key] { return cached }
        guard let image = renderedIcon(from: source, preset: preset, pixels: 96) else { return nil }
        iconPreviews[key] = image
        return image
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
            badgeColor(for: preset).setFill()
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

    private static func badgeColor(for preset: Shortcut.IconPreset) -> NSColor {
        switch preset {
        case .personal:
            return NSColor(srgbRed: 0.43, green: 0.22, blue: 0.72, alpha: 1)
        case .code:
            return NSColor(srgbRed: 0.08, green: 0.48, blue: 0.27, alpha: 1)
        case .research:
            return NSColor(srgbRed: 0.73, green: 0.16, blue: 0.42, alpha: 1)
        default:
            return NSColor(srgbRed: 0.08, green: 0.34, blue: 0.70, alpha: 1)
        }
    }

    /// Ad-hoc signature, otherwise macOS refuses to launch a bundle whose
    /// contents changed after the first run.
    @discardableResult
    private static func sign(_ bundle: URL) -> Bool {
        installDirectoryOverride != nil
            || Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", bundle.path]) == 0
    }

    private static func verify(_ bundle: URL) -> Bool {
        installDirectoryOverride != nil
            || Graft.runTool("/usr/bin/codesign", ["--verify", "--deep", "--strict",
                                                   bundle.path]) == 0
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
