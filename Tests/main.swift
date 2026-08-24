import Foundation

// Every test runs against a throwaway Application Support and Applications
// directory, so nothing here can reach a real profile or a real app.

var failures = 0
var checks = 0

func check(_ condition: Bool, _ what: String) {
    checks += 1
    if condition {
        print("  ok    \(what)")
    } else {
        failures += 1
        print("  FAIL  \(what)")
    }
}

func section(_ name: String) {
    print("\n\(name)")
}

let fm = FileManager.default
let root = URL(fileURLWithPath: NSTemporaryDirectory())
    .appending(path: "claude-graft-tests-\(UUID().uuidString)")
let support = root.appending(path: "Application Support")
let apps = root.appending(path: "Applications")

try! fm.createDirectory(at: support, withIntermediateDirectories: true)
try! fm.createDirectory(at: apps, withIntermediateDirectories: true)

Graft.applicationSupportOverride = support
Installer.installDirectoryOverride = apps
Installer.registersWithLaunchServices = false

defer { try? fm.removeItem(at: root) }

// MARK: - Helpers

func makeProfile(_ name: String, account: String?, org: String? = nil,
                 chats: [String] = [], extras: [String: String] = [:]) -> URL {
    let profile = support.appending(path: name)
    try? fm.createDirectory(at: profile, withIntermediateDirectories: true)
    if let account {
        var config: [String: Any] = ["lastKnownAccountUuid": account]
        config["oauth:tokenCache"] = "token-for-\(name)"
        for (k, v) in extras { config[k] = v }
        let data = try! JSONSerialization.data(withJSONObject: config)
        try! data.write(to: profile.appending(path: "config.json"))
    }
    if let account, let org {
        for store in Graft.chatStores {
            let dir = profile.appending(path: store).appending(path: account).appending(path: org)
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            for chat in chats {
                try! "{}".write(to: dir.appending(path: "local_\(chat).json"), atomically: true, encoding: .utf8)
            }
        }
    }
    return profile
}

/// An application Graft did not create, of the kind that must never be touched.
func makeForeignApp(named name: String) -> URL {
    let bundle = apps.appending(path: "\(name).app")
    let binary = bundle.appending(path: "Contents/MacOS/Something")
    try? fm.createDirectory(at: binary.deletingLastPathComponent(), withIntermediateDirectories: true)
    try! "not ours".write(to: binary, atomically: true, encoding: .utf8)
    return bundle
}

func isIntact(_ foreign: URL) -> Bool {
    let binary = foreign.appending(path: "Contents/MacOS/Something")
    return (try? String(contentsOf: binary, encoding: .utf8)) == "not ours"
}

func chatsVisible(to profile: URL) -> [String] {
    let store = profile.appending(path: "claude-code-sessions")
    guard let accounts = try? fm.contentsOfDirectory(atPath: store.path) else { return [] }
    var found: [String] = []
    for account in accounts where !account.hasPrefix(".") {
        let accountDir = store.appending(path: account)
        guard let orgs = try? fm.contentsOfDirectory(atPath: accountDir.path) else { continue }
        for org in orgs where !org.hasPrefix(".") {
            let files = (try? fm.contentsOfDirectory(atPath: accountDir.appending(path: org).path)) ?? []
            found += files.filter { $0.hasPrefix("local_") }
        }
    }
    return found.sorted()
}

func sourcePath(ofBundle bundle: URL) -> String? {
    guard let data = try? Data(contentsOf: bundle.appending(path: "Contents/Resources/graft.json")),
          let config = try? JSONDecoder().decode(GraftConfig.self, from: data)
    else { return nil }
    return config.sourceDir
}

// MARK: - Never touch anything Graft did not create

section("Foreign applications")
do {
    let claude = makeForeignApp(named: "Claude")
    let other = makeForeignApp(named: "Pages")

    check(Installer.installedBundle(for: Shortcut(name: "Pages")) == nil,
          "an app Graft did not create is not seen as installed")

    var reserved = false
    do { _ = try Installer.install(Shortcut(name: "Claude"), sourceDir: nil) }
    catch { reserved = true }
    check(reserved, "the name Claude is refused outright")
    check(isIntact(claude), "Claude.app is untouched after that attempt")

    var refused = false
    do { _ = try Installer.install(Shortcut(name: "Pages"), sourceDir: nil) }
    catch { refused = true }
    check(refused, "installing over another application is refused")
    check(isIntact(other), "that application is untouched")

    Installer.uninstall(Shortcut(name: "Pages"))
    check(isIntact(other), "uninstall leaves a foreign app of the same name alone")

    let store = ShortcutStore()
    store.shortcuts = [Shortcut(name: "Pages")]
    store.delete(store.shortcuts[0].id)
    check(isIntact(other), "deleting a shortcut named after a real app spares the app")
    check(store.shortcuts.isEmpty, "the shortcut is still removed from the list")

    var alsoReserved = false
    do { _ = try Installer.install(Shortcut(name: "Claude Graft"), sourceDir: nil) }
    catch { alsoReserved = true }
    check(alsoReserved, "the name Claude Graft is reserved too")

    try? fm.removeItem(at: claude)
    try? fm.removeItem(at: other)
}

// MARK: - Creating, updating and renaming

section("Create and update")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["a1", "a2"],
                           extras: ["userThemeMode": "dark", "locale": "en-GB"])
    var shortcut = Shortcut(name: "Work", source: .main)

    let bundle = try! Installer.install(shortcut, sourceDir: main)
    check(fm.fileExists(atPath: bundle.appending(path: "Contents/MacOS/launcher").path),
          "the bundle carries a launcher")
    check(Installer.isGraftBundle(bundle), "the bundle is recognisable as ours")
    check(Installer.installedBundle(for: shortcut) == bundle, "it is found again afterwards")
    check(sourcePath(ofBundle: bundle) == main.path, "its description records the source profile")
    check(fm.fileExists(atPath: shortcut.profileDir.path), "the profile folder was created")

    // Update in place: same bundle, no duplicates.
    let again = try! Installer.install(shortcut, sourceDir: main, previousName: shortcut.name)
    check(again == bundle, "updating writes to the same bundle")
    let installed = (try? fm.contentsOfDirectory(atPath: apps.path))?.filter { $0.hasSuffix(".app") }
    check(installed?.count == 1, "updating does not leave a second copy behind")

    // Update after switching the source to none.
    let detached = try! Installer.install(shortcut, sourceDir: nil, previousName: shortcut.name)
    check(sourcePath(ofBundle: detached) == nil, "switching to its own chats clears the source")
    check(chatsVisible(to: shortcut.profileDir).isEmpty, "and the borrowed chats are gone")

    // And back again.
    _ = try! Installer.install(shortcut, sourceDir: main, previousName: shortcut.name)
    check(chatsVisible(to: shortcut.profileDir) == ["local_a1.json", "local_a2.json"],
          "switching back restores them")

    // Rename.
    let oldName = shortcut.name
    shortcut.name = "Work Two"
    let renamed = try! Installer.install(shortcut, sourceDir: main, previousName: oldName)
    check(renamed.lastPathComponent == "Work Two.app", "renaming installs under the new name")
    check(!fm.fileExists(atPath: apps.appending(path: "Work.app").path),
          "and removes the bundle under the old name")

    // A rename onto an occupied name must change nothing at all.
    let occupied = makeForeignApp(named: "Taken")
    var clash = shortcut
    clash.name = "Taken"
    var blocked = false
    do { _ = try Installer.install(clash, sourceDir: main, previousName: shortcut.name) }
    catch { blocked = true }
    check(blocked, "renaming onto an occupied name is refused")
    check(isIntact(occupied), "the occupying app survives")
    check(fm.fileExists(atPath: renamed.path), "and the original bundle is still there")

    try? fm.removeItem(at: occupied)
    try? fm.removeItem(at: renamed)
    try? fm.removeItem(at: shortcut.profileDir)
    try? fm.removeItem(at: main)
}

