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

    static let claudeApp = URL(fileURLWithPath: "/Applications/Claude.app")

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

    /// Move a profile's own file or folder aside rather than destroying it. The
    /// first stash is the pre-graft state and is never overwritten; later ones
    /// are drift from the shared copy, so they are discarded.
    /// Hidden sibling, so the app never mistakes a stashed organization folder
    /// for a real one when it scans the store.
    private static func stashURL(for url: URL) -> URL {
        url.deletingLastPathComponent()
            .appending(path: "." + url.lastPathComponent + stashSuffix)
    }

    private static func stash(_ url: URL) {
        guard exists(url), !isSymlink(url) else { return }
        let stashed = stashURL(for: url)
        if exists(stashed) {
            try? fm.removeItem(at: url)
        } else {
            try? fm.moveItem(at: url, to: stashed)
        }
    }

    /// Put back whatever this profile owned before the graft.
    private static func unstash(_ link: URL) {
        if isSymlink(link) { try? fm.removeItem(at: link) }
        let stashed = stashURL(for: link)
        guard exists(stashed), !exists(link) else { return }
        try? fm.moveItem(at: stashed, to: link)
    }

    /// Point `link` at `target`, preserving anything already there.
    @discardableResult
    static func relink(target: URL, at link: URL) -> Bool {
        guard exists(target) else { return false }
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
        let url = profile.appending(path: "config.json")
        guard let data = try? Data(contentsOf: url),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return [:] }
        return obj
    }

    /// The account this profile is currently signed into.
    static func account(of profile: URL) -> String? {
        configJSON(of: profile)["lastKnownAccountUuid"] as? String
    }

    // MARK: - Grafting

    /// Share the safe files, then map this profile's chat directory onto the
    /// source's. Chats are stored per account, so when the two profiles are on
    /// different accounts the link has to be made one level deeper: the
    /// destination's own <account>/<org> directory points at the source's.
    static func graft(from source: URL, into profile: URL) {
        try? fm.createDirectory(at: profile, withIntermediateDirectories: true)

        for item in sharedItems {
            relink(target: source.appending(path: item), at: profile.appending(path: item))
        }
        linkChatStores(from: source, into: profile)
        copyAppearance(from: source, into: profile)
    }

    static func linkChatStores(from source: URL, into profile: URL) {
        guard let sourceAccount = account(of: source) else { return }
        let ownAccount = account(of: profile)

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

            if isSymlink(dst) { try? fm.removeItem(at: dst) }
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
        var into = configJSON(of: profile)
        for key in appearanceKeys where from[key] != nil {
            into[key] = from[key]
        }
        guard let data = try? JSONSerialization.data(withJSONObject: into,
                                                     options: [.prettyPrinted, .sortedKeys])
        else { return }
        try? data.write(to: profile.appending(path: "config.json"))
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

    static func isRunning(profile: URL) -> Bool {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        task.arguments = ["-f", "user-data-dir=\(profile.path)"]
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try? task.run()
        task.waitUntilExit()
        return task.terminationStatus == 0
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
