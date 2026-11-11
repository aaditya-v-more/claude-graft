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
        pruneBackups(backups)
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
        let hash = SHA256.hash(data: infoData + Data(SidebarStorage.script.utf8) + Data(helperFormat.utf8))
            .map { String(format: "%02x", $0) }.joined()
        let cache = root.appending(path: "sidebar-runtime.noindex")
        let key = String(hash.prefix(20))
        let destination = cache.appending(path: "\(key)/Sidebar.app")
        if Graft.exists(destination.appending(path: "Contents/Resources/graft-sidebar.json")),
           Graft.runTool("/usr/bin/codesign", ["--verify", "--strict", destination.path]) == 0 {
            pruneHelperCache(cache, keeping: key)
            return destination
        }
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
        try sealIntegrity(of: app, headerHash: archive.hash)
        let entitlements = staging.appending(path: "entitlements.plist")
        try PropertyListSerialization.data(fromPropertyList: ["com.apple.security.cs.allow-jit": true], format: .xml, options: 0)
            .write(to: entitlements)
        guard Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", "--entitlements", entitlements.path, app.path]) == 0,
              Graft.runTool("/usr/bin/codesign", ["--verify", "--strict", app.path]) == 0 else { throw Failure("runtime-signing-failed") }
        try installHelper(app, at: destination, cache: cache)
        pruneHelperCache(cache, keeping: key)
        return destination
    }

    /// A copy already at the destination is one `prepareHelper` found wanting
    /// — a folder left half written by a pass that was killed, or one that no
    /// longer verifies — and refusing over it refused every sync on that
    /// Claude until somebody found the folder and deleted it by hand. It is
    /// moved aside rather than deleted in place, because a removal that fails
    /// part way leaves a third state, and the moved copy goes with the prune.
    static func installHelper(_ app: URL, at destination: URL, cache: URL) throws {
        try Graft.fm.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        if Graft.exists(destination) {
            let aside = cache.appending(path: ".damaged-\(UUID().uuidString).noindex")
            try Graft.fm.moveItem(at: destination, to: aside)
            Diagnostics.note("sidebar.helper-replaced", ["reason": "cached copy failed verification"])
        }
        try Graft.fm.moveItem(at: app, to: destination)
    }

    /// Kept for this machine's own Claude only. Every Claude update and every
    /// change to the script makes a new key, and each copy is a clone of the
    /// whole Electron runtime: free on the disk while the clone shares its
    /// blocks, but every Claude update unshares them, and a backup copies each
    /// one in full. Nothing ever removed them: this machine was keeping two,
    /// made two days apart. Runs under the launch lock, so no other helper is
    /// running out of a folder this removes; a staging folder is left for an
    /// hour all the same, in case a caller without the lock is building in it.
    static func pruneHelperCache(_ cache: URL, keeping key: String, now: Date = Date()) {
        var excluded = cache
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? excluded.setResourceValues(values)
        let entries = (try? Graft.fm.contentsOfDirectory(at: cache, includingPropertiesForKeys: [.creationDateKey])) ?? []
        for entry in entries {
            let name = entry.lastPathComponent
            let created = (try? entry.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? now
            let isKey = name.count == 20 && name.allSatisfy { $0.isHexDigit && !$0.isUppercase }
            let remove = (isKey && name != key)
                || name.hasPrefix(".damaged-")
                || (name.hasPrefix(".build-") && now.timeIntervalSince(created) > 3600)
            if remove { try? Graft.fm.removeItem(at: entry) }
        }
    }

    /// Every pass that writes leaves one, each holding whole sidebar records
    /// for every profile in it, and nothing removed them. An interrupted pass
    /// is recovered from the newest; twenty is room for several bad days.
    static let keptBackups = 20

    static func pruneBackups(_ backups: URL, keeping count: Int = keptBackups) {
        let files = ((try? Graft.fm.contentsOfDirectory(at: backups, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [])
            .filter { $0.pathExtension == "json" }
        guard files.count > count else { return }
        func modified(_ url: URL) -> Date {
            (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
        }
        let newestFirst = files.sorted { (modified($0), $0.lastPathComponent) > (modified($1), $1.lastPathComponent) }
        for file in newestFirst.dropFirst(count) { try? Graft.fm.removeItem(at: file) }
    }

    /// Part of the cache key, so a helper built by an earlier version of this
    /// code is rebuilt rather than reused: the one cached before the runtime
    /// began checking its integrity dictionary still verifies as signed and
    /// still cannot start.
    static let helperFormat = "sealed-integrity-1"

    /// Electron 44 refuses an `ElectronAsarIntegrity` dictionary that does not
    /// hash to a digest written into the framework when the app was packaged,
    /// so a helper carrying its own archive must carry its own digest as well.
    /// The slot is found by its sentinel and only ever written in this
    /// helper's own clone of the framework, which is then signed again since
    /// the write breaks the signature it was cloned with. A runtime without a
    /// sealed slot has nothing to seal; a slot of an unknown version refuses
    /// the helper rather than guessing at its layout.
    static func sealIntegrity(of app: URL, headerHash: String) throws {
        let framework = app.appending(path: "Contents/Frameworks/Electron Framework.framework")
        let binary = framework.appending(path: "Electron Framework").resolvingSymlinksInPath()
        guard binary.path.hasPrefix(app.resolvingSymlinksInPath().path + "/") else { throw Failure("unsupported-runtime") }
        var digest = SHA256()
        for part in ["Resources/app.asar", "SHA256", headerHash] { digest.update(data: Data(part.utf8)) }
        let sealed = Data(digest.finalize())
        let image = try Data(contentsOf: binary, options: .alwaysMapped)
        let sentinel = Data("AGbevlPCksUGKNL8TSn7wGmJEuJsXb2A".utf8)
        var slots: [Int] = [], from = image.startIndex
        while let found = image.range(of: sentinel, in: from..<image.endIndex) {
            let used = image[found.upperBound], version = image[found.upperBound + 1]
            guard used == 0 || used == 1 else { throw Failure("unsupported-runtime") }
            if used == 1 {
                guard version == 1 else { throw Failure("unsupported-runtime") }
                slots.append(found.upperBound + 2 - image.startIndex)
            }
            from = found.upperBound
        }
        guard !slots.isEmpty else { return }
        let handle = try FileHandle(forUpdating: binary)
        defer { try? handle.close() }
        for offset in slots {
            try handle.seek(toOffset: UInt64(offset))
            try handle.write(contentsOf: sealed)
        }
        try handle.close()
        guard Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", framework.path]) == 0
        else { throw Failure("runtime-signing-failed") }
        // The helper processes keep Anthropic's signature, and with it library
        // validation, which refuses a framework signed by anybody else. Signed
        // again here they load the copy beside them, and the originals they
        // were cloned from are not touched.
        let frameworks = app.appending(path: "Contents/Frameworks")
        for item in try Graft.fm.contentsOfDirectory(atPath: frameworks.path) where item.hasSuffix(".app") {
            guard Graft.runTool("/usr/bin/codesign", ["--force", "--sign", "-", frameworks.appending(path: item).path]) == 0
            else { throw Failure("runtime-signing-failed") }
        }
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

// MARK: - Project order

/// Claude keeps the order of the sidebar's project headings per account, in
/// one Local Storage entry, so two grafted accounts drift apart as soon as one
/// of them is reordered. The newer order wins and is copied to the others.
/// Folders and repositories are named the same in every profile; a saved
/// environment (`preset::<id>`) is not, so it is matched by name and target,
/// then by folder, and left out where the other account has no equivalent.
extension SidebarSync {
    static let projectOrderPrefix = "LSS-persisted.code-projects-order."
    static let presetsPrefix = "persisted.ccd-session-presets."

    struct ProjectInput {
        var path: String
        var order: String?
        var presets: String?
    }

    static func translateProjectOrder(_ order: [String], from source: [[String: Any]],
                                      to destination: [[String: Any]]) -> [String] {
        func canonical(_ value: Any?) -> String {
            guard let value, JSONSerialization.isValidJSONObject(value),
                  let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
            else { return "" }
            return String(decoding: data, as: UTF8.self)
        }
        var byNameAndTarget: [String: String] = [:], byFolder: [String: String] = [:]
        var destinationIDs = Set<String>()
        for preset in destination {
            guard let id = preset["id"] as? String else { continue }
            destinationIDs.insert(id)
            let target = preset["target"] as? [String: Any]
            let identity = (preset["name"] as? String ?? "") + "\u{0}" + canonical(target)
            if byNameAndTarget[identity] == nil { byNameAndTarget[identity] = id }
            if let folder = target?["cwd"] as? String, byFolder[folder] == nil { byFolder[folder] = id }
        }
        var sourceByID: [String: [String: Any]] = [:]
        for preset in source { if let id = preset["id"] as? String { sourceByID[id] = preset } }
        var seen = Set<String>(), result: [String] = []
        for key in order {
            var mapped: String? = key
            if key.hasPrefix("preset::") {
                let id = String(key.dropFirst("preset::".count))
                mapped = nil
                if let preset = sourceByID[id] {
                    let target = preset["target"] as? [String: Any]
                    let match = byNameAndTarget[(preset["name"] as? String ?? "") + "\u{0}" + canonical(target)]
                        ?? (target?["cwd"] as? String).flatMap { byFolder[$0] }
                    mapped = match.map { "preset::" + $0 }
                } else if destinationIDs.contains(id) {
                    mapped = key
                }
            }
            if let mapped, seen.insert(mapped).inserted { result.append(mapped) }
        }
        return result
    }

    /// Which profiles get which stored value. Only a strictly newer order is
    /// copied, and the copy keeps its time, so the next pass finds nothing to do
    /// and two orders saved at the same moment never take turns overwriting.
    static func planProjectOrders(_ inputs: [ProjectInput]) -> [String: String] {
        struct Parsed { var path: String; var order: [String]?; var time: Double; var presets: [[String: Any]] }
        func object(_ text: String?) -> [String: Any]? {
            text.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] }
        }
        let parsed = inputs.map { input -> Parsed in
            let order = object(input.order)
            let presets = (object(input.presets)?["state"] as? [String: Any])?["presets"] as? [[String: Any]]
            return Parsed(path: input.path, order: order?["value"] as? [String],
                          time: (order?["timestamp"] as? NSNumber)?.doubleValue ?? 0, presets: presets ?? [])
        }
        // A time JSON cannot carry back out is a store nobody should copy from.
        guard let newest = parsed.filter({ $0.order != nil }).max(by: { $0.time < $1.time }),
              newest.time.isFinite, let order = newest.order else { return [:] }
        var plan: [String: String] = [:]
        for item in parsed where item.path != newest.path {
            if item.order != nil && item.time >= newest.time { continue }
            var wanted = translateProjectOrder(order, from: newest.presets, to: item.presets)
            // What only this account lists — an environment the other has no
            // equivalent for, a project it has not seen yet — keeps its place
            // in the list rather than being dropped by a copy.
            let carried = Set(wanted)
            wanted += (item.order ?? []).filter { !carried.contains($0) }
            if item.order == wanted && item.time == newest.time { continue }
            // A whole time past what an integer holds stays a double rather
            // than trapping in the conversion.
            let time = Int64(exactly: newest.time).map { NSNumber(value: $0) } ?? NSNumber(value: newest.time)
            let value: [String: Any] = ["value": wanted, "tabId": "graft", "timestamp": time]
            guard let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]) else { continue }
            plan[item.path] = String(decoding: data, as: UTF8.self)
        }
        return plan
    }

    /// Every profile reachable from this one through a mirror pair.
    static func linkedProfiles(of profile: URL) -> [URL] {
        var pairs = candidates()
        var connected: Set<String> = [profile.resolvingSymlinksInPath().path]
        for _ in 0..<pairs.count {
            for pair in pairs where connected.contains(pair.borrower.path) || connected.contains(pair.source.path) {
                connected.formUnion([pair.borrower.path, pair.source.path])
            }
        }
        pairs = pairs.filter { connected.contains($0.borrower.path) && connected.contains($0.source.path) }
        guard !pairs.isEmpty else { return [] }
        return connected.sorted().map { URL(fileURLWithPath: $0) }
    }

    /// Every group of profiles sharing a history, once each. A Claude quitting
    /// says nothing about which group it belonged to, and a group that leaves
    /// Claude's own profile out is as likely to have been reordered as one
    /// that takes it in.
    static func synchronizeEveryProjectOrder() {
        var done = Set<String>()
        for pair in candidates() where !done.contains(pair.borrower.path) {
            done.formUnion(linkedProfiles(of: pair.borrower).map(\.path))
            synchronizeProjectOrder(beforeOpening: pair.borrower)
        }
    }

    static func synchronizeProjectOrder(beforeOpening profile: URL) {
        guard !storageBlocked else { return }
        let profiles = linkedProfiles(of: profile)
        guard profiles.count > 1 else { return }
        func anyRunning() -> Bool {
            let running = Graft.runningClaudes()
            return profiles.contains { profile in running.contains { Graft.samePath($0, profile) } }
        }
        guard !anyRunning() else {
            Diagnostics.note("projects.sync", ["status": "waiting"])
            return
        }
        do {
            var accounts: [String: String] = [:]
            for item in profiles {
                guard let account = Graft.account(of: item) else { throw Failure("account-not-ready") }
                accounts[item.path] = account
            }
            let read = try projectStorage(action: "projects-read", profiles: profiles.map {
                ["path": $0.path, "keys": [projectOrderPrefix + accounts[$0.path]!, presetsPrefix + accounts[$0.path]!]]
            })
            // What Claude shows comes from the desktop preferences file; the
            // Local Storage entry carries the time. Where the two disagree, the
            // preferences are what this account actually displays.
            let plan = planProjectOrders(profiles.map {
                let values = read[$0.path] ?? [:], account = accounts[$0.path]!
                return ProjectInput(path: $0.path,
                                    order: effectiveProjectOrder(values[projectOrderPrefix + account],
                                                                 desktop: desktopProjectOrder($0, account: account)),
                                    presets: values[presetsPrefix + account])
            })
            guard !plan.isEmpty else {
                Diagnostics.note("projects.sync", ["status": "unchanged"])
                return
            }
            guard !anyRunning() else { throw Failure("profile-running") }
            let targets = profiles.filter { plan[$0.path] != nil }
            var orders: [String: [String]] = [:]
            for item in targets {
                let value = try JSONSerialization.jsonObject(with: Data(plan[item.path]!.utf8)) as? [String: Any]
                guard let order = value?["value"] as? [String] else { throw Failure("verification-failed") }
                orders[item.path] = order
            }
            // The helper writes the preferences file, before the timed copy:
            // it is the file people edit by hand, and only `preferencesText`
            // keeps its key order, indent and mode. A pass stopped between the
            // two leaves the displayed order new and its time old, which the
            // next pass copies nowhere, rather than the reverse, which would
            // carry the old order out under a new time.
            let written = try projectStorage(action: "projects-write", profiles: targets.map {
                let account = accounts[$0.path]!, key = projectOrderPrefix + account
                return ["path": $0.path, "keys": [key], "values": [key: plan[$0.path]!],
                        "prefs": ["key": "code-projects-order." + account, "order": orders[$0.path]!]]
            })
            for item in targets where written[item.path]?[projectOrderPrefix + accounts[item.path]!] != plan[item.path]
                || desktopProjectOrder(item, account: accounts[item.path]!) != orders[item.path] {
                throw Failure("verification-failed")
            }
            Diagnostics.note("projects.sync", ["status": "synced",
                "profiles": targets.map(\.lastPathComponent).joined(separator: ", ")])
        } catch {
            Diagnostics.note("projects.sync-skipped", ["reason": error.localizedDescription])
        }
    }

    /// The order Claude reads at startup, from `claude_desktop_config.json`.
    static func desktopProjectOrder(_ profile: URL, account: String) -> [String]? {
        let file = profile.appending(path: "claude_desktop_config.json")
        guard !Graft.isSymlink(file), let data = try? Data(contentsOf: file),
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        return ((root["preferences"] as? [String: Any])?["epitaxyPrefs"] as? [String: Any])?[
            "code-projects-order." + account] as? [String]
    }

    /// The stored entry with its list replaced by the one Claude displays.
    static func effectiveProjectOrder(_ stored: String?, desktop: [String]?) -> String? {
        guard let desktop else { return stored }
        var value = stored.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] }
            ?? ["tabId": "graft", "timestamp": 0]
        value["value"] = desktop
        return (try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]))
            .map { String(decoding: $0, as: UTF8.self) } ?? stored
    }

    static var projectStorageOverride: ((String, [[String: Any]]) throws -> [String: [String: String]])?

    static func projectStorage(action: String, profiles: [[String: Any]]) throws -> [String: [String: String]] {
        if let projectStorageOverride { return try projectStorageOverride(action, profiles) }
        let helper = try prepareHelper()
        let scratch = root.appending(path: ".projects-\(UUID().uuidString).noindex")
        try Graft.fm.createDirectory(at: scratch, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        defer { try? Graft.fm.removeItem(at: scratch) }
        let output = scratch.appending(path: "result.json")
        let file = scratch.appending(path: "request.json")
        try JSONSerialization.data(withJSONObject: ["action": action, "profiles": profiles,
            "scratch": scratch.path, "output": output.path], options: [.sortedKeys]).write(to: file)
        let task = Process()
        task.executableURL = helper.appending(path: "Contents/MacOS/sidebar-storage")
        task.arguments = [file.path]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
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
        guard task.terminationStatus == 0,
              let reply = try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as? [String: Any]
        else { throw Failure("storage-unavailable") }
        guard reply["ok"] as? Bool == true, let list = reply["profiles"] as? [[String: Any]] else {
            throw Failure(reply["error"] as? String ?? "storage-unavailable")
        }
        var result: [String: [String: String]] = [:]
        for item in list {
            guard let path = item["path"] as? String, let values = item["values"] as? [String: Any] else { continue }
            result[path] = values.compactMapValues { $0 as? String }
        }
        return result
    }
}