// MARK: - Grafting between accounts

section("Grafting")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["shared"],
                           extras: ["userThemeMode": "dark", "locale": "en-GB"])
    try! "{\"preferences\":{}}".write(to: main.appending(path: "claude_desktop_config.json"),
                                      atomically: true, encoding: .utf8)

    // Different account: the link has to go one level deeper.
    let work = makeProfile("Claude-Work", account: "BBBB", org: "ORG-B", chats: ["mine"])
    Graft.graft(from: main, into: work)
    check(chatsVisible(to: work) == ["local_shared.json"], "it reads the source account's chats")

    let ownFolder = work.appending(path: "claude-code-sessions/BBBB/.ORG-B.graft-own")
    check(fm.fileExists(atPath: ownFolder.appending(path: "local_mine.json").path),
          "its own chats are stashed, not destroyed")
    check(!fm.fileExists(atPath: work.appending(path: "claude-code-sessions/BBBB/ORG-B.graft-own").path),
          "the stash is hidden, so it cannot be read back as an organization")

    let config = Graft.configJSON(of: work)
    check(config["oauth:tokenCache"] as? String == "token-for-Claude-Work",
          "the login is not overwritten by the source's")
    check(config["userThemeMode"] as? String == "dark", "the theme is copied across")
    check(config["locale"] as? String == "en-GB", "so is the locale")
    check(Graft.isSymlink(work.appending(path: "claude_desktop_config.json")),
          "shared settings are linked")
    check(!Graft.exists(work.appending(path: "extensions-blocklist.json")),
          "the per-organization blocklist is never linked")

    Graft.graft(from: main, into: work)
    check(chatsVisible(to: work) == ["local_shared.json"], "grafting twice changes nothing")

    Graft.ungraft(work)
    check(chatsVisible(to: work) == ["local_mine.json"], "ungrafting gives back its own chats")
    check(!Graft.isSymlink(work.appending(path: "claude_desktop_config.json")),
          "and drops the links")

    // Same account: sharing the whole store is enough.
    let twin = makeProfile("Claude-Twin", account: "AAAA", org: "ORG-A")
    Graft.graft(from: main, into: twin)
    check(Graft.isSymlink(twin.appending(path: "claude-code-sessions")),
          "the same account shares the whole store")
    check(chatsVisible(to: twin) == ["local_shared.json"], "and sees the same chats")

    for profile in [main, work, twin] { try? fm.removeItem(at: profile) }
}

// MARK: - What a link cannot protect

// Claude writes config.json, and recreates chat directories, by renaming over
// whatever is there. A rename leaves a real file where the symlink was, so the
// profile goes back to writing its own copy without anything noticing. These
// cover what has to happen the next time that profile is grafted.

section("Data written after a graft")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["shared"])
    try! "{\"preferences\":{}}".write(to: main.appending(path: "claude_desktop_config.json"),
                                      atomically: true, encoding: .utf8)

    let work = makeProfile("Claude-Work", account: "BBBB", org: "ORG-B", chats: ["mine"])
    let settings = work.appending(path: "claude_desktop_config.json")
    try! "own settings".write(to: settings, atomically: true, encoding: .utf8)
    Graft.graft(from: main, into: work)

    // Claude renames a temporary over the link and carries on writing its own.
    try? fm.removeItem(at: settings)
    try! "own settings, edited since".write(to: settings, atomically: true, encoding: .utf8)

    let ownChats = work.appending(path: "claude-code-sessions/BBBB/ORG-B")
    try? fm.removeItem(at: ownChats)
    try! fm.createDirectory(at: ownChats, withIntermediateDirectories: true)
    try! "{}".write(to: ownChats.appending(path: "local_written_since.json"),
                    atomically: true, encoding: .utf8)

    Graft.graft(from: main, into: work)
    Graft.ungraft(work)

    check(chatsVisible(to: work) == ["local_mine.json", "local_written_since.json"],
          "a chat written after the graft survives the next one, and so does one from before it")
    check((try? String(contentsOf: settings, encoding: .utf8)) == "own settings, edited since",
          "an edit made after the graft is what comes back, not the copy it replaced")

    for profile in [main, work] { try? fm.removeItem(at: profile) }
}

section("An orphaned stash")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["shared"])
    let work = makeProfile("Claude-Work", account: "BBBB", org: "ORG-B", chats: ["mine"])
    Graft.graft(from: main, into: work)

    let ownChats = work.appending(path: "claude-code-sessions/BBBB/ORG-B")
    try? fm.removeItem(at: ownChats)
    try! fm.createDirectory(at: ownChats, withIntermediateDirectories: true)
    try! "{}".write(to: ownChats.appending(path: "local_written_since.json"),
                    atomically: true, encoding: .utf8)

    Graft.ungraft(work)
    check(!Graft.exists(work.appending(path: "claude-code-sessions/BBBB/.ORG-B.graft-own")),
          "ungrafting folds the stash back in rather than abandoning it beside the link")
    check(chatsVisible(to: work) == ["local_mine.json", "local_written_since.json"],
          "so the profile is handed both halves of what it owns")

    for profile in [main, work] { try? fm.removeItem(at: profile) }
}

