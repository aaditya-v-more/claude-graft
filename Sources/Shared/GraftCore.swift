import AppKit
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

    // MARK: - Session records

    /// Where the command line files one transcript per session, under one
    /// directory per working directory. Redirected by the test suite, like
    /// `applicationSupportOverride`.
    static var claudeProjectsOverride: URL?

    static var claudeProjects: URL {
        claudeProjectsOverride
            ?? fm.homeDirectoryForCurrentUser.appending(path: ".claude/projects")
    }

    /// What a transcript says about the session it holds. A record is what
    /// lists a session; the transcript is the session. Everything a sidebar
    /// can show comes out of the record, so every field of one has to be
    /// pulled out of the other.
    ///
    /// Codable because the sweep writes down the facts behind every record
    /// it files, which is how a later pass tells a record still its own from
    /// one Claude has rewritten — and because a pass that had to read two
    /// hundred megabytes of transcript to learn them should only ever do
    /// that once.
    struct SessionFacts: Codable, Equatable {
        var cliSessionId: String
        var bridgeIds: [String]
        var ownerAccount: String
        var ownerOrganization: String
        var title: String
        var cwd: String
        /// Milliseconds since the epoch, which is what the records carry.
        var createdAt: Double
        var lastActivityAt: Double
        var model: String
        var effort: String
        var permissionMode: String
        /// One per distinct prompt, which is as close to "turns" as a
        /// transcript gets. Claude's own records count only the turns that
        /// finished, and disagree with this by the prompts still in flight;
        /// the number is a badge, not a boundary.
        var prompts: Int
        var branches: [String]
    }

    /// Hands every `"key":"value"` pair in one line to `take`, in the order
    /// they are written.
    ///
    /// One pass rather than a search for each field. A transcript runs to
    /// megabytes and there are a dozen fields worth having, so searching it
    /// once per field made finding them the slow part of a sweep — measured
    /// at twelve seconds over two hundred megabytes, against one for this.
    /// Bytes rather than characters for the same reason: nothing read here
    /// needs to know what a grapheme is.
    ///
    /// Strings are walked the way JSON writes them, backslash escapes and
    /// all. A value that ends early takes the rest of the line with it, so a
    /// quote pasted into a conversation would otherwise turn its content
    /// into keys.
    private static func pairs(in bytes: [UInt8], from lower: Int, to upper: Int,
                              take: (ArraySlice<UInt8>, ArraySlice<UInt8>) -> Void) {
        let quote = UInt8(ascii: "\""), backslash = UInt8(ascii: "\\"), colon = UInt8(ascii: ":")

        /// The index of the closing quote of the string opening at `i`, or
        /// nil if the line runs out inside it.
        func closingQuote(from i: Int) -> Int? {
            var j = i + 1
            while j < upper {
                if bytes[j] == backslash { j += 2; continue }
                if bytes[j] == quote { return j }
                j += 1
            }
            return nil
        }

        var i = lower
        while i < upper {
            guard bytes[i] == quote else { i += 1; continue }
            guard let keyEnd = closingQuote(from: i) else { return }
            // A string with a colon after it is a key. Anything else is a
            // value, and what is inside a value is none of this scan's
            // business — skipping it whole is what keeps pasted text out.
            guard keyEnd + 2 < upper, bytes[keyEnd + 1] == colon, bytes[keyEnd + 2] == quote
            else { i = keyEnd + 1; continue }
            guard let valueEnd = closingQuote(from: keyEnd + 2) else { return }
            take(bytes[(i + 1)..<keyEnd], bytes[(keyEnd + 3)..<valueEnd])
            i = valueEnd + 1
        }
    }

    /// A pair's value as a string. The escaped case goes back through the
    /// JSON reader rather than being unpicked by hand, because a title is
    /// the one field here a person writes, and `é` in a sidebar is
    /// worse than the cost of parsing six bytes.
    private static func text(_ value: ArraySlice<UInt8>) -> String {
        let backslash = UInt8(ascii: "\\")
        guard value.contains(backslash) else { return String(decoding: value, as: UTF8.self) }
        var quoted = [UInt8(ascii: "\"")]
        quoted.append(contentsOf: value)
        quoted.append(UInt8(ascii: "\""))
        return (try? JSONSerialization.jsonObject(with: Data(quoted),
                                                  options: [.fragmentsAllowed])) as? String
            ?? String(decoding: value, as: UTF8.self)
    }

    private static func matches(_ key: ArraySlice<UInt8>, _ name: StaticString) -> Bool {
        guard key.count == name.utf8CodeUnitCount else { return false }
        var i = key.startIndex
        for offset in 0..<name.utf8CodeUnitCount {
            if key[i] != name.utf8Start[offset] { return false }
            i += 1
        }
        return true
    }

    private static let fractionalISO: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()
    private static let wholeSecondISO = ISO8601DateFormatter()

    /// `"2026-08-29T17:32:48.893Z"` as milliseconds since the epoch. Every
    /// stamp transcripts have been seen carrying was fractional, but a whole
    /// one is still a stamp.
    private static func epochMilliseconds(_ stamp: String) -> Double? {
        guard let date = fractionalISO.date(from: stamp) ?? wholeSecondISO.date(from: stamp)
        else { return nil }
        return (date.timeIntervalSince1970 * 1000).rounded()
    }

    /// Reads one transcript and pulls out what a record needs. Nil when no
    /// bridge line is in it: that is a session the terminal ran on its own,
    /// which was never going to be in a sidebar, so nothing is missing.
    ///
    /// The first value a key takes on a line is that line's, which holds for
    /// every field read here but one. `type` does not: an answer is written
    /// `{"parentUuid":…,"message":{…,"type":"message",…},"type":"assistant"}`,
    /// so the first `type` on the line belongs to the message nested inside
    /// it and the line's own comes last. A marker line — a bridge, a title —
    /// opens with its own `type` and is read by that; an answer is recognised
    /// by carrying `assistant` anywhere, which is the only rule the shape
    /// above leaves standing.
    static func sessionFacts(inTranscriptAt url: URL, cliSessionId: String) -> SessionFacts? {
        guard let data = try? Data(contentsOf: url, options: [.mappedIfSafe]) else { return nil }
        let bytes = [UInt8](data)
        let newline = UInt8(ascii: "\n")

        var bridgeIds: [String] = []
        var ownerAccount = "", ownerOrganization = ""
        var title: String?
        var firstStamp: String?
        var lastStamp: String?
        // A session that got no answer back leaves no line carrying these,
        // so they hold the last values the transcript ever mentioned rather
        // than an honest read; the record still needs something in them.
        var model = "claude-sonnet-5", effort = "medium", permissionMode = "auto", cwd = ""
        var promptIds: Set<String> = []
        var branches: [String] = []

        var lineStart = 0
        while lineStart < bytes.count {
            var lineEnd = lineStart
            while lineEnd < bytes.count, bytes[lineEnd] != newline { lineEnd += 1 }
            defer { lineStart = lineEnd + 1 }
            guard lineEnd > lineStart else { continue }

            var type: String?
            var assistant = false
            var stamp: String?, lineCwd: String?, mode: String?, branch: String?
            var promptId: String?, lineModel: String?, lineEffort: String?
            var bridgeId: String?, account: String?, organization: String?, customTitle: String?

            pairs(in: bytes, from: lineStart, to: lineEnd) { key, value in
                func first(_ slot: inout String?) { if slot == nil { slot = text(value) } }
                if matches(key, "type") {
                    first(&type)
                    if matches(value, "assistant") { assistant = true }
                }
                else if matches(key, "timestamp") { first(&stamp) }
                else if matches(key, "cwd") { first(&lineCwd) }
                else if matches(key, "permissionMode") { first(&mode) }
                else if matches(key, "gitBranch") { first(&branch) }
                else if matches(key, "promptId") { first(&promptId) }
                else if matches(key, "model") { first(&lineModel) }
                else if matches(key, "effort") { first(&lineEffort) }
                else if matches(key, "bridgeSessionId") { first(&bridgeId) }
                else if matches(key, "ownerAccountUuid") { first(&account) }
                else if matches(key, "ownerOrganizationUuid") { first(&organization) }
                else if matches(key, "customTitle") { first(&customTitle) }
            }

            if type == "bridge-session" {
                if let bridgeId, !bridgeIds.contains(bridgeId) { bridgeIds.append(bridgeId) }
                if ownerAccount.isEmpty {
                    ownerAccount = account ?? ""
                    ownerOrganization = organization ?? ""
                }
                continue
            }
            if type == "custom-title" {
                // The last one wins: a session re-named keeps the newer name.
                title = customTitle ?? title
                continue
            }
            if let stamp {
                if firstStamp == nil { firstStamp = stamp }
                lastStamp = stamp
            }
            if assistant {
                if let lineModel { model = lineModel }
                if let lineEffort { effort = lineEffort }
            }
            if let lineCwd { cwd = lineCwd }
            if let mode { permissionMode = mode }
            if let branch, !branches.contains(branch) { branches.append(branch) }
            if let promptId { promptIds.insert(promptId) }
        }

        guard !bridgeIds.isEmpty,
              let first = firstStamp, let last = lastStamp,
              let created = epochMilliseconds(first), let active = epochMilliseconds(last),
              !ownerAccount.isEmpty, !ownerOrganization.isEmpty
        else { return nil }

        return SessionFacts(cliSessionId: cliSessionId,
                            bridgeIds: bridgeIds,
                            ownerAccount: ownerAccount,
                            ownerOrganization: ownerOrganization,
                            title: title ?? "New session",
                            cwd: cwd,
                            createdAt: created,
                            lastActivityAt: active,
                            model: model,
                            effort: effort,
                            permissionMode: permissionMode,
                            prompts: promptIds.count,
                            branches: branches)
    }

    /// The record that puts a session back in a sidebar, shaped like the
    /// ones Claude Desktop writes for the sessions it owns.
    ///
    /// The bridge id is the one thing renamed on the way: the transcript
    /// calls it `cse_…`, the record calls the same id `session_…` — measured
    /// against a session that had both written down. The name is fixed by
    /// the session too, `local_<cliSessionId>`, so a later pass can only
    /// ever find the record where this one put it, and the marker a delete
    /// leaves behind names the session by the same id.
    static func sessionRecord(for facts: SessionFacts) -> [String: Any] {
        var record: [String: Any] = [
            "sessionId": "local_\(facts.cliSessionId)",
            "cliSessionId": facts.cliSessionId,
            "cwd": facts.cwd,
            "originCwd": facts.cwd,
            "lastFocusedAt": facts.lastActivityAt,
            "createdAt": facts.createdAt,
            "lastActivityAt": facts.lastActivityAt,
            "model": facts.model,
            "effort": facts.effort,
            "isArchived": false,
            "title": facts.title,
            "titleSource": "auto",
            "permissionMode": facts.permissionMode,
            // A record written by the desktop carries its MCP server config
            // here. Nothing the transcript holds can recover that, and an
            // empty list is what a session sees when none is configured.
            "remoteMcpServersConfig": [String](),
            "chromePermissionMode": "skip_all_permission_checks",
            "completedTurns": facts.prompts,
            "lastSpawnRootDetected": false,
            "bridgeSessionIds": facts.bridgeIds.map {
                $0.hasPrefix("cse_") ? "session_" + $0.dropFirst(4) : $0
            },
            "remoteControlAutoEligible": true,
            "alwaysAllowedReasons": [String](),
            "sessionPermissionUpdates": [String](),
            "classifierSummaryEnabled": true,
            "reportFindingsCard": true,
            "spawnSeed": [String: Any](),
        ]
        if !facts.branches.isEmpty { record["writtenBranches"] = facts.branches }
        return record
    }

    /// Parsed records, keyed by file and re-read only when the file changes.
    /// A sweep of several stores walks hundreds of these every time, and each
    /// one is small; reading them all is what the budget is for, not the
    /// parsing.
    private static var recordCache: [String: (stamp: Date, size: Int, cliSessionId: String?)] = [:]
    private static let recordCacheLock = NSLock()

    /// One transcript's parse, remembered against the file it came from.
    struct CachedTranscript: Codable {
        var modified: Double
        var size: Int
        var facts: SessionFacts?
    }

    /// Parsed transcripts, kept on disk rather than only in memory.
    ///
    /// A shortcut files records on its way to opening Claude, and a launcher
    /// is a process that exits the moment it has handed over — so a cache
    /// that lived only in memory would be cold every single time, and cold
    /// is two hundred megabytes of transcript standing between a double
    /// click and a window. On disk it is paid once, by whoever pays it
    /// first.
    private static var transcriptCache: [String: CachedTranscript] = [:]
    private static var transcriptCacheLoaded = false
    private static var transcriptCacheDirty = false
    private static let transcriptCacheLock = NSLock()

    static var transcriptCacheFile: URL {
        applicationSupport.appending(path: "ClaudeGraft/transcript-cache.json")
    }

    private static func loadTranscriptCache() {
        guard !transcriptCacheLoaded else { return }
        transcriptCacheLoaded = true
        guard let data = try? Data(contentsOf: transcriptCacheFile),
              let cache = try? JSONDecoder().decode([String: CachedTranscript].self, from: data)
        else { return }
        transcriptCache = cache
    }

    /// Writes the cache back, keeping only what this pass looked at, and
    /// folding in anything another process learned meanwhile. Two Grafts can
    /// be walking this at once — a launcher opening a shortcut while the menu
    /// bar app runs a pass of its own — and the worst a lost entry costs is
    /// one transcript parsed twice.
    private static func saveTranscriptCache(visited: Set<String>) {
        transcriptCacheLock.lock()
        defer { transcriptCacheLock.unlock() }
        guard transcriptCacheDirty else { return }
        transcriptCacheDirty = false
        transcriptCache = transcriptCache.filter { visited.contains($0.key) }
        var merged = transcriptCache
        if let data = try? Data(contentsOf: transcriptCacheFile),
           let onDisk = try? JSONDecoder().decode([String: CachedTranscript].self, from: data) {
            for (path, entry) in onDisk where merged[path] == nil && visited.contains(path) {
                merged[path] = entry
            }
        }
        guard let data = try? JSONEncoder().encode(merged) else { return }
        try? fm.createDirectory(at: transcriptCacheFile.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        try? data.write(to: transcriptCacheFile, options: .atomic)
    }

    /// Every directory under Application Support holding a chat store —
    /// including profiles this app was never told about, because the
    /// question a sweep asks is whether any Claude anywhere is listing the
    /// session, and a record in a profile outside every shortcut still means
    /// one is.
    static func sessionStoreProfiles() -> [URL] {
        guard let names = try? fm.contentsOfDirectory(atPath: applicationSupport.path) else { return [] }
        return names
            .filter { !$0.hasPrefix(".") }
            .map { applicationSupport.appending(path: $0) }
            .filter { isDirectory($0.appending(path: "claude-code-sessions")) }
    }

    /// What one walk of the chat stores found.
    struct StoreContents {
        /// The session a record describes, against the organisation directory
        /// the record sits in. The directory rather than the file name,
        /// because knowing where a record was is what makes its absence
        /// later mean something.
        var records: [String: String] = [:]
        /// The moment of every deletion marker.
        var deletions: [Double] = []
        /// What every deletion marker is named after. A record this app filed
        /// is named for its session, so the marker Claude leaves when one is
        /// deleted names the session too — which is the one identity a
        /// deletion can be read by rather than inferred.
        var deleted: Set<String> = []
        /// Every organisation directory this walk actually managed to read.
        /// A directory missing from here was not looked in, which is a
        /// different thing from being looked in and found empty.
        var stores: Set<String> = []
    }

    /// Everything the chat stores on the machine hold, in one walk.
    /// Organization directories grafted by hand resolve through to where they
    /// really sit, so a store two profiles share is only read once and a
    /// record filed through a link counts the same as one filed beside it.
    static func sessionStoreContents() -> StoreContents {
        var contents = StoreContents()
        var walked: Set<String> = []

        for profile in sessionStoreProfiles() {
            let store = profile.appending(path: "claude-code-sessions")
            guard let accounts = try? fm.contentsOfDirectory(atPath: store.path) else { continue }
            for account in accounts
            where !account.hasPrefix(".") && !account.hasSuffix(stashSuffix) {
                let accountDir = store.appending(path: account)
                guard let orgs = try? fm.contentsOfDirectory(atPath: accountDir.path) else { continue }
                for org in orgs
                where !org.hasPrefix(".") && !org.hasSuffix(stashSuffix) {
                    let orgDir = accountDir.appending(path: org)
                    guard let names = try? fm.contentsOfDirectory(atPath: orgDir.path) else { continue }
                    let resolved = orgDir.resolvingSymlinksInPath().path
                    contents.stores.insert(resolved)
                    for name in names {
                        let file = orgDir.appending(path: name).resolvingSymlinksInPath()
                        guard walked.insert(file.path).inserted else { continue }
                        if name.hasPrefix("local_"), name.hasSuffix(".json") {
                            if let session = cliSessionId(ofRecordAt: file) {
                                contents.records[session] = resolved
                            }
                        } else if name.hasPrefix("deleted_") {
                            contents.deleted.insert(String(name.dropFirst("deleted_".count)))
                            if let text = try? String(contentsOf: file, encoding: .utf8),
                               let when = Double(text.trimmingCharacters(in: .whitespacesAndNewlines)) {
                                contents.deletions.append(when)
                            }
                        }
                    }
                }
            }
        }
        return contents
    }

    private static func cliSessionId(ofRecordAt url: URL) -> String? {
        let attributes = try? fm.attributesOfItem(atPath: url.path)
        let stamp = attributes?[.modificationDate] as? Date ?? .distantPast
        let size = (attributes?[.size] as? Int) ?? 0

        recordCacheLock.lock()
        if let cached = recordCache[url.path], cached.stamp == stamp, cached.size == size {
            recordCacheLock.unlock()
            return cached.cliSessionId
        }
        recordCacheLock.unlock()

        let cliSessionId = (try? Data(contentsOf: url))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            .flatMap { $0["cliSessionId"] as? String }

        recordCacheLock.lock()
        recordCache[url.path] = (stamp, size, cliSessionId)
        recordCacheLock.unlock()
        return cliSessionId
    }

    /// What a sweep remembers between passes: where every record it has seen
    /// was sitting, so one that is gone from a store this pass did read is
    /// known to have been deleted by hand rather than merely out of sight;
    /// every session withdrawn that way, so none of them is ever brought
    /// back; and the facts behind every record it filed itself, so a record
    /// still holding those exact bytes is known to be one of ours to keep
    /// current.
    struct SessionRecordState: Codable, Equatable {
        var records: [String: String] = [:]
        var withdrawn: [String] = []
        var authored: [String: SessionFacts] = [:]
        /// Records missing on the last pass and not yet missing on a second.
        var vanished: [String] = []

        init(records: [String: String] = [:],
             withdrawn: [String] = [],
             authored: [String: SessionFacts] = [:],
             vanished: [String] = []) {
            self.records = records
            self.withdrawn = withdrawn
            self.authored = authored
            self.vanished = vanished
        }

        /// Every key is optional on the way in: a state file from a version
        /// before one of them existed loads with the rest, claiming nothing
        /// it does not say.
        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            records = try container.decodeIfPresent([String: String].self, forKey: .records) ?? [:]
            withdrawn = try container.decodeIfPresent([String].self, forKey: .withdrawn) ?? []
            authored = try container.decodeIfPresent([String: SessionFacts].self, forKey: .authored) ?? [:]
            vanished = try container.decodeIfPresent([String].self, forKey: .vanished) ?? []
        }
    }

    static var sessionRecordStateFile: URL {
        applicationSupport.appending(path: "ClaudeGraft/session-records.json")
    }

    static func loadSessionRecordState() -> SessionRecordState {
        guard let data = try? Data(contentsOf: sessionRecordStateFile),
              let state = try? JSONDecoder().decode(SessionRecordState.self, from: data)
        else { return SessionRecordState() }
        return state
    }

    static func saveSessionRecordState(_ state: SessionRecordState) {
        guard let data = try? JSONEncoder().encode(state) else { return }
        try? fm.createDirectory(at: sessionRecordStateFile.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        try? data.write(to: sessionRecordStateFile, options: .atomic)
    }

    /// What a pass makes of one transcript.
    enum SessionFiling: Equatable {
        case file
        /// Claude wrote its own record, or an earlier pass wrote this one.
        case alreadyRecorded
        /// Deleted in a sidebar once, and not to be brought back.
        case withdrawn
        /// Written too recently to be sure Claude will not write the record
        /// itself — the owner's own window always does, within moments.
        case tooRecent
        /// No profile on this machine holds the owner's account, so there is
        /// nowhere the record would be read. Asked again next pass, because
        /// accounts move between profiles.
        case noOwnerProfile
        /// Run from a terminal, or by a Claude without a bridge: never in a
        /// sidebar, so nothing is missing.
        case notADesktopSession
    }

    /// Whether a transcript still needs a record written for it.
    static func sessionFiling(facts: SessionFacts?,
                              recorded: Set<String>,
                              withdrawn: Set<String>,
                              deletions: [Double],
                              lastWrite: Date,
                              ownerProfile: URL?,
                              ownerIsRunning: Bool = true,
                              now: Date,
                              quietWindow: TimeInterval) -> SessionFiling {
        guard let facts else { return .notADesktopSession }
        guard !recorded.contains(facts.cliSessionId) else { return .alreadyRecorded }
        guard !withdrawn.contains(facts.cliSessionId) else { return .withdrawn }

        // A marker left behind before the first pass names a record Claude
        // wrote, by an id no transcript carries — so the only trace of which
        // session went is timing: the one that had just gone quiet is the one
        // that was on screen when the delete was pressed. A session still
        // being written grows past the window and files later, so this can
        // only hold back one whose final line fell inside it, and a marker
        // with no session near it deletes nothing but itself. Every marker
        // written since is read by name instead, which needs no guessing.
        if deletions.contains(where: { facts.lastActivityAt > $0 - 60_000 && facts.lastActivityAt <= $0 }) {
            return .withdrawn
        }

        // The wait is for one thing only: the Claude signed into the owner's
        // account writing the record itself, which it does within a second of
        // a session opening. No such Claude running is nobody about to write
        // it, and waiting then is waiting for something that is not coming —
        // which is what made closing a chat and reaching straight for the
        // other profile the one move guaranteed to miss it, every time, since
        // a sidebar is built once at launch and that launch is the press.
        if ownerIsRunning {
            guard now.timeIntervalSince(lastWrite) >= quietWindow else { return .tooRecent }
        }
        guard ownerProfile != nil else { return .noOwnerProfile }
        return .file
    }

    /// What a pass does with a record that already exists on disk.
    enum SessionUpdate: Equatable {
        /// Nothing to do: the record is not one this app wrote, or it already
        /// says what the transcript says.
        case leave
        /// The transcript has moved past the record, which still holds what
        /// this app last wrote — so the record is brought up to date.
        case refresh
        /// Something else has rewritten the record — Claude's own hand,
        /// always richer than a parsed transcript — and this app never
        /// touches it again.
        case takenOver
    }

    /// Whether a record this app filed gets rewritten as its transcript moves
    /// on. A record only stays this app's while it holds byte for byte what
    /// the last pass wrote there: Claude Desktop rewrites the records of the
    /// sessions it takes over — measured coming down over a filed record
    /// within seconds of the session being opened in the owner's window —
    /// and flattening one of those back into a parsed version would throw
    /// away everything a transcript cannot recover.
    static func sessionUpdateDecision(authored: SessionFacts?,
                                      facts: SessionFacts,
                                      diskMatchesAuthored: Bool) -> SessionUpdate {
        guard let authored, authored != facts else { return .leave }
        return diskMatchesAuthored ? .refresh : .takenOver
    }

    /// The profile a session's owner account lives on now. Accounts move
    /// between profiles — a migration, a sign-in — so it is asked for
    /// rather than assumed; the profile holding the account is where the
    /// record belongs, and where every profile grafted onto it reads.
    static func ownerProfile(for facts: SessionFacts, among profiles: [URL]) -> URL? {
        var seen: Set<String> = []
        for profile in profiles {
            guard seen.insert(profile.resolvingSymlinksInPath().path).inserted else { continue }
            if account(of: profile) == facts.ownerAccount { return profile }
        }
        return nil
    }

    /// Writes a record for every session whose transcript survived without
    /// one, keeps the ones it wrote current while their transcripts move,
    /// and hands back what it filed so the caller can say so.
    ///
    /// A Claude Desktop signed into one account will not write the record
    /// for a session owned by another, and the command line keeps one login
    /// for the whole machine — so every session started from a grafted
    /// profile has been closing without a record and vanishing from every
    /// sidebar while its transcript sat whole on disk. Filing the record
    /// where the owner's account lives puts it back in front of the session
    /// and everything grafted onto it; deleting it from a sidebar is
    /// remembered, so a recovery is a one-way thing nobody has to hold down.
    ///
    /// A session is held back until its transcript has been quiet for
    /// `quietWindow`, a minute by default. The owner's own window writes its
    /// record within moments of a session opening — measured at about a
    /// second — so a minute is far more than that wait needs, and anything
    /// still moving gets kept current by a later pass rather than frozen at
    /// the first write.
    ///
    /// `filingInto` is what the caller knows about; every profile with a chat
    /// store is added to it, because a launcher knows its own shortcut and
    /// its source and nothing else, and the account that owns a session may
    /// be sitting on neither.
    @discardableResult
    static func fileMissingSessionRecords(filingInto profiles: [URL],
                                          now: Date = Date(),
                                          quietWindow: TimeInterval = 60) -> [SessionFacts] {
        var state = loadSessionRecordState()
        let contents = sessionStoreContents()
        let candidates = profiles + sessionStoreProfiles()

        let recorded = Set(contents.records.keys)
        var withdrawn = Set(state.withdrawn)
        let vanished = Set(state.vanished)
        var missing: Set<String> = []

        // A record gone from a store this pass did read was deleted by hand,
        // and two things have to hold before a pass will say so.
        //
        // The store has to have been read. Every directory in the walk is
        // taken with a `try?`, so a profile merely out of sight — stashed
        // behind a graft, deleted, waiting on a permission — comes back
        // holding nothing, and reading that as a sidebar emptied by hand
        // withdrew every session it held. Graft stashes organization folders
        // itself, which made its own graft the surest way to do it.
        //
        // And the record has to be missing twice over. Claude re-files a
        // session under a new record name as it goes — one was seen holding a
        // session that had been filed under a different name an hour before —
        // so there is a moment when the old name is gone and the new one is
        // not yet there. A pass landing in it reads a deletion. Withdrawing
        // is forever; being sure of it costs one more pass.
        for (session, store) in state.records
        where store.hasPrefix("/") && contents.stores.contains(store)
            && !recorded.contains(session) {
            if vanished.contains(session) {
                withdrawn.insert(session)
                state.authored.removeValue(forKey: session)
            } else {
                missing.insert(session)
            }
        }

        // Where a record was is remembered until it is seen again or given up
        // on, which is what leaves the next pass something to agree with. A
        // state file written before records were remembered by directory named
        // the file instead; those entries go on sight, since every record
        // still on disk is learned again right here.
        var remembered = state.records.filter { $0.value.hasPrefix("/") }
        for (session, store) in contents.records { remembered[session] = store }
        for session in withdrawn { remembered.removeValue(forKey: session) }
        state.records = remembered
        state.vanished = missing.sorted()

        var filed: [SessionFacts] = []
        var visited: Set<String> = []
        // One `pgrep` per profile per pass, not one per transcript.
        var runningProfiles: [String: Bool] = [:]
        func running(_ profile: URL) -> Bool {
            if let known = runningProfiles[profile.path] { return known }
            let answer = isRunning(profile: profile)
            runningProfiles[profile.path] = answer
            return answer
        }
        guard let projects = try? fm.contentsOfDirectory(atPath: claudeProjects.path) else {
            state.withdrawn = withdrawn.sorted()
            saveSessionRecordState(state)
            return filed
        }

        for project in projects {
            let dir = claudeProjects.appending(path: project)
            for name in (try? fm.contentsOfDirectory(atPath: dir.path)) ?? [] {
                guard name.hasSuffix(".jsonl") else { continue }
                let transcript = dir.appending(path: name)
                visited.insert(transcript.path)
                let attributes = try? fm.attributesOfItem(atPath: transcript.path)
                let lastWrite = attributes?[.modificationDate] as? Date ?? .distantPast

                let facts = transcriptFacts(at: transcript,
                                            cliSessionId: String(name.dropLast(".jsonl".count)))
                guard let facts else { continue }
                // A session opened and closed with nothing said in it never
                // had a conversation to lose; listing it would put an empty
                // chat in front of every sidebar for the asking.
                guard facts.prompts > 0 else { continue }
                // The records this app files are named for their session, so
                // a marker naming one names the session outright. Nothing to
                // infer, and nothing for an unreadable store to confuse.
                if contents.deleted.contains(facts.cliSessionId) {
                    withdrawn.insert(facts.cliSessionId)
                    state.authored.removeValue(forKey: facts.cliSessionId)
                }
                guard !withdrawn.contains(facts.cliSessionId) else { continue }
                // A record that went missing this very pass is a question the
                // next pass answers. Filing one now would answer it wrong:
                // the record comes back under this app's name, the pass after
                // finds it there, and a chat taken out of a sidebar is quietly
                // put back by the thing that was meant to notice it going.
                guard !missing.contains(facts.cliSessionId) else { continue }
                let owner = ownerProfile(for: facts, among: candidates)

                if recorded.contains(facts.cliSessionId) {
                    // A record on disk that this app did not write belongs
                    // to whoever did. One still holding byte for byte what a
                    // pass filed here is this app's to keep current, and the
                    // transcript moving on past it is the moment to do so.
                    if let owner, let authored = state.authored[facts.cliSessionId] {
                        switch sessionUpdateDecision(authored: authored,
                                                     facts: facts,
                                                     diskMatchesAuthored:
                                                        recordOnDisk(matches: authored,
                                                                     at: recordFile(for: facts, in: owner))) {
                        case .leave:
                            break
                        case .takenOver:
                            state.authored.removeValue(forKey: facts.cliSessionId)
                        case .refresh:
                            if rewriteSessionRecord(for: facts, into: owner) {
                                state.authored[facts.cliSessionId] = facts
                            }
                        }
                    }
                    continue
                }

                guard let owner,
                      case .file = sessionFiling(facts: facts,
                                                 recorded: recorded,
                                                 withdrawn: withdrawn,
                                                 deletions: contents.deletions,
                                                 lastWrite: lastWrite,
                                                 ownerProfile: owner,
                                                 ownerIsRunning: running(owner),
                                                 now: now,
                                                 quietWindow: quietWindow)
                else { continue }

                if writeSessionRecord(for: facts, into: owner) {
                    filed.append(facts)
                    state.records[facts.cliSessionId] = recordFile(for: facts, in: owner)
                        .deletingLastPathComponent().path
                    state.authored[facts.cliSessionId] = facts
                }
            }
        }

        state.withdrawn = withdrawn.sorted()
        saveSessionRecordState(state)
        saveTranscriptCache(visited: visited)
        return filed
    }

    private static func transcriptFacts(at url: URL, cliSessionId: String) -> SessionFacts? {
        let attributes = try? fm.attributesOfItem(atPath: url.path)
        let stamp = (attributes?[.modificationDate] as? Date ?? .distantPast).timeIntervalSince1970
        let size = (attributes?[.size] as? Int) ?? 0

        transcriptCacheLock.lock()
        loadTranscriptCache()
        if let cached = transcriptCache[url.path], cached.modified == stamp, cached.size == size {
            transcriptCacheLock.unlock()
            return cached.facts
        }
        transcriptCacheLock.unlock()

        let facts = sessionFacts(inTranscriptAt: url, cliSessionId: cliSessionId)
        transcriptCacheLock.lock()
        transcriptCache[url.path] = CachedTranscript(modified: stamp, size: size, facts: facts)
        transcriptCacheDirty = true
        transcriptCacheLock.unlock()
        return facts
    }

    /// The bytes a set of facts files as a record. Written with sorted keys
    /// and no formatting, so the same facts always produce the same bytes —
    /// which is how a later pass tells a record still its own from one that
    /// something else has rewritten.
    private static func sessionRecordData(for facts: SessionFacts) -> Data? {
        try? JSONSerialization.data(withJSONObject: sessionRecord(for: facts),
                                    options: [.sortedKeys])
    }

    /// Where a session's record lives, resolved through any graft link on
    /// the way, because where a link points is where both windows read.
    private static func recordFile(for facts: SessionFacts, in profile: URL) -> URL {
        profile
            .appending(path: "claude-code-sessions")
            .appending(path: facts.ownerAccount)
            .appending(path: facts.ownerOrganization)
            .resolvingSymlinksInPath()
            .appending(path: "local_\(facts.cliSessionId).json")
    }

    /// Whether the record on disk is still the one this app wrote. Claude
    /// writes its own shape over a record it takes an interest in, so this
    /// is the line between keeping a record current and flattening one.
    private static func recordOnDisk(matches facts: SessionFacts, at file: URL) -> Bool {
        guard let onDisk = try? Data(contentsOf: file),
              let authored = sessionRecordData(for: facts)
        else { return false }
        return onDisk == authored
    }

    /// Puts one record where the session's owner will list it.
    static func writeSessionRecord(for facts: SessionFacts, into profile: URL) -> Bool {
        let file = recordFile(for: facts, in: profile)
        guard !exists(file), let data = sessionRecordData(for: facts) else { return false }
        do {
            try fm.createDirectory(at: file.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)
            try data.write(to: file, options: .atomic)
            return true
        } catch {
            return false
        }
    }

    /// Brings a record this app owns up to date with its transcript. The
    /// caller has already established that the record on disk is the one
    /// this app wrote; nothing else may be overwritten with a parsed version.
    static func rewriteSessionRecord(for facts: SessionFacts, into profile: URL) -> Bool {
        let file = recordFile(for: facts, in: profile)
        guard let data = sessionRecordData(for: facts) else { return false }
        do {
            try data.write(to: file, options: .atomic)
            return true
        } catch {
            return false
        }
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

    /// Claude itself rather than one of the processes it starts: not a helper,
    /// and not the bundled command line, whose path is lowercase.
    static func isClaudeProcess(_ command: String) -> Bool {
        command.contains("Claude.app/Contents/MacOS/Claude")
            && !command.contains("Helper")
    }

    /// The main binary of Claude itself, on no profile in particular.
    static func isDefaultInstance(_ command: String) -> Bool {
        isClaudeProcess(command) && !command.contains("--user-data-dir=")
    }

    /// `dataDirPattern`'s anchoring, for a command line already in hand.
    /// Without it one profile's path is a prefix of another's and every
    /// shorter-named profile looks like it is running.
    static func carriesDataDir(_ command: String, _ profile: URL) -> Bool {
        let needle = "user-data-dir=\(profile.path)"
        var rest = command[...]
        while let found = rest.range(of: needle) {
            if found.upperBound == command.endIndex || command[found.upperBound] == " " {
                return true
            }
            rest = command[found.upperBound...]
        }
        return false
    }

    static func commandLines() -> [String] {
        processes().map(\.command)
    }

    /// Every process on the machine, paired with the pid that owns it.
    ///
    /// `ps` rather than `pgrep`, and not only for the pid: pgrep leaves out its
    /// own ancestors, so a Claude that started the process doing the asking is
    /// a Claude it will not report.
    static func processes() -> [(id: pid_t, command: String)] {
        output("/bin/ps", ["-Ao", "pid=,command="])
            .split(separator: "\n")
            .compactMap { line in
                let text = line.trimmingCharacters(in: .whitespaces)
                guard let gap = text.firstIndex(of: " "),
                      let id = pid_t(text[text.startIndex..<gap])
                else { return nil }
                return (id, String(text[text.index(after: gap)...]))
            }
    }

    /// The pid of the Claude holding this profile, if one holds it.
    ///
    /// `isRunning` answers with a pgrep, which is all a yes or no needs but the
    /// wrong tool here: every helper Claude starts carries the same
    /// `--user-data-dir`, so the first pid pgrep offers is a renderer. Only the
    /// browser process answers to being shown.
    static func processIdentifier(of profile: URL) -> pid_t? {
        let claudes = processes().filter { isClaudeProcess($0.command) }
        if samePath(profile, mainProfile) {
            return claudes.first { isDefaultInstance($0.command) }?.id
        }
        return claudes.first { carriesDataDir($0.command, profile) }?.id
    }

    /// Show the Claude that is already there, the way clicking a Dock icon
    /// would. Two of them, because neither half does the other's job.
    ///
    /// The activation is what brings it forward: reopen on its own was
    /// measured leaving the app exactly where it was in the stacking order,
    /// three times out of three. The reopen is what gets a window back. Claude
    /// answers Electron's `window-all-closed` with an empty handler, so closing
    /// the last window leaves the process alive with nothing on screen, and its
    /// `activate` handler — which is what a reopen fires — builds the main
    /// window again when it finds none.
    @discardableResult
    static func reveal(pid: pid_t) -> Bool {
        let reopen = NSAppleEventDescriptor(
            eventClass: AEEventClass(kCoreEventClass),
            eventID: AEEventID(kAEReopenApplication),
            targetDescriptor: NSAppleEventDescriptor(processIdentifier: pid),
            returnID: AEReturnID(kAutoGenerateReturnID),
            transactionID: AETransactionID(kAnyTransactionID))
        // No reply is waited for. The answer would arrive on a run loop a
        // background queue does not have, and there is nothing in it worth
        // holding a click for.
        let delivered = (try? reopen.sendEvent(options: [.noReply], timeout: 5)) != nil
        onMain { NSRunningApplication(processIdentifier: pid)?.activate(options: []) }
        return delivered
    }

    /// Every caller here reaches `open` from a background queue, because
    /// finding out who is running means reading `ps`. The activation is AppKit,
    /// so it goes back.
    static func onMain(_ work: @escaping () -> Void) {
        if Thread.isMainThread { work() } else { DispatchQueue.main.async(execute: work) }
    }

    /// Always a new instance. LaunchServices left to pick for itself reopens
    /// whichever instance of the bundle started first — measured, with three
    /// running — and that is somebody else's profile.
    static func launchArguments(for profile: URL) -> [String] {
        var arguments = ["-n", "-a", claudeApp.path]
        // The main profile is the one launched with no --user-data-dir at all,
        // and that absence is the only mark it has. Naming it here would start
        // a Claude that nothing afterwards recognises as the main one.
        if !samePath(profile, mainProfile) {
            arguments += ["--args", "--user-data-dir=\(profile.path)"]
        }
        return arguments
    }

    @discardableResult
    static func launch(profile: URL) -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = launchArguments(for: profile)
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return false }
        return true
    }

    /// Opening a profile that is already open shows the Claude that is there.
    ///
    /// Claude Desktop never takes Electron's single-instance lock, so nothing
    /// on its side refuses a second process on one `--user-data-dir`: both come
    /// up and both write the same chat store, which is the one loss this app
    /// warns about everywhere else. Pressing Open twice was all it took.
    @discardableResult
    static func open(profile: URL) -> Bool {
        guard !isRunning(profile: profile) else {
            guard let pid = processIdentifier(of: profile) else { return false }
            let shown = reveal(pid: pid)
            fileMissingSessionRecords(filingInto: [profile, mainProfile])
            return shown
        }
        fileMissingSessionRecords(filingInto: [profile, mainProfile])
        return launch(profile: profile)
    }

    /// Records are filed on the way in because that is the only moment they
    /// are read: Claude builds its sidebar as it starts, so one landing a
    /// second later waits for the next launch to be seen. That is why this
    /// sits ahead of `launch` and behind the running check — a Claude already
    /// up read its store when it started and is not about to read it again.

    /// What a generated shortcut does when clicked.
    static func run(_ config: GraftConfig) {
        let profile = URL(fileURLWithPath: config.profileDir)
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)

        let filing = [profile, mainProfile]
            + (config.sourceDir.flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
                .map { [$0] } ?? [])

        // While it is running its files are open, so re-linking is off the
        // table and the Claude already on them is the whole answer.
        //
        // Records are still filed on the way past, after the window is up. A
        // profile that is already open is the one case where nothing else
        // will do it: the sweep has no timer, and this press is not going to
        // start anything. Without this, going looking for a chat that had not
        // arrived yet was the one act guaranteed to leave it not arriving —
        // the press that should have fetched it did nothing at all, and the
        // session waited on a launch that had already happened.
        guard !isRunning(profile: profile) else {
            if let pid = processIdentifier(of: profile) { reveal(pid: pid) }
            fileMissingSessionRecords(filingInto: filing)
            return
        }
        apply(config)
        // After the links, so a record filed through a graft lands where the
        // link now points rather than where it pointed last time.
        fileMissingSessionRecords(filingInto: filing)
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
