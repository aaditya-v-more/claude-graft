import AppKit
import Foundation

/// Describes one grafted Claude Desktop profile: where its data lives, and
/// which other profile — if any — it borrows its Claude Code chats from.
struct GraftConfig: Codable, Equatable {
    /// Absolute path of the `--user-data-dir` this shortcut launches with.
    var profileDir: String
    /// Absolute path of the profile to inherit from. `nil` keeps chats separate.
    ///
    /// A profile borrowing another's chats always gets its own copy of them.
    /// Bundles written by earlier versions carry a `mirrorChats` key saying
    /// whether they wanted one; decoding ignores what it does not know, so an
    /// old bundle migrates the next time its launcher runs and no bundle has
    /// to be rewritten for it to happen.
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

    /// Chat history lives in these two, keyed by <accountUuid>/<orgUuid> —
    /// except where a store spells those two shorter, which `counterpartDirectory`
    /// is the rule for.
    static let chatStores = ["claude-code-sessions", "local-agent-mode-sessions"]

    /// Sits beside the account directories in a chat store without being one.
    /// It is keyed by organization, so a walk that took it for an account
    /// reported its organization folders as chat stores of their own.
    static let nonAccountStoreItems: Set<String> = ["skills-plugin"]

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
    static func stashURL(for url: URL) -> URL {
        url.deletingLastPathComponent()
            .appending(path: "." + url.lastPathComponent + stashSuffix)
    }

    /// `<store>/<account>/<org>` is three components, which is how far above an
    /// organization folder this app can have put something away.
    static let chatStoreDepth = 3

    /// Whether this app has stashed a folder, or any folder it sits inside.
    ///
    /// There are two shapes and `stashedCounterpart` has always known both: a
    /// cross-account graft stashes the organization folder itself, and two
    /// profiles on one account stash the whole store above it, leaving the same
    /// folder one level further down. Everything that asked only about the
    /// sibling saw a store put away whole as a profile that had never held
    /// anything — and filed a whole history into the empty folder standing in
    /// its place, unarchived, with the real one orphaned beside it.
    static func isStashedAway(_ folder: URL) -> Bool {
        var here = folder.standardizedFileURL
        for _ in 0..<chatStoreDepth {
            if exists(stashURL(for: here)) { return true }
            here = here.deletingLastPathComponent()
        }
        return false
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
        guard !exists(stashed) else {
            Diagnostics.note("stash.blocked", [
                "folder": url.path,
                "because": "a stash is already there and could not be folded back in",
            ])
            return
        }
        Diagnostics.note("stash", ["folder": url.path, "held": entries(of: url)])
        try? fm.moveItem(at: url, to: stashed)
    }

    /// How much is in a folder, for the log alone. A count is what makes a
    /// stash line worth reading a week later.
    private static func entries(of url: URL) -> Int {
        ((try? fm.contentsOfDirectory(atPath: url.path)) ?? []).count
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
            // live copy supersedes it. Anything else is a directory against a
            // file, which is not a pair this can reason about either way round,
            // and is left alone rather than guessed at.
            if !isDirectory(stashed), !isDirectory(live) { try? fm.removeItem(at: stashed) }
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
        let held = entries(of: stashed)
        guard !exists(link) else {
            Diagnostics.note("unstash.absorb",
                             ["folder": link.path, "returning": held, "onto": entries(of: link)])
            return absorb(stashed, into: link)
        }
        Diagnostics.note("unstash", ["folder": link.path, "returning": held])
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
    ///
    /// A directory, and the test is not decoration. Claude writes
    /// `<org>.profile-origin.json` as a plain file beside the organization
    /// folders and writes it as the organization is created, so for a moment it
    /// is the newest thing there — and handing that name back as an
    /// organization stashed the profile's own copy of the file and built a
    /// directory where it had been.
    static func newestChild(of dir: URL) -> String? {
        guard let names = try? fm.contentsOfDirectory(atPath: dir.path) else { return nil }
        return names
            .filter { !$0.hasPrefix(".") && !$0.hasSuffix(stashSuffix) }
            .filter { isDirectory(dir.appending(path: $0)) }
            .map { (name: $0, date: modified(dir.appending(path: $0))) }
            .sorted { $0.date > $1.date }
            .first?.name
    }

    /// The directory `parent` keeps `name`'s contents in.
    ///
    /// `<accountUuid>/<orgUuid>` is the shape, except where it is not. A
    /// profile in local mode was measured naming both halves by their first
    /// eight characters — `local-agent-mode-sessions/ed417e0f/00000000` against
    /// `claude-code-sessions/ed417e0f-5edd-…/00000000-0000-…`, the same account
    /// and the same organization in the same profile. Asking such a store for
    /// the full uuid finds nothing at all, and a graft that read that as an
    /// empty store put the borrowing profile's whole history away.
    ///
    /// A shortened name is taken only when one of the two is a prefix of the
    /// other, the shorter is at least eight characters, and it is the only
    /// directory there that fits. Nothing here may put one account's chats into
    /// another's folder, which is the mistake `readableConfigJSON` exists to
    /// prevent; ambiguity gives back nil and the caller falls back to the name
    /// it was given.
    static func counterpartDirectory(in parent: URL, for name: String) -> String? {
        if isDirectory(parent.appending(path: name)) { return name }
        let candidates = ((try? fm.contentsOfDirectory(atPath: parent.path)) ?? [])
            .filter { !$0.hasPrefix(".") && !$0.hasSuffix(stashSuffix) }
            .filter { min($0.count, name.count) >= 8 && ($0.hasPrefix(name) || name.hasPrefix($0)) }
            .filter { isDirectory(parent.appending(path: $0)) }
        return candidates.count == 1 ? candidates.first : nil
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
    private static var recordCache: [String: (stamp: Date, size: Int, sessions: RecordSessions)] = [:]
    private static let recordCacheLock = NSLock()

    /// Drop what a walk did not find. Claude re-files a session under a new
    /// record name as it goes, so a cache keyed by path grows by one entry
    /// every time it does — and the menu bar app runs for weeks and walks the
    /// stores on every dropdown. A launcher would never notice; the app that
    /// is always running is the one that pays.
    private static func forgetRecordsOutsideOf(_ walked: Set<String>) {
        recordCacheLock.lock()
        defer { recordCacheLock.unlock() }
        recordCache = recordCache.filter { walked.contains($0.key) }
    }

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
    ///
    /// Sorted, because a walk of them decides which of two mirrored copies of
    /// one record gets written down as the place it lives, and a report read
    /// after the fact is worth nothing if that flips between passes.
    static func sessionStoreProfiles() -> [URL] {
        guard let names = try? fm.contentsOfDirectory(atPath: applicationSupport.path) else { return [] }
        return names
            .filter { !$0.hasPrefix(".") }
            .sorted()
            .map { applicationSupport.appending(path: $0) }
            .filter { isDirectory($0.appending(path: "claude-code-sessions")) }
    }

    static var shortcutsFile: URL {
        applicationSupport.appending(path: "ClaudeGraft/shortcuts.json")
    }

    /// The folder of every shortcut this app has made, read out of the list
    /// the window keeps.
    ///
    /// A launcher knows its own profile, its source and nothing else, so the
    /// sweep it runs needs telling where the rest of this app's profiles are
    /// — and that list is the only thing on the machine that says which of
    /// the Claude profiles sitting in Application Support are this app's
    /// doing. It is read rather than decoded into `Shortcut`, which lives in
    /// the app and never reaches a launcher.
    static func shortcutProfiles() -> [URL] {
        guard let data = try? Data(contentsOf: shortcutsFile),
              let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]]
        else { return [] }
        return list
            .compactMap { $0["folder"] as? String }
            .filter { validateFolder($0) == nil }
            .map { applicationSupport.appending(path: $0) }
    }

