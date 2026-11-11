import Foundation

/// The two lines that send a Claude's Claude Code through the relay, put into
/// that Claude's own Code environment and taken out again.
///
/// Claude keeps that environment in `ccd-environment-config.json` inside the
/// profile: `envVars` is the base64 of what Electron's safe storage made of a
/// JSON object of names and values, and it is what the Code tab's own editor
/// shows and saves. The running app decrypts it once and keeps the result, and
/// its editor saves over the whole field, so a write landing while that Claude
/// is open is not seen and is then written over. Every write here happens with
/// the profile closed, under the launch lock every route that opens a Claude
/// takes.
///
/// Encrypting needs Claude's keychain item, and a shortcut's launcher asking
/// for it would put a keychain dialog in front of every shortcut. So Graft does
/// the reading and the encrypting, and leaves two finished values per profile
/// in the journal: the field as it stands without the lines, and the same with
/// them. Putting one in place of the other is all a launcher does, and only
/// while the file still holds exactly the one expected — anything else is an
/// edit Graft has not read yet, and is left for Graft to read.
enum PoolEnvironment {
    static let fileName = "ccd-environment-config.json"
    static let field = "envVars"
    static let scopedField = "scopedEnvVars"
    static let proxyName = "HTTPS_PROXY"
    static let certificateName = "NODE_EXTRA_CA_CERTS"

    static var proxyValue: String { "http://127.0.0.1:\(UsagePool.port)" }
    static var certificateValue: String { UsagePool.certificateFile.path }
    static var lines: [String: String] { [proxyName: proxyValue, certificateName: certificateValue] }

    static var journalFile: URL { UsagePool.directory.appending(path: "environment.json") }

    struct Journal: Codable, Equatable {
        /// The relay a launcher may start, as the Graft that wrote this knows
        /// it. A launcher has no other way to find one.
        var relay: String?
        /// By profile path.
        var profiles: [String: Entry] = [:]
    }

    struct Entry: Codable, Equatable {
        var wanted: Bool
        /// The field exactly as it reads without the lines. Nil is no field.
        var plain: String?
        var pooled: String
    }

    /// The field as it is on disk, read without decrypting anything.
    enum Field: Equatable {
        case absent
        case value(String)
        /// A file that will not parse, or one Claude's own store is part way
        /// through writing: nothing is decided on it and nothing is written.
        case unreadable

        var value: String? {
            if case .value(let text) = self { return text }
            return nil
        }
    }

    static func key(for profile: URL) -> String { profile.standardizedFileURL.path }

    static func file(in profile: URL) -> URL { profile.appending(path: fileName) }

    /// Claude's store recovers a torn write from this on its next start, so a
    /// file with one beside it is about to be replaced by Claude itself.
    static func isMidWrite(_ profile: URL) -> Bool {
        FileManager.default.fileExists(atPath: file(in: profile).path + ".journal")
    }