section("A config caught mid-write")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["shared"],
                           extras: ["userThemeMode": "dark", "locale": "en-GB"])
    let work = makeProfile("Claude-Work", account: "BBBB", org: "ORG-B", chats: ["mine"])

    // What a read landing part way through Claude's rename sees.
    let config = work.appending(path: "config.json")
    try! "{\"lastKnownAccountUuid\":\"BBBB\",\"oauth:tokenCache\":\"the-login\"".write(
        to: config, atomically: true, encoding: .utf8)

    Graft.graft(from: main, into: work)
    check((try? String(contentsOf: config, encoding: .utf8))?.contains("the-login") == true,
          "a config that will not parse is left as it is, login and all")
    check(chatsVisible(to: work) == ["local_mine.json"],
          "and the profile keeps reading its own chats")
    check(!Graft.isSymlink(work.appending(path: "claude-code-sessions")),
          "an account nothing can read is never taken for the source's own")

    // A profile that has never been signed in has no config at all, and
    // sharing the whole store is what that case is for.
    let fresh = support.appending(path: "Claude-Fresh")
    try! fm.createDirectory(at: fresh, withIntermediateDirectories: true)
    Graft.graft(from: main, into: fresh)
    check(Graft.isSymlink(fresh.appending(path: "claude-code-sessions")),
          "a profile with no config yet still shares the whole store")

    for profile in [main, work, fresh] { try? fm.removeItem(at: profile) }
}

// MARK: - Deleting a profile

section("Profile deletion")
do {
    let main = makeProfile("Claude", account: "AAAA", org: "ORG-A", chats: ["keep"])
    let work = makeProfile("Claude-Work", account: "BBBB", org: "ORG-B", chats: ["gone"])

    func refusal(_ url: URL) -> Graft.ProfileError? {
        do { try Graft.deleteProfile(url); return nil }
        catch let error as Graft.ProfileError { return error }
        catch { return nil }
    }

    check(refusal(main) == .mainProfile, "Claude's own profile is refused as the main profile")
    check(fm.fileExists(atPath: main.path), "it is still there")
    check(refusal(support.appending(path: "Some/Nested/Path")) == .outsideApplicationSupport,
          "a nested folder is refused")
    check(refusal(fm.homeDirectoryForCurrentUser) == .outsideApplicationSupport,
          "the home directory is refused")
    check(refusal(support) == .outsideApplicationSupport,
          "Application Support itself is refused")
    check(refusal(URL(fileURLWithPath: "/")) == .outsideApplicationSupport,
          "the root of the disk is refused")
    check(refusal(support.appending(path: "Claude/../Claude")) == .mainProfile,
          "a path that walks back round to the main profile is still refused")

    let store = ShortcutStore()
    var keeper = Shortcut(name: "Work", source: .main)
    keeper.folder = "Claude-Work"
    var sameFolder = Shortcut(name: "Work Copy", source: .main)
    sameFolder.folder = "Claude-Work"
    store.shortcuts = [keeper, sameFolder]

    let message = store.delete(keeper.id, deletingProfile: true)
    check(message != nil, "deleting a profile another shortcut uses is reported")
    check(fm.fileExists(atPath: work.path), "and the profile survives")
    check(store.shortcuts.count == 1, "the shortcut itself is still removed")

    let clean = store.delete(sameFolder.id, deletingProfile: true)
    check(clean == nil, "the last shortcut may delete its profile")
    check(!fm.fileExists(atPath: work.path), "the profile folder is gone")
    check(store.shortcuts.isEmpty, "and so is the shortcut")

    // Keeping the profile is the other path.
    let kept = makeProfile("Claude-Kept", account: "CCCC", org: "ORG-C")
    var keepShortcut = Shortcut(name: "Keep", source: .own)
    keepShortcut.folder = "Claude-Kept"
    store.shortcuts = [keepShortcut]
    store.delete(keepShortcut.id, deletingProfile: false)
    check(fm.fileExists(atPath: kept.path), "deleting the shortcut alone keeps the profile")

    try? fm.removeItem(at: main)
    try? fm.removeItem(at: kept)
}

// MARK: - Naming

section("Naming")
do {
    check(Shortcut.folderName(for: "Claude 2") == "Claude-2", "Claude 2 becomes Claude-2")
    check(Shortcut.folderName(for: "Work") == "Claude-Work", "Work becomes Claude-Work")
    check(Shortcut.folderName(for: "Work Account") == "Claude-Work-Account", "spaces become dashes")
    check(Shortcut.folderName(for: "  ") == "Claude-Profile", "an empty name still yields a folder")

    let store = ShortcutStore()
    store.shortcuts = []
    check(store.uniqueName() == "Claude 2", "the first suggestion is Claude 2")
    store.shortcuts = [Shortcut(name: "Claude 2")]
    check(store.uniqueName() == "Claude 3", "the next one steps past it")

    // A shortcut may not inherit from itself, directly or through a chain.
    var a = Shortcut(name: "A")
    var b = Shortcut(name: "B")
    b.source = .shortcut(a.id)
    a.source = .main
    store.shortcuts = [a, b]
    let options = store.availableSources(for: b)
    check(!options.contains(.shortcut(b.id)), "a shortcut is not offered itself as a source")

    a.source = .shortcut(b.id)
    store.shortcuts = [a, b]
    check(!store.availableSources(for: b).contains(.shortcut(a.id)),
          "nor one that would close a loop")
}

// MARK: - Running processes

section("Process helpers")

// runTool must not spin the caller's run loop. It is called from the main
// thread during a view update, and waitUntilExit there re-enters AppKit
// layout, which crashed the app when the status row asked whether Claude was
// running. Reaching these checks at all means the call returned on its own.
check(Graft.runTool("/usr/bin/true", []) == 0, "a tool that succeeds reports zero")
check(Graft.runTool("/usr/bin/false", []) != 0, "a tool that fails does not")
check(Graft.runTool("/nonexistent/tool", []) == -1, "a missing tool is reported, not fatal")
check(!Graft.isRunning(profile: support.appending(path: "Claude-NeverLaunched")),
      "an unused profile is not running")

// MARK: - Guarding against self-reference

section("Self-reference")

do {
    // A profile grafted from itself would stash every file it owns and leave
    // links pointing at their own empty names.
    let profile = makeProfile("Claude-Selfie", account: "AAAA", org: "ORG1", chats: ["1"])
    try! "kept".write(to: profile.appending(path: "window-state.json"), atomically: true, encoding: .utf8)

    Graft.graft(from: profile, into: profile)

    check(!Graft.isSymlink(profile.appending(path: "window-state.json")),
          "grafting a profile from itself changes nothing")
    check((try? String(contentsOf: profile.appending(path: "window-state.json"), encoding: .utf8)) == "kept",
          "and its own files are untouched")
    let store = profile.appending(path: "claude-code-sessions/AAAA/ORG1")
    check(fm.fileExists(atPath: store.appending(path: "local_1.json").path),
          "and its chats are still there")

    check(!Graft.relink(target: profile, at: profile), "relink refuses a link to itself")
    check(Graft.isDirectory(profile), "the folder survives that too")
}

