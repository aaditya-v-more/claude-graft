import CryptoKit
import Darwin
import Foundation

enum SidebarSync {
    struct Snapshot: Codable, Equatable {
        var pins: [String]
        var order: [String]
        var sort: String
        var orderTime: Double = 0
        var scope: String?
        var fingerprint: String = ""

        func restricted(to shared: Set<String>) -> Snapshot {
            let members = Set(pins).intersection(shared)
            var seen = Set<String>()
            return Snapshot(pins: members.sorted(),
                order: (order + pins).filter { members.contains($0) && seen.insert($0).inserted },
                sort: sort, orderTime: orderTime)
        }

        func replacing(_ shared: Set<String>, with other: Snapshot) -> Snapshot {
            var result = self
            result.pins = Set(pins.filter { !shared.contains($0) } + other.pins).sorted()
            result.order = replaceShared(order, shared: shared, wanted: other.order)
            result.sort = other.sort
            result.orderTime = max(orderTime, other.orderTime)
            return result
        }

        func sameChoices(as other: Snapshot) -> Bool {
            Set(pins) == Set(other.pins) && order == other.order && sort == other.sort
        }
    }

    static func replaceShared(_ existing: [String], shared: Set<String>, wanted: [String]) -> [String] {
        var index = 0
        var result: [String] = []
        for item in existing {
            if !shared.contains(item) { result.append(item) }
            else if index < wanted.count { result.append(wanted[index]); index += 1 }
        }
        result += wanted.dropFirst(index)
        var seen = Set<String>()
        return result.filter { seen.insert($0).inserted }
    }

    /// A baseline makes unpinning a change, rather than an invitation to put
    /// the other profile's pin back. Simultaneous reorders prefer the newer
    /// ordering; a tie and conflicting sort choices prefer the chat source.
    static func merge(_ borrower: Snapshot, _ source: Snapshot, baseline: Snapshot?) -> Snapshot {
        let left = Set(borrower.pins), right = Set(source.pins)
        let old = Set(baseline?.pins ?? [])
        var pins = Set<String>()
        for id in left.union(right).union(old) {
            let a = left.contains(id), b = right.contains(id)
            let pinned: Bool
            if let _ = baseline {
                pinned = a == b ? a : (a == old.contains(id) ? b : a)
            } else { pinned = a || b }
            if pinned { pins.insert(id) }
        }
        let preferred: Snapshot
        if let baseline, borrower.order == baseline.order { preferred = source }
        else if let baseline, source.order == baseline.order { preferred = borrower }
        else { preferred = borrower.orderTime > source.orderTime ? borrower : source }
        var seen = Set<String>()
        let order = (preferred.order + source.order + borrower.order + pins.sorted())
            .filter { pins.contains($0) && seen.insert($0).inserted }
        let sort: String
        if let baseline, source.sort == baseline.sort { sort = borrower.sort }
        else { sort = source.sort }
        return Snapshot(pins: pins.sorted(), order: order, sort: sort,
                        orderTime: max(borrower.orderTime, source.orderTime))
    }

    private struct Pair {
        var key: String
        var borrower: URL
        var source: URL
        var borrowerStore: URL
        var sourceStore: URL
        var shared: Set<String>
    }

    private struct State: Codable {
        var version = 1
        var pairs: [String: Snapshot] = [:]
    }

    private struct Reply: Decodable {
        struct Profile: Decodable {
            var path: String
            var pins: [String]
            var order: [String]
            var sort: String
            var orderTime: Double
            var scope: String?
            var fingerprint: String
            var snapshot: Snapshot {
                Snapshot(pins: pins, order: order, sort: sort, orderTime: orderTime,
                         scope: scope, fingerprint: fingerprint)
            }
        }
        var ok: Bool
        var profiles: [Profile]?
        var error: String?
    }

    private static var root: URL { Graft.applicationSupport.appending(path: "ClaudeGraft") }
    private static let processLock = NSLock()
    private static var storageBlocked = false
    static var storageOverride: (([URL], [String: Snapshot]?, [String: Set<String>]) throws -> [String: Snapshot])?

