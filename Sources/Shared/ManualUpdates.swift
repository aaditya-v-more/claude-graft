import Foundation

/// Claude reads update policy before starting its updater. A timer that notices
/// work later cannot stop an already staged update from quitting the process.
/// This uses Claude's configuration library, never its bundle or login file.
enum ManualUpdates {
    struct SavedKey: Codable {
        var name: String
        var previous: Bool?
    }

    struct Change: Codable {
        var folder: String
        var configID: String
        var keys: [SavedKey]
        var createdConfig: Bool
        var createdMetadata: Bool
        var createdLibrary: Bool?
        var createdFolder: Bool?
    }

    struct State: Codable {
        var enabled = false
        var changes: [String: Change] = [:]
    }

    enum Failure: LocalizedError {
        case unreadable(String)
        case managed
        case invalidProfile

        var errorDescription: String? {
            switch self {
            case .unreadable(let name):
                return L10n.format("Manual update mode could not read %@. Its existing configuration was kept.", name)
            case .managed:
                return L10n.text("Claude has managed or remotely supplied configuration. Its administrator needs to set disableAutoUpdates; a local setting cannot guarantee protection.")
            case .invalidProfile:
                return L10n.text("Manual update mode refused a configuration outside this profile's own folder.")
            }
        }
    }

    static var stateFile: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/manual-updates.json") }
    private static let lock = NSLock()
    private static let policyKeys = ["disableAutoUpdates", "autoUpdate.disabled"]

    static func readState() throws -> State {
        guard Graft.exists(stateFile) else { return State() }
        guard let data = try? Data(contentsOf: stateFile),
              let state = try? JSONDecoder().decode(State.self, from: data)
        else { throw Failure.unreadable(L10n.text("Graft's saved update settings")) }
        return state
    }

    private static func save(_ state: State) throws {
        try JSONEncoder().encode(state).write(to: stateFile, options: .atomic)
    }

    private static func withState(_ action: (inout State) throws -> Void) throws {
        lock.lock()
        defer { lock.unlock() }
        try Graft.fm.createDirectory(at: stateFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        let file = stateFile.deletingLastPathComponent().appending(path: "manual-updates.lock")
        let descriptor = open(file.path, O_RDWR | O_CREAT, 0o600)
        guard descriptor >= 0 else { throw Failure.unreadable(L10n.text("Graft's update settings lock")) }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX) == 0 else { throw Failure.unreadable(L10n.text("Graft's update settings lock")) }
        defer { flock(descriptor, LOCK_UN) }
        var state = try readState()
        try action(&state)
    }

    static func setEnabled(_ enabled: Bool, profiles: [URL]) throws {
        try withState { state in
            if enabled { try checkManagedPolicy() }
            state.enabled = enabled
            try save(state)
            try synchronize(profiles, state: &state)
        }
        Diagnostics.note("updates.manual-mode", ["enabled": enabled])
    }

    static func synchronize(_ profiles: [URL]) throws {
        guard Graft.exists(stateFile) else { return }
        try withState { try synchronize(profiles, state: &$0) }
    }

    private static func synchronize(_ profiles: [URL], state: inout State) throws {
        if state.enabled {
            try checkManagedPolicy()
            for profile in Set(profiles) { try protect(profile, state: &state) }
        } else {
            for key in state.changes.keys.sorted() {
                guard let change = state.changes[key] else { continue }
                try restore(change)
                state.changes[key] = nil
                try save(state)
            }
        }
    }

    // Device-managed values can override local ones. Refusing that arrangement
    // is safer than showing a protection switch that Claude would ignore.
    static func checkManagedPolicy() throws {
        guard Graft.applicationSupportOverride == nil else { return }
        for path in ["/Library/Managed Preferences/com.anthropic.claudefordesktop.plist",
                     "/Library/Managed Preferences/\(NSUserName())/com.anthropic.claudefordesktop.plist"] {
            if Graft.exists(URL(fileURLWithPath: path)) { throw Failure.managed }
        }
    }

    static func library(for profile: URL) throws -> URL {
        guard Graft.samePath(profile.deletingLastPathComponent(), Graft.applicationSupport),
              profile.lastPathComponent == "Claude" || Graft.validateFolder(profile.lastPathComponent) == nil
        else { throw Failure.invalidProfile }
        let folder = profile.lastPathComponent.hasSuffix("-3p")
            ? profile.lastPathComponent : profile.lastPathComponent + "-3p"
        return try library(folder: folder)
    }

    private static func library(folder: String) throws -> URL {
        guard Graft.validateFolder(folder) == nil, folder.hasSuffix("-3p") else { throw Failure.invalidProfile }
        let dir = Graft.applicationSupport.appending(path: folder).appending(path: "configLibrary")
        // The trusted Application Support directory may itself be relocated.
        // Check the two components beneath it directly: Foundation resolves
        // aliases differently when the final directory does not exist yet.
        guard !Graft.isSymlink(dir.deletingLastPathComponent()), !Graft.isSymlink(dir)
        else { throw Failure.invalidProfile }
        return dir
    }