do {
    let profile = makeProfile("Claude-Loop", account: "BBBB", org: "ORG2")
    var shortcut = Shortcut(name: "Loop", folder: "Claude-Loop", source: .own)
    shortcut.installedName = nil
    var thrown: Error?
    do { _ = try Installer.install(shortcut, sourceDir: profile) }
    catch { thrown = error }
    check(thrown != nil, "installing a shortcut sourced from its own profile is refused")
}

// MARK: - Folder names

section("Profile folder names")

check(Graft.validateFolder("Claude-Work") == nil, "an ordinary folder name is fine")
check(Graft.validateFolder("") != nil, "an empty one is not")
check(Graft.validateFolder("  ") != nil, "nor is whitespace")
check(Graft.validateFolder("../Claude") != nil, "nor one that climbs out")
check(Graft.validateFolder("a/b") != nil, "nor one with a separator")
check(Graft.validateFolder(".hidden") != nil, "nor a hidden name")
check(Graft.validateFolder("Claude") != nil, "Claude's own folder is refused")
check(Graft.validateFolder("ClaudeGraft") != nil, "and so is Graft's own")

do {
    var shortcut = Shortcut(name: "Escapee", folder: "../Escaped", source: .own)
    shortcut.installedName = nil
    var thrown: Error?
    do { _ = try Installer.install(shortcut, sourceDir: nil) } catch { thrown = error }
    check(thrown != nil, "a shortcut with a path for a folder cannot be installed")
    check(!fm.fileExists(atPath: support.appending(path: "../Escaped").path),
          "and nothing is created outside Application Support")
}

// MARK: - Awkward names

section("Awkward names")

do {
    let shortcut = Shortcut(name: "Ben & Co <work>", folder: "Claude-Ben", source: .own)
    let bundle = try! Installer.install(shortcut, sourceDir: nil)
    let plist = try! String(contentsOf: bundle.appending(path: "Contents/Info.plist"), encoding: .utf8)
    check(!plist.contains("Ben & Co"), "an ampersand in a name is escaped")
    check(plist.contains("Ben &amp; Co &lt;work&gt;"), "and so are angle brackets")
    let parsed = (try? PropertyListSerialization.propertyList(
        from: Data(plist.utf8), options: [], format: nil)) as? [String: Any]
    check((parsed?["CFBundleName"] as? String) == "Ben & Co <work>",
          "the plist still reads back as the name typed")
}

// MARK: - Sharing a chat store

section("Chat store neighbours")

do {
    let store = ShortcutStore()
    let main = Shortcut(name: "From Main", folder: "Claude-A", source: .main)
    let alsoMain = Shortcut(name: "Also Main", folder: "Claude-B", source: .main)
    let alone = Shortcut(name: "Alone", folder: "Claude-C", source: .own)
    store.shortcuts = [main, alsoMain, alone]

    let neighbours = store.chatStoreNeighbours(of: main).map(\.name)
    check(neighbours.contains("Claude"), "the main profile counts as a neighbour")
    check(neighbours.contains("Also Main"), "so does another shortcut on the same source")
    check(!neighbours.contains("Alone"), "one with its own chats does not")
    check(!neighbours.contains("From Main"), "and neither does the shortcut itself")

    check(store.chatStoreNeighbours(of: alone).isEmpty, "a profile on its own has no neighbours")

    let chained = Shortcut(name: "Chained", folder: "Claude-D", source: .shortcut(main.id))
    store.shortcuts.append(chained)
    check(Graft.samePath(store.chatRoot(for: chained), Graft.mainProfile),
          "a chain of sources resolves to the profile at its end")
    check(store.chatStoreNeighbours(of: chained).map(\.name).contains("From Main"),
          "so everything along it shares the same store")
}

do {
    let store = ShortcutStore()
    let target = Shortcut(name: "Target", folder: "Claude-T", source: .own)
    let follower = Shortcut(name: "Follower", folder: "Claude-F", source: .shortcut(target.id))
    store.shortcuts = [target, follower]
    store.delete(target.id)
    check(store.shortcuts.first?.source == Shortcut.Source.own,
          "deleting a source leaves its followers on their own chats, not silently un-grafted")
}

// MARK: - Plan usage

section("Plan usage")

func writeUsage(_ profile: URL, _ samples: [(Double, Int, Int)]) {
    let payload: [String: Any] = [
        "version": 2,
        "samples": samples.map { ["t": $0.0, "org": "ORG1", "u": ["fh": $0.1, "sd": $0.2]] },
    ]
    let data = try! JSONSerialization.data(withJSONObject: payload)
    try! data.write(to: profile.appending(path: "plan-usage-history.json"))
}

do {
    let profile = makeProfile("Claude-Usage", account: "AAAA", org: "ORG1")
    check(Graft.usage(of: profile) == nil, "a profile with no history reports nothing")

    let now = Date().timeIntervalSince1970 * 1000
    writeUsage(profile, [(now - 60_000, 10, 20), (now, 45, 69)])
    let usage = Graft.usage(of: profile)
    check(usage?.fiveHour == 45, "the newest five-hour figure is the one reported")
    check(usage?.week == 69, "and so is the weekly one")
    check(usage?.organization == "ORG1", "the organization comes through")
    check(usage?.isStale == false, "a fresh sample is not stale")

    writeUsage(profile, [(now - 6 * 60 * 60 * 1000, 90, 90)])
    check(Graft.usage(of: profile)?.isStale == true,
          "a sample older than the five-hour window is stale")

    // Claude has been seen to write samples with a missing figure.
    let partial: [String: Any] = [
        "version": 2,
        "samples": [["t": now, "org": "ORG1", "u": ["sd": 5]],
                    ["t": now - 1000, "org": "ORG1", "u": ["fh": 7, "sd": 8]]],
    ]
    try! JSONSerialization.data(withJSONObject: partial)
        .write(to: profile.appending(path: "plan-usage-history.json"))
    check(Graft.usage(of: profile)?.fiveHour == 7,
          "a sample missing a figure is skipped for the last complete one")

    try! Data("not json".utf8).write(to: profile.appending(path: "plan-usage-history.json"))
    check(Graft.usage(of: profile) == nil, "a corrupt history is ignored rather than fatal")

    // The parse is cached against the file's stamp and size; a later write has
    // to be picked up rather than served from that cache.
    writeUsage(profile, [(now, 1, 2)])
    check(Graft.usage(of: profile)?.fiveHour == 1, "a rewritten history is re-read")
    writeUsage(profile, [(now, 3, 4)])
    check(Graft.usage(of: profile)?.fiveHour == 3, "and re-read again when it changes")

    let empty: [String: Any] = ["version": 2, "samples": []]
    try! JSONSerialization.data(withJSONObject: empty)
        .write(to: profile.appending(path: "plan-usage-history.json"))
    check(Graft.usage(of: profile) == nil, "so is an empty one")
}