    static func outer(in profile: URL) -> [String: Any]? {
        guard !isMidWrite(profile) else { return nil }
        let url = file(in: profile)
        guard let data = try? Data(contentsOf: url) else {
            return FileManager.default.fileExists(atPath: url.path) ? nil : [:]
        }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    static func field(in profile: URL) -> Field {
        guard let outer = outer(in: profile) else { return .unreadable }
        switch outer[field] {
        case nil, is NSNull: return .absent
        case let text as String: return .value(text)
        default: return .unreadable
        }
    }

    /// Replaces the one field and nothing else, by renaming a complete file
    /// over the old one, which is how Claude's own store writes it too.
    @discardableResult
    static func write(_ value: String?, field name: String = field, into profile: URL) -> Bool {
        guard var object = outer(in: profile) else { return false }
        object[name] = value
        guard let data = try? JSONSerialization.data(
            withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        else { return false }
        let url = file(in: profile)
        let temporary = profile.appending(path: ".\(fileName).graft-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: temporary.path, contents: data,
                                             attributes: [.posixPermissions: 0o600])
        else { return false }
        guard rename(temporary.path, url.path) == 0 else {
            unlink(temporary.path)
            return false
        }
        return true
    }

    static func readJournal() -> Journal {
        journalOnDisk() ?? Journal()
    }

    /// Nil when there is no journal, or none that reads: after `brew
    /// uninstall --zap` the folder holding it is gone, and the relay works
    /// from the copy it read last.
    static func journalOnDisk() -> Journal? {
        (try? Data(contentsOf: journalFile)).flatMap { try? JSONDecoder().decode(Journal.self, from: $0) }
    }

    static func writeJournal(_ journal: Journal) {
        try? FileManager.default.createDirectory(at: UsagePool.directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        guard let data = try? JSONEncoder().encode(journal) else { return }
        let temporary = UsagePool.directory.appending(path: ".environment-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: temporary.path, contents: data,
                                             attributes: [.posixPermissions: 0o600]),
              rename(temporary.path, journalFile.path) == 0
        else {
            unlink(temporary.path)
            return
        }
    }

    /// Puts the right value in place for a profile about to be opened. Runs
    /// under the launch lock with the profile known to be closed.
    ///
    /// `relayReady` is asked only when the lines are, or are about to be, in
    /// place: they point Claude Code at a proxy, and a proxy that is not there
    /// leaves it unable to reach the service at all. A Claude that cannot have
    /// its relay opens without the lines rather than without Claude Code.
    static func settle(beforeOpening profile: URL, relayReady: () -> Bool) {
        let journal = readJournal()
        guard let entry = journal.profiles[key(for: profile)] else { return }
        let current = field(in: profile)
        guard current != .unreadable else {
            Diagnostics.note("pool.environment unreadable", ["profile": profile.lastPathComponent])
            return
        }
        let value = current.value
        var wanted = entry.wanted
        if wanted, value == entry.pooled || value == entry.plain, !relayReady() {
            wanted = false
            Diagnostics.note("pool.environment no relay", ["profile": profile.lastPathComponent])
        }
        let target = wanted ? entry.pooled : entry.plain
        guard value != target else { return }
        guard value == (wanted ? entry.plain : entry.pooled) else {
            Diagnostics.note("pool.environment changed since read", ["profile": profile.lastPathComponent])
            return
        }
        let written = write(target, into: profile)
        Diagnostics.note("pool.environment written", ["profile": profile.lastPathComponent,
                                                      "lines": wanted, "ok": written])
    }

    /// Puts every closed profile's environment back as it was without the
    /// lines, for when Graft itself is going: `graft-launch --stop-sharing`,
    /// for the cask's uninstall step to run, and the relay once its own file
    /// has been gone a while. Nothing else takes the lines out of Claude's own
    /// profile, which has no launcher, and a Claude still holding them after
    /// the relay has gone — the next logout, at the latest — has no Claude Code
    /// at all. The swap needs no key: the journal holds the finished value.
    ///
    /// Every entry is marked unwanted, so a launcher left behind puts nothing
    /// back. A Graft that is still installed sets them again on its next pass.
    ///
    /// Answers the profiles that may still point at the relay: one that is
    /// open, which would only write its own copy back, and one whose field is
    /// neither value Graft left, which without the key cannot be read. Lines
    /// copied into a folder's scoped values cannot be reached this way either.
    @discardableResult
    static func takeOutLines(_ remembered: Journal? = nil, running: (URL) -> Bool) -> [String] {
        let onDisk = journalOnDisk()
        var journal = onDisk ?? remembered ?? Journal()
        // A zap has taken the folder. Taking the launch lock or writing a note
        // would make it again, and no launcher can be acting on a journal that
        // is not there.
        let present = FileManager.default.fileExists(atPath: Graft.applicationSupport
            .appending(path: "ClaudeGraft").path)
        var left: [String] = []
        for (path, entry) in journal.profiles.sorted(by: { $0.key < $1.key }) {
            let profile = URL(fileURLWithPath: path)
            func restore() -> Bool {
                guard FileManager.default.fileExists(atPath: profile.path) else { return true }
                guard !running(profile) else { return false }
                let current = field(in: profile)
                guard current != .unreadable else { return false }
                if current.value == entry.plain { return true }
                guard current.value == entry.pooled else { return false }
                let written = write(entry.plain, into: profile)
                if present {
                    Diagnostics.note("pool.environment taken out", ["profile": profile.lastPathComponent,
                                                                    "ok": written])
                }
                return written
            }
            if !(present ? SidebarSync.withLaunchLock(restore) : restore()) { left.append(path) }
            journal.profiles[path]?.wanted = false
        }
        if onDisk != nil { writeJournal(journal) }
        return left
    }

    /// What a launcher asks: the relay a running Graft keeps up, or one it
    /// starts itself from the path Graft left. A relay started this way knows
    /// nobody until Graft tells it, and passes everything through untouched.
    static func ensureRelay(_ path: String?) -> Bool {
        if relayAnswers() { return true }
        guard let path, FileManager.default.isExecutableFile(atPath: path),
              spawnRelay(path) != nil else { return false }
        for _ in 0..<40 {
            usleep(100_000)
            if relayAnswers() { return true }
        }
        return false
    }

    static func relayAnswers() -> Bool {
        (try? PoolSocket.send(PoolCommand(), timeout: 2)) != nil
    }

    /// In a session of its own, so it outlives whoever started it: a Claude
    /// pointed at the relay cannot reach the service without it. Every
    /// descriptor the caller holds is closed on the way in.
    static func spawnRelay(_ executable: String) -> pid_t? {
        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT))
        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0)
        posix_spawn_file_actions_addopen(&actions, 1, "/dev/null", O_WRONLY, 0)
        posix_spawn_file_actions_addopen(&actions, 2, "/dev/null", O_WRONLY, 0)

        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let environment = ["HOME=\(home)", "PATH=/usr/bin:/bin:/usr/sbin:/sbin",
                           "LANG=\(ProcessInfo.processInfo.environment["LANG"] ?? "en_US.UTF-8")"]
        // Named even though it is the default, so a Graft pointed somewhere
        // else for a test starts a relay that keeps its socket where that
        // Graft will look for it.
        let arguments = [executable, "--support", Graft.applicationSupport.path]
        var argv: [UnsafeMutablePointer<CChar>?] = arguments.map { strdup($0) } + [nil]
        var envp: [UnsafeMutablePointer<CChar>?] = environment.map { strdup($0) } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        var pid: pid_t = 0
        let result = posix_spawn(&pid, executable, &actions, &attributes, &argv, &envp)
        guard result == 0 else {
            Diagnostics.note("relay spawn failed", ["errno": Int(result)])
            return nil
        }
        return pid
    }
}
