import AppKit

/// Opening must follow the selected profile even when its app was renamed in
/// Finder. Installation still uses the requested name when updating a bundle.
enum ProfileOpener {
    enum Destination: Equatable {
        case main
        case shortcut(URL)
    }

    enum Failure: LocalizedError {
        case missingShortcut
        case couldNotOpen

        var errorDescription: String? {
            switch self {
            case .missingShortcut:
                return L10n.text("This profile's shortcut could not be found. Open Claude Graft and update the shortcut, then try again.")
            case .couldNotOpen:
                return L10n.text("The selected profile could not be opened. Try again after any Claude update finishes.")
            }
        }
    }

    static func bundle(for shortcut: Shortcut) -> URL? {
        func matches(_ bundle: URL) -> Bool {
            guard bundle.pathExtension == "app",
                  let data = try? Data(contentsOf: bundle.appending(path: "Contents/Resources/graft.json")),
                  let config = try? JSONDecoder().decode(GraftConfig.self, from: data)
            else { return false }
            return Graft.samePath(URL(fileURLWithPath: config.profileDir), shortcut.profileDir)
        }

        for directory in Installer.searchDirectories {
            for name in [shortcut.name, shortcut.installedName].compactMap({ $0 }) {
                let candidate = directory.appending(path: name + ".app")
                if matches(candidate) { return candidate }
            }
        }
        for directory in Installer.searchDirectories {
            let candidates = (try? FileManager.default.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
            if let found = candidates.sorted(by: { $0.path < $1.path }).first(where: matches) {
                return found
            }
        }
        return nil
    }

    static func destination(profile: URL, shortcut: Shortcut?) throws -> Destination {
        if let shortcut, Graft.samePath(shortcut.profileDir, profile), let bundle = bundle(for: shortcut) {
            return .shortcut(bundle)
        }
        guard shortcut == nil, Graft.samePath(profile, Graft.mainProfile) else {
            throw Failure.missingShortcut
        }
        return .main
    }

    /// The launcher checks for an existing Claude on this exact profile. Letting
    /// LaunchServices reuse an instance can skip that check and the graft itself.
    static func open(profile: URL, shortcut: Shortcut?, completion: @escaping (Error?) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            func finish(_ error: Error?) {
                if let error {
                    Diagnostics.note("profile.open-failed", ["profile": profile.lastPathComponent,
                                                             "reason": error.localizedDescription])
                }
                DispatchQueue.main.async { completion(error) }
            }
            do {
                switch try destination(profile: profile, shortcut: shortcut) {
                case .main:
                    finish(Graft.open(profile: profile) ? nil : Failure.couldNotOpen)
                case .shortcut(let bundle):
                    Diagnostics.note("profile.open", ["profile": profile.lastPathComponent, "bundle": bundle.path])
                    DispatchQueue.main.async {
                        let configuration = NSWorkspace.OpenConfiguration()
                        configuration.createsNewApplicationInstance = true
                        NSWorkspace.shared.openApplication(at: bundle, configuration: configuration) { _, error in
                            finish(error)
                        }
                    }
                }
            } catch { finish(error) }
        }
    }
}