do {
    // The usage file belongs to one account and must never be linked away.
    check(!Graft.sharedItems.contains("plan-usage-history.json"),
          "usage history is not one of the shared files")
}

section("Command output")

check(Graft.output("/bin/echo", ["hello"]).trimmingCharacters(in: .whitespacesAndNewlines) == "hello",
      "output is captured")
check(Graft.output("/nonexistent/tool", []).isEmpty, "a missing tool yields nothing")

// MARK: - What the menu bar reports

section("Usage monitor")

/// Spins the run loop until the monitor has published, or gives up.
func settle(_ monitor: UsageMonitor, expecting count: Int) {
    let deadline = Date().addingTimeInterval(5)
    while monitor.entries.count != count, Date() < deadline {
        RunLoop.current.run(until: Date().addingTimeInterval(0.05))
    }
}

do {
    let now = Date().timeIntervalSince1970 * 1000
    _ = makeProfile("Claude", account: "MAIN", org: "ORGM")
    writeUsage(support.appending(path: "Claude"), [(now, 12, 34)])

    let listed = makeProfile("Claude-Listed", account: "AAAA", org: "ORG1")
    writeUsage(listed, [(now, 77, 88)])
    _ = makeProfile("Claude-Draft", account: "BBBB", org: "ORG2")

    let store = ShortcutStore()
    var installed = Shortcut(name: "Listed", folder: "Claude-Listed", source: .main)
    installed.installedName = "Listed"
    let draft = Shortcut(name: "Draft", folder: "Claude-Draft", source: .main)
    store.shortcuts = [installed, draft]

    let monitor = UsageMonitor()
    monitor.refresh(store)
    settle(monitor, expecting: 2)

    check(monitor.entries.count == 2, "the main profile and each created shortcut are listed")
    check(monitor.entries.first?.name == "Claude", "the main profile comes first")
    check(!monitor.entries.contains { $0.name == "Draft" }, "a draft shortcut is not listed")
    check(monitor.entries.last?.usage?.fiveHour == 77, "each entry carries its own figures")
    check(monitor.entries.first?.shortcut == nil, "the main profile has no shortcut behind it")
    // Whether a Claude is open on the main profile is a fact about this machine
    // rather than about the fixture: that instance is recognised by carrying no
    // profile flag at all, which no temporary directory can redirect. The
    // figures this refresh read off disk are replayed with nothing open, since
    // which account is open has a section of its own below.
    monitor.setEntriesForTesting(monitor.entries.map {
        var settled = $0
        settled.isRunning = false
        return settled
    })
    check(monitor.headline == 77, "the headline comes from the figures a refresh read off disk")

    // A stale figure says nothing about the window that is running now.
    writeUsage(listed, [(now - 8 * 60 * 60 * 1000, 99, 99)])
    let second = UsageMonitor()
    second.refresh(store)
    settle(second, expecting: 2)
    check(second.headline == 12, "a stale figure is left out of the headline")
}

section("Preferences")

do {
    let suite = UserDefaults(suiteName: "graft.tests.\(UUID().uuidString)")!
    let settings = AppSettings(defaults: suite)
    check(settings.showInMenuBar, "the menu bar item is on by default")

    settings.showInMenuBar = false
    let reopened = AppSettings(defaults: suite)
    check(!reopened.showInMenuBar, "and turning it off survives a restart")
}

// MARK: - When the windows reset

section("Reset times")

do {
    let profile = makeProfile("Claude-Resets", account: "AAAA", org: "ORG1")
    let now = Date().timeIntervalSince1970 * 1000
    let hour = 3_600_000.0

    // Opened two hours ago: three of the five hours left.
    writeUsage(profile, [(now - 3 * hour, 0, 40), (now - 2 * hour, 5, 41), (now, 30, 45)])
    let usage = Graft.usage(of: profile)
    let remaining = usage?.fiveHourReset?.timeIntervalSinceNow ?? 0
    check(remaining > 2.9 * 3600 && remaining < 3.1 * 3600,
          "the five-hour window closes five hours after it opened")

    writeUsage(profile, [(now - hour, 0, 40), (now, 0, 40)])
    check(Graft.usage(of: profile)?.fiveHourReset == nil,
          "nothing to report when no window is open")

    // A history that starts partway through a window cannot say when it began.
    writeUsage(profile, [(now - hour, 20, 40), (now, 25, 41)])
    check(Graft.usage(of: profile)?.fiveHourReset == nil,
          "nor when the history never saw the window open")

    // Weekly resets roll forward across stretches Claude was not running.
    let day = 24 * hour
    writeUsage(profile, [(now - 20 * day, 0, 60), (now - 19.9 * day, 0, 2), (now, 10, 30)])
    let week = Graft.usage(of: profile)?.weekReset
    check(week != nil, "a weekly reset seen once gives the next one")
    check(week.map { $0 > Date() } == true, "and it is always in the future")
    check(week.map { $0.timeIntervalSinceNow < 7 * 24 * 3600 } == true,
          "within one cycle, however long ago it was seen")
}

section("Countdown wording")

do {
    let now = Date()
    func text(_ seconds: TimeInterval) -> String? {
        Graft.countdown(to: now.addingTimeInterval(seconds), from: now)
    }
    check(text(2 * 86_400 + 3 * 3_600 + 40 * 60) == "2d 3h 40m", "days, hours and minutes together")
    check(text(3 * 3_600 + 40 * 60) == "3h 40m", "hours and minutes once a day is gone")
    check(text(40 * 60) == "40m", "minutes alone under the hour")
    check(text(20) == "1m", "the last seconds round up rather than reading zero")
    check(text(-60) == nil, "a time already past reads as nothing")
}

// MARK: - The live usage endpoint

section("Usage endpoint")