    /// Serializes launchers as well as the main app. Otherwise two shortcuts
    /// can each observe closed profiles and open the same databases at once.
    static func withLaunchLock<T>(_ work: () -> T) -> T {
        processLock.lock()
        defer { processLock.unlock() }
        try? Graft.fm.createDirectory(at: root, withIntermediateDirectories: true)
        let fd = Darwin.open(root.appending(path: "sidebar-launch.lock").path, O_CREAT | O_RDWR, 0o600)
        storageBlocked = fd < 0 || flock(fd, LOCK_EX) != 0
        defer {
            storageBlocked = false
            if fd >= 0 { _ = flock(fd, LOCK_UN); Darwin.close(fd) }
        }
        return work()
    }

    private static func sessionIDs(_ directory: URL) throws -> Set<String> {
        let profile = directory.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        guard !Graft.isSymlink(directory), directory.resolvingSymlinksInPath().path
            .hasPrefix(profile.resolvingSymlinksInPath().appending(path: "claude-code-sessions").path + "/") else {
            throw Failure("linked-chat-store")
        }
        return Set(try Graft.fm.contentsOfDirectory(atPath: directory.path)
            .filter { $0.hasPrefix("local_") && $0.hasSuffix(".json") }
            .map { String($0.dropLast(5)) })
    }

    private static func candidates() -> [Pair] {
        Graft.loadMirrorState().pairs.keys.sorted().compactMap { key in
            guard let folders = Graft.pairFolders(key) else { return nil }
            func profile(_ store: URL) -> URL? {
                let code = store.deletingLastPathComponent().deletingLastPathComponent()
                let profile = code.deletingLastPathComponent()
                guard code.lastPathComponent == "claude-code-sessions",
                      let account = Graft.account(of: profile),
                      store.deletingLastPathComponent().lastPathComponent == account,
                      Graft.samePath(profile.deletingLastPathComponent(), Graft.applicationSupport)
                else { return nil }
                return profile
            }
            guard let borrower = profile(folders.one), let source = profile(folders.other),
                  !Graft.samePath(borrower, source),
                  let one = try? sessionIDs(folders.one), let two = try? sessionIDs(folders.other),
                  !one.intersection(two).isEmpty else { return nil }
            return Pair(key: key, borrower: borrower, source: source,
                        borrowerStore: folders.one, sourceStore: folders.other, shared: one.intersection(two))
        }
    }

