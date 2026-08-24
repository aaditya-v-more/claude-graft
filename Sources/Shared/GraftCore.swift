import Foundation

/// Describes one grafted Claude Desktop profile: where its data lives, and
/// which other profile — if any — it borrows its Claude Code chats from.
struct GraftConfig: Codable {
    /// Absolute path of the `--user-data-dir` this shortcut launches with.
    var profileDir: String
    /// Absolute path of the profile to inherit from. `nil` keeps chats separate.
    var sourceDir: String?
}

enum Graft {
    static let fm = FileManager.default

    /// Redirected by the test suite so nothing it does can reach real profiles.
    static var applicationSupportOverride: URL?

    static var applicationSupport: URL {
        applicationSupportOverride
            ?? fm.homeDirectoryForCurrentUser.appending(path: "Library/Application Support")
    }

    /// The profile the stock Claude.app uses when launched normally.
    static var mainProfile: URL { applicationSupport.appending(path: "Claude") }

    /// Claude Desktop, wherever it was installed. /Applications is the normal
    /// place; a per-user install is the only other one worth looking in.
    static var claudeApp: URL {
        let user = fm.homeDirectoryForCurrentUser.appending(path: "Applications/Claude.app")
        let system = URL(fileURLWithPath: "/Applications/Claude.app")
        if !fm.fileExists(atPath: system.path), fm.fileExists(atPath: user.path) { return user }
        return system
    }