do {
    let body: [String: Any] = [
        "five_hour": ["utilization": 42, "resets_at": "2026-08-24T09:30:00Z"],
        "seven_day": ["utilization": 71.4, "resets_at": "2026-08-25T22:51:00.000Z"],
        "subscription_type": "max",
    ]
    let reading = UsageAPI.reading(from: body)
    check(reading?.fiveHour == 42, "the five-hour figure is read")
    check(reading?.week == 71, "a fractional weekly figure rounds")
    check(reading?.plan == "Max", "the plan name is tidied up")
    check(reading?.fiveHourReset != nil, "a plain ISO reset time parses")
    check(reading?.weekReset != nil, "and so does one with fractional seconds")

    // Reset times come from the service rather than being worked out, which is
    // the whole point of preferring it over the file on disk.
    let expected = ISO8601DateFormatter().date(from: "2026-08-24T09:30:00Z")
    check(reading?.fiveHourReset == expected, "the reset time is exactly what was sent")

    check(UsageAPI.reading(from: ["seven_day": ["utilization": 10]]) == nil,
          "an answer without the five-hour window is refused")
    check(UsageAPI.reading(from: [:]) == nil, "and so is an empty one")

    let epoch: [String: Any] = ["five_hour": ["utilization": 5, "resets_at": 1_800_000_000]]
    check(UsageAPI.reading(from: epoch)?.fiveHourReset != nil, "epoch seconds parse too")

    let missingWeek: [String: Any] = ["five_hour": ["utilization": 5]]
    check(UsageAPI.reading(from: missingWeek)?.week == 0,
          "a missing weekly window reads as nothing used, not as a failure")
}

section("Borrowed credentials")

do {
    // The endpoint is Anthropic's own; a token must never go anywhere else.
    check(UsageAPI.endpoint.host == "api.anthropic.com", "usage is read from Anthropic")
    check(UsageAPI.endpoint.scheme == "https", "over https")

    // A profile with no cached login yields nothing rather than throwing.
    let bare = makeProfile("Claude-NoLogin", account: "AAAA")
    check((try? ClaudeCredentials.token(for: bare, prompting: .no)) ?? nil == nil,
          "a profile with no stored login has no token")

    // Starting a session goes to Anthropic, per profile, with a real model id.
    check(SessionStarter.endpoint.host == "api.anthropic.com", "sessions start against Anthropic")
    check(SessionStarter.endpoint.scheme == "https", "over https as well")
    check(SessionStarter.model.hasPrefix("claude-haiku"), "and on Haiku, the cheapest way to open a window")

    check(SessionStarter.start(profile: bare, interactive: false) != nil,
          "a profile with no login cannot start a session")

    // The two scopes are asked for separately: reading usage needs one, sending
    // a message needs the other.
    check(ClaudeCredentials.usageScope != ClaudeCredentials.inferenceScope,
          "reading usage and running the model are different permissions")
}

// MARK: - One place the version is written down

section("Version")

do {
    let repo = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
    let version = ((try? String(contentsOf: repo.appending(path: "VERSION"), encoding: .utf8)) ?? "")
        .trimmingCharacters(in: .whitespacesAndNewlines)

    check(version.first?.isNumber == true && version.split(separator: ".").count >= 2,
          "the VERSION file holds something that reads as a version")

    // A second copy of the number in the source plist is a second copy to
    // forget, and an update feed cannot tell two builds answering to one
    // number apart.
    let plist = (try? String(contentsOf: repo.appending(path: "Resources/Info.plist"),
                             encoding: .utf8)) ?? ""
    check(plist.contains("<string>0.0.0</string>"),
          "the source plist carries only the placeholder the build overwrites")
    check(!plist.contains("<string>\(version)</string>"),
          "so the number itself lives in exactly one file")

    // Outside a bundle there is no version to inherit, and the placeholder is
    // what says so rather than a plausible-looking number.
    check(Installer.graftVersion == "0.0.0",
          "a shortcut built by something that is not an app is stamped unmistakably")
}

// MARK: - Updating itself

section("Updates")

do {
    let repo = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
    func read(_ path: String) -> String {
        (try? String(contentsOf: repo.appending(path: path), encoding: .utf8)) ?? ""
    }
    let plist = read("Resources/Info.plist")

    // The feed is baked into every copy ever shipped and cannot be changed for
    // one already installed, so it is worth being sure about.
    check(plist.contains("<key>SUFeedURL</key><string>https://"),
          "the update feed is fetched over https")
    check(plist.contains("<key>SUPublicEDKey</key>") && !plist.contains("<key>SUPublicEDKey</key><string></string>"),
          "and an update is refused unless it is signed by the key shipped alongside it")

    // Two places name the account, and a rename that touched only one would
    // leave the feed advertising downloads nobody can reach.
    let release = read("release.sh")
    let owner = "aaditya-v-more"
    check(plist.contains("https://\(owner).github.io/"),
          "the feed and the release assets agree on whose account they are")
    check(release.contains("github.com/\(owner)/claude-graft/releases/download/"),
          "so a download the feed advertises resolves to something")

    // Fetched over the wire, then trusted to verify every future update.
    let fetch = read("Tools/fetch-sparkle.sh")
    check(fetch.contains("SHA256=\"") && fetch.contains("VERSION=\""),
          "the update framework is pinned to one version and one checksum")

    // Present, they need entitlements and signing this app does not do, and
    // launchd refuses them for a non-sandboxed app.
    check(read("build.sh").contains("XPCServices"),
          "Sparkle's XPC services are stripped from the embedded copy")

    // Sparkle starts the new version by ending this one, and this app refuses a
    // terminate nobody asked for. Missing that is what left an update installed
    // and never started, twice. Sparkle's own header says the relaunch hook may
    // not be called, so one place to set the flag is not enough.
    let updater = read("Sources/App/Updater.swift")
    let marks = updater.components(separatedBy: "isRelaunchingForUpdate = true").count - 1
    check(marks >= 3,
          "the terminate is marked as asked-for at every hook before an install, not just one")
    check(updater.contains("automaticallyDownloadsUpdates = true"),
          "an update installs itself rather than waiting to be noticed")
    check(updater.contains("immediateInstallHandler()"),
          "and now, rather than on a quit that a menu bar app may not see for weeks")

    // An enclosure without a signature is one every client refuses, and
    // generate_appcast omits it silently when the key does not match.
    let appcast = read("docs/appcast.xml")
    if !appcast.isEmpty {
        let enclosures = appcast.components(separatedBy: "<enclosure ").dropFirst()
        check(!enclosures.isEmpty && enclosures.allSatisfy { $0.contains("sparkle:edSignature") },
              "every download the published feed offers carries a signature")
    }
}

// MARK: - Who is allowed to put a keychain dialog on screen

section("Asking for keychain access")