    /// The profiles a sweep may write a record into: whatever the caller
    /// named, Claude's own, and the ones this app made.
    ///
    /// Every profile with a chat store is worth *reading* — the question a
    /// sweep asks is whether any Claude anywhere is already listing the
    /// session, and a record in a profile outside every shortcut still means
    /// one is. Writing is a different question with a different answer. A
    /// Claude profile this app did not make belongs to whatever did make it:
    /// filing into it puts chats in somebody else's sidebar, under names of
    /// this app's choosing, in a store nothing here is keeping straight. The
    /// two lists were one list, so the account that owned a session was
    /// enough to have records written into a profile this app had never
    /// heard of.
    ///
    /// The caller's own list comes first, and it is first for the same reason
    /// it exists: with one account on two profiles, the one about to build a
    /// sidebar is the one that named itself.
    static func recordFilingProfiles(named: [URL]) -> [URL] {
        var seen: Set<String> = []
        return (named + [mainProfile] + shortcutProfiles()).filter {
            seen.insert($0.resolvingSymlinksInPath().path).inserted
        }
    }

    /// What one walk of the chat stores found.
    struct StoreContents {
        /// The session a record describes, against the organisation directory
        /// the record sits in. The directory rather than the file name,
        /// because knowing where a record was is what makes its absence
        /// later mean something.
        var records: [String: String] = [:]
        /// The moment of every deletion marker, against the name it carries.
        /// Named rather than counted because a marker naming a session this
        /// machine has a transcript for is read outright, and only the rest are
        /// left to be guessed at by when they were written.
        var deletions: [String: Double] = [:]
        /// What every deletion marker is named after. A record this app filed
        /// is named for its session, so the marker Claude leaves when one is
        /// deleted names the session too — which is the one identity a
        /// deletion can be read by rather than inferred.
        var deleted: Set<String> = []
        /// Every organisation directory this walk actually managed to read.
        /// A directory missing from here was not looked in, which is a
        /// different thing from being looked in and found empty.
        var stores: Set<String> = []
        /// Every session a record says it has carried on from. Claude gives a
        /// conversation a new command line session as it goes, and the
        /// transcript of the old one stays on disk, whole, with no record
        /// naming it — which is exactly what a session that closed without a
        /// record looks like from here.
        var superseded: Set<String> = []
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
            for account in accounts.sorted()
            where !account.hasPrefix(".") && !account.hasSuffix(stashSuffix)
                && !nonAccountStoreItems.contains(account) {
                let accountDir = store.appending(path: account)
                guard let orgs = try? fm.contentsOfDirectory(atPath: accountDir.path) else { continue }
                for org in orgs.sorted()
                where !org.hasPrefix(".") && !org.hasSuffix(stashSuffix) {
                    let orgDir = accountDir.appending(path: org)
                    guard let names = try? fm.contentsOfDirectory(atPath: orgDir.path) else { continue }
                    let resolved = orgDir.resolvingSymlinksInPath().path
                    contents.stores.insert(resolved)
                    for name in names.sorted() {
                        let file = orgDir.appending(path: name).resolvingSymlinksInPath()
                        guard walked.insert(file.path).inserted else { continue }
                        if name.hasPrefix("local_"), name.hasSuffix(".json") {
                            let sessions = sessions(ofRecordAt: file)
                            // The first store to hold it keeps it. A mirrored
                            // record sits in two, and letting the last one win
                            // left the place a session was filed changing from
                            // pass to pass in the one file written to be read
                            // afterwards.
                            if let session = sessions.cliSessionId,
                               contents.records[session] == nil {
                                contents.records[session] = resolved
                            }
                            contents.superseded.formUnion(sessions.prior)
                        } else if name.hasPrefix("deleted_") {
                            let marker = String(name.dropFirst("deleted_".count))
                            contents.deleted.insert(marker)
                            if let text = try? String(contentsOf: file, encoding: .utf8),
                               let when = Double(text.trimmingCharacters(in: .whitespacesAndNewlines)) {
                                contents.deletions[marker] = when
                            }
                        }
                    }
                }
            }
        }
        forgetRecordsOutsideOf(walked)
        return contents
    }

    /// Which command line sessions one record speaks for.
    struct RecordSessions: Equatable {
        /// The session it holds now.
        var cliSessionId: String?
        /// The sessions it grew out of. A conversation carried on past a
        /// compaction, or resumed, gets a new command line session and the
        /// record keeps the old ids here.
        var prior: [String] = []
    }

    static func sessions(ofRecordAt url: URL) -> RecordSessions {
        let attributes = try? fm.attributesOfItem(atPath: url.path)
        let stamp = attributes?[.modificationDate] as? Date ?? .distantPast
        let size = (attributes?[.size] as? Int) ?? 0

        recordCacheLock.lock()
        if let cached = recordCache[url.path], cached.stamp == stamp, cached.size == size {
            recordCacheLock.unlock()
            return cached.sessions
        }
        recordCacheLock.unlock()

        let record = (try? Data(contentsOf: url))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        let sessions = RecordSessions(cliSessionId: record?["cliSessionId"] as? String,
                                      prior: (record?["priorCliSessionIds"] as? [String]) ?? [])

        recordCacheLock.lock()
        recordCache[url.path] = (stamp, size, sessions)
        recordCacheLock.unlock()
        return sessions
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

        // A marker naming a record Claude wrote names it by an id no transcript
        // carries, so the only trace of which session went is timing: the one
        // that had just gone quiet is the one that was on screen when the
        // delete was pressed. A session still being written grows past the
        // window and files later, so this can only hold back one whose final
        // line fell inside it.
        //
        // Only those markers reach here. `deletions` used to carry every marker
        // on the machine, including the ones this app can read outright by
        // name, and they accumulate and are never pruned — so one deletion went
        // on suppressing whatever else had happened to go quiet in the minute
        // before it, for as long as the marker existed. The caller keeps back
        // any marker naming a session it has a transcript for; those are read
        // by name below, which needs no guessing at all.
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

    /// Whether a record may be written into an organisation folder.
    ///
    /// A folder this pass could not read is not a folder with nothing in it.
    /// It is stashed behind a graft, or waiting on a permission, or gone for
    /// the moment while Claude renames something over it — and from outside,
    /// every session it holds looks like a session nobody ever filed. Writing
    /// those records again gives the profile a second copy of a history it
    /// already has, and a record written fresh says `isArchived: false`, so a
    /// chat put out of sight by hand comes back into the sidebar. Reported as
    /// archived conversations unarchiving themselves, and reproduced by
    /// stashing one organization folder and running a single pass: four
    /// sessions refiled, the folder rebuilt over the top of the stash, and
    /// the archived one back with its flag cleared.
    ///
    /// A folder that is simply not there yet is a different thing and is
    /// filed into, since that is how a profile gets its first record — unless
    /// this app stashed it itself, which is the one absence it can recognise.
    ///
    /// The absence has to be asked about all the way up. A cross-account graft
    /// stashes the organization folder and leaves a sibling beside it; two
    /// profiles on one account stash the whole store, and then there is no
    /// sibling to find because there is no folder left to stand next to. Asking
    /// only about the sibling caught the first shape and waved the second one
    /// through, so a same-account graft was followed by the sweep rebuilding
    /// `<account>/<org>` inside the emptied store and filing the whole history
    /// into it — every chat a second time, none of them archived, and the real
    /// store orphaned in the stash. `isStashedAway` is both shapes.
    static func mayFileRecords(inOrganisation dir: URL, storesRead: Set<String>) -> Bool {
        guard exists(dir) else { return !isStashedAway(dir) }
        return storesRead.contains(dir.resolvingSymlinksInPath().path)
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
        let candidates = recordFilingProfiles(named: profiles)
        Diagnostics.note("sweep.begin", [
            "storesRead": contents.stores.count,
            "recordsSeen": contents.records.count,
            "deletionMarkers": contents.deleted.count,
            "filingInto": candidates.map(\.lastPathComponent),
            "remembered": state.records.count,
            "vanishedLastPass": state.vanished.count,
        ])

        let recorded = Set(contents.records.keys)
        var withdrawn = Set(state.withdrawn)
        let vanished = Set(state.vanished)
        var missing: Set<String> = []
        // Sessions whose record is remembered in a store this pass never got
        // into. They are not gone and they are not missing; they are out of
        // sight, and the record still sitting there is the one the profile
        // reads — archived, renamed, whatever has been done to it since.
        var outOfSight: Set<String> = []
        // Named rather than counted, because which store went quiet is the
        // whole diagnosis and a number says nothing.
        var stashedStores: Set<String> = []
        var unreadStores: Set<String> = []
        // Sessions remembered at a folder that has stopped existing, which is
        // a pair to drop rather than a fact to act on.
        var forgotten: Set<String> = []
        var goneStores: Set<String> = []

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
        //
        // The same reading has to hold on the way in. A store that was not
        // read tells a pass nothing about what is in it, so the sessions it
        // holds are left exactly as they are — neither withdrawn, which was
        // already the rule, nor filed all over again, which was not.
        var storeAnswers: [String: (stashed: Bool, present: Bool)] = [:]
        func answers(for store: String) -> (stashed: Bool, present: Bool) {
            if let known = storeAnswers[store] { return known }
            let url = URL(fileURLWithPath: store)
            let answer = (stashed: isStashedAway(url), present: exists(url))
            storeAnswers[store] = answer
            return answer
        }

        for (session, store) in state.records where store.hasPrefix("/") {
            // A store with a stash beside it is one this app emptied itself. A
            // mirror's first pass moves the profile's own chats to the hidden
            // sibling and leaves a real, readable, empty folder behind, so the
            // store is read, is found holding nothing, and reads exactly like a
            // sidebar someone cleared by hand. `mayFileRecords` already asks
            // this on the way in and it has to be asked on the way out too:
            // withdrawing is forever, and this is how 150 sessions across two
            // profiles were withdrawn in a single pass.
            guard !answers(for: store).stashed else {
                outOfSight.insert(session)
                stashedStores.insert(store)
                continue
            }
            // A folder that is not there, with nothing stashed anywhere above
            // it to say this app put it away, is a folder nobody writes to any
            // more: the profile was deleted, or signing in again moved
            // <account>/<org> out from under the pair. Remembering it leaves
            // the session out of sight on every pass from here on, so it is
            // never filed anywhere again — the transcript whole on disk and the
            // chat in nobody's sidebar, with no finding naming it because the
            // folder it names cannot be reported on. `forgetStalePairs` makes
            // the same call for a mirror, and for the same reason: forgetting
            // is what lets the session look new and land where the account
            // lives now.
            guard answers(for: store).present else {
                forgotten.insert(session)
                goneStores.insert(store)
                Diagnostics.note("sweep.forget", [
                    "session": session, "store": store,
                    "because": "the folder is gone and nothing here stashed it",
                ])
                continue
            }
            guard contents.stores.contains(store) else {
                outOfSight.insert(session)
                unreadStores.insert(store)
                continue
            }
            guard !recorded.contains(session) else { continue }
            if vanished.contains(session) {
                withdrawn.insert(session)
                state.authored.removeValue(forKey: session)
                Diagnostics.note("sweep.withdraw", [
                    "session": session, "store": store, "because": "missing from a store read twice",
                ])
            } else {
                missing.insert(session)
            }
        }

        // Where a record was is remembered until it is seen again or given up
        // on, which is what leaves the next pass something to agree with. A
        // state file written before records were remembered by directory named
        // the file instead; those entries go on sight, since every record
        // still on disk is learned again right here.
        var remembered = state.records
            .filter { $0.value.hasPrefix("/") && !forgotten.contains($0.key) }
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
            Diagnostics.note("sweep.end", ["filed": 0, "because": "no transcripts to read"])
            return filed
        }

        let transcripts: [(dir: URL, names: [String])] = projects.map { project in
            let dir = claudeProjects.appending(path: project)
            let names = ((try? fm.contentsOfDirectory(atPath: dir.path)) ?? [])
                .filter { $0.hasSuffix(".jsonl") }
            return (dir, names)
        }

        // Markers this app cannot read by name, which are the only ones worth
        // guessing about. A marker naming a session with a transcript is
        // matched outright further down.
        let named = Set(transcripts.flatMap { $0.names }
            .map { String($0.dropLast(".jsonl".count)) })
        let guessableDeletions = contents.deletions
            .filter { !named.contains($0.key) }
            .map(\.value)

        // Every session on this machine is stamped with one of a handful of
        // accounts, and answering this reads a config.json per candidate.
        var owners: [String: URL?] = [:]
        func owner(of facts: SessionFacts) -> URL? {
            if let known = owners[facts.ownerAccount] { return known }
            let answer = ownerProfile(for: facts, among: candidates)
            owners[facts.ownerAccount] = answer
            return answer
        }

        for (dir, names) in transcripts {
            for name in names {
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
                    Diagnostics.note("sweep.withdraw", [
                        "session": facts.cliSessionId, "because": "a deletion marker names it",
                    ])
                }
                guard !withdrawn.contains(facts.cliSessionId) else { continue }
                // A record that went missing this very pass is a question the
                // next pass answers. Filing one now would answer it wrong:
                // the record comes back under this app's name, the pass after
                // finds it there, and a chat taken out of a sidebar is quietly
                // put back by the thing that was meant to notice it going.
                guard !missing.contains(facts.cliSessionId) else { continue }
                // And a record in a store this pass could not open is a
                // record, not an absence. The session is listed already; the
                // pass just cannot see it from here.
                guard !outOfSight.contains(facts.cliSessionId) else { continue }
                // A conversation that carried on under a new command line
                // session is one the sidebar lists under the id it carried on
                // as. Its earlier transcript keeps sitting there with nobody's
                // record naming it, so filing it puts the same conversation in
                // the sidebar a second time — with the same title, the same
                // folder, and none of what was done to the first, archiving
                // included.
                guard !contents.superseded.contains(facts.cliSessionId) else { continue }
                let owner = owner(of: facts)

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
                                                 deletions: guessableDeletions,
                                                 lastWrite: lastWrite,
                                                 ownerProfile: owner,
                                                 ownerIsRunning: running(owner),
                                                 now: now,
                                                 quietWindow: quietWindow)
                else { continue }

                // Nothing above knows what the store the record is headed for
                // looked like, only what this session's own history says. A
                // profile with its store stashed away has no sessions on
                // record at all as far as a walk can tell, and a first pass
                // has nothing remembered to contradict it.
                let destination = recordFile(for: facts, in: owner).deletingLastPathComponent()
                guard mayFileRecords(inOrganisation: destination,
                                     storesRead: contents.stores) else {
                    Diagnostics.note("sweep.refused", [
                        "session": facts.cliSessionId,
                        "destination": destination.path,
                        "because": exists(destination)
                            ? "the folder is there but this pass never read it"
                            : "this app stashed the folder itself",
                    ])
                    continue
                }

                if writeSessionRecord(for: facts, into: owner) {
                    Diagnostics.note("sweep.file", [
                        "session": facts.cliSessionId,
                        "owner": facts.ownerAccount,
                        "into": owner.lastPathComponent,
                        "title": facts.title,
                        "cwd": facts.cwd,
                    ])
                    filed.append(facts)
                    state.records[facts.cliSessionId] = destination.path
                    state.authored[facts.cliSessionId] = facts
                }
            }
        }

        state.withdrawn = withdrawn.sorted()
        // The loop above withdrew every session a marker names, which happens
        // after the record memory was squared up — so without this those keep
        // an entry naming a folder their record has already gone from, and the
        // two passes it then takes to rediscover them as missing inflate the
        // very counts a diagnosis is read off.
        for session in withdrawn { state.records.removeValue(forKey: session) }
        saveSessionRecordState(state)
        Diagnostics.note("sweep.end", [
            "filed": filed.count,
            "withdrawn": withdrawn.count,
            "missingThisPass": missing.count,
            "outOfSight": outOfSight.count,
            "forgotten": forgotten.count,
            "storesStashed": stashedStores.sorted(),
            "storesUnread": unreadStores.sorted(),
            "storesGone": goneStores.sorted(),
        ])
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

    // MARK: - Mirrored chat folders

    /// A stable digest of a record's bytes.
    ///
    /// Stable across processes, which `hashValue` is not: the launcher and the
    /// app both have to agree on whether a file has moved since the last pass,
    /// and they are different processes every time.
    static func digest(_ data: Data) -> String {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        data.withUnsafeBytes { bytes in
            for byte in bytes {
                hash ^= UInt64(byte)
                hash = hash &* 0x100_0000_01b3
            }
        }
        return String(hash, radix: 16)
    }

    /// What both sides of a mirrored pair agreed on last time, so a later pass
    /// can tell a record somebody edited from one somebody deleted. Without a
    /// baseline the two look identical: a name on one side and not the other.
    struct MirrorState: Codable, Equatable {
        /// Pair of folders, then record name, then the digest both held.
        var pairs: [String: [String: String]] = [:]

        init(pairs: [String: [String: String]] = [:]) { self.pairs = pairs }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            pairs = try container.decodeIfPresent([String: [String: String]].self,
                                                  forKey: .pairs) ?? [:]
        }
    }

    static var mirrorStateFile: URL {
        applicationSupport.appending(path: "ClaudeGraft/mirrored-chats.json")
    }

    static func loadMirrorState() -> MirrorState {
        guard let data = try? Data(contentsOf: mirrorStateFile),
              let state = try? JSONDecoder().decode(MirrorState.self, from: data)
        else { return MirrorState() }
        return state
    }

    static func saveMirrorState(_ state: MirrorState) {
        guard let data = try? JSONEncoder().encode(state) else { return }
        try? fm.createDirectory(at: mirrorStateFile.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        try? data.write(to: mirrorStateFile, options: .atomic)
    }

    /// A byte no path can contain, so the two halves of a key always come
    /// back apart the way they went together.
    static let pairSeparator = "\u{0}"

    static func pairKey(_ one: URL, _ other: URL) -> String {
        one.resolvingSymlinksInPath().path + pairSeparator + other.resolvingSymlinksInPath().path
    }

    /// The two halves of a key, in the roles they were written in.
    ///
    /// A pair is always written with the borrowing folder first, because
    /// `mirrorChatFolders` is only ever called with the profile's own folder
    /// ahead of the one it is borrowing from. That order is the only record of
    /// which side is which: after a merge both folders hold the same bytes,
    /// and nothing on disk tells a lender from a borrower.
    static func pairHalves(_ key: String) -> (borrower: String, source: String)? {
        let halves = key.components(separatedBy: pairSeparator)
        guard halves.count == 2, halves.allSatisfy({ $0.hasPrefix("/") }) else { return nil }
        return (borrower: halves[0], source: halves[1])
    }

    /// The two folders a key names, or nil for anything that is not one.
    static func pairFolders(_ key: String) -> (one: URL, other: URL)? {
        guard let halves = pairHalves(key) else { return nil }
        return (URL(fileURLWithPath: halves.borrower), URL(fileURLWithPath: halves.source))
    }

    /// Every pair with a half at or inside this folder, whichever half it is.
    /// For the cases where both roles are equally finished: a profile deleted,
    /// a shortcut deleted. Undoing a graft is not one of them.
    static func mirrorPairs(under folder: URL) -> [String] {
        mirrorPairs(matching: folder, borrowingHalfOnly: false)
    }

    /// Every pair this folder borrows through: the half its own Claude reads
    /// and writes, never the half it is lending to somebody else.
    ///
    /// Which is also the question "has mirroring been set up here before" —
    /// asked of the state file rather than of the stash, because a profile
    /// that had nothing to put away leaves no stash behind and would
    /// otherwise look new for ever, and looking new for ever means the pass
    /// after the first one stashes away everything the first one mirrored in.
    ///
    /// A source sits under a pair as surely as a borrower does, and every
    /// question a graft asks — is this a first pass, what should stop being
    /// mirrored, which copies come back out — is about the borrowing half
    /// alone. Asked of `mirrorPairs(under:)` instead, opening the profile
    /// others borrow from ran that profile's own `ungraft` over their pairs:
    /// it took its folder to be the borrowed one, and since a lender has no
    /// stash to say what it owns, every record the merge had put there was
    /// removed. It then forgot the pairs, so the borrower's next launch read
    /// as a first pass and stashed the merge away. Both sidebars emptied, on
    /// every launch, for as long as the shortcut existed.
    static func mirrorPairs(borrowedBy folder: URL) -> [String] {
        mirrorPairs(matching: folder, borrowingHalfOnly: true)
    }

    private static func mirrorPairs(matching folder: URL, borrowingHalfOnly: Bool) -> [String] {
        let path = folder.resolvingSymlinksInPath().path
        return loadMirrorState().pairs.keys.filter { key in
            guard let roles = pairHalves(key) else { return false }
            let sides = borrowingHalfOnly ? [roles.borrower] : [roles.borrower, roles.source]
            return sides.contains { $0 == path || $0.hasPrefix(path + "/") }
        }.sorted()
    }

    /// What one pass makes of one name held by two mirrored folders.
    enum MirrorAction: Equatable {
        case nothing
        /// One side holds the newer copy; the other gets it.
        case copyToOther
        case copyToOne
        /// It was there last time and is gone from the other side now, which
        /// is a deletion to carry across rather than a copy to undo.
        case removeFromOne
        case removeFromOther
        /// Both sides moved since the last pass. Nothing here can say which
        /// is wanted, so the caller settles it on what the records say about
        /// themselves.
        case conflict
    }

    /// Which way one record moves.
    ///
    /// The baseline is what makes a missing file mean something. A name on one
    /// side and not the other is either a record just written or a record just
    /// deleted, and the two are the same on disk; the difference is only
    /// whether the last pass saw it on both. Getting this backwards deletes a
    /// new chat or resurrects a deleted one, so it is a function with no
    /// filesystem in it and the suite drives every branch.
    static func mirrorDecision(one: String?, other: String?, baseline: String?) -> MirrorAction {
        if one == other { return .nothing }
        if let one, other == nil {
            return baseline == nil ? .copyToOther : (baseline == one ? .removeFromOne : .conflict)
        }
        if let other, one == nil {
            return baseline == nil ? .copyToOne : (baseline == other ? .removeFromOther : .conflict)
        }
        if one == baseline { return .copyToOne }
        if other == baseline { return .copyToOther }
        return .conflict
    }

    /// Bring every pair this app has ever mirrored back into line.
    ///
    /// A shortcut being opened brings its own pair up to date on the way past,
    /// which covers the profile doing the borrowing and nobody else. The
    /// source is the case that matters and the case that misses: archive a
    /// chat in the borrowing profile, open the source, and the source is not
    /// the one running `apply` — so without this the change sits there and the
    /// sidebar it was meant for never sees it.
    ///
    /// The pairs are the keys of the state file. Every pass already writes
    /// down which two folders belong together, so there is nothing else to
    /// keep, and a launcher that knows only its own profile can still put
    /// everything in step.
    @discardableResult
    static func mirrorKnownPairs() -> Int {
        var moved = 0
        for key in loadMirrorState().pairs.keys.sorted() {
            guard let pair = pairFolders(key) else { continue }
            moved += mirrorChatFolders(pair.one, pair.other)
        }
        return moved
    }

    /// The names a mirror pass is responsible for. A sidebar is built out of
    /// records and the markers that say which have been deleted; everything
    /// else in an organization folder belongs to the profile that wrote it and
    /// is left where it is.
    static func isMirrored(_ name: String) -> Bool {
        (name.hasPrefix("local_") && name.hasSuffix(".json")) || name.hasPrefix("deleted_")
    }

    /// When a record says it last moved, for settling a name both sides have
    /// rewritten since the last pass. The file's own timestamp is the fallback,
    /// since a marker has no inside to read.
    private static func recordMoment(at url: URL) -> Double {
        if let value = (try? Data(contentsOf: url))
            .flatMap({ try? JSONSerialization.jsonObject(with: $0) as? [String: Any] })
            .flatMap({ $0["lastActivityAt"] as? Double }) { return value }
        return modified(url).timeIntervalSince1970 * 1000
    }

    /// Bring two organization folders into line, and remember what they agreed
    /// on so the next pass can read an absence.
    ///
    /// A folder that could not be read is not a folder with nothing in it —
    /// the same rule the session sweep learned — and here the cost of getting
    /// it wrong is worse than a duplicate: every record on the other side
    /// would look deleted. So a walk that fails on either side does nothing at
    /// all rather than half a sync.
    @discardableResult
    static func mirrorChatFolders(_ one: URL, _ other: URL) -> Int {
        guard !samePath(one, other) else { return 0 }
        for folder in [one, other] where !exists(folder) {
            // Only the folder itself, never the path to it. A profile someone
            // deleted has no account directory left, and a pass whose whole
            // job is keeping two sidebars in step must not build a deleted
            // profile back up around a folder it wanted to write into.
            // Setting a graft up is `mirrorChatStores`, and it makes the path
            // there where it knows the profile is real.
            guard isDirectory(folder.deletingLastPathComponent()) else { return 0 }
            try? fm.createDirectory(at: folder, withIntermediateDirectories: false)
        }
        guard let oneNames = try? fm.contentsOfDirectory(atPath: one.path),
              let otherNames = try? fm.contentsOfDirectory(atPath: other.path)
        else {
            Diagnostics.note("mirror.unread", ["one": one.path, "other": other.path])
            return 0
        }

        var state = loadMirrorState()
        let key = pairKey(one, other)
        var baseline = state.pairs[key] ?? [:]
        var moved = 0

        // A side holding none of the names the baseline describes, while the
        // other still holds them, has not had its sidebar cleared one chat at a
        // time. It has been stashed, replaced, or emptied wholesale, and
        // carrying that across as deletions takes the other profile's history
        // with it. Seen for real: a sign-in moved where a mirror thought its own
        // folder was, `openForMirror` stashed the live set as though it were the
        // profile's own, and the next pass read 150 absences as 150 deletions
        // and removed every one of them from the profile they had been borrowed
        // from. Forgetting the baseline instead makes the survivors look new, so
        // the emptied side is filled again rather than the full one emptied.
        if !baseline.isEmpty {
            let described = Set(baseline.keys)
            let hereHasNone = Set(oneNames.filter(isMirrored)).isDisjoint(with: described)
            let thereHasNone = Set(otherNames.filter(isMirrored)).isDisjoint(with: described)
            if hereHasNone != thereHasNone { baseline = [:] }
        }

        func read(_ folder: URL, _ name: String) -> (data: Data, digest: String)? {
            guard let data = try? Data(contentsOf: folder.appending(path: name)) else { return nil }
            return (data, digest(data))
        }
        func put(_ data: Data, _ folder: URL, _ name: String) -> Bool {
            (try? data.write(to: folder.appending(path: name), options: .atomic)) != nil
        }
        /// A removal that did not happen must not be written down as one. The
        /// baseline was dropped whether or not the file went, so a removal the
        /// filesystem refused left a name on one side, absent on the other,
        /// with no baseline — which is a new chat, and the next pass copied the
        /// deleted conversation straight back to the side it was deleted from.
        func drop(_ folder: URL, _ name: String) -> Bool {
            (try? fm.removeItem(at: folder.appending(path: name))) != nil
        }

        let names = Set(oneNames.filter(isMirrored)).union(otherNames.filter(isMirrored))
        for name in names.sorted() {
            let here = read(one, name), there = read(other, name)
            var action = mirrorDecision(one: here?.digest, other: there?.digest,
                                        baseline: baseline[name])
            // Both sides moved. What the records say about themselves is the
            // only thing left to go on, and a record that still exists beats
            // one that was deleted — a chat put back is a nuisance, a chat
            // taken away for good is not.
            if action == .conflict {
                switch (here, there) {
                case (nil, .some): action = .copyToOne
                case (.some, nil): action = .copyToOther
                case (.some, .some):
                    action = recordMoment(at: one.appending(path: name))
                        >= recordMoment(at: other.appending(path: name)) ? .copyToOther : .copyToOne
                case (nil, nil): action = .nothing
                }
            }

            switch action {
            case .nothing:
                // Where a name held identically by both sides first earns a
                // baseline. `mirrorDecision` answers this only when the two
                // digests are equal, so either side names it.
                if let digest = here?.digest { baseline[name] = digest }
            case .copyToOther, .copyToOne:
                let source = action == .copyToOther ? here : there
                let destination = action == .copyToOther ? other : one
                guard let source, put(source.data, destination, name) else { break }
                baseline[name] = source.digest
                moved += 1
            case .removeFromOne, .removeFromOther:
                guard drop(action == .removeFromOne ? one : other, name) else { break }
                baseline.removeValue(forKey: name)
                moved += 1
            case .conflict:
                break
            }
        }

        // A name neither side holds any more is never visited by the loop
        // above, so its entry sat in the baseline for good — growing the state
        // file, and standing ready to read the name coming back on one side
        // alone as a deletion to carry across.
        state.pairs[key] = baseline.filter { names.contains($0.key) }
        saveMirrorState(state)
        return moved
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
        mirrorChatStores(from: source, into: profile)
        copyAppearance(from: source, into: profile)
    }

    /// Make a folder ready to be filled from the source: real, so the profile
    /// can write into it at all, and emptied of what it held.
    ///
    /// Real, because a link is the reason a grafted profile cannot archive
    /// anything. Emptied, because the stash is the only record of which chats
    /// were the profile's own before any of this, and going back to its own
    /// chats has to be exact rather than a guess at which record belonged to
    /// whom. So the profile's own chats go to the same hidden sibling a linked
    /// graft would have moved them to — and `seedOwnChats` then copies them
    /// straight back in, because sharing a history merges the two rather than
    /// standing one in for the other. The stash keeps the copy that makes the
    /// merge reversible; the folder gets the copy the person actually reads.
    ///
    /// `firstPass` is what stops the second launch stashing away everything
    /// the first one mirrored in. `apply` runs on every launch, and without
    /// it this would put the borrowed chats away and fetch them again on an
    /// endless loop. It is also what stops the seeding running twice, which
    /// would fetch back every shared chat the person had since deleted.
    static func openForMirror(_ folder: URL, firstPass: Bool) {
        if isSymlink(folder) {
            try? fm.removeItem(at: folder)
        } else if firstPass {
            stash(folder)
        }
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    /// Take the borrowed copies back out of a folder about to be stashed a
    /// second time.
    ///
    /// A first pass that finds a stash already in place is a second one: the
    /// pair that would have said so was dropped — a source deleted, a folder
    /// moved, a lender running its own `ungraft` over somebody else's pairs —
    /// while the merge it set up is still sitting in the folder. `stash` folds
    /// a stash it finds back into the live folder before moving the lot aside,
    /// which is right for a link the profile has written a real file over and
    /// wrong here: it would put the borrowed records into the stash beside the
    /// profile's own and leave nothing saying which of the merged records were
    /// whose. That is how one machine's stash grew from 155 to 159, a launch at
    /// a time, and the count is all anybody would ever have noticed.
    ///
    /// A record the source is holding byte for byte that the stash does not
    /// name is a borrowed one — the same test `unmirrorChatStores` removes a
    /// copy on, for the same reason: bytes that match are bytes nobody loses,
    /// and these come straight back in on the pass that follows. A genuine
    /// first pass has no stash to find and nothing to do here.
    ///
    /// Only ever inside the profile. A shortcut left on the shape an older
    /// version made — its own chats in the stash and a link where they used to
    /// be — has a folder whose contents are the source's own files, and every
    /// one of them matches the source byte for byte because it *is* the
    /// source: this would delete the lender's whole history and call it
    /// tidying up. The link is not always the folder itself, either. A
    /// released version sharing one account linked the entire store, so the
    /// organization folder under it is a real directory reached through a
    /// symlinked parent, and asking `isSymlink` about it answers no. What both
    /// shapes have in common is the rule the rest of this app is built on: the
    /// folder resolves outside the profile. `openForMirror` unpicks the link
    /// a moment later, and there is nothing to weigh until it has.
    @discardableResult
    static func dropBorrowedCopies(from folder: URL, sharing source: URL, store: URL) -> Int {
        let profile = store.deletingLastPathComponent().resolvingSymlinksInPath().path + "/"
        guard folder.resolvingSymlinksInPath().path.hasPrefix(profile) else { return 0 }
        guard let own = stashedCounterpart(of: folder, store: store) else { return 0 }
        var dropped = 0
        for name in (try? fm.contentsOfDirectory(atPath: folder.path)) ?? [] where isMirrored(name) {
            let here = folder.appending(path: name)
            guard !exists(own.appending(path: name)),
                  let mine = try? Data(contentsOf: here),
                  let theirs = try? Data(contentsOf: source.appending(path: name)),
                  mine == theirs,
                  (try? fm.removeItem(at: here)) != nil
            else { continue }
            dropped += 1
        }
        guard dropped > 0 else { return 0 }
        Diagnostics.note("mirror.reborrowed", [
            "folder": folder.path, "dropped": dropped,
            "because": "a stash already names what this profile brought, so these were borrowed",
        ])
        return dropped
    }

    /// Put the profile's own chats into the shared set, so a graft merges the
    /// two histories instead of replacing one with the other.
    ///
    /// Copied rather than moved. The stash has to go on holding them: it is
    /// what `unstash` hands back when the shortcut returns to its own chats,
    /// and with the copies gone from the shared set by then it is the only
    /// thing that still knows which of the merged records this profile
    /// brought to it.
    ///
    /// Where its own copy went depends on how the two profiles are signed in,
    /// so both shapes are looked for: a cross-account graft stashes the
    /// organization folder itself, while two profiles on one account stash the
    /// whole store above it, leaving the same records one level further down.
    @discardableResult
    static func seedOwnChats(into folder: URL, store: URL) -> Int {
        guard let own = stashedCounterpart(of: folder, store: store) else { return 0 }
        var seeded = 0
        for name in (try? fm.contentsOfDirectory(atPath: own.path)) ?? [] where isMirrored(name) {
            let to = folder.appending(path: name)
            guard !exists(to),
                  (try? fm.copyItem(at: own.appending(path: name), to: to)) != nil
            else { continue }
            seeded += 1
        }
        Diagnostics.note("mirror.seed", ["folder": folder.path, "own": seeded])
        return seeded
    }

    /// The stashed copy of an organization folder, whichever way it was put
    /// away. Nil rather than a guess when neither shape is there, since a
    /// profile that had no chats of its own leaves no stash behind at all.
    static func stashedCounterpart(of folder: URL, store: URL) -> URL? {
        let sibling = stashURL(for: folder)
        if isDirectory(sibling) { return sibling }
        let account = folder.deletingLastPathComponent().lastPathComponent
        let inside = stashURL(for: store)
            .appending(path: account)
            .appending(path: folder.lastPathComponent)
        return isDirectory(inside) ? inside : nil
    }

    /// Give a profile its own copy of the source's chats rather than a link to
    /// them, and keep the two in step.
    ///
    /// A link is the reason a grafted profile cannot archive, rename or delete
    /// anything: Claude Desktop will not write a record into a folder that
    /// resolves outside the profile. A real folder it will. So the folder is
    /// made real and a pass carries records both ways, which costs a second
    /// copy of some small JSON and buys a profile that behaves like a profile.
    ///
    /// The pairing is this profile's `<account>/<org>` against the source's
    /// active one, because that is where each side's Claude actually reads.
    /// Linking those two was how sharing used to work, and the link is the
    /// reason a grafted profile could not archive; only the pairing survived
    /// it.
    static func mirrorChatStores(from source: URL, into profile: URL) {
        guard let sourceAccount = account(of: source) else { return }
        // Unreadable is not the same as signed out. A config caught mid-rename
        // read as "no account, so the same account as the source" once linked
        // an entire store away; here the same mistake would copy one account's
        // chats into another's folder.
        guard let ownConfig = readableConfigJSON(of: profile) else { return }
        let ownAccount = ownConfig["lastKnownAccountUuid"] as? String

        for store in chatStores {
            let src = source.appending(path: store)
            let dst = profile.appending(path: store)
            guard isDirectory(src) else { continue }

            if let ownAccount, ownAccount != sourceAccount {
                mirrorAcrossAccounts(from: src, account: sourceAccount,
                                     into: dst, account: ownAccount)
            } else {
                mirrorWithinOneAccount(from: src, into: dst,
                                       account: ownAccount ?? sourceAccount)
            }

            // Keyed by organization rather than account, and not a sidebar, so
            // it stays a link.
            //
            // After both branches rather than inside one of them. It sat behind
            // the same-account branch's `continue`, so two profiles on one
            // account never shared it while two on different accounts did — and
            // it cannot simply move to the top either, since a same-account
            // graft puts the whole store away and builds it back, taking any
            // link made first to the stash with everything else.
            let skills = src.appending(path: "skills-plugin")
            if isDirectory(skills) {
                relink(target: skills, at: dst.appending(path: "skills-plugin"))
            }
        }
    }

    /// One account on both sides: every organization it has is shared, so every
    /// organization is mirrored. A link would have taken the whole store, so the
    /// whole store is what goes to the stash.
    private static func mirrorWithinOneAccount(from src: URL, into dst: URL, account: String) {
        // Read the source before anything of this profile's is moved.
        //
        // Opening the destination first was a store emptied on the strength of
        // a source that turned out to have nothing under this account —
        // `<account>` spelled shorter, holding only `skills-plugin`, or signed
        // in as somebody else. The profile's whole history went to the stash,
        // an empty folder stood where it had been, and no pair was written
        // down, so every launch after did it again.
        guard let sourceAccountDir = counterpartDirectory(in: src, for: account) else {
            Diagnostics.note("mirror.nothingToShare", [
                "source": src.path, "account": account,
                "because": "the source holds no folder for this account",
            ])
            return
        }
        let theirAccount = src.appending(path: sourceAccountDir)
        let orgs = ((try? fm.contentsOfDirectory(atPath: theirAccount.path)) ?? [])
            .filter { !$0.hasPrefix(".") && !$0.hasSuffix(stashSuffix) }
            .filter { isDirectory(theirAccount.appending(path: $0)) }
            .sorted()
        guard !orgs.isEmpty else {
            Diagnostics.note("mirror.nothingToShare", [
                "source": theirAccount.path, "account": account,
                "because": "the source holds no organization folder under this account",
            ])
            return
        }

        // The destination names the account the way the destination names it,
        // which is not always the way the source does.
        let ownAccountDir = counterpartDirectory(in: dst, for: account) ?? account
        let mine = orgs.map { org in
            dst.appending(path: ownAccountDir)
                .appending(path: counterpartDirectory(
                    in: dst.appending(path: ownAccountDir), for: org) ?? org)
        }
        let theirs = orgs.map { theirAccount.appending(path: $0) }
        forgetStalePairs(under: dst,
                         keeping: zip(mine, theirs).map { (mine: $0, theirs: $1) })

        // Read before the store is opened, and held for every organization
        // under it: the first pass stashes the store as a whole, so asking
        // again after the first organization has been paired would say no to
        // seeding all the rest.
        let firstPass = mirrorPairs(borrowedBy: dst).isEmpty
        if firstPass {
            for (folder, source) in zip(mine, theirs) {
                dropBorrowedCopies(from: folder, sharing: source, store: dst)
            }
        }
        openForMirror(dst, firstPass: firstPass)
        for (folder, source) in zip(mine, theirs) {
            // The path is made here, where the profile is known to be a real one
            // being configured, rather than in the keeping-in-step pass, which
            // must not build a deleted profile back up around a folder it wanted
            // to write into. Without this the pass found no parent and did
            // nothing, which is a same-account graft that mirrored not one file.
            openForMirror(folder, firstPass: false)
            if firstPass { seedOwnChats(into: folder, store: dst) }
            mirrorChatFolders(folder, source)
        }
    }

    /// Two accounts: this profile's own `<account>/<org>` is paired with the
    /// source's active one, because that is where each side's Claude reads.
    private static func mirrorAcrossAccounts(from src: URL, account sourceAccount: String,
                                             into dst: URL, account ownAccount: String) {
        guard let sourceAccountDir = counterpartDirectory(in: src, for: sourceAccount),
              let sourceOrg = newestChild(of: src.appending(path: sourceAccountDir))
        else {
            Diagnostics.note("mirror.nothingToShare", [
                "source": src.path, "account": sourceAccount,
                "because": "the source holds no organization folder under this account",
            ])
            return
        }
        let ownAccountDir = counterpartDirectory(in: dst, for: ownAccount) ?? ownAccount
        // The source is asked second, and asked about its own spelling of this
        // profile's account: a profile with no organization folder of its own
        // yet may still have one sitting in the store it is borrowing from.
        let orgHeldBySource = counterpartDirectory(in: src, for: ownAccount)
            .flatMap { newestChild(of: src.appending(path: $0)) }
        guard let ownOrg = newestChild(of: dst.appending(path: ownAccountDir))
                ?? orgHeldBySource else { return }

        // A store-wide link, from when these two were on one account.
        if isSymlink(dst) { openForMirror(dst, firstPass: false) }
        let mine = dst.appending(path: ownAccountDir).appending(path: ownOrg)
        let theirs = src.appending(path: sourceAccountDir).appending(path: sourceOrg)
        try? fm.createDirectory(at: mine.deletingLastPathComponent(),
                                withIntermediateDirectories: true)
        forgetStalePairs(under: dst, keeping: [(mine: mine, theirs: theirs)])
        let firstPass = mirrorPairs(borrowedBy: mine).isEmpty
        if firstPass { dropBorrowedCopies(from: mine, sharing: theirs, store: dst) }
        openForMirror(mine, firstPass: firstPass)
        if firstPass { seedOwnChats(into: mine, store: dst) }
        mirrorChatFolders(mine, theirs)
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

    /// Stop mirroring anything belonging to a profile, without moving a file.
    ///
    /// The pairs outlive the graft that made them: they are the keys of a
    /// state file every launcher reads, so a shortcut sent back to its own
    /// chats would go on having its folder squared up against a source it no
    /// longer borrows from — by whichever profile happened to open next, and
    /// for as long as the file said so. A profile that has been deleted is
    /// the other way in: there is nothing left to settle, only a pair to drop.
    static func forgetMirrors(of profile: URL) {
        let stale = Set(mirrorPairs(under: profile))
        guard !stale.isEmpty else { return }
        Diagnostics.note("mirror.forget",
                         ["profile": profile.lastPathComponent, "pairs": stale.count])
        dropPairs(stale)
    }

    private static func dropPairs(_ stale: Set<String>) {
        var state = loadMirrorState()
        state.pairs = state.pairs.filter { !stale.contains($0.key) }
        saveMirrorState(state)
    }

    /// Drop the pairs this store borrows through that name a folder nobody
    /// writes to any more.
    ///
    /// Where a profile's chats live is `<account>/<org>`, and signing in again
    /// can move both — on either side. The pair an earlier pass wrote down then
    /// names a folder nobody reads, while `mirrorKnownPairs` goes on squaring it
    /// up from every launcher: a folder nobody writes to is one that looks
    /// emptied, which is carried to the other profile as a deletion, and one
    /// that gets written to is one `newestChild` can pick up again as the
    /// organization to share. The pairs this pass is actually mirroring are the
    /// ones left alone, which is why it is given them whole rather than a list
    /// of folders to find a half in — a pair survives only by being one of them.
    ///
    /// A set rather than one pair, because two profiles on one account mirror
    /// every organization the account has and no single one of them is the pair
    /// worth keeping.
    ///
    /// Borrowed pairs alone. What this store lends to somebody else is that
    /// profile's mirror, and its own launch is what keeps it current; dropping
    /// it here reads as a first pass over there.
    static func forgetStalePairs(under store: URL, keeping live: [(mine: URL, theirs: URL)]) {
        let kept = Set(live.map { pairKey($0.mine, $0.theirs) })
        let stale = Set(mirrorPairs(borrowedBy: store).filter { !kept.contains($0) })
        guard !stale.isEmpty else { return }
        Diagnostics.note("mirror.stale", [
            "store": store.path,
            "keeping": kept.map { pairHalves($0).map { "\($0.borrower) -> \($0.source)" } ?? $0 }.sorted(),
            "dropped": stale.count,
            "because": "these name a folder one side has stopped writing to",
        ])
        dropPairs(stale)
    }

    /// Take a mirrored profile back off the shared set, and lose nothing on
    /// the way.
    ///
    /// A mirrored folder is a real one full of copies, so unlike a link it
    /// cannot simply be dropped: what is in it has to be settled first. One
    /// last pass carries everything the profile did while it was mirroring —
    /// a chat started, one archived, one deleted — over to the profile it
    /// borrowed from, and only then are the copies taken out.
    ///
    /// Which copies is not a guess and does not trust the pass: a record goes
    /// only when the other side is holding the same bytes this moment. So a
    /// source that has been deleted, or a folder that could not be read,
    /// leaves every copy where it is rather than taking a chat away on the
    /// strength of a sync that did not happen. What it did during the graft is
    /// on the other side by then, which is where a link would have put it all
    /// along.
    ///
    /// What the profile brought to the merge is the exception, and the stash
    /// is what names it. Those records are on both sides too, so the test
    /// above matches them exactly as it matches a borrowed one, and taking
    /// them out would hand this profile's own history to the one it borrowed
    /// from and keep nothing back.
    @discardableResult
    static func unmirrorChatStores(_ profile: URL) -> Int {
        // The pairs this profile borrows through, never the ones it lends. A
        // lender picking up a borrower's pair reads its own folder as the
        // borrowed one, finds no stash to say what it owns, and hands its
        // whole history over — which is what opening the source profile did on
        // every launch. `apply` calls `ungraft` on any profile with no source
        // of its own, so being nobody's borrower is the ordinary case here and
        // the ordinary answer is to do nothing at all.
        let keys = mirrorPairs(borrowedBy: profile)
        guard !keys.isEmpty else { return 0 }
        var removed = 0

        for key in keys {
            guard let pair = pairFolders(key) else { continue }
            let (mine, theirs) = (pair.one, pair.other)

            mirrorChatFolders(pair.one, pair.other)

            // Which of the merged records this profile brought to the graft,
            // asked of the stash because that is the only thing that still
            // knows. Since the merge these have been on both sides, so the
            // handover above leaves them byte for byte identical and the test
            // below would take every one of them out of the profile that
            // owns them — the chats would be left in the profile they were
            // only ever lent to.
            let store = mine.deletingLastPathComponent().deletingLastPathComponent()
            let own = stashedCounterpart(of: mine, store: store)

            for name in (try? fm.contentsOfDirectory(atPath: mine.path)) ?? [] where isMirrored(name) {
                // Kept rather than removed and fetched back out of the stash,
                // so that a chat of its own the profile archived while the
                // graft was up stays archived. Restoring the stashed copy over
                // it would put the conversation back in the sidebar unarchived,
                // which is the one symptom this app gets reported for most.
                if let own, exists(own.appending(path: name)) { continue }
                guard let here = try? Data(contentsOf: mine.appending(path: name)),
                      let there = try? Data(contentsOf: theirs.appending(path: name)),
                      here == there
                else { continue }
                try? fm.removeItem(at: mine.appending(path: name))
                removed += 1
            }
        }
        // Only the pairs this pass settled. `forgetMirrors` drops every pair
        // with a half under the profile, and the ones it lends are somebody
        // else's mirror: a borrower whose pair has been dropped reads its next
        // launch as a first pass, stashes the merged folder, and the stash
        // stops naming what that profile brought to the merge.
        dropPairs(Set(keys))
        Diagnostics.note("unmirror", [
            "profile": profile.lastPathComponent, "pairs": keys.count, "copiesRemoved": removed,
        ])
        return removed
    }

    /// Undo a graft: drop every symlink this profile holds so it falls back to
    /// its own storage. Real files it wrote itself are left alone.
    static func ungraft(_ profile: URL) {
        Diagnostics.note("ungraft.begin", ["profile": profile.lastPathComponent])
        unmirrorChatStores(profile)
        for item in sharedItems {
            unstash(profile.appending(path: item))
        }
        for store in chatStores {
            let dst = profile.appending(path: store)
            if isSymlink(dst) {
                unstash(dst)
                continue
            }
            for account in (try? fm.contentsOfDirectory(atPath: dst.path)) ?? []
            where !account.hasSuffix(stashSuffix) {
                let accountDir = dst.appending(path: account)
                if isSymlink(accountDir) { unstash(accountDir); continue }
                guard let orgs = try? fm.contentsOfDirectory(atPath: accountDir.path) else { continue }
                for org in orgs where !org.hasSuffix(stashSuffix) {
                    unstash(accountDir.appending(path: org))
                }
            }
            // A whole store goes to the stash when both profiles are on one
            // account, and it has to come back whether what stands in its
            // place is the link that replaced it or the real folder a mirror
            // made. Only the link was ever looked for, so a mirrored profile
            // on one account went back to its own chats and found none.
            unstash(dst)
            unstash(dst.appending(path: "skills-plugin"))
        }
        Diagnostics.note("ungraft.end", ["profile": profile.lastPathComponent])
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
        forgetMirrors(of: profile)
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
    private static func launch(profile: URL) -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = launchArguments(for: profile)
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return false }
        return true
    }

    /// Mirror, file, then write down what the pass left behind.
    ///
    /// The three go together and in this order. Mirroring runs first because a
    /// record arriving that way has to be on disk before the sweep decides
    /// whether anyone has filed one. The report runs last because it is the
    /// file somebody reads after an incident, and a pass that leaves it
    /// describing the previous one is a pass that cannot be reconstructed —
    /// which is what pressing Open used to do, having been given the first two
    /// steps and not the third.
    @discardableResult
    static func squareUp(filingInto profiles: [URL],
                         checkingRunning: Bool = true) -> [SessionFacts] {
        mirrorKnownPairs()
        let filed = fileMissingSessionRecords(filingInto: profiles)
        writeStateReport(checkingRunning: checkingRunning)
        return filed
    }

    /// Opening a profile that is already open shows the Claude that is there.
    ///
    /// Claude Desktop never takes Electron's single-instance lock, so nothing
    /// on its side refuses a second process on one `--user-data-dir`: both come
    /// up and both write the same chat store, which is the one loss this app
    /// warns about everywhere else. Pressing Open twice was all it took.
    ///
    /// Records are filed on the way in because that is the only moment they are
    /// read: Claude builds its sidebar as it starts, so one landing a second
    /// later waits for the next launch to be seen. That is why the filing sits
    /// ahead of `launch` and behind the running check — a Claude already up
    /// read its store when it started and is not about to read it again.
    ///
    /// And why a pid that cannot be found does not skip it. `isRunning` asks
    /// pgrep while `processIdentifier` reads `ps` for the browser process
    /// alone, so the two can disagree — and returning there passed over the one
    /// case nothing else covers, since a press on a profile already open is not
    /// going to start anything and there is no timer behind it.
    @discardableResult
    static func open(profile: URL) -> Bool {
        guard !isRunning(profile: profile) else {
            var shown = false
            if let pid = processIdentifier(of: profile) { shown = reveal(pid: pid) }
            squareUp(filingInto: [profile, mainProfile])
            return shown
        }
        squareUp(filingInto: [profile, mainProfile])
        return launch(profile: profile)
    }

    /// What a generated shortcut does when clicked.
    static func run(_ config: GraftConfig) {
        Diagnostics.who = "launcher:" + URL(fileURLWithPath: config.profileDir).lastPathComponent
        let profile = URL(fileURLWithPath: config.profileDir)
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)
        Diagnostics.note("launcher.run", [
            "profile": profile.lastPathComponent,
            "source": config.sourceDir ?? "",
        ])
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
            squareUp(filingInto: filing, checkingRunning: false)
            return
        }
        apply(config)
        // After the links, so a record filed through a graft lands where the
        // link now points rather than where it pointed last time. The mirror
        // runs here for the same reason and one more: this profile may be the
        // source somebody else borrows from, and opening it is the only
        // moment their changes have to reach the sidebar about to be built.
        squareUp(filingInto: filing, checkingRunning: false)
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