    private static func readObject(_ file: URL, optional: Bool = false) throws -> [String: Any] {
        if optional && !Graft.exists(file) { return [:] }
        guard !Graft.isSymlink(file), let data = try? Data(contentsOf: file),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw Failure.unreadable(file.lastPathComponent) }
        return object
    }

    private static func writeObject(_ object: [String: Any], to file: URL) throws {
        try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .prettyPrinted])
            .write(to: file, options: .atomic)
    }

    private static func bool(_ value: Any) -> Bool? {
        guard CFGetTypeID(value as CFTypeRef) == CFBooleanGetTypeID() else { return nil }
        return value as? Bool
    }

    private static func validID(_ id: String) -> Bool {
        UUID(uuidString: id)?.uuidString.lowercased() == id
    }

    private static func protect(_ profile: URL, state: inout State) throws {
        let dir = try library(for: profile)
        let folder = dir.deletingLastPathComponent().lastPathComponent
        let metadataFile = dir.appending(path: "_meta.json")
        let hadMetadata = Graft.exists(metadataFile)
        var metadata = try readObject(metadataFile, optional: true)
        guard metadata["hybridPointer"] == nil else { throw Failure.managed }
        guard metadata["entries"] == nil || metadata["entries"] is [[String: Any]]
        else { throw Failure.unreadable(L10n.text("Claude's configuration list")) }
        let applied = metadata["appliedId"] as? String
        if let applied, !validID(applied) { throw Failure.unreadable(L10n.text("Claude's selected configuration")) }
        if metadata["appliedId"] != nil && applied == nil { throw Failure.unreadable(L10n.text("Claude's selected configuration")) }
        let id = applied ?? UUID().uuidString.lowercased()
        let file = dir.appending(path: id + ".json")
        var config = try readObject(file, optional: applied == nil)
        guard config["bootstrapUrl"] == nil && config["bootstrap.url"] == nil else { throw Failure.managed }
        let key = folder + "/" + id
        var keys = state.changes[key]?.keys ?? []
        for name in policyKeys where config[name] != nil {
            guard let previous = bool(config[name]!) else { throw Failure.unreadable(name) }
            if !keys.contains(where: { $0.name == name }) {
                keys.append(SavedKey(name: name, previous: previous))
            }
        }
        if keys.isEmpty { keys = [SavedKey(name: policyKeys[0], previous: nil)] }
        if state.changes[key] == nil {
            state.changes[key] = Change(folder: folder, configID: id, keys: keys,
                                        createdConfig: applied == nil, createdMetadata: !hadMetadata,
                                        createdLibrary: !Graft.exists(dir),
                                        createdFolder: !Graft.exists(dir.deletingLastPathComponent()))
        }
        state.changes[key]?.keys = keys
        // The undo record lands first, including on a disk that fills up
        // between these writes. It contains no provider credentials.
        try save(state)
        let needsWrite = state.changes[key]!.keys.contains { saved in
            config[saved.name].flatMap(bool) != true
        }
        for saved in state.changes[key]!.keys { config[saved.name] = true }
        try Graft.fm.createDirectory(at: dir, withIntermediateDirectories: true)
        if needsWrite { try writeObject(config, to: file) }
        if applied == nil {
            var entries = metadata["entries"] as? [[String: Any]] ?? []
            if !entries.contains(where: { $0["id"] as? String == id }) {
                entries.append(["id": id, "name": "Manual updates"])
            }
            metadata["entries"] = entries
            metadata["appliedId"] = id
            try writeObject(metadata, to: metadataFile)
        }
    }

    private static func restore(_ change: Change) throws {
        let dir = try library(folder: change.folder)
        guard validID(change.configID),
              change.keys.allSatisfy({ policyKeys.contains($0.name) }) else { throw Failure.invalidProfile }
        let file = dir.appending(path: change.configID + ".json")
        guard Graft.exists(file) else { return }
        var config = try readObject(file)
        for saved in change.keys {
            // A setting edited after our write belongs to that edit. Removing
            // the policy must not revert unrelated changes made in Claude.
            guard let value = config[saved.name], bool(value) == true else { continue }
            config[saved.name] = saved.previous
        }
        if change.createdConfig && config.isEmpty {
            let metadataFile = dir.appending(path: "_meta.json")
            var metadata = try readObject(metadataFile, optional: true)
            if metadata["appliedId"] as? String == change.configID { metadata["appliedId"] = nil }
            if let entries = metadata["entries"] as? [[String: Any]] {
                metadata["entries"] = entries.filter { $0["id"] as? String != change.configID }
            } else if metadata["entries"] != nil { throw Failure.unreadable(L10n.text("Claude's configuration list")) }
            if change.createdMetadata, metadata.keys.allSatisfy({ $0 == "entries" }),
               (metadata["entries"] as? [Any] ?? []).isEmpty {
                if Graft.exists(metadataFile) { try Graft.fm.removeItem(at: metadataFile) }
            } else if Graft.exists(metadataFile) {
                try writeObject(metadata, to: metadataFile)
            }
            try Graft.fm.removeItem(at: file)
            // rmdir is atomic and refuses a directory somebody has filled
            // since the check. Only directories this mode created qualify.
            if change.createdLibrary == true { _ = rmdir(dir.path) }
            if change.createdFolder == true { _ = rmdir(dir.deletingLastPathComponent().path) }
        } else {
            try writeObject(config, to: file)
        }
    }
}