    static func synchronize(beforeOpening profile: URL) {
        guard !storageBlocked else { return }
        var pairs = candidates()
        var connected: Set<String> = [profile.resolvingSymlinksInPath().path]
        for _ in 0..<pairs.count {
            for pair in pairs where connected.contains(pair.borrower.path) || connected.contains(pair.source.path) {
                connected.formUnion([pair.borrower.path, pair.source.path])
            }
        }
        pairs = pairs.filter { connected.contains($0.borrower.path) && connected.contains($0.source.path) }
        guard !pairs.isEmpty else { return }
        let profiles = connected.sorted().map { URL(fileURLWithPath: $0) }
        let running = Graft.runningClaudes()
        guard !profiles.contains(where: { profile in running.contains { Graft.samePath($0, profile) } }) else {
            recordStatus("waiting", profiles: profiles)
            return
        }
        do {
            let stateFile = root.appending(path: "sidebar-sync.json")
            var state: State
            if Graft.exists(stateFile) { state = try JSONDecoder().decode(State.self, from: Data(contentsOf: stateFile)) }
            else { state = State() }
            guard state.version == 1 else { throw Failure("unsupported-baseline") }
            var shared: [String: Set<String>] = [:]
            let original = try storage(profiles, changes: nil, shared: [:])
            func matches(_ snapshot: Snapshot?, _ store: URL) -> Bool {
                snapshot?.scope == store.deletingLastPathComponent().lastPathComponent + "/" + store.lastPathComponent
            }
            pairs = pairs.filter { matches(original[$0.borrower.path], $0.borrowerStore)
                && matches(original[$0.source.path], $0.sourceStore) }
            guard !pairs.isEmpty else { throw Failure("account-not-ready") }
            for pair in pairs {
                shared[pair.borrower.path, default: []].formUnion(pair.shared)
                shared[pair.source.path, default: []].formUnion(pair.shared)
            }
            var wanted = original
            for _ in 0...pairs.count {
                var changed = false
                for pair in pairs {
                    guard let one = wanted[pair.borrower.path], let two = wanted[pair.source.path] else {
                        throw Failure("missing-profile")
                    }
                    let a = one.restricted(to: pair.shared), b = two.restricted(to: pair.shared)
                    let merged = merge(a, b, baseline: state.pairs[pair.key]?.restricted(to: pair.shared))
                    changed = changed || !a.sameChoices(as: merged) || !b.sameChoices(as: merged)
                    wanted[pair.borrower.path] = one.replacing(pair.shared, with: merged)
                    wanted[pair.source.path] = two.replacing(pair.shared, with: merged)
                    state.pairs[pair.key] = merged
                }
                if !changed { break }
            }
            let affected = profiles.filter { shared[$0.path] != nil }
            for pair in pairs {
                for folder in [pair.borrowerStore, pair.sourceStore] {
                    for id in pair.shared {
                        let file = folder.appending(path: id + ".json")
                        guard !Graft.isSymlink(file),
                              (try JSONSerialization.jsonObject(with: Data(contentsOf: file))) is [String: Any]
                        else { throw Failure("unreadable-session") }
                    }
                }
            }
            let changed = affected.contains { !original[$0.path]!.sameChoices(as: wanted[$0.path]!) }
            if changed {
                guard !Graft.runningClaudes().contains(where: { running in
                    profiles.contains { Graft.samePath($0, running) }
                }) else { throw Failure("profile-running") }
                let verified = try storage(affected, changes: wanted, shared: shared)
                for item in affected {
                    guard let result = verified[item.path],
                          result.restricted(to: shared[item.path]!).sameChoices(
                            as: wanted[item.path]!.restricted(to: shared[item.path]!))
                    else { throw Failure("verification-failed") }
                }
            }
            for pair in pairs {
                try updateRecordFlags(pair.borrowerStore, shared: pair.shared, pins: Set(wanted[pair.borrower.path]!.pins))
                try updateRecordFlags(pair.sourceStore, shared: pair.shared, pins: Set(wanted[pair.source.path]!.pins))
            }
            let liveKeys = Set(Graft.loadMirrorState().pairs.keys)
            state.pairs = state.pairs.filter { liveKeys.contains($0.key) }
            try JSONEncoder().encode(state).write(to: stateFile, options: .atomic)
            recordStatus("synced", profiles: affected)
        } catch {
            Diagnostics.note("sidebar.sync-skipped", ["reason": error.localizedDescription])
            recordStatus("retry", profiles: profiles)
        }
    }