do {
    // Every case reads silently first, so none of this is reached while the
    // build is still on the item's ACL. It decides what happens the first time
    // a new build is not.
    check(!ClaudeCredentials.mayRaiseDialog(.no, alreadyAsked: false, declined: false),
          "a pass that must stay silent stays silent even with the keychain shut to it")
    check(ClaudeCredentials.mayRaiseDialog(.onceIfShut, alreadyAsked: false, declined: false),
          "the first pass to find it shut asks, rather than showing worse figures without saying why")
    check(!ClaudeCredentials.mayRaiseDialog(.onceIfShut, alreadyAsked: true, declined: false),
          "having asked once, the next thirty-second tick does not ask again")
    check(!ClaudeCredentials.mayRaiseDialog(.onceIfShut, alreadyAsked: false, declined: true),
          "and nothing asks again once the answer has been no")
    check(ClaudeCredentials.mayRaiseDialog(.yes, alreadyAsked: true, declined: true),
          "but pressing Refresh Usage asks whatever has gone before, since that is the way back in")

    // The dropdown tells someone to choose Always Allow. After a decline there
    // is no dialog to choose it in, so it must say something else.
    check(ClaudeCredentials.Failure.keychainDeclined.errorDescription
            != ClaudeCredentials.Failure.noKeychainAccess.errorDescription,
          "being declined reads differently from never having been asked")

    // A dialog nobody asked for is only fair while someone is looking at the
    // app. The monitor starts willing; the app turns it off for a login launch.
    let monitor = UsageMonitor()
    check(monitor.mayPromptUnasked, "a monitor is willing to ask until something says otherwise")
}

// MARK: - Nothing asks for a session by itself

section("Starting a session")

do {
    let account = makeProfile("Claude-Session", account: nil)

    check(SessionStarter.claim(account), "the first press takes the account")
    check(!SessionStarter.claim(account), "a second press while that one is still open is turned away")
    SessionStarter.release(account)
    check(SessionStarter.claim(account), "and the account is free again once it finishes")
    SessionStarter.release(account)

    // A view refreshes when it appears and again every thirty seconds. A
    // session wired into one of those would open a five-hour window on every
    // account, over and over, without anybody pressing anything.
    let appSource = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appending(path: "Sources/App")
    let swiftFiles = ((try? fm.contentsOfDirectory(at: appSource, includingPropertiesForKeys: nil)) ?? [])
        .filter { $0.pathExtension == "swift" }
        .sorted { $0.lastPathComponent < $1.lastPathComponent }
    check(swiftFiles.count > 5, "the app's own source is there to be read")

    let runsOnItsOwn = ["onAppear", "onReceive", "onChange", ".task", "Timer",
                        "asyncAfter", "FinishLaunching"]
    var callers: [String] = []
    var quiet: [String] = []
    var automatic: [String] = []
    for file in swiftFiles {
        guard let source = try? String(contentsOf: file, encoding: .utf8) else { continue }
        for line in source.split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            if line.contains("SessionStarter.start(") {
                callers.append(file.lastPathComponent)
                if !line.contains("interactive: true") { quiet.append(file.lastPathComponent) }
            }
            guard line.contains("startSession"), !line.contains("func startSession") else { continue }
            if runsOnItsOwn.contains(where: line.contains) { automatic.append(file.lastPathComponent) }
        }
    }

    check(callers.sorted() == ["MainProfileDetail.swift", "MenuBarContent.swift", "ShortcutDetail.swift"],
          "only the three Start Session buttons ask Anthropic for a session")
    check(automatic.isEmpty, "and nothing that runs on its own is wired to one")
    check(quiet.isEmpty, "each of the three says a person asked, which is what allows the keychain prompt")

    // `.yes` asks unconditionally. Nothing should name it directly: it is
    // reached by saying a person pressed something, which is auditable.
    var namesYes: [String] = []
    for file in swiftFiles {
        guard let source = try? String(contentsOf: file, encoding: .utf8) else { continue }
        if source.contains("prompting: .yes") { namesYes.append(file.lastPathComponent) }
    }
    check(namesYes.isEmpty, "and nothing reaches for the unconditional keychain prompt by hand")
}

// MARK: - How hard the endpoint is asked

section("Polling and backoff")

do {
    check(UsageMonitor.liveInterval == 300,
          "a tick nobody asked for reuses a figure up to five minutes old")

    // The cache used to be consulted before anyone asked who was waiting, so
    // Refresh Usage handed back whatever was already in hand and the figure
    // could sit five minutes stale with no way to hurry it.
    check(UsageMonitor.mayUseCache(age: 120, freshness: .cached),
          "two minutes old is fresh enough for a background pass")
    check(!UsageMonitor.mayUseCache(age: 120, freshness: .recent),
          "but not for someone who just asked to see the number")
    check(UsageMonitor.mayUseCache(age: 10, freshness: .recent),
          "asking twice in ten seconds still only asks the service once")
    check(UsageMonitor.recentInterval < UsageMonitor.liveInterval,
          "someone looking gets a shorter cache than a timer does")
    check(UsageMonitor.recentInterval >= 30,
          "with a floor, since a button can be pressed faster than a service wants to answer")

    // Without backoff a refused call would be retried on the next tick, turning
    // one failure into a hundred and twenty attempts an hour.
    let first = UsageMonitor.retryDelay(afterFailures: 1, retryAfter: nil)
    let second = UsageMonitor.retryDelay(afterFailures: 2, retryAfter: nil)
    let fifth = UsageMonitor.retryDelay(afterFailures: 5, retryAfter: nil)
    check(first >= 60, "the first failure waits at least a minute")
    check(second > first, "and each further one waits longer")
    check(fifth >= 1800, "settling at half an hour")
    check(UsageMonitor.retryDelay(afterFailures: 50, retryAfter: nil) == fifth,
          "the wait is capped rather than growing without end")

    check(UsageMonitor.retryDelay(afterFailures: 1, retryAfter: 900) == 900,
          "a Retry-After from the service is honoured exactly")
    check(UsageMonitor.retryDelay(afterFailures: 4, retryAfter: 30) >= 60,
          "a suspiciously short one is still floored at a minute")

    let now = Date()
    check(UsageMonitor.mayAttempt(now: now, until: nil, interactive: false, serverAsked: false),
          "nothing outstanding means go ahead")
    check(!UsageMonitor.mayAttempt(now: now, until: now.addingTimeInterval(300),
                                   interactive: false, serverAsked: false),
          "a background pass waits its turn")
    check(UsageMonitor.mayAttempt(now: now, until: now.addingTimeInterval(300),
                                  interactive: true, serverAsked: false),
          "a person pressing refresh may skip our own wait")
    check(!UsageMonitor.mayAttempt(now: now, until: now.addingTimeInterval(300),
                                   interactive: true, serverAsked: true),
          "but not one the service asked for")
    check(UsageMonitor.mayAttempt(now: now, until: now.addingTimeInterval(-1),
                                  interactive: false, serverAsked: true),
          "and an expired wait releases either way")
}