    /// A profile folder name has to be one plain component sitting directly in
    /// Application Support. Anything else could send a graft, or a delete, at
    /// somebody else's data.
    static func validateFolder(_ folder: String) -> String? {
        let trimmed = folder.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return "The profile folder needs a name." }
        if trimmed.contains("/") || trimmed.contains(":") {
            return "The profile folder must be a single folder name, not a path."
        }
        if trimmed == "." || trimmed == ".." || trimmed.hasPrefix(".") {
            return "“\(trimmed)” is not a usable folder name."
        }
        if trimmed == "Claude" {
            return "That is Claude's own profile folder. Pick another name."
        }
        if trimmed == "ClaudeGraft" {
            return "That folder belongs to Claude Graft itself. Pick another name."
        }
        return nil
    }

    /// Files a second profile can share wholesale. Everything absent from this
    /// list is either credential material, per-organization cache, or a store
    /// two running instances cannot both hold open. See README.
    static let sharedItems = [
        "claude_desktop_config.json",
        "Claude Extensions",
        "Claude Extensions Settings",
        "extensions-installations.json",
        "window-state.json",
        "git-worktrees.json",
        "claude-ssh-remote",
        "ssh_configs.json",
    ]

    /// Chat history lives in these two, keyed by <accountUuid>/<orgUuid>.
    static let chatStores = ["claude-code-sessions", "local-agent-mode-sessions"]

    /// Keys copied out of config.json. The rest of that file is this profile's
    /// own credentials, so it is never linked.
    static let appearanceKeys = ["userThemeMode", "locale"]

    // MARK: - Filesystem helpers

    static func isSymlink(_ url: URL) -> Bool {
        (try? fm.attributesOfItem(atPath: url.path)[.type] as? FileAttributeType) == .typeSymbolicLink
    }

    static func exists(_ url: URL) -> Bool {
        fm.fileExists(atPath: url.path) || isSymlink(url)
    }

    static func isDirectory(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        return fm.fileExists(atPath: url.path, isDirectory: &isDir) && isDir.boolValue
    }

    /// Suffix for anything a profile owned before it was grafted.
    static let stashSuffix = ".graft-own"

    /// Hidden sibling, so the app never mistakes a stashed organization folder
    /// for a real one when it scans the store.
    private static func stashURL(for url: URL) -> URL {
        url.deletingLastPathComponent()
            .appending(path: "." + url.lastPathComponent + stashSuffix)
    }

    /// Move a profile's own file or folder aside rather than destroying it.
    ///
    /// A stash already sitting beside the link does not mean the item is a
    /// redundant copy of the shared one. Claude writes config.json by renaming
    /// a temporary file over it, and a rename replaces a symlink with a
    /// regular file, so the profile quietly goes back to writing its own copy
    /// while the stash still holds the pre-graft state. A chat directory
    /// Claude recreates when it cannot follow the link does the same. Deleting
    /// in that case threw away every chat written since the graft.
    private static func stash(_ url: URL) {
        guard exists(url), !isSymlink(url) else { return }
        let stashed = stashURL(for: url)
        if exists(stashed) { absorb(stashed, into: url) }
        // Anything absorb could not move is still in there and is still this
        // profile's; leave both alone rather than write over one of them.
        guard !exists(stashed) else { return }
        try? fm.moveItem(at: url, to: stashed)
    }

    /// Fold a stash back into the copy the profile is using now, so that one
    /// item holds everything it owns. A name that appears in both is the same
    /// chat, or the same file written twice, and the live copy is the version
    /// the profile went on using.
    private static func absorb(_ stashed: URL, into live: URL) {
        guard isDirectory(stashed), isDirectory(live),
              !isSymlink(stashed), !isSymlink(live)
        else {
            // Two files: the profile overwrote the stashed one itself, so the
            // live copy supersedes it. A stashed directory against a live file
            // is neither, and is left alone rather than guessed at.
            if !isDirectory(stashed) { try? fm.removeItem(at: stashed) }
            return
        }

        for name in (try? fm.contentsOfDirectory(atPath: stashed.path)) ?? [] {
            let from = stashed.appending(path: name)
            let to = live.appending(path: name)
            if exists(to) {
                absorb(from, into: to)
            } else {
                try? fm.moveItem(at: from, to: to)
            }
        }
        // An empty shell left behind would read as a stash again on the next
        // launch. Anything still in there failed to move and stays.
        if ((try? fm.contentsOfDirectory(atPath: stashed.path)) ?? [""]).isEmpty {
            try? fm.removeItem(at: stashed)
        }
    }

    /// Put back what this profile owns: the state it had before the graft, and
    /// anything it has written since the link stopped being followed. Bailing
    /// out because something already sits at the link is what left stashes
    /// orphaned, and an orphaned stash is what armed the next graft to delete.
    private static func unstash(_ link: URL) {
        if isSymlink(link) { try? fm.removeItem(at: link) }
        let stashed = stashURL(for: link)
        guard exists(stashed) else { return }
        guard !exists(link) else { return absorb(stashed, into: link) }
        try? fm.moveItem(at: stashed, to: link)
    }

    /// Point `link` at `target`, preserving anything already there.
    @discardableResult
    static func relink(target: URL, at link: URL) -> Bool {
        guard exists(target) else { return false }
        // Linking something to itself would stash the real thing away and leave
        // a symlink pointing at its own empty name.
        guard !samePath(target, link) else { return false }
        if isSymlink(link) {
            if (try? fm.destinationOfSymbolicLink(atPath: link.path)) == target.path { return true }
            try? fm.removeItem(at: link)
        } else if exists(link) {
            stash(link)
        }
        try? fm.createDirectory(at: link.deletingLastPathComponent(), withIntermediateDirectories: true)
        do {
            try fm.createSymbolicLink(atPath: link.path, withDestinationPath: target.path)
            return true
        } catch {
            return false
        }
    }

    /// Most recently touched child directory, used to resolve the active
    /// organization inside an account directory.
    static func newestChild(of dir: URL) -> String? {
        guard let names = try? fm.contentsOfDirectory(atPath: dir.path) else { return nil }
        return names
            .filter { !$0.hasPrefix(".") && !$0.hasSuffix(stashSuffix) }
            .map { (name: $0, date: modified(dir.appending(path: $0))) }
            .sorted { $0.date > $1.date }
            .first?.name
    }

    static func modified(_ url: URL) -> Date {
        (try? fm.attributesOfItem(atPath: url.path)[.modificationDate] as? Date) ?? .distantPast
    }

    // MARK: - Profile identity

    static func configJSON(of profile: URL) -> [String: Any] {
        readableConfigJSON(of: profile) ?? [:]
    }

    /// The same, except that a file which is there and will not parse comes
    /// back as nil rather than as an empty config. Claude writes this file by
    /// renaming a temporary over it, so a read landing mid-rename sees a
    /// truncated one; a profile that has simply never been signed in has no
    /// file at all. Anything that would write the config back, or decide where
    /// a profile's chats go, has to tell those two apart.
    static func readableConfigJSON(of profile: URL) -> [String: Any]? {
        let url = profile.appending(path: "config.json")
        guard let data = try? Data(contentsOf: url) else {
            return exists(url) ? nil : [:]
        }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    /// The account this profile is currently signed into.
    static func account(of profile: URL) -> String? {
        configJSON(of: profile)["lastKnownAccountUuid"] as? String
    }

    // MARK: - Plan usage

    /// How much of a plan's two windows has been spent, as Claude records it in
    /// the profile. `fh` is the five-hour window, `sd` the seven-day one, both
    /// percentages already used.
    struct Usage: Equatable {
        var fiveHour: Int
        var week: Int
        var organization: String?
        var sampled: Date
        /// Worked out from the history rather than reported: Claude does not
        /// record when a window closes, but a reset shows up as the figure
        /// dropping back to nothing.
        var fiveHourReset: Date?
        var weekReset: Date?

        /// Claude only writes this while it is running, so an old sample says
        /// nothing useful about a five-hour window that has since rolled over.
        var isStale: Bool { Date().timeIntervalSince(sampled) > 5 * 60 * 60 }
    }

    /// Parsed history, keyed by file and only re-read when the file changes.
    /// Claude appends to this all day and it grows into the hundreds of
    /// kilobytes; polling it meant parsing the lot every couple of seconds.
    private static var usageCache: [String: (stamp: Date, size: Int, usage: Usage?)] = [:]
    private static let usageCacheLock = NSLock()

    /// The most recent sample a profile recorded. Nil when Claude has never run
    /// on it, or has not reported usage yet.
    static func usage(of profile: URL) -> Usage? {
        let url = profile.appending(path: "plan-usage-history.json")

        let attributes = try? fm.attributesOfItem(atPath: url.path)
        let stamp = attributes?[.modificationDate] as? Date ?? .distantPast
        let size = (attributes?[.size] as? Int) ?? 0

        usageCacheLock.lock()
        if let cached = usageCache[url.path], cached.stamp == stamp, cached.size == size {
            usageCacheLock.unlock()
            return cached.usage
        }
        usageCacheLock.unlock()

        let parsed = parseUsage(at: url)
        usageCacheLock.lock()
        usageCache[url.path] = (stamp, size, parsed)
        usageCacheLock.unlock()
        return parsed
    }

    private static func parseUsage(at url: URL) -> Usage? {
        guard let data = try? Data(contentsOf: url),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let raw = root["samples"] as? [[String: Any]]
        else { return nil }

        // Only samples carrying both figures are usable.
        var samples: [(time: Date, fiveHour: Int, week: Int, org: String?)] = []
        samples.reserveCapacity(raw.count)
        for sample in raw {
            guard let stamp = sample["t"] as? Double,
                  let values = sample["u"] as? [String: Any],
                  let fiveHour = values["fh"] as? Int,
                  let week = values["sd"] as? Int
            else { continue }
            samples.append((Date(timeIntervalSince1970: stamp / 1000), fiveHour, week, sample["org"] as? String))
        }
        guard let latest = samples.last else { return nil }

        return Usage(fiveHour: latest.fiveHour,
                     week: latest.week,
                     organization: latest.org,
                     sampled: latest.time,
                     fiveHourReset: fiveHourReset(from: samples),
                     weekReset: weekReset(from: samples))
    }

    private static let fiveHourWindow: TimeInterval = 5 * 60 * 60
    private static let weekWindow: TimeInterval = 7 * 24 * 60 * 60

    /// The window opened at the first sample after the figure was last zero,
    /// and closes five hours later. Nothing to report when none is open, or
    /// when the history starts partway through one.
    private static func fiveHourReset(
        from samples: [(time: Date, fiveHour: Int, week: Int, org: String?)]
    ) -> Date? {
        guard let latest = samples.last, latest.fiveHour > 0 else { return nil }
        for index in stride(from: samples.count - 1, to: 0, by: -1)
        where samples[index - 1].fiveHour == 0 && samples[index].fiveHour > 0 {
            let reset = samples[index].time.addingTimeInterval(fiveHourWindow)
            return reset > Date() ? reset : nil
        }
        return nil
    }

    /// Weekly resets are a cycle, so the last one seen can be rolled forward
    /// even across stretches where Claude was not running to record it.
    private static func weekReset(
        from samples: [(time: Date, fiveHour: Int, week: Int, org: String?)]
    ) -> Date? {
        var lastReset: Date?
        for (previous, current) in zip(samples, samples.dropFirst())
        where current.week < previous.week - 2 {
            lastReset = current.time
        }
        guard var reset = lastReset?.addingTimeInterval(weekWindow) else { return nil }
        let now = Date()
        var rolls = 0
        while reset <= now, rolls < 520 {
            reset.addTimeInterval(weekWindow)
            rolls += 1
        }
        return reset
    }

    /// "2d 3h 40m", "3h 40m", "12m" — days only when there is at least one.
    static func countdown(to date: Date, from now: Date = Date()) -> String? {
        let remaining = Int(date.timeIntervalSince(now))
        guard remaining > 0 else { return nil }
        let days = remaining / 86_400
        let hours = (remaining % 86_400) / 3_600
        let minutes = (remaining % 3_600) / 60
        if days > 0 { return "\(days)d \(hours)h \(minutes)m" }
        if hours > 0 { return "\(hours)h \(minutes)m" }
        return "\(max(minutes, 1))m"
    }

    // MARK: - Grafting

    /// Share the safe files, then map this profile's chat directory onto the
    /// source's. Chats are stored per account, so when the two profiles are on
    /// different accounts the link has to be made one level deeper: the
    /// destination's own <account>/<org> directory points at the source's.
    static func graft(from source: URL, into profile: URL) {
        // A profile pointed at itself would stash every one of its own files
        // away and replace them with links to nothing.
        guard !samePath(source, profile) else { return }
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)

        for item in sharedItems {
            relink(target: source.appending(path: item), at: profile.appending(path: item))
        }
        linkChatStores(from: source, into: profile)
        copyAppearance(from: source, into: profile)
    }

    static func linkChatStores(from source: URL, into profile: URL) {
        guard let sourceAccount = account(of: source) else { return }
        // A profile whose config cannot be read is one Claude is part way
        // through writing, not one signed into the same account as the source.
        // Reading it as the latter linked the whole store away, stash and all,
        // over a file that was unreadable for a moment.
        guard let ownConfig = readableConfigJSON(of: profile) else { return }
        let ownAccount = ownConfig["lastKnownAccountUuid"] as? String

        for store in chatStores {
            let src = source.appending(path: store)
            let dst = profile.appending(path: store)
            guard isDirectory(src) else { continue }

            // Same account on both sides: the whole store can be shared.
            guard let ownAccount, ownAccount != sourceAccount else {
                relink(target: src, at: dst)
                continue
            }

            guard let sourceOrg = newestChild(of: src.appending(path: sourceAccount)) else { continue }
            // This profile's own organization id, from whichever layout exists.
            guard let ownOrg = newestChild(of: dst.appending(path: ownAccount))
                    ?? newestChild(of: src.appending(path: ownAccount)) else { continue }

            // The two profiles were on one account last time and the whole
            // store is a link. Take the profile's own store back before
            // linking one organization inside it, or the store-wide stash is
            // orphaned and the next graft has something to delete.
            unstash(dst)
            try? fm.createDirectory(at: dst, withIntermediateDirectories: true)
            relink(target: src.appending(path: sourceAccount).appending(path: sourceOrg),
                   at: dst.appending(path: ownAccount).appending(path: ownOrg))

            // Keyed by organization rather than account, so it is safe as-is.
            let skills = src.appending(path: "skills-plugin")
            if isDirectory(skills) {
                relink(target: skills, at: dst.appending(path: "skills-plugin"))
            }
        }
    }

    /// Theme and locale sit in config.json beside this profile's credentials,
    /// so those two keys are copied rather than the file being linked.
    static func copyAppearance(from source: URL, into profile: URL) {
        let from = configJSON(of: source)
        guard !from.isEmpty else { return }

        // Writing the two appearance keys over a config caught mid-rename
        // took the profile's login and the account its chats are filed under
        // along with it.
        guard var into = readableConfigJSON(of: profile) else { return }

        // Nothing to gain from rewriting a file that holds a login, on every
        // launch, to put back the two values already in it.
        let wanted = appearanceKeys.filter { from[$0] != nil }
        guard wanted.contains(where: { !sameJSON(into[$0], from[$0]) }) else { return }
        for key in wanted { into[key] = from[key] }

        guard let data = try? JSONSerialization.data(withJSONObject: into,
                                                     options: [.prettyPrinted, .sortedKeys])
        else { return }
        try? data.write(to: profile.appending(path: "config.json"), options: .atomic)
    }

    /// Everything JSONSerialization hands back is an NSObject, and comparing
    /// two of those is the only comparison `Any?` allows.
    private static func sameJSON(_ a: Any?, _ b: Any?) -> Bool {
        switch (a, b) {
        case (nil, nil): return true
        case let (a as NSObject, b as NSObject): return a == b
        default: return false
        }
    }

    /// Undo a graft: drop every symlink this profile holds so it falls back to
    /// its own storage. Real files it wrote itself are left alone.
    static func ungraft(_ profile: URL) {
        for item in sharedItems {
            unstash(profile.appending(path: item))
        }
        for store in chatStores {
            let dst = profile.appending(path: store)
            if isSymlink(dst) {
                unstash(dst)
                continue
            }
            guard let accounts = try? fm.contentsOfDirectory(atPath: dst.path) else { continue }
            for account in accounts where !account.hasSuffix(stashSuffix) {
                let accountDir = dst.appending(path: account)
                if isSymlink(accountDir) { unstash(accountDir); continue }
                guard let orgs = try? fm.contentsOfDirectory(atPath: accountDir.path) else { continue }
                for org in orgs where !org.hasSuffix(stashSuffix) {
                    unstash(accountDir.appending(path: org))
                }
            }
            unstash(dst.appending(path: "skills-plugin"))
        }
    }

    // MARK: - Removing a profile

    /// Compares two locations by their real path. `deletingLastPathComponent`
    /// leaves a trailing slash behind, and symlinked parents (a temporary
    /// directory, /var) would otherwise look like different places.
    static func samePath(_ a: URL, _ b: URL) -> Bool {
        func normalize(_ url: URL) -> String {
            var path = url.resolvingSymlinksInPath().standardizedFileURL.path
            while path.count > 1, path.hasSuffix("/") { path.removeLast() }
            return path
        }
        return normalize(a) == normalize(b)
    }

    enum ProfileError: LocalizedError, Equatable {
        case mainProfile
        case outsideApplicationSupport
        case running

        var errorDescription: String? {
            switch self {
            case .mainProfile:
                return "That folder belongs to Claude itself and will not be deleted."
            case .outsideApplicationSupport:
                return "Only folders directly inside ~/Library/Application Support can be deleted."
            case .running:
                return "Claude is still running on this profile. Quit it first."
            }
        }
    }

    /// Deletes a profile's data. Guarded on every side: the folder has to sit
    /// directly in Application Support, must not be Claude's own profile, and
    /// must not be in use. Losing one means losing a login and its chats.
    static func deleteProfile(_ url: URL) throws {
        let profile = url
        guard samePath(profile.deletingLastPathComponent(), applicationSupport),
              !profile.lastPathComponent.isEmpty
        else { throw ProfileError.outsideApplicationSupport }
        guard !samePath(profile, mainProfile) else { throw ProfileError.mainProfile }
        guard !isRunning(profile: profile) else { throw ProfileError.running }
        guard isDirectory(profile) else { return }
        try fm.removeItem(at: profile)
    }

    // MARK: - Launching

    /// Run a tool to completion and hand back its exit status.
    ///
    /// This waits on a semaphore rather than calling `waitUntilExit`, which
    /// spins the calling thread's run loop. On the main thread that re-enters
    /// AppKit, so a call made during a view update would come back round into
    /// layout and crash. Blocking outright is the safe behaviour.
    @discardableResult
    static func runTool(_ tool: String, _ arguments: [String]) -> Int32 {
        guard fm.isExecutableFile(atPath: tool) else { return -1 }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: tool)
        task.arguments = arguments
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice

        let finished = DispatchSemaphore(value: 0)
        task.terminationHandler = { _ in finished.signal() }
        do { try task.run() } catch { return -1 }
        finished.wait()
        return task.terminationStatus
    }

    /// Same discipline as `runTool`, with the output collected. The pipe is
    /// drained on another queue so a tool that outruns the buffer cannot wedge
    /// the caller.
    static func output(_ tool: String, _ arguments: [String]) -> String {
        guard fm.isExecutableFile(atPath: tool) else { return "" }
        let task = Process()
        let pipe = Pipe()
        task.executableURL = URL(fileURLWithPath: tool)
        task.arguments = arguments
        task.standardOutput = pipe
        task.standardError = FileHandle.nullDevice

        var collected = Data()
        let drained = DispatchSemaphore(value: 0)
        DispatchQueue.global(qos: .userInitiated).async {
            collected = pipe.fileHandleForReading.readDataToEndOfFile()
            drained.signal()
        }
        do {
            try task.run()
        } catch {
            // Nothing will ever close the write end, so release the reader.
            try? pipe.fileHandleForWriting.close()
            drained.wait()
            return ""
        }
        drained.wait()
        return String(decoding: collected, as: UTF8.self)
    }

    /// Regex metacharacters have to survive being put inside a pgrep pattern.
    static func escapedForPattern(_ text: String) -> String {
        var escaped = ""
        for character in text {
            if "\\.[]()*+?{}|^$".contains(character) { escaped.append("\\") }
            escaped.append(character)
        }
        return escaped
    }

    /// Anchored at the end of the argument. Without that, one profile's path is
    /// a prefix of another's — `…/Claude` sits inside `…/Claude-2` — and every
    /// profile looks like it is running as soon as any longer-named one is.
    static func dataDirPattern(for profile: URL) -> String {
        "user-data-dir=\(escapedForPattern(profile.path))([[:space:]]|$)"
    }

    static func isRunning(profile: URL) -> Bool {
        // Claude launched the ordinary way carries no --user-data-dir at all,
        // so the main profile has to be recognised by the absence of one.
        if samePath(profile, mainProfile) { return defaultInstanceIsRunning() }
        return runTool("/usr/bin/pgrep", ["-f", dataDirPattern(for: profile)]) == 0
    }

    /// True when a Claude is running on no profile in particular.
    static func defaultInstanceIsRunning() -> Bool {
        commandLines().contains(where: isDefaultInstance)
    }

    /// The main binary of Claude itself: not a helper process, and not the
    /// bundled command line, whose path is lowercase.
    static func isDefaultInstance(_ command: String) -> Bool {
        command.contains("Claude.app/Contents/MacOS/Claude")
            && !command.contains("Helper")
            && !command.contains("--user-data-dir=")
    }

    static func commandLines() -> [String] {
        output("/bin/ps", ["-Ao", "command"])
            .split(separator: "\n")
            .map(String.init)
    }

    @discardableResult
    static func launch(profile: URL) -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-n", "-a", claudeApp.path, "--args", "--user-data-dir=\(profile.path)"]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return false }
        return true
    }

    /// What a generated shortcut does when clicked.
    static func run(_ config: GraftConfig) {
        let profile = URL(fileURLWithPath: config.profileDir)
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)

        // While it is running its files are open; re-linking under a live
        // instance would be unsafe, so only add a window.
        if !isRunning(profile: profile) {
            apply(config)
        }
        launch(profile: profile)
    }

    /// Bring a profile's storage in line with its configuration.
    static func apply(_ config: GraftConfig) {
        let profile = URL(fileURLWithPath: config.profileDir)
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)
        if let source = config.sourceDir, !source.isEmpty {
            graft(from: URL(fileURLWithPath: source), into: profile)
        } else {
            ungraft(profile)
        }
    }
}