    private static func updateRecordFlags(_ folder: URL, shared: Set<String>, pins: Set<String>) throws {
        for id in shared.sorted() {
            let file = folder.appending(path: id + ".json")
            guard !Graft.isSymlink(file),
                  var record = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any]
            else { throw Failure("unreadable-session") }
            let pinned = pins.contains(id)
            if record["isStarred"] as? Bool == pinned { continue }
            record["isStarred"] = pinned
            try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
                .write(to: file, options: .atomic)
        }
    }

    static func status(for profile: URL) -> String? {
        guard let data = try? Data(contentsOf: root.appending(path: "sidebar-sync-status.json")),
              let states = try? JSONDecoder().decode([String: String].self, from: data),
              let state = states[profile.path] else { return nil }
        switch state {
        case "synced": return L10n.text("Pinned chats and sort order are synced.")
        case "waiting": return L10n.text("Quit the linked Claude apps, then open a shortcut to sync the sidebar.")
        default: return L10n.text("Sidebar sync could not finish. It will retry when you next open a shortcut.")
        }
    }

    static func forgetPairs(except keys: Set<String>) {
        let file = root.appending(path: "sidebar-sync.json")
        guard let data = try? Data(contentsOf: file),
              var state = try? JSONDecoder().decode(State.self, from: data), state.version == 1 else { return }
        let remaining = state.pairs.filter { keys.contains($0.key) }
        guard remaining.count != state.pairs.count else { return }
        state.pairs = remaining
        try? JSONEncoder().encode(state).write(to: file, options: .atomic)
    }

    private static func recordStatus(_ status: String, profiles: [URL]) {
        let file = root.appending(path: "sidebar-sync-status.json")
        var states = (try? Data(contentsOf: file)).flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        profiles.forEach { states[$0.path] = status }
        try? JSONEncoder().encode(states).write(to: file, options: .atomic)
        Diagnostics.note("sidebar.sync", ["status": status, "profiles": profiles.map(\.lastPathComponent).joined(separator: ", ")])
    }

    struct Failure: LocalizedError {
        var reason: String
        init(_ reason: String) { self.reason = reason }
        var errorDescription: String? { reason }
    }

    static func storage(_ profiles: [URL], changes: [String: Snapshot]?,
                                shared: [String: Set<String>]) throws -> [String: Snapshot] {
        if let storageOverride { return try storageOverride(profiles, changes, shared) }
        let helper = try prepareHelper()
        let scratch = root.appending(path: ".sidebar-\(UUID().uuidString).noindex")
        try Graft.fm.createDirectory(at: scratch, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? Graft.fm.removeItem(at: scratch) }
        let output = scratch.appending(path: "result.json")
        let backups = root.appending(path: "sidebar-backups")
        try Graft.fm.createDirectory(at: backups, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let entries: [[String: Any]] = profiles.map { profile in
            var entry: [String: Any] = ["path": profile.path]
            if let snapshot = changes?[profile.path], let ids = shared[profile.path] {
                let scoped = snapshot.restricted(to: ids)
                entry["change"] = ["expected": snapshot.fingerprint, "shared": ids.sorted(),
                    "pins": scoped.pins, "order": scoped.order, "sort": scoped.sort]
            }
            return entry
        }
        let request: [String: Any] = ["action": changes == nil ? "read" : "write", "profiles": entries,
            "scratch": scratch.path, "output": output.path,
            "backup": backups.appending(path: "\(UUID().uuidString).json").path]
        let file = scratch.appending(path: "request.json")
        try JSONSerialization.data(withJSONObject: request, options: [.sortedKeys]).write(to: file)
        let task = Process()
        task.executableURL = helper.appending(path: "Contents/MacOS/sidebar-storage")
        task.arguments = [file.path]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        // Inherited Electron switches must not turn the storage helper into
        // an inspector or redirect it to a different application entry point.
        task.environment = ProcessInfo.processInfo.environment.filter {
            !$0.key.hasPrefix("ELECTRON_") && !$0.key.hasPrefix("NODE_") && !$0.key.hasPrefix("DYLD_")
        }
        let done = DispatchSemaphore(value: 0)
        task.terminationHandler = { _ in done.signal() }
        try task.run()
        if done.wait(timeout: .now() + 25) == .timedOut {
            task.terminate()
            if done.wait(timeout: .now() + 3) == .timedOut { kill(task.processIdentifier, SIGKILL); done.wait() }
            throw Failure("storage-timeout")
        }
        guard task.terminationStatus == 0 else { throw Failure("storage-unavailable") }
        let reply = try JSONDecoder().decode(Reply.self, from: Data(contentsOf: output))
        guard reply.ok, let result = reply.profiles, result.count == profiles.count else {
            throw Failure(reply.error ?? "storage-unavailable")
        }
        return Dictionary(uniqueKeysWithValues: result.map { ($0.path, $0.snapshot) })
    }

    /// Copies use APFS clones: the installed runtime stays untouched and the
    /// helper follows Claude's database format without shipping another browser.
    /// Framework symlinks do not work here; Electron resolves its application
    /// resources relative to the framework's real location.
    static func prepareHelper() throws -> URL {
        let source = Graft.claudeApp.appending(path: "Contents")
        let infoData = try Data(contentsOf: source.appending(path: "Info.plist"))
        let hash = SHA256.hash(data: infoData + Data(SidebarStorage.script.utf8)).map { String(format: "%02x", $0) }.joined()
        let cache = root.appending(path: "sidebar-runtime.noindex")
        let destination = cache.appending(path: "\(hash.prefix(20))/Sidebar.app")
        if Graft.exists(destination.appending(path: "Contents/Resources/graft-sidebar.json")),
           Graft.runTool("/usr/bin/codesign", ["--verify", "--strict", destination.path]) == 0 { return destination }
        let staging = cache.appending(path: ".build-\(UUID().uuidString).noindex")
        defer { try? Graft.fm.removeItem(at: staging) }
        let app = staging.appending(path: "Sidebar.app")
        let contents = app.appending(path: "Contents")
        for directory in ["MacOS", "Resources", "Frameworks"] {
            try Graft.fm.createDirectory(at: contents.appending(path: directory), withIntermediateDirectories: true)
        }
        let sourceInfo = try PropertyListSerialization.propertyList(from: infoData, format: nil) as? [String: Any]
        guard let executable = sourceInfo?["CFBundleExecutable"] as? String,
              let name = sourceInfo?["CFBundleName"] as? String,
              !executable.contains("/"), !name.contains("/") else { throw Failure("unsupported-runtime") }
        try Graft.fm.copyItem(at: source.appending(path: "MacOS/\(executable)"),
                             to: contents.appending(path: "MacOS/sidebar-storage"))
        for framework in try Graft.fm.contentsOfDirectory(atPath: source.appending(path: "Frameworks").path) {
            guard Graft.runTool("/bin/cp", ["-cR", source.appending(path: "Frameworks/\(framework)").path,
                contents.appending(path: "Frameworks/\(framework)").path]) == 0 else { throw Failure("runtime-copy-failed") }
        }
        let archive = try asar([("package.json", Data(#"{"name":"graft-sidebar","version":"1.0.0","main":"main.js"}"#.utf8)),
                                ("main.js", Data(SidebarStorage.script.utf8))])
        try archive.data.write(to: contents.appending(path: "Resources/app.asar"))
        let info: [String: Any] = ["CFBundleExecutable": "sidebar-storage", "CFBundleName": name,
            "CFBundleDisplayName": "Claude Graft Sidebar", "CFBundleIdentifier": "graft.claude-graft.sidebar",
            "CFBundleVersion": "1", "CFBundlePackageType": "APPL", "NSPrincipalClass": "AtomApplication",
            "LSUIElement": true, "ElectronAsarIntegrity": ["Resources/app.asar": ["algorithm": "SHA256", "hash": archive.hash]]]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: contents.appending(path: "Info.plist"))
        try Data(hash.utf8).write(to: contents.appending(path: "Resources/graft-sidebar.json"))
        let entitlements = staging.appending(path: "entitlements.plist")
        try PropertyListSerialization.data(fromPropertyList: ["com.apple.security.cs.allow-jit": true], format: .xml, options: 0)
            .write(to: entitlements)
        guard Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", "--entitlements", entitlements.path, app.path]) == 0,
              Graft.runTool("/usr/bin/codesign", ["--verify", "--strict", app.path]) == 0 else { throw Failure("runtime-signing-failed") }
        try Graft.fm.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        if Graft.exists(destination) { throw Failure("runtime-cache-damaged") }
        try Graft.fm.moveItem(at: app, to: destination)
        return destination
    }

    static func asar(_ files: [(String, Data)]) throws -> (data: Data, hash: String) {
        func sha(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
        func word(_ number: Int) -> Data { var value = UInt32(number).littleEndian; return withUnsafeBytes(of: &value) { Data($0) } }
        var entries: [String: Any] = [:], body = Data()
        for (name, data) in files {
            entries[name] = ["size": data.count, "offset": String(body.count),
                "integrity": ["algorithm": "SHA256", "hash": sha(data), "blockSize": 4_194_304, "blocks": [sha(data)]]]
            body.append(data)
        }
        let header = try JSONSerialization.data(withJSONObject: ["files": entries], options: [.sortedKeys])
        var payload = word(header.count) + header
        payload.append(Data(repeating: 0, count: (4 - payload.count % 4) % 4))
        let pickle = word(payload.count) + payload
        return (word(4) + word(pickle.count) + pickle + body, sha(header))
    }
}