// MARK: - Telling instances apart

section("Which Claude is running")

func matches(_ pattern: String, _ command: String) -> Bool {
    guard let regex = try? NSRegularExpression(pattern: pattern) else { return false }
    let range = NSRange(command.startIndex..., in: command)
    return regex.firstMatch(in: command, range: range) != nil
}

do {
    let main = support.appending(path: "Claude")
    let second = support.appending(path: "Claude-2")

    let mainPattern = Graft.dataDirPattern(for: main)
    let secondPattern = Graft.dataDirPattern(for: second)

    let runningSecond = "/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=\(second.path)"
    // One profile's path sits inside another's, so an unanchored match made
    // every shorter-named profile look like it was running.
    check(matches(secondPattern, runningSecond), "a profile matches its own instance")
    check(!matches(mainPattern, runningSecond),
          "and not one whose folder merely starts with the same name")

    let runningMainWithFlag = "/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=\(main.path)"
    check(matches(mainPattern, runningMainWithFlag), "an exact path still matches")
    check(matches(mainPattern, runningMainWithFlag + " --other-flag"),
          "including when further arguments follow")

    // Paths land inside a regex, so anything special in them has to be quoted.
    let awkward = support.appending(path: "Claude (work)")
    check(matches(Graft.dataDirPattern(for: awkward),
                  "Claude --user-data-dir=\(awkward.path)"),
          "a folder name with brackets in it still matches itself")
}

do {
    // Claude launched normally carries no --user-data-dir at all, so the main
    // profile cannot be recognised by one.
    check(Graft.isDefaultInstance("/Applications/Claude.app/Contents/MacOS/Claude"),
          "a plain launch is the default instance")
    check(!Graft.isDefaultInstance(
            "/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=/tmp/Claude-2"),
          "one launched on a profile is not")
    check(!Graft.isDefaultInstance(
            "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper --type=gpu-process"),
          "nor is a helper process")
    check(!Graft.isDefaultInstance(
            "/Users/x/Library/Application Support/Claude/claude-code/2.1.237/claude.app/Contents/MacOS/claude -p hi"),
          "nor the bundled command line, whose path is lowercase")
}

// MARK: - What the bar shows

section("Menu bar figure")

func entry(_ name: String, fiveHour: Int, running: Bool, live: Bool = true,
           sampled: Date = Date()) -> UsageMonitor.Entry {
    UsageMonitor.Entry(name: name,
                       profile: support.appending(path: name),
                       usage: Graft.Usage(fiveHour: fiveHour, week: 0,
                                          organization: nil, sampled: sampled),
                       isRunning: running,
                       shortcut: nil,
                       isLive: live)
}

do {
    let monitor = UsageMonitor()

    // An idle account sitting at its limit should not shout over the one being
    // used: the figure follows whatever is open.
    monitor.setEntriesForTesting([entry("Claude", fiveHour: 100, running: false),
                                  entry("Claude 2", fiveHour: 46, running: true)])
    check(monitor.headlineEntry?.name == "Claude 2", "the open account is the one shown")
    check(monitor.headline == 46, "and its figure is the one in the bar")

    monitor.setEntriesForTesting([entry("Claude", fiveHour: 30, running: true),
                                  entry("Claude 2", fiveHour: 80, running: true)])
    check(monitor.headline == 80, "with several open, the tightest window wins")

    monitor.setEntriesForTesting([entry("Claude", fiveHour: 100, running: false),
                                  entry("Claude 2", fiveHour: 46, running: false)])
    check(monitor.headline == 100, "with nothing open it falls back to the tightest")

    // A figure read off disk hours ago says nothing about the window now.
    let old = Date().addingTimeInterval(-8 * 3600)
    monitor.setEntriesForTesting([entry("Claude", fiveHour: 99, running: false, live: false, sampled: old),
                                  entry("Claude 2", fiveHour: 12, running: false, live: false)])
    check(monitor.headline == 12, "a stale figure is left out")

    monitor.setEntriesForTesting([])
    check(monitor.headline == nil, "nothing to show when there is nothing to show")
}

section("Quitting")

do {
    // Measured by filling the bar until items overflowed: the ones macOS had no
    // room for came back at negative x, while still answering isVisible true.
    let screen = CGRect(x: 0, y: 0, width: 1710, height: 1073)
    check(MenuBarPlacement.isReachable(CGRect(x: 903, y: 1073, width: 36, height: 39),
                                       onAnyOf: [screen]),
          "an item the bar had room for is somewhere a pointer can reach")
    check(!MenuBarPlacement.isReachable(CGRect(x: -71, y: 1073, width: 45, height: 39),
                                        onAnyOf: [screen]),
          "one pushed off the end of a full bar is not")
    check(!MenuBarPlacement.isReachable(CGRect(x: -26, y: 1073, width: 45, height: 39),
                                        onAnyOf: [screen]),
          "and half of one hanging off the edge does not count as a way back")
    check(!MenuBarPlacement.isReachable(CGRect(x: 903, y: 1073, width: 36, height: 39),
                                        onAnyOf: []),
          "with no screen at all there is nowhere for it to be")

    check(!QuitPolicy.endsTheApp(askedFor: false, installingUpdate: false, systemGoingDown: false, menuBarShowing: true),
          "command-Q puts the window away and leaves the menu bar item reporting")
    check(QuitPolicy.endsTheApp(askedFor: true, installingUpdate: false, systemGoingDown: false, menuBarShowing: true),
          "Quit in the dropdown is the one that ends it")
    check(QuitPolicy.endsTheApp(askedFor: false, installingUpdate: false, systemGoingDown: true, menuBarShowing: true),
          "a logout is not something to argue with")

    // Without this the app would have no window, no item, and no way out.
    // Sparkle starts the new version by asking this one to terminate. Refusing
    // that leaves the old copy running with the update already staged, and the
    // window put away on the way past — which reads as the app quitting itself.
    check(QuitPolicy.endsTheApp(askedFor: false, installingUpdate: true,
                                systemGoingDown: false, menuBarShowing: true),
          "an update replacing the app is allowed to end it, or the new version never starts")
    check(!QuitPolicy.endsTheApp(askedFor: false, installingUpdate: false,
                                 systemGoingDown: false, menuBarShowing: true),
          "and nothing else about that changed: a plain terminate is still refused")

    check(QuitPolicy.endsTheApp(askedFor: false, installingUpdate: false, systemGoingDown: false, menuBarShowing: false),
          "with nothing in the bar to go back to, a quit is a quit")
}

print("\n\(checks - failures)/\(checks) checks passed")
if failures > 0 {
    print("\(failures) FAILED")
    exit(1)
}
