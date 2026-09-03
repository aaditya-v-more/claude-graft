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
// Nothing in a temporary directory can be made to run, and asking for real
// would answer differently depending on whether somebody had Claude open while
// the suite ran.
Graft.runningClaudesOverride = { [] }

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

/// The shape every released version left on disk when it shared chats: the
/// profile's own set moved to the hidden sibling, and a link where they were.
/// Built by hand because nothing in this app makes one any more — which is the
/// point of the tests that use it, since a shortcut out in the world still has
/// one and has to be able to migrate off it.
func linkTheOldWay(_ folder: URL, to target: URL) {
    let stash = Graft.stashURL(for: folder)
    if Graft.exists(folder) { try! fm.moveItem(at: folder, to: stash) }
    try! fm.createSymbolicLink(at: folder, withDestinationURL: target)
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
    check(Installer.builtBy(bundle) == Installer.graftVersion,
          "stamped with the version of Graft that wrote it, which is how a stale one is spotted")
    check(!Installer.refreshLauncher(for: shortcut),
          "so a shortcut this version wrote is left alone")

    // A shortcut left behind by an older Graft goes on behaving like that
    // Graft — the Dock, Finder and Spotlight run the binary in the bundle and
    // ask this app nothing — so the launcher inside it is replaced at launch.
    do {
        let plist = bundle.appending(path: "Contents/Info.plist")
        let aged = (try! String(contentsOf: plist, encoding: .utf8))
            .replacingOccurrences(of: Installer.graftVersion, with: "0.0.1")
        try! aged.write(to: plist, atomically: true, encoding: .utf8)
        try! Data("stale".utf8).write(to: bundle.appending(path: "Contents/MacOS/launcher"))

        check(Installer.builtBy(bundle) == "0.0.1", "a bundle remembers which Graft wrote it")
        check(Installer.refreshLauncher(for: shortcut), "and an older one is brought up to date")
        check(Installer.builtBy(bundle) == Installer.graftVersion,
              "restamped, so the next launch leaves it alone")
        let refreshed = try! Data(contentsOf: bundle.appending(path: "Contents/MacOS/launcher"))
        check(refreshed.count > 5, "carrying this version's launcher rather than the old one")
        check(sourcePath(ofBundle: bundle) == main.path,
              "and nothing else about the shortcut was rewritten")
    }
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
    check(chatsVisible(to: work) == ["local_mine.json", "local_shared.json"],
          "sharing a history merges the two rather than standing one in for the other")
    check(chatsVisible(to: main) == ["local_mine.json", "local_shared.json"],
          "and the profile lent from ends up holding both of them too")

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
    check(chatsVisible(to: work) == ["local_mine.json", "local_shared.json"],
          "grafting twice changes nothing")

    Graft.ungraft(work)
    check(chatsVisible(to: work) == ["local_mine.json"], "ungrafting gives back its own chats")
    check(!Graft.isSymlink(work.appending(path: "claude_desktop_config.json")),
          "and drops the links")

    // Same account: every organization it has is shared.
    let twin = makeProfile("Claude-Twin", account: "AAAA", org: "ORG-A")
    Graft.graft(from: main, into: twin)
    check(!Graft.isSymlink(twin.appending(path: "claude-code-sessions"))
            && Graft.isDirectory(twin.appending(path: "claude-code-sessions")),
          "the same account shares the whole store, as folders rather than a link")
    check(chatsVisible(to: twin) == ["local_mine.json", "local_shared.json"],
          "and sees everything that profile holds, merges it was lent included")

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

    check(chatsVisible(to: work) == ["local_mine.json"],
          "what the profile owned before the graft is what it gets back")
    check(Graft.exists(main.appending(path: "claude-code-sessions/AAAA/ORG-A/local_written_since.json")),
          "and a chat it wrote while borrowing is handed over rather than lost")
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
          "ungrafting folds the stash back in rather than abandoning it beside the copies")
    check(chatsVisible(to: work) == ["local_mine.json"],
          "so the profile is handed back what it owned")
    check(Graft.exists(main.appending(path: "claude-code-sessions/AAAA/ORG-A/local_written_since.json")),
          "and what it wrote while borrowing went to the profile it borrowed from")

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
    check(chatsVisible(to: fresh) == ["local_shared.json"],
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

    // Claude's own profile has no shortcut to ask on its behalf, so nothing
    // ever asked: opening it while a shortcut grafted from it was running gave
    // no warning at all, which is the pairing this warning exists for.
    let fromMain = store.chatStoreNeighbours(of: nil).map(\.name)
    check(fromMain.contains("From Main") && fromMain.contains("Also Main"),
          "Claude's own profile can ask who else is on its chats")
    check(!fromMain.contains("Claude"),
          "without counting itself among them")
    check(!fromMain.contains("Alone"),
          "and a profile keeping its own chats is still nobody's neighbour")
}

// The wording is shared because three buttons say it, and only one of the
// three used to say anything at all.
do {
    check(ChatConflict.message(sharers: ["Claude 2"]).contains("Claude 2 is already open"),
          "one other instance reads as a sentence")
    check(ChatConflict.message(sharers: ["Claude 2", "Work"]).contains("Claude 2 and Work are"),
          "so does a pair")
    check(ChatConflict.message(sharers: ["A", "B", "C"]).contains("A, B and C are"),
          "and a list longer than that")
    check(ChatConflict.message(sharers: ["Claude 2"]).contains("can lose messages"),
          "and it says what is actually at stake, not just who is open")
}

// A profile that is already open is not about to be opened: pressing Open on
// it brings its own Claude forward, adds no second reader, and the Claude it
// brings forward has been on those chats all along.
do {
    check(ChatConflict.sharersToAskAbout(profileIsOpen: false,
                                         openNeighbours: ["Claude"]) == ["Claude"],
          "opening a profile onto chats something else is reading is worth asking about")
    check(ChatConflict.sharersToAskAbout(profileIsOpen: true,
                                         openNeighbours: ["Claude"]).isEmpty,
          "but a profile already open is only being brought forward, so nothing is asked")
    check(ChatConflict.sharersToAskAbout(profileIsOpen: false, openNeighbours: []).isEmpty,
          "and with nobody else on those chats there was never a question")
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

    // One account, one figure. The two windows used to read the history file
    // for themselves while the menu bar showed what the endpoint had just said,
    // and the file is only written while that Claude runs — so a profile left
    // closed for days had the window reporting a week that had since reset,
    // beside a bar reporting the week actually in progress.
    check(monitor.entry(for: listed)?.usage?.fiveHour == 77,
          "the monitor answers for one profile by path")
    check(monitor.entry(for: support.appending(path: "Claude-Absent")) == nil,
          "and says nothing about a profile it does not carry")

    let views = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appending(path: "Sources/App")
    for view in ["ShortcutDetail.swift", "MainProfileDetail.swift"] {
        let source = (try? String(contentsOf: views.appending(path: view), encoding: .utf8)) ?? ""
        check(source.contains("UsageMonitor"),
              "\(view) is fed by the monitor the menu bar reads")
        check(!source.contains("Graft.usage("),
              "\(view) does not go back to the history file behind it")
        check(source.contains("usage.invalidate("),
              "\(view) drops the reading its own Start Session has just made wrong")
    }

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

    // Sparkle's interactive check puts up a panel and waits on Install and
    // Relaunch. The scheduled check installed silently and the button did not,
    // which is one app behaving two ways — and pressing a line that already
    // reads "version 1.0.6 is available" is not a request to be asked about it.
    check(updater.contains("checkForUpdatesInBackground()"),
          "the dropdown's check installs rather than asking")
    check(!updater.contains("updater.checkForUpdates()"),
          "so Sparkle's own panel is never what a press puts on screen")

    // The app carries links of its own to the site and the source, compiled
    // into copies that will never be built again. GitHub redirects a renamed
    // repository but gives Pages nothing, so a rename that missed one of these
    // would take it quietly dead.
    let links = read("Sources/App/Links.swift")
    let site = read("docs/index.html")
    let readme = read("README.md")
    check(links.contains("static let owner = \"\(owner)\""),
          "the app sends people to the same account the feed does")
    check(site.contains("github.com/\(owner)/claude-graft"),
          "and so does the site")

    // The tip jar is a second account under a second spelling. GitHub Sponsors
    // cannot pay into an Indian account, so the money goes through Ko-fi, whose
    // handle carries no hyphens — neither name can be built out of the other,
    // which is why all four places are read rather than derived from one.
    let kofi = "aadityavmore"
    check(links.contains("static let kofiAccount = \"\(kofi)\"") && links.contains("https://ko-fi.com/"),
          "the app's support link goes to the tip jar")
    check(read(".github/FUNDING.yml").contains("ko_fi: \(kofi)"),
          "and so does the button at the top of the repository")
    check(site.contains("https://ko-fi.com/\(kofi)"),
          "and the one on the site")
    check(readme.contains("https://ko-fi.com/\(kofi)"),
          "and the one in the README")

    // A sponsor page nobody can pay into is worse than no link at all: it reads
    // as an oversight rather than a decision, and it takes a press to find out
    // that the money has nowhere to go.
    check(![links, site, readme].contains(where: { $0.contains("github.com/sponsors") }),
          "and nothing still offers the sponsor page that cannot take the money")

    // An enclosure without a signature is one every client refuses, and
    // generate_appcast omits it silently when the key does not match.
    let appcast = read("docs/appcast.xml")
    if !appcast.isEmpty {
        let enclosures = appcast.components(separatedBy: "<enclosure ").dropFirst()
        check(!enclosures.isEmpty && enclosures.allSatisfy { $0.contains("sparkle:edSignature") },
              "every download the published feed offers carries a signature")

        // generate_appcast prunes to a few entries per branch point by default,
        // so each release quietly dropped the oldest one. Nothing broke — the
        // newest is what gets offered — but the signature in an entry is over
        // an archive that is never built again, so a dropped entry is not one
        // that can be regenerated later.
        check(release.contains("--maximum-versions 0"),
              "the feed keeps every release it has ever carried")
        check(release.contains("[ \"$AFTER\" -eq \"$((BEFORE + 1))\" ]"),
              "and a run that adds one while losing another is caught, not waved through")
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

// MARK: - Opening a Claude onto chats something else is already reading

section("Warning before a shared store is opened")

do {
    let sources = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appending(path: "Sources/App")
    func read(_ name: String) -> String {
        (try? String(contentsOf: sources.appending(path: name), encoding: .utf8)) ?? ""
    }

    // The dropdown went straight to openApplication, so the same click that
    // asked from the window asked nothing from the menu bar.
    for file in ["MenuBarContent.swift", "MainProfileDetail.swift", "ShortcutDetail.swift"] {
        check(read(file).contains("ChatConflict"),
              "\(file) asks before opening a profile something else is on")
    }

    // A popover gives up key window the moment anything else appears, taking a
    // sheet with it, which is why the dropdown's question is an NSAlert.
    check(read("MenuBarContent.swift").contains("askInPopover"),
          "and the dropdown asks in the one way a popover can")
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

    // Opening the dropdown starts a pass, so a figure a few seconds old was
    // always the one Refresh Usage found — and the minute-long floor meant it
    // handed that straight back and asked nobody anything.
    check(!UsageMonitor.mayUseCache(age: 30, freshness: .now),
          "someone who pressed the button gets the service asked, not the last answer")
    check(UsageMonitor.mayUseCache(age: 1, freshness: .now),
          "though a double-click is still one request")
    check(UsageMonitor.nowInterval < UsageMonitor.recentInterval,
          "pressing for a figure beats merely looking at one")

    // A press landing inside the pass the dropdown itself started used to be
    // dropped on the floor, which is indistinguishable from a button that does
    // nothing at all.
    check(UsageMonitor.arrival(passInFlight: false, interactive: true) == .start,
          "with nothing running, a press goes straight out")
    check(UsageMonitor.arrival(passInFlight: false, interactive: false) == .start,
          "and so does a tick")
    check(UsageMonitor.arrival(passInFlight: true, interactive: true) == .queue,
          "a press arriving mid-pass waits for its turn rather than being lost")
    check(UsageMonitor.arrival(passInFlight: true, interactive: false) == .drop,
          "but a tick is worth dropping, since the pass will answer it anyway")

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

// MARK: - Opening a profile that is already open

section("Showing the Claude that is already there")

do {
    // Claude Desktop never takes Electron's single-instance lock, so a second
    // launch on one --user-data-dir is two processes writing one chat store.
    // Pressing Open twice was all it took, so a profile that is already open
    // has to be shown rather than opened.
    let two = support.appending(path: "Claude-2")
    let running = "/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=\(two.path)"
    check(Graft.carriesDataDir(running, two), "a Claude on this profile is recognised as being on it")
    check(Graft.carriesDataDir(running + " --enable-logging", two),
          "including when further arguments follow")

    // Profile paths are prefixes of each other, and an unanchored match makes
    // every shorter-named profile look like it is the one running.
    let one = support.appending(path: "Claude")
    check(!Graft.carriesDataDir(running, one),
          "and a shorter profile whose path it starts with is not that profile")
    check(!Graft.carriesDataDir("/Applications/Claude.app/Contents/MacOS/Claude", two),
          "nor is a Claude carrying no profile at all")

    // Only the browser process answers to being brought forward. Every helper
    // Claude starts repeats the same --user-data-dir, which is why the pid
    // cannot come from the pgrep that answers isRunning.
    let helper = "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper --type=renderer --user-data-dir=\(two.path)"
    check(Graft.carriesDataDir(helper, two), "a helper carries its profile too")
    check(!Graft.isClaudeProcess(helper), "but a helper is not the Claude to show")
    check(Graft.isClaudeProcess(running), "the browser process is")
    check(!Graft.isClaudeProcess(
            "/Users/x/Library/Application Support/Claude/claude-code/2.1.237/claude.app/Contents/MacOS/claude -p hi"),
          "and neither is the bundled command line")

    // ps rather than pgrep, which leaves out its own ancestors: a Claude that
    // started the process doing the asking is one pgrep will not report.
    let mine = Graft.processes().first { $0.id == getpid() }
    check(mine != nil, "the process list finds the process reading it")
    check(mine?.command.isEmpty == false, "and pairs every pid with the command that started it")

    check(Graft.processIdentifier(of: support.appending(path: "Claude-NeverLaunched")) == nil,
          "a profile nothing is running on has no Claude to show")

    // Claude launched the ordinary way carries no --user-data-dir at all, and
    // that absence is the only mark the main profile has: naming it would
    // start a Claude nothing afterwards recognises as the main one.
    let mainArguments = Graft.launchArguments(for: Graft.mainProfile)
    check(!mainArguments.contains { $0.contains("user-data-dir") },
          "the main profile is launched with no profile named")
    check(Graft.launchArguments(for: two).contains("--user-data-dir=\(two.path)"),
          "and any other profile is launched with its own")
    check(mainArguments.contains("-n"),
          "always as a new instance, since LaunchServices reopens whichever started first")
}

do {
    let sources = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appending(path: "Sources")
    func read(_ name: String) -> String {
        (try? String(contentsOf: sources.appending(path: name), encoding: .utf8)) ?? ""
    }

    // Handing Claude's own bundle to LaunchServices reopens whichever instance
    // of it started first, and with a shortcut running that is a grafted
    // profile — so Open on the main profile brought a graft forward instead.
    for file in ["App/MainProfileDetail.swift", "App/MenuBarContent.swift"] {
        check(!read(file).contains("openApplication(at: Graft.claudeApp"),
              "\(file) does not ask LaunchServices to pick an instance of Claude")
    }

    // Everything that opens a profile goes through the rule, so there is one
    // place that knows a running profile is shown rather than opened again.
    let appSources = (try? fm.contentsOfDirectory(at: sources.appending(path: "App"),
                                                  includingPropertiesForKeys: nil)) ?? []
    var launchers: [String] = []
    for file in appSources where file.pathExtension == "swift" {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
        if text.contains("Graft.launch(") { launchers.append(file.lastPathComponent) }
    }
    check(launchers.isEmpty, "and nothing in the app launches a Claude without asking who is there")
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

section("Session records")

// A Claude Desktop signed into one account will not write the record for a
// session the command line owns to another account, and the command line
// holds one login for the whole machine — so a session started from a
// grafted profile closes without a record and vanishes from every sidebar
// while its transcript sits whole on disk. The sweep below writes the
// missing record; everything in it comes out of the transcript.

Graft.claudeProjectsOverride = root.appending(path: "claude-projects")
let ownerAccount = "0a0a0a0a-0a0a-4a0a-8a0a-0a0a0a0a0a0a"
let ownerOrg = "0b0b0b0b-0b0b-4b0b-8b0b-0b0b0b0b0b0b"
let borrowerAccount = "0c0c0c0c-0c0c-4c0c-8c0c-0c0c0c0c0c0c"
let borrowerOrg = "0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d"

/// One transcript, laid out the way the command line writes them: a bridge
/// line naming the owner, a prompt, an answer, and a title line.
func makeTranscript(session: String,
                    bridge: String? = "cse_01BridgeSession0000",
                    owner: String = ownerAccount,
                    org: String = ownerOrg,
                    title: String? = nil,
                    spoke: Bool = true,
                    first: String = "2026-08-29T17:00:00.000Z",
                    last: String = "2026-08-29T17:00:10.000Z",
                    extra: [String] = []) -> URL {
    let dir = Graft.claudeProjects.appending(path: "-Users-test-work")
    try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    var lines: [String] = []
    if let bridge {
        lines.append("{\"type\":\"bridge-session\",\"sessionId\":\"\(session)\","
                     + "\"bridgeSessionId\":\"\(bridge)\",\"lastSequenceNum\":0,"
                     + "\"ownerAccountUuid\":\"\(owner)\",\"ownerOrganizationUuid\":\"\(org)\"}")
    }
    if spoke {
        lines.append("{\"parentUuid\":null,\"isSidechain\":false,\"promptId\":\"prompt-one\","
                     + "\"cwd\":\"/Users/test/work\",\"permissionMode\":\"auto\",\"gitBranch\":\"main\","
                     + "\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"},"
                     + "\"uuid\":\"u-1\",\"timestamp\":\"\(first)\"}")
        lines.append("{\"parentUuid\":\"u-1\",\"isSidechain\":false,\"cwd\":\"/Users/test/work\","
                     + "\"permissionMode\":\"auto\",\"gitBranch\":\"main\",\"type\":\"assistant\","
                     + "\"message\":{\"id\":\"m-1\",\"model\":\"claude-opus-5\",\"content\":[{\"text\":\"hi\",\"type\":\"text\"}]},"
                     + "\"uuid\":\"a-1\",\"timestamp\":\"\(last)\",\"effort\":\"max\"}")
    } else {
        // A session opened and closed with nothing said in it still leaves a
        // transcript: a bridge line, a stamp, and no conversation at all.
        lines.append("{\"cwd\":\"/Users/test/work\",\"permissionMode\":\"auto\","
                     + "\"type\":\"file-history-summary\",\"timestamp\":\"\(last)\"}")
    }
    if let title {
        lines.append("{\"type\":\"custom-title\",\"customTitle\":\"\(title)\",\"sessionId\":\"\(session)\"}")
    }
    lines += extra
    let file = dir.appending(path: "\(session).jsonl")
    try! lines.joined(separator: "\n").write(to: file, atomically: true, encoding: .utf8)
    return file
}

do {
    let facts = Graft.sessionFacts(inTranscriptAt: makeTranscript(session: "11111111-1111-4111-8111-111111111111",
                                                                  title: "Test 4"),
                                   cliSessionId: "11111111-1111-4111-8111-111111111111")
    check(facts?.title == "Test 4", "the title comes from the title line")
    check(facts?.ownerAccount == ownerAccount && facts?.ownerOrganization == ownerOrg,
          "the owner and the organization come from the bridge line")
    check(facts?.cwd == "/Users/test/work" && facts?.permissionMode == "auto",
          "so do the working directory and the permission mode")
    check(facts?.model == "claude-opus-5" && facts?.effort == "max",
          "the model and the effort come from the answer")
    check(facts?.bridgeIds == ["cse_01BridgeSession0000"],
          "the bridge id is the one thing a record cannot live without")
    check(facts?.branches == ["main"], "the branch the session ran on is written down")
    if let facts {
        check(facts.lastActivityAt - facts.createdAt == 10_000 && facts.createdAt > 1_700_000_000_000,
              "the times are the transcript's first and last, in milliseconds")
    }
    check(facts?.prompts == 1, "one prompt is one turn")

    // Tool results carry their prompt's id, so a session heavy with them is
    // not a session of many turns.
    let busy = Graft.sessionFacts(inTranscriptAt: makeTranscript(
        session: "22222222-2222-4222-8222-222222222222",
        extra: ["{\"parentUuid\":\"a-1\",\"isSidechain\":false,\"promptId\":\"prompt-one\","
                + "\"cwd\":\"/Users/test/work\",\"type\":\"user\","
                + "\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"t-1\",\"type\":\"tool_result\",\"content\":\"ok\"}]},"
                + "\"uuid\":\"u-2\",\"timestamp\":\"2026-08-29T17:00:11.000Z\"}"]),
        cliSessionId: "22222222-2222-4222-8222-222222222222")
    check(busy?.prompts == 1, "tool results share their prompt's id and count as no turn of their own")
    check(busy?.title == "New session", "a session never named is listed under the name Claude gives unnamed ones")

    check(Graft.sessionFacts(inTranscriptAt: makeTranscript(session: "33333333-3333-4333-8333-333333333333",
                                                             bridge: nil),
                             cliSessionId: "33333333-3333-4333-8333-333333333333") == nil,
          "a transcript the terminal ran has no bridge line, and nothing is missing from it")

    if let facts {
        let record = Graft.sessionRecord(for: facts)
        check(record["sessionId"] as? String == "local_11111111-1111-4111-8111-111111111111",
              "the record is named for its session, so a later pass can only find it in one place")
        check((record["bridgeSessionIds"] as? [String]) == ["session_01BridgeSession0000"],
              "the bridge id is renamed the way records spell it: cse_ becomes session_")
        check(record["cliSessionId"] as? String == "11111111-1111-4111-8111-111111111111"
              && record["isArchived"] as? Bool == false && record["titleSource"] as? String == "auto",
              "the rest of the record says what Claude's own records say about a session")
    }
}

do {
    let facts = Graft.SessionFacts(cliSessionId: "s-1", bridgeIds: ["cse_x"],
                                   ownerAccount: ownerAccount, ownerOrganization: ownerOrg,
                                   title: "T", cwd: "/w", createdAt: 0, lastActivityAt: 1_000,
                                   model: "m", effort: "low", permissionMode: "auto",
                                   prompts: 1, branches: [])
    let then = Date(timeIntervalSince1970: 1)
    let later = then.addingTimeInterval(10 * 60)
    let owner = URL(fileURLWithPath: "/tmp/nowhere")

    check(Graft.sessionFiling(facts: nil, recorded: [], withdrawn: [], deletions: [],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .notADesktopSession,
          "a transcript with no bridge line is not missing anything")
    check(Graft.sessionFiling(facts: facts, recorded: ["s-1"], withdrawn: [], deletions: [],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .alreadyRecorded,
          "a session some Claude already wrote a record for is left alone")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: ["s-1"], deletions: [],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .withdrawn,
          "a session deleted in a sidebar once is never brought back")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [],
                              deletions: [1_000 + 30_000],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .withdrawn,
          "a session that had just gone quiet before a deletion marker is the one the marker took")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [],
                              deletions: [1_000 + 10 * 60_000],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .file,
              "a marker long after the session closed is about some other session")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [],
                              deletions: [1_000 - 5 * 60_000],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .file,
          "and a marker from before the session existed says nothing about it")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [], deletions: [],
                              lastWrite: later, ownerProfile: owner, now: later, quietWindow: 300) == .tooRecent,
          "a transcript written moments ago waits, in case Claude is about to write the record itself")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [], deletions: [],
                              lastWrite: later, ownerProfile: owner, ownerIsRunning: false,
                              now: later, quietWindow: 300) == .file,
          "but with no Claude up on that account there is nobody to wait for, and it files at once")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [], deletions: [],
                              lastWrite: then, ownerProfile: nil, now: later, quietWindow: 300) == .noOwnerProfile,
          "a session whose owner lives on no profile here waits for one to appear")
    check(Graft.sessionFiling(facts: facts, recorded: [], withdrawn: [], deletions: [],
                              lastWrite: then, ownerProfile: owner, now: later, quietWindow: 300) == .file,
          "with everything in place, the record is written")
}

do {
    let early = Graft.SessionFacts(cliSessionId: "s-2", bridgeIds: ["cse_x"],
                                  ownerAccount: ownerAccount, ownerOrganization: ownerOrg,
                                  title: "T", cwd: "/w", createdAt: 0, lastActivityAt: 1_000,
                                  model: "m", effort: "low", permissionMode: "auto",
                                  prompts: 1, branches: [])
    var later = early
    later.title = "T, continued"

    check(Graft.sessionUpdateDecision(authored: nil, facts: later, diskMatchesAuthored: true) == .leave,
          "a record this app never wrote is not this app's to bring up to date")
    check(Graft.sessionUpdateDecision(authored: early, facts: early, diskMatchesAuthored: true) == .leave,
          "a record that already says what the transcript says is left as it is")
    check(Graft.sessionUpdateDecision(authored: early, facts: later, diskMatchesAuthored: true) == .refresh,
          "a record behind its transcript, still holding what this app wrote, is brought up")
    check(Graft.sessionUpdateDecision(authored: early, facts: later, diskMatchesAuthored: false) == .takenOver,
          "once something else has rewritten the record, this app never touches it again")
}

do {
    // The world as the machine holds it: a profile on the owner's account,
    // and a second profile grafted onto it, its organization directory a
    // link into the first.
    let home = makeProfile("Claude-3", account: ownerAccount, org: ownerOrg)
    let second = makeProfile("Claude-2", account: borrowerAccount, org: borrowerOrg)
    let homeOrg = home.appending(path: "claude-code-sessions")
        .appending(path: ownerAccount).appending(path: ownerOrg)
    let secondOrg = second.appending(path: "claude-code-sessions")
        .appending(path: borrowerAccount).appending(path: borrowerOrg)
    try! fm.removeItem(at: secondOrg)
    try! fm.createSymbolicLink(atPath: secondOrg.path, withDestinationPath: homeOrg.path)

    // Everything the sweep will be shown. The parser fixtures are gone, so
    // what is here is only the story this pass has to get right.
    try? fm.removeItem(at: Graft.claudeProjects)

    // The lost session: owned by the account Claude-3 holds, started from
    // the grafted profile, which is signed into another — so no Claude ever
    // wrote its record.
    let lostId = "44444444-4444-4444-8444-444444444444"
    _ = makeTranscript(session: lostId, title: "Lost work")
    let lostRecord = homeOrg.appending(path: "local_\(lostId).json")

    // Its twin, deleted by hand before Graft ever ran: no record anywhere,
    // and a marker carrying nothing but the moment of the press.
    let twinId = "55555555-5555-4555-8555-555555555555"
    let twin = makeTranscript(session: twinId, title: "Deleted by hand",
                              first: "2026-08-29T16:40:00.000Z",
                              last: "2026-08-29T16:40:10.000Z")
    if let twinFacts = Graft.sessionFacts(inTranscriptAt: twin, cliSessionId: twinId) {
        try! "\(Int(twinFacts.lastActivityAt + 30_000))"
            .write(to: homeOrg.appending(path: "deleted_66666666-6666-4666-8666-666666666666"),
                   atomically: true, encoding: .utf8)
    }

    // A session owned by the other account, whose record belongs on the far
    // side of the graft link.
    let borrowedId = "77777777-7777-4777-8777-777777777777"
    _ = makeTranscript(session: borrowedId, owner: borrowerAccount, org: borrowerOrg,
                       title: "From the other account")

    // And a session whose owner no profile on this machine holds.
    let strangerId = "88888888-8888-4888-8888-888888888888"
    let strangerAccount = "99999999-9999-4999-8999-999999999999"
    _ = makeTranscript(session: strangerId, owner: strangerAccount,
                       org: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                       title: "Stranger's session")

    // A session opened and closed with nothing said in it: a bridge line, a
    // stamp, and no conversation to lose.
    let quietId = "10101010-1010-4101-8101-101010101010"
    _ = makeTranscript(session: quietId, spoke: false)

    let filing = [Graft.mainProfile, home, second]

    // These transcripts were written a moment ago, and it makes no difference:
    // no Claude on either owner's account is running, so nothing is about to
    // write these records but this sweep, and there is nothing to wait for.
    let first = Graft.fileMissingSessionRecords(filingInto: filing, now: Date())
    check(Set(first.map(\.title)) == ["Lost work", "From the other account"],
          "with no Claude up on the owner's account, a session that closed without a record gets one at once")
    check(Graft.exists(lostRecord),
          "the record lands in the owner's organization, where the session's own window and everything grafted onto it lists it")
    let record = (try? Data(contentsOf: lostRecord))
        .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    check(record?["cliSessionId"] as? String == lostId
          && record?["sessionId"] as? String == "local_\(lostId)",
          "the record names its session both of the ways the records do")
    check((record?["bridgeSessionIds"] as? [String]) == ["session_01BridgeSession0000"],
          "the bridge id is spelled the way records spell it")
    check(!Graft.exists(homeOrg.appending(path: "local_\(twinId).json")),
          "the session deleted by hand before the first pass is not brought back")
    check(Graft.exists(secondOrg.appending(path: "local_\(borrowedId).json")),
          "a record filed through a graft lands where the link points, which is where both windows read")
    check(!Graft.exists(homeOrg.appending(path: "local_\(quietId).json")),
          "a session with nothing said in it gets no record")

    // A pass that finds everything already in place files nothing.
    check(Graft.fileMissingSessionRecords(filingInto: filing,
                                          now: Date().addingTimeInterval(90)).isEmpty,
          "a session already recorded is left alone, however many passes go by")

    // The session carries on: another prompt, another answer, a new name.
    // The record follows while it is still this app's to move.
    _ = makeTranscript(session: lostId, title: "Lost work, continued",
                       last: "2026-08-29T17:05:00.000Z",
                       extra: ["{\"parentUuid\":\"a-1\",\"isSidechain\":false,\"promptId\":\"prompt-two\","
                               + "\"cwd\":\"/Users/test/work\",\"type\":\"user\","
                               + "\"message\":{\"role\":\"user\",\"content\":\"more\"},"
                               + "\"uuid\":\"u-3\",\"timestamp\":\"2026-08-29T17:04:50.000Z\"}",
                               "{\"parentUuid\":\"u-3\",\"isSidechain\":false,\"cwd\":\"/Users/test/work\","
                               + "\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5\"},"
                               + "\"uuid\":\"a-2\",\"timestamp\":\"2026-08-29T17:05:00.000Z\"}"])
    check(Graft.fileMissingSessionRecords(filingInto: filing,
                                          now: Date().addingTimeInterval(120)).isEmpty,
          "a transcript moving on is not a recovery, so nothing is reported as filed")
    let carried = (try? Data(contentsOf: lostRecord))
        .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    check(carried?["title"] as? String == "Lost work, continued"
          && carried?["completedTurns"] as? Int == 2,
          "but the record follows its session: the new name and the second turn are in it")

    // Claude takes the session back: the owner's window opened it, and its
    // own record went down over the one this app filed. What a transcript
    // cannot recover must not be flattened away.
    try! ("{\"cliSessionId\":\"\(lostId)\",\"remoteMcpServersConfig\":"
        + "[{\"name\":\"a server the transcript never heard of\"}],\"title\":\"Lost work\"}")
        .write(to: lostRecord, atomically: true, encoding: .utf8)
    _ = makeTranscript(session: lostId, title: "Lost work, carried further",
                       last: "2026-08-29T17:10:00.000Z")
    check(Graft.fileMissingSessionRecords(filingInto: filing,
                                          now: Date().addingTimeInterval(180)).isEmpty,
          "a record rewritten by Claude is left exactly as Claude wrote it")
    check((try? String(contentsOf: lostRecord, encoding: .utf8))?
              .contains("a server the transcript never heard of") == true,
          "what the host wrote is still there word for word")
    check(Graft.loadSessionRecordState().authored[lostId] == nil,
          "and the sweep stops claiming the record as its own")

    // Deleted in a sidebar: the record, removed by hand the way Claude
    // removes one, marker and all.
    try! fm.removeItem(at: homeOrg.appending(path: "local_\(borrowedId).json"))
    try! "1788024768893"
        .write(to: homeOrg.appending(path: "deleted_\(borrowedId)"), atomically: true, encoding: .utf8)
    check(Graft.fileMissingSessionRecords(filingInto: filing,
                                          now: Date().addingTimeInterval(240)).isEmpty,
          "the sweep saw the record go, and does not put it back")
    check(!Graft.exists(secondOrg.appending(path: "local_\(borrowedId).json")),
          "the delete in the sidebar sticks, through the link and all")
    let withdrawnState = Graft.loadSessionRecordState()
    check(withdrawnState.withdrawn.contains(borrowedId),
          "the withdrawal is written down, so no later pass forgets it")
    check(withdrawnState.authored[borrowedId] == nil,
          "and a withdrawn session's authored copy goes with it")

    // The owner's account turns up on a new profile, and the session that
    // had nowhere to go now goes there.
    let late = makeProfile("Claude-4", account: strangerAccount,
                           org: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
    let fifth = Graft.fileMissingSessionRecords(filingInto: filing + [late],
                                                now: Date().addingTimeInterval(300))
    check(Set(fifth.map(\.title)) == ["Stranger's session"],
          "a session whose owner arrived on a later pass is filed when the profile holding the account appears")
    check(!Graft.exists(homeOrg.appending(path: "local_\(twinId).json")),
          "and the deletion marker holds its session back on every pass, not just the first")

    // A state file from a version that kept no authored records still
    // loads, claiming nothing as this app's to move.
    let current = Graft.loadSessionRecordState()
    try! JSONSerialization.data(withJSONObject: ["records": current.records,
                                                 "withdrawn": current.withdrawn])
        .write(to: Graft.sessionRecordStateFile, options: .atomic)
    check(Graft.loadSessionRecordState().authored.isEmpty,
          "a state file without an authored map loads with nothing claimed as authored")
    check(Graft.fileMissingSessionRecords(filingInto: filing + [late],
                                          now: Date().addingTimeInterval(360)).isEmpty,
          "and a pass over that state touches nothing it does not own")
    check((try? String(contentsOf: lostRecord, encoding: .utf8))?
              .contains("a server the transcript never heard of") == true,
          "the host's record is still there, passes later")
}

do {
    // A store out of sight is not a store somebody emptied. Every directory
    // in the walk is read with a `try?`, so a profile that has been deleted,
    // or stashed behind a graft, or is waiting on a permission, comes back
    // holding nothing at all — and reading that as a sidebar cleared by hand
    // withdrew every session it held, forever. Graft stashes org folders
    // itself, so its own graft was the surest way to trigger it.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)

    let account = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
    let org = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
    let only = makeProfile("Claude-5", account: account, org: org)
    let onlyOrg = only.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    let id = "12121212-1212-4121-8121-121212121212"
    _ = makeTranscript(session: id, owner: account, org: org, title: "Only copy")

    let start = Date().addingTimeInterval(120)
    check(Graft.fileMissingSessionRecords(filingInto: [only], now: start).count == 1,
          "a session whose owner is here and whose record is not gets one")
    check(Graft.exists(onlyOrg.appending(path: "local_\(id).json")),
          "and it is on disk where the owner reads")

    // The profile goes out of sight the way a deleted one does: gone from
    // Application Support entirely, so the walk finds nothing of it.
    let aside = root.appending(path: "Claude-5.aside")
    try! fm.moveItem(at: only, to: aside)
    _ = Graft.fileMissingSessionRecords(filingInto: [], now: start.addingTimeInterval(60))
    check(Graft.loadSessionRecordState().withdrawn.isEmpty,
          "a pass that could not look anywhere withdraws nothing")
    try! fm.moveItem(at: aside, to: only)
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(120))
    check(Graft.loadSessionRecordState().withdrawn.isEmpty,
          "and the session is still the sweep's to keep once the store is back")

    // Deleted by hand, with the store right there to be read: that is the
    // absence the rule is actually about — once the pass after agrees. Claude
    // re-files a session under a new record name as it runs, so there is a
    // moment when the old name has gone and the new one has not landed, and a
    // single pass cannot tell that from a delete.
    try! fm.removeItem(at: onlyOrg.appending(path: "local_\(id).json"))
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(180))
    check(Graft.loadSessionRecordState().withdrawn.isEmpty,
          "one pass finding a record gone is a record being renamed, as often as not")
    check(Graft.loadSessionRecordState().vanished == [id],
          "so the pass notes it and leaves the next one something to agree with")
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(240))
    check(Graft.loadSessionRecordState().withdrawn == [id],
          "a record still gone the pass after is one somebody deleted")

    // And a record that comes back under a new name is not a deletion at all.
    try? fm.removeItem(at: Graft.sessionRecordStateFile)
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(300))
    let renamed = onlyOrg.appending(path: "local_\(id).json")
    let onDisk = try! Data(contentsOf: renamed)
    try! fm.removeItem(at: renamed)
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(360))
    try! onDisk.write(to: onlyOrg.appending(path: "local_99999999-9999-4999-8999-999999999999.json"))
    _ = Graft.fileMissingSessionRecords(filingInto: [only], now: start.addingTimeInterval(420))
    check(Graft.loadSessionRecordState().withdrawn.isEmpty,
          "a session whose record turned up again under another name was never deleted")
}

do {
    // Graft names its records for the session, so the marker Claude leaves
    // when one is deleted names the session too. That is a deletion read
    // rather than inferred, and it needs no memory of earlier passes.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)

    let account = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
    let org = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"
    let home = makeProfile("Claude-6", account: account, org: org)
    let homeOrg = home.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    let id = "13131313-1313-4131-8131-131313131313"
    _ = makeTranscript(session: id, owner: account, org: org, title: "Named by its session")
    try! "1788024768893".write(to: homeOrg.appending(path: "deleted_\(id)"),
                              atomically: true, encoding: .utf8)

    let now = Date().addingTimeInterval(120)
    check(Graft.fileMissingSessionRecords(filingInto: [home], now: now).isEmpty,
          "a marker carrying the session's own name is a delete, first pass and all")
    check(!Graft.exists(homeOrg.appending(path: "local_\(id).json")),
          "so nothing is written for it")
    check(Graft.loadSessionRecordState().withdrawn == [id],
          "and it is written down, so the marker may be tidied away without the session coming back")
}

do {
    // A store this pass could not read is not a store with nothing in it.
    // Everything sitting in one looks unfiled from outside, and a record
    // written fresh says the session is not archived — so a graft that
    // stashed a store had the sweep on the way past file its whole history
    // back, and every conversation archived by hand that day was in the
    // sidebar again by the afternoon.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)

    let account = "f1f1f1f1-f1f1-4f1f-8f1f-f1f1f1f1f1f1"
    let org = "f2f2f2f2-f2f2-4f2f-8f2f-f2f2f2f2f2f2"
    let home = makeProfile("Claude-7", account: account, org: org)
    let homeOrg = home.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    let id = "14141414-1414-4141-8141-141414141414"
    _ = makeTranscript(session: id, owner: account, org: org, title: "Archived by hand")

    let start = Date().addingTimeInterval(120)
    check(Graft.fileMissingSessionRecords(filingInto: [home], now: start).count == 1,
          "a session that closed without a record gets one")
    let record = homeOrg.appending(path: "local_\(id).json")

    // Archiving it is Claude writing the flag into the record this pass filed.
    var archived = (try! JSONSerialization.jsonObject(with: try! Data(contentsOf: record))) as! [String: Any]
    archived["isArchived"] = true
    try! JSONSerialization.data(withJSONObject: archived).write(to: record)

    // And then the store goes behind a graft, which is this app stashing the
    // organization folder under a hidden name of its own.
    let stash = homeOrg.deletingLastPathComponent()
        .appending(path: ".\(org)\(Graft.stashSuffix)")
    try! fm.moveItem(at: homeOrg, to: stash)

    check(Graft.fileMissingSessionRecords(filingInto: [home],
                                          now: start.addingTimeInterval(60)).isEmpty,
          "a pass that could not read the store files nothing back into it")
    check(!Graft.exists(homeOrg),
          "and does not build the organization folder back over the top of the stash")

    // Not `try!`: a pass that rebuilt the folder leaves the stash with
    // nowhere to go, and the checks below are a better account of what went
    // wrong than a trap on the way there.
    try? fm.moveItem(at: stash, to: homeOrg)
    check(Graft.fileMissingSessionRecords(filingInto: [home],
                                          now: start.addingTimeInterval(120)).isEmpty,
          "the store coming back into sight brings nothing with it either")
    let after = (try? Data(contentsOf: record))
        .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    check(after?["isArchived"] as? Bool == true,
          "so a conversation archived by hand is still archived")
    check(((try? fm.contentsOfDirectory(atPath: homeOrg.path)) ?? []).count == 1,
          "and the profile has one record for it rather than two")

    check(!Graft.mayFileRecords(inOrganisation: homeOrg, storesRead: []),
          "an organization folder no walk got into is not one to write records into")
    check(Graft.mayFileRecords(inOrganisation: homeOrg,
                               storesRead: [homeOrg.resolvingSymlinksInPath().path]),
          "one the walk read is")
    let never = homeOrg.deletingLastPathComponent().appending(path: "f3f3f3f3-f3f3-4f3f-8f3f-f3f3f3f3f3f3")
    check(Graft.mayFileRecords(inOrganisation: never, storesRead: []),
          "and a folder that is simply not there yet is how a profile gets its first record")
}

do {
    // Every profile holding a chat store is read, because a record in one
    // means some Claude is already listing the session. Writing is a
    // different question with a different answer: a Claude profile this app
    // did not make belongs to whatever did make it, and filing into it puts
    // chats in somebody else's sidebar under names of this app's choosing.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)
    let list = Graft.applicationSupport
        .appending(path: "ClaudeGraft")
        .appending(path: "shortcuts.json")
    try? fm.removeItem(at: list)

    let account = "f4f4f4f4-f4f4-4f4f-8f4f-f4f4f4f4f4f4"
    let org = "f5f5f5f5-f5f5-4f5f-8f5f-f5f5f5f5f5f5"
    let stranger = makeProfile("Claude-Somebody-Else", account: account, org: org)
    let strangerOrg = stranger.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    let id = "15151515-1515-4151-8151-151515151515"
    _ = makeTranscript(session: id, owner: account, org: org, title: "Somebody else's")

    let start = Date().addingTimeInterval(120)
    check(Graft.fileMissingSessionRecords(filingInto: [Graft.mainProfile], now: start).isEmpty,
          "a session whose owner lives only on a profile this app never made is left where it is")
    check(((try? fm.contentsOfDirectory(atPath: strangerOrg.path)) ?? []).isEmpty,
          "and nothing of this app's is written into that profile")

    // A launcher knows its own profile, its source and nothing else, so the
    // list the window keeps is how a session owned by another of this app's
    // profiles is still filed by one.
    try! JSONSerialization
        .data(withJSONObject: [["id": UUID().uuidString,
                                "name": "Somebody Else",
                                "folder": "Claude-Somebody-Else"]])
        .write(to: list)
    check(Graft.fileMissingSessionRecords(filingInto: [Graft.mainProfile],
                                          now: start.addingTimeInterval(60)).count == 1,
          "the same profile is filed into once it is one of this app's own")
    check(Graft.recordFilingProfiles(named: []).contains { Graft.samePath($0, Graft.mainProfile) },
          "Claude's own profile is always one a record may be filed into")
    try? fm.removeItem(at: list)
    check(!Graft.recordFilingProfiles(named: []).contains { Graft.samePath($0, stranger) },
          "and a profile named in no list of this app's is not")
}

do {
    // A conversation carried on past a compaction gets a new command line
    // session, and the record names the ones it grew out of. Those older
    // transcripts sit on disk whole with no record naming them, which is
    // exactly what a session that closed without one looks like — so the
    // sweep filed them, and the sidebar showed the same conversation twice:
    // once as the person left it, once fresh and unarchived.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)

    let account = "f6f6f6f6-f6f6-4f6f-8f6f-f6f6f6f6f6f6"
    let org = "f7f7f7f7-f7f7-4f7f-8f7f-f7f7f7f7f7f7"
    let home = makeProfile("Claude-8", account: account, org: org)
    let homeOrg = home.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)

    let earlier = "16161616-1616-4161-8161-161616161616"
    let carriedOn = "17171717-1717-4171-8171-171717171717"
    _ = makeTranscript(session: earlier, owner: account, org: org, title: "Long conversation")
    _ = makeTranscript(session: carriedOn, owner: account, org: org, title: "Long conversation")

    // Claude's own record for the conversation as it stands now, naming the
    // session it carried on from — and archived, the way a person left it.
    let record: [String: Any] = ["sessionId": "local_18181818-1818-4181-8181-181818181818",
                                 "cliSessionId": carriedOn,
                                 "priorCliSessionIds": [earlier],
                                 "title": "Long conversation",
                                 "isArchived": true]
    try! JSONSerialization.data(withJSONObject: record)
        .write(to: homeOrg.appending(path: "local_18181818-1818-4181-8181-181818181818.json"))

    let now = Date().addingTimeInterval(120)
    check(Graft.fileMissingSessionRecords(filingInto: [home], now: now).isEmpty,
          "the transcript a conversation grew out of is not a session anybody lost")
    check(!Graft.exists(homeOrg.appending(path: "local_\(earlier).json")),
          "so the conversation is listed once, as its own record has it, and not again beside itself")

    let both = Graft.sessions(ofRecordAt: homeOrg.appending(path: "local_18181818-1818-4181-8181-181818181818.json"))
    check(both.cliSessionId == carriedOn && both.prior == [earlier],
          "a record says which session it holds now and which it has taken over")
}

do {
    // Archiving is a record write, and every route a pass can take past an
    // archived record has to leave the flag where it found it. These are the
    // four shapes an archived record turns up in.
    try? fm.removeItem(at: Graft.claudeProjects)
    try? fm.removeItem(at: Graft.sessionRecordStateFile)

    let account = "f8f8f8f8-f8f8-4f8f-8f8f-f8f8f8f8f8f8"
    let org = "f9f9f9f9-f9f9-4f9f-8f9f-f9f9f9f9f9f9"
    let home = makeProfile("Claude-9", account: account, org: org)
    let homeOrg = home.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    var clock = Date().addingTimeInterval(120)
    func pass() -> [Graft.SessionFacts] {
        clock = clock.addingTimeInterval(120)
        return Graft.fileMissingSessionRecords(filingInto: [home], now: clock)
    }
    /// Claude archiving a record in place: the flag flipped, everything else
    /// left as it was.
    func archive(_ file: URL) {
        guard var record = (try? Data(contentsOf: file))
            .flatMap({ try? JSONSerialization.jsonObject(with: $0) as? [String: Any] })
        else { return check(false, "there was a record at \(file.lastPathComponent) to archive") }
        record["isArchived"] = true
        try! JSONSerialization.data(withJSONObject: record).write(to: file, options: .atomic)
    }
    func archived(_ file: URL) -> Bool {
        (try? Data(contentsOf: file))
            .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            .flatMap { $0["isArchived"] as? Bool } ?? false
    }

    // One: a record this app filed, archived by hand, with nothing else
    // happening. The pass after has no reason to touch it and does not.
    let quiet = "19191919-1919-4191-8191-191919191919"
    _ = makeTranscript(session: quiet, owner: account, org: org, title: "Archived and left alone")
    check(pass().count == 1, "a session that closed without a record gets one")
    let quietRecord = homeOrg.appending(path: "local_\(quiet).json")
    archive(quietRecord)
    check(pass().isEmpty && archived(quietRecord),
          "a record archived by hand is still archived after a pass that files nothing")

    // Two: the same, but the transcript moves on afterwards — which is the
    // one thing that sends a pass back to a record it wrote. The flag says
    // Claude has been here since, so the record stops being this app's.
    let moving = "1a1a1a1a-1a1a-41a1-81a1-1a1a1a1a1a1a"
    _ = makeTranscript(session: moving, owner: account, org: org, title: "Still being written")
    _ = pass()
    let movingRecord = homeOrg.appending(path: "local_\(moving).json")
    check(Graft.loadSessionRecordState().authored[moving] != nil,
          "a record this app filed is one it knows it wrote")
    archive(movingRecord)
    _ = makeTranscript(session: moving, owner: account, org: org, title: "Still being written",
                       last: "2026-08-29T19:30:00.000Z")
    check(pass().isEmpty && archived(movingRecord),
          "and a transcript moving on past an archived record does not unarchive it")
    check(Graft.loadSessionRecordState().authored[moving] == nil,
          "the pass gives up its claim instead, the record having been rewritten by another hand")

    // Three: Claude takes the session over and re-files it under a name of
    // its own, archived. The name this app used is gone, and nothing brings
    // it back — the session is recorded, under the id the record carries.
    let renamed = "1b1b1b1b-1b1b-41b1-81b1-1b1b1b1b1b1b"
    _ = makeTranscript(session: renamed, owner: account, org: org, title: "Taken over")
    _ = pass()
    let mine = homeOrg.appending(path: "local_\(renamed).json")
    var host = (try! JSONSerialization.jsonObject(with: try! Data(contentsOf: mine))) as! [String: Any]
    host["sessionId"] = "local_1c1c1c1c-1c1c-41c1-81c1-1c1c1c1c1c1c"
    host["isArchived"] = true
    let theirs = homeOrg.appending(path: "local_1c1c1c1c-1c1c-41c1-81c1-1c1c1c1c1c1c.json")
    try! fm.removeItem(at: mine)
    try! JSONSerialization.data(withJSONObject: host).write(to: theirs)
    _ = makeTranscript(session: renamed, owner: account, org: org, title: "Taken over",
                       last: "2026-08-29T20:00:00.000Z")
    check(pass().isEmpty, "a session re-filed under Claude's own name is not a session missing a record")
    check(!Graft.exists(mine) && archived(theirs),
          "so the name this app used stays gone, and the archived record keeps its flag")

    // Four: an archived record caught mid-write, which parses as nothing at
    // all. One pass cannot tell that from a delete and does not guess.
    let half = "1d1d1d1d-1d1d-41d1-81d1-1d1d1d1d1d1d"
    _ = makeTranscript(session: half, owner: account, org: org, title: "Caught mid-write")
    _ = pass()
    let halfRecord = homeOrg.appending(path: "local_\(half).json")
    archive(halfRecord)
    let whole = try! Data(contentsOf: halfRecord)
    try! Data("{\"cliSess".utf8).write(to: halfRecord, options: .atomic)
    check(pass().isEmpty, "a record that will not parse is not a record to be written again")
    try! whole.write(to: halfRecord, options: .atomic)
    check(pass().isEmpty && archived(halfRecord),
          "and once it reads again it is the archived record it always was")
}

section("Mirrored chat folders")

do {
    // A record on one side and not the other is either one just written or one
    // just deleted, and on disk those are the same thing. What tells them
    // apart is whether the last pass saw it on both — so the baseline is the
    // whole rule, and getting it backwards either loses a new chat or brings a
    // deleted one back.
    check(Graft.digest(Data("a chat record".utf8)) == Graft.digest(Data("a chat record".utf8)),
          "the same bytes digest the same way twice")
    check(Graft.digest(Data("a chat record".utf8)) != Graft.digest(Data("a chat recorD".utf8)),
          "and one byte apart digests differently")

    check(Graft.mirrorDecision(one: "x", other: "x", baseline: "x") == .nothing,
          "two folders holding the same record have nothing to do")
    check(Graft.mirrorDecision(one: nil, other: nil, baseline: "x") == .nothing,
          "and a record gone from both is gone")

    check(Graft.mirrorDecision(one: "x", other: nil, baseline: nil) == .copyToOther,
          "a record the last pass never saw is a new one, and it crosses over")
    check(Graft.mirrorDecision(one: nil, other: "x", baseline: nil) == .copyToOne,
          "whichever side wrote it")

    check(Graft.mirrorDecision(one: "x", other: nil, baseline: "x") == .removeFromOne,
          "a record both sides held, now gone from one, was deleted rather than never written")
    check(Graft.mirrorDecision(one: nil, other: "x", baseline: "x") == .removeFromOther,
          "and the deletion carries across rather than the record coming back")

    check(Graft.mirrorDecision(one: "y", other: "x", baseline: "x") == .copyToOther,
          "the side that moved away from what both held is the side that changed")
    check(Graft.mirrorDecision(one: "x", other: "y", baseline: "x") == .copyToOne,
          "read either way round")

    check(Graft.mirrorDecision(one: "y", other: "z", baseline: "x") == .conflict,
          "two sides that both moved is not a question this can answer")
    check(Graft.mirrorDecision(one: "y", other: "z", baseline: nil) == .conflict,
          "nor is one with nothing remembered behind it")
    check(Graft.mirrorDecision(one: "y", other: nil, baseline: "x") == .conflict,
          "and a record edited on one side while the other deleted it is the same question")

    // The state file loads from a version that never wrote it, claiming
    // nothing, the way the session record state does.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    check(Graft.loadMirrorState() == Graft.MirrorState(),
          "with no state file yet, a pass starts remembering nothing")
    var state = Graft.MirrorState()
    state.pairs["a\u{0000}b"] = ["local_1.json": "beef"]
    Graft.saveMirrorState(state)
    check(Graft.loadMirrorState() == state,
          "and what one pass wrote down is what the next one reads back")
    try! Data("{}".utf8).write(to: Graft.mirrorStateFile)
    check(Graft.loadMirrorState() == Graft.MirrorState(),
          "a state file from before any of this existed loads as a pass with nothing remembered")
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // A bundle written by a version that still had the choice carries a
    // `mirrorChats` key. Decoding ignores what it does not know, so nothing
    // has to be rewritten for one to migrate — it just stops being asked.
    let old = try! JSONDecoder().decode(GraftConfig.self,
        from: Data("{\"profileDir\":\"/tmp/p\",\"sourceDir\":\"/tmp/s\",\"mirrorChats\":false}".utf8))
    check(old.profileDir == "/tmp/p" && old.sourceDir == "/tmp/s",
          "a shortcut written while linking was still an option reads back without complaint")

    try? fm.removeItem(at: Graft.mirrorStateFile)
    let source = makeProfile("Claude-Src", account: "SRCACC", org: "SRCORG", chats: ["one", "two"])
    let borrower = makeProfile("Claude-Mirror", account: "OWNACC", org: "OWNORG")
    let srcOrg = source.appending(path: "claude-code-sessions")
        .appending(path: "SRCACC").appending(path: "SRCORG")
    let ownOrg = borrower.appending(path: "claude-code-sessions")
        .appending(path: "OWNACC").appending(path: "OWNORG")

    // What a released version left on disk: the profile's own chats in the
    // stash and a link where they used to be. Built by hand because nothing
    // in this app makes one any more, which is the point of the test.
    let ownStash = ownOrg.deletingLastPathComponent().appending(path: ".OWNORG.graft-own")
    try! fm.createDirectory(at: ownStash, withIntermediateDirectories: true)
    try! "{}".write(to: ownStash.appending(path: "local_mine.json"),
                    atomically: true, encoding: .utf8)
    try! fm.removeItem(at: ownOrg)
    try! fm.createSymbolicLink(at: ownOrg, withDestinationURL: srcOrg)
    check(Graft.isSymlink(ownOrg), "the profile starts out with a link where its chats go")

    Graft.apply(GraftConfig(profileDir: borrower.path, sourceDir: source.path))
    check(!Graft.isSymlink(ownOrg) && Graft.isDirectory(ownOrg),
          "converting takes the link away and leaves a folder the profile can write into")
    check(Graft.exists(ownOrg.appending(path: "local_one.json"))
          && Graft.exists(ownOrg.appending(path: "local_two.json")),
          "and the first pass fills it with the chats it was borrowing")
    check(Graft.exists(srcOrg.appending(path: "local_one.json")),
          "the source keeps its own, since a copy was made rather than a move")

    // The whole point: the borrowing profile writes, and the change crosses.
    try! Data("{\"isArchived\":true}".utf8)
        .write(to: ownOrg.appending(path: "local_one.json"))
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check((try? String(contentsOf: srcOrg.appending(path: "local_one.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "a record the borrowing profile rewrote reaches the source")

    // And the other way, which is how it sees anything new.
    try! Data("{\"title\":\"three\"}".utf8)
        .write(to: srcOrg.appending(path: "local_three.json"))
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check(Graft.exists(ownOrg.appending(path: "local_three.json")),
          "a chat started on the source turns up in the profile borrowing from it")

    // A delete is not an absence to be filled back in.
    try! fm.removeItem(at: ownOrg.appending(path: "local_two.json"))
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check(!Graft.exists(srcOrg.appending(path: "local_two.json")),
          "a chat deleted on one side is deleted on the other, not copied back")
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check(!Graft.exists(ownOrg.appending(path: "local_two.json")),
          "and it stays deleted through the pass after")

    // Both sides moved. The record that says it moved later is the one kept.
    try! Data("{\"lastActivityAt\":100}".utf8).write(to: ownOrg.appending(path: "local_one.json"))
    try! Data("{\"lastActivityAt\":200}".utf8).write(to: srcOrg.appending(path: "local_one.json"))
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check((try? String(contentsOf: ownOrg.appending(path: "local_one.json"), encoding: .utf8))
            == "{\"lastActivityAt\":200}",
          "with both sides rewritten, the record that moved more recently wins")

    // Everything else in the folder is the profile's own business.
    try! Data("{}".utf8).write(to: ownOrg.appending(path: "scheduled-tasks.json"))
    Graft.mirrorChatFolders(ownOrg, srcOrg)
    check(!Graft.exists(srcOrg.appending(path: "scheduled-tasks.json")),
          "a file that is not a record or a marker is left where its profile put it")

    // A folder that cannot be listed is not an empty one, and here reading it
    // as empty would delete every record on the other side.
    let unreadable = borrower.appending(path: "not-a-folder")
    try! Data("{}".utf8).write(to: unreadable)
    let before = ((try? fm.contentsOfDirectory(atPath: srcOrg.path)) ?? []).count
    check(Graft.mirrorChatFolders(unreadable, srcOrg) == 0,
          "a pass that could not read one side does nothing at all")
    check(((try? fm.contentsOfDirectory(atPath: srcOrg.path)) ?? []).count == before,
          "so the side it could read is left exactly as it was")
}

do {
    // Archive a chat in the profile that borrowed it, then open the profile it
    // was borrowed from. The second one is not running `apply` — it has no
    // source of its own — so unless a pass goes looking for every pair this
    // app knows about, the change sits there and the sidebar being built never
    // sees it. That is the whole workflow the mirror exists for.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let host = makeProfile("Claude-Host", account: "HOSTACC", org: "HOSTORG", chats: ["shared"])
    let guest = makeProfile("Claude-Guest", account: "GUESTACC", org: "GUESTORG")
    let hostOrg = host.appending(path: "claude-code-sessions")
        .appending(path: "HOSTACC").appending(path: "HOSTORG")
    let guestOrg = guest.appending(path: "claude-code-sessions")
        .appending(path: "GUESTACC").appending(path: "GUESTORG")

    Graft.apply(GraftConfig(profileDir: guest.path, sourceDir: host.path))
    check(Graft.exists(guestOrg.appending(path: "local_shared.json")),
          "the borrowing profile starts with a copy of the chat")

    // The borrowing profile archives it, the way a Claude with a real folder
    // now can.
    try! Data("{\"isArchived\":true}".utf8)
        .write(to: guestOrg.appending(path: "local_shared.json"))

    // And the source is opened. Nothing hands it the pair; it works it out
    // from what earlier passes wrote down.
    check(Graft.mirrorKnownPairs() > 0,
          "opening the source puts every pair this app knows about back in step")
    check((try? String(contentsOf: hostOrg.appending(path: "local_shared.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "so the archive made in the borrowing profile is there when the source builds its sidebar")
    check(Graft.mirrorKnownPairs() == 0,
          "and a pass with nothing left to carry moves nothing")
}

do {
    // Everything an earlier version can leave behind, converted.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Legacy-Src", account: "LSRC", org: "LORG", chats: ["kept"])
    let old = makeProfile("Claude-Legacy", account: "LOWN", org: "LOWNORG", chats: ["mine"])
    let srcOrg = src.appending(path: "claude-code-sessions").appending(path: "LSRC").appending(path: "LORG")
    let ownOrg = old.appending(path: "claude-code-sessions").appending(path: "LOWN").appending(path: "LOWNORG")
    let stash = ownOrg.deletingLastPathComponent()
        .appending(path: ".LOWNORG\(Graft.stashSuffix)")

    // Grafted the old way, which stashes what the profile had.
    linkTheOldWay(ownOrg, to: srcOrg)
    check(Graft.isSymlink(ownOrg) && Graft.exists(stash),
          "a released version leaves a link with the profile's own chats stashed beside it")

    Graft.apply(GraftConfig(profileDir: old.path, sourceDir: src.path))
    check(!Graft.isSymlink(ownOrg) && Graft.exists(stash),
          "converting takes the link and leaves the stash exactly where it was")
    check(Graft.exists(ownOrg.appending(path: "local_kept.json")),
          "and the profile has its own copy of what it was borrowing")

    // The upgrade's whole promise. A released version put the profile's own
    // chats in the stash and left a link standing in for them, so the sidebar
    // showed the source's history instead of its own. Converting has to fetch
    // them back out and merge the two, or upgrading reads as a history that
    // never came back.
    check(chatsVisible(to: old) == ["local_kept.json", "local_mine.json"],
          "the chats the link stood in for are fetched back out of the stash and merged in")
    check(Graft.exists(srcOrg.appending(path: "local_mine.json")),
          "and reach the profile it borrows from, which the link never let them do")
    check(Graft.exists(stash.appending(path: "local_mine.json")),
          "while the stash goes on naming them, so going back is still exact")

    // Sent back to its own chats, it stops being mirrored — the pair lives in
    // a file every launcher reads, so left there it would go on being squared
    // up by whichever profile opened next.
    check(!Graft.loadMirrorState().pairs.isEmpty, "while grafted, the pair is remembered")
    Graft.apply(GraftConfig(profileDir: old.path, sourceDir: nil))
    check(Graft.loadMirrorState().pairs.isEmpty,
          "going back to its own chats is the end of the mirroring, not a pair left running")
    try! Data("{\"isArchived\":true}".utf8).write(to: srcOrg.appending(path: "local_kept.json"))
    Graft.mirrorKnownPairs()
    check((try? String(contentsOf: srcOrg.appending(path: "local_kept.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "and nothing reaches across the two afterwards")

    // A profile somebody deleted must not be built back up by a pass whose
    // only job is keeping two sidebars in step.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let doomed = makeProfile("Claude-Doomed", account: "DACC", org: "DORG")
    let doomedOrg = doomed.appending(path: "claude-code-sessions")
        .appending(path: "DACC").appending(path: "DORG")
    Graft.mirrorChatFolders(doomedOrg, srcOrg)
    check(!Graft.loadMirrorState().pairs.isEmpty, "a pair mirrors while both profiles are there")
    try! fm.removeItem(at: doomed)
    check(Graft.mirrorChatFolders(doomedOrg, srcOrg) == 0,
          "a pass over a profile that has been deleted carries nothing")
    check(!Graft.exists(doomed),
          "and does not put the deleted profile back to have somewhere to write")
}

section("Going back to its own chats")
do {
    // A mirrored folder is a real one full of copies, so unlike a link it
    // cannot simply be dropped. What the profile owned before the graft, what
    // it did during it, and what it was only ever borrowing all have to end up
    // somewhere, and each of the three somewhere different.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let lender = makeProfile("Claude-Lender", account: "LEND", org: "LENDORG", chats: ["theirs"])
    let keeper = makeProfile("Claude-Keeper", account: "KEEP", org: "KEEPORG", chats: ["ours"])
    let lentOrg = lender.appending(path: "claude-code-sessions")
        .appending(path: "LEND").appending(path: "LENDORG")
    let keptOrg = keeper.appending(path: "claude-code-sessions")
        .appending(path: "KEEP").appending(path: "KEEPORG")

    Graft.apply(GraftConfig(profileDir: keeper.path, sourceDir: lender.path))
    check(chatsVisible(to: keeper) == ["local_ours.json", "local_theirs.json"],
          "sharing a history shows both sets of chats rather than only the borrowed one")
    check(chatsVisible(to: lender) == ["local_ours.json", "local_theirs.json"],
          "and the profile lent from is left holding both of them as well")

    // The stash still has to hold what the profile brought, even though the
    // same records are now in the shared set: it is the only thing that says
    // which of the merged records were this profile's, and going back has to
    // be exact rather than a guess at which record belonged to whom.
    let stash = keptOrg.deletingLastPathComponent().appending(path: ".KEEPORG\(Graft.stashSuffix)")
    check(Graft.exists(stash.appending(path: "local_ours.json")),
          "what the profile brought to the merge is written down as well as shared")

    // Now it behaves like a profile: a chat started, one archived, and one of
    // its own archived too — which the stash holds an older copy of.
    try? Data("{\"title\":\"during\"}".utf8).write(to: keptOrg.appending(path: "local_during.json"))
    try? Data("{\"isArchived\":true}".utf8).write(to: keptOrg.appending(path: "local_theirs.json"))
    try? Data("{\"isArchived\":true}".utf8).write(to: keptOrg.appending(path: "local_ours.json"))

    Graft.apply(GraftConfig(profileDir: keeper.path, sourceDir: nil))
    check(chatsVisible(to: keeper) == ["local_ours.json"],
          "going back to its own chats gives back exactly what it started with")
    check((try? String(contentsOf: keptOrg.appending(path: "local_ours.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "and gives it back as it was left, rather than as the stash remembers it")
    check(!Graft.exists(stash),
          "with nothing left in it, the stash goes rather than arming the next graft")
    check(Graft.exists(lentOrg.appending(path: "local_during.json")),
          "a chat started while mirroring is handed over before the copies are taken out")
    check((try? String(contentsOf: lentOrg.appending(path: "local_theirs.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "and so is an archive it made, which is the whole reason for copying")
    check(Graft.loadMirrorState().pairs.isEmpty,
          "the pair is dropped, so nothing squares the two up afterwards")
    check(Graft.exists(lentOrg.appending(path: "local_ours.json")),
          "what was merged in stays with the profile it was merged into, which is the price of sharing")

    // Twice over is a thing a launcher does on every run.
    Graft.apply(GraftConfig(profileDir: keeper.path, sourceDir: nil))
    check(chatsVisible(to: keeper) == ["local_ours.json"],
          "and a second pass over an ungrafted profile changes nothing")
}

do {
    // Opening the profile that lends. `apply` calls `ungraft` on anything with
    // no source of its own, so this runs every time that shortcut is pressed —
    // and undoing a graft used to mean every pair with a half under the
    // profile, which is every pair somebody else borrows from it. The lender
    // read its own folder as the borrowed one; a lender keeps no stash, so
    // nothing was held back; and a merge leaves both sides holding the same
    // bytes, so the test for what to take out matched all of it. One press,
    // the whole shared history gone from the sidebar being built.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let lender = makeProfile("Claude-Lends", account: "LENDS", org: "LENDSORG", chats: ["theirs"])
    let borrower = makeProfile("Claude-Borrows", account: "BORROWS", org: "BORROWSORG", chats: ["ours"])
    let both = ["local_ours.json", "local_theirs.json"]

    Graft.apply(GraftConfig(profileDir: borrower.path, sourceDir: lender.path))
    check(chatsVisible(to: lender) == both,
          "sharing a history leaves both sets of chats in the profile lent from")

    Graft.apply(GraftConfig(profileDir: lender.path, sourceDir: nil))
    check(chatsVisible(to: lender) == both,
          "and opening that profile leaves them there rather than stripping the merge back out")
    check(chatsVisible(to: borrower) == both,
          "with the profile that borrowed them untouched as well")
    check(!Graft.loadMirrorState().pairs.isEmpty,
          "the pair belongs to the profile doing the borrowing, so the lender does not forget it")

    // Which is what the borrower's next launch turns on. A pair that has been
    // forgotten reads as a first pass, and a first pass stashes.
    let stash = borrower.appending(path: "claude-code-sessions")
        .appending(path: "BORROWS").appending(path: ".BORROWSORG\(Graft.stashSuffix)")
    Graft.apply(GraftConfig(profileDir: borrower.path, sourceDir: lender.path))
    check(!Graft.exists(stash.appending(path: "local_theirs.json")),
          "so the launch after is not a first pass and the stash goes on naming what was brought")
    check(chatsVisible(to: borrower) == both, "and both histories are still where they were")

    // The lender going back to its own chats is not a thing it can do: it never
    // borrowed. Going back is the borrower's, and it still works.
    Graft.apply(GraftConfig(profileDir: borrower.path, sourceDir: nil))
    check(chatsVisible(to: borrower) == ["local_ours.json"],
          "the profile that did borrow still gets back exactly what it brought")
}

do {
    // A pair dropped while the merge it set up is still sitting in the folder —
    // a source deleted, an organization moved, an older Graft ungrafting the
    // lender. The launch after looks like a first pass, and a first pass
    // stashes: `stash` folds a stash it finds back into the live folder before
    // moving the lot aside, so the borrowed records would go in beside the
    // profile's own and nothing would be left saying which were whose. That is
    // a stash growing from 155 to 159 a launch at a time, and the count is all
    // anybody would have seen.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Refold-Src", account: "RFSRC", org: "RFORG", chats: ["theirs"])
    let own = makeProfile("Claude-Refold", account: "RFOWN", org: "RFOWNORG", chats: ["ours"])
    let config = GraftConfig(profileDir: own.path, sourceDir: src.path)
    let stash = own.appending(path: "claude-code-sessions")
        .appending(path: "RFOWN").appending(path: ".RFOWNORG\(Graft.stashSuffix)")

    Graft.apply(config)
    check(Graft.exists(stash.appending(path: "local_ours.json"))
          && !Graft.exists(stash.appending(path: "local_theirs.json")),
          "a first pass writes down what the profile brought to the merge and nothing else")

    try? fm.removeItem(at: Graft.mirrorStateFile)
    Graft.apply(config)
    check(!Graft.exists(stash.appending(path: "local_theirs.json")),
          "and a second first pass does not fold the borrowed chats in beside them")
    check(Graft.exists(stash.appending(path: "local_ours.json")),
          "while what the profile did bring is still named there")
    check(chatsVisible(to: own) == ["local_ours.json", "local_theirs.json"],
          "with both histories back in the folder the person actually reads")

    // Which is the whole reason the stash is kept: hand the borrowed ones over,
    // keep the ones it came with.
    Graft.apply(GraftConfig(profileDir: own.path, sourceDir: nil))
    check(chatsVisible(to: own) == ["local_ours.json"],
          "so going back is still exact rather than a guess at which record belonged to whom")
}

do {
    // The pass that must never happen: `apply` runs on every launch, so a
    // second one that put the borrowed chats away as though they were the
    // profile's own would empty the sidebar and fetch it all again, for ever.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Twice-Src", account: "TSRC", org: "TORG", chats: ["one"])
    let twice = makeProfile("Claude-Twice", account: "TOWN", org: "TOWNORG", chats: ["own"])
    let config = GraftConfig(profileDir: twice.path, sourceDir: src.path)
    let ownOrg = twice.appending(path: "claude-code-sessions")
        .appending(path: "TOWN").appending(path: "TOWNORG")
    let stash = ownOrg.deletingLastPathComponent().appending(path: ".TOWNORG\(Graft.stashSuffix)")

    Graft.apply(config)
    Graft.apply(config)
    Graft.apply(config)
    check(chatsVisible(to: twice) == ["local_one.json", "local_own.json"],
          "launching a mirrored shortcut again leaves the merged set where it is")
    check(Graft.exists(stash.appending(path: "local_own.json")),
          "the record of what the profile brought stays put rather than being folded back in")
    check(!Graft.exists(stash.appending(path: "local_one.json")),
          "nothing borrowed is stashed away by the pass after the first")

    // The other half of that rule, and the one the stash arms: seeding the
    // profile's own chats into the shared set runs on the first pass alone.
    // Running it on every launch would fetch back every merged chat the person
    // had since deleted, which is the same endless loop pointed the other way.
    try? fm.removeItem(at: ownOrg.appending(path: "local_own.json"))
    try? fm.removeItem(at: src.appending(path: "claude-code-sessions")
        .appending(path: "TSRC").appending(path: "TORG")
        .appending(path: "local_own.json"))
    Graft.apply(config)
    check(chatsVisible(to: twice) == ["local_one.json"],
          "a merged chat deleted afterwards is not fetched back out of the stash")
}

do {
    // A source somebody deleted, or a folder that could not be read. Nothing
    // was handed over, so nothing may be taken away.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let gone = makeProfile("Claude-Gone", account: "GONE", org: "GONEORG", chats: ["borrowed"])
    let orphan = makeProfile("Claude-Orphan", account: "ORPH", org: "ORPHORG")
    Graft.apply(GraftConfig(profileDir: orphan.path, sourceDir: gone.path))
    check(chatsVisible(to: orphan) == ["local_borrowed.json"], "the profile has its copy")

    try? fm.removeItem(at: gone)
    Graft.apply(GraftConfig(profileDir: orphan.path, sourceDir: nil))
    check(chatsVisible(to: orphan) == ["local_borrowed.json"],
          "with the source gone there is nowhere to hand the copies back, so they stay")
    check(Graft.loadMirrorState().pairs.isEmpty,
          "the pair still goes, since there is nothing left to keep in step")
}

do {
    // Going back to its own chats is the only way off, so it has to carry
    // everything: a record the profile wrote while it was borrowing belongs to
    // the profile it borrowed from, and a copy taken away before that handover
    // is a chat in nobody's sidebar.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let host = makeProfile("Claude-Back-Src", account: "BSRC", org: "BORG", chats: ["shared"])
    let guest = makeProfile("Claude-Back", account: "BOWN", org: "BOWNORG", chats: ["private"])
    let guestOrg = guest.appending(path: "claude-code-sessions")
        .appending(path: "BOWN").appending(path: "BOWNORG")
    let hostOrg = host.appending(path: "claude-code-sessions")
        .appending(path: "BSRC").appending(path: "BORG")

    Graft.apply(GraftConfig(profileDir: guest.path, sourceDir: host.path))
    try? Data("{\"title\":\"late\"}".utf8).write(to: guestOrg.appending(path: "local_late.json"))

    Graft.apply(GraftConfig(profileDir: guest.path, sourceDir: nil))
    check(Graft.exists(hostOrg.appending(path: "local_late.json")),
          "going back hands over what the profile wrote while it was borrowing")
    check(Graft.loadMirrorState().pairs.isEmpty, "with the pair dropped on the way past")
    check(!Graft.isSymlink(guestOrg) && Graft.isDirectory(guestOrg),
          "and nothing anywhere puts a link back, since there is no link left to put")
    check(chatsVisible(to: guest) == ["local_private.json"],
          "so its own chats are what it comes back to")
}

do {
    // Both profiles on one account. A link takes the whole store rather than
    // one organization inside it, so that is what a mirror has to stand in
    // for — and the folder it writes into is one nothing had made yet, which
    // is a same-account graft that mirrored not a single file.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let first = makeProfile("Claude-Same-A", account: "SAME", org: "SAMEORG", chats: ["shared"])
    let second = makeProfile("Claude-Same-B", account: "SAME", org: "SAMEORG", chats: ["alone"])
    let secondOrg = second.appending(path: "claude-code-sessions")
        .appending(path: "SAME").appending(path: "SAMEORG")
    let firstOrg = first.appending(path: "claude-code-sessions")
        .appending(path: "SAME").appending(path: "SAMEORG")

    Graft.apply(GraftConfig(profileDir: second.path, sourceDir: first.path))
    check(chatsVisible(to: second) == ["local_alone.json", "local_shared.json"],
          "two profiles on one account merge the whole store, organization by organization")
    check(chatsVisible(to: first) == ["local_alone.json", "local_shared.json"],
          "and what the borrowing profile had is written down and shared rather than only put away")

    try? Data("{\"isArchived\":true}".utf8).write(to: secondOrg.appending(path: "local_shared.json"))
    Graft.mirrorKnownPairs()
    check((try? String(contentsOf: firstOrg.appending(path: "local_shared.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "an archive made on one side reaches the other")

    Graft.apply(GraftConfig(profileDir: second.path, sourceDir: nil))
    check(chatsVisible(to: second) == ["local_alone.json"],
          "and going back to its own chats brings a whole store out of the stash")
}

do {
    // The other side holding the name is not the other side holding the chat.
    // A source that cannot be written to — read-only, or simply full — takes
    // none of the last pass, and a copy dropped because a stale version of it
    // happened to sit over there is a chat lost for a reason nobody could see.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let readonly = makeProfile("Claude-RO-Src", account: "ROSRC", org: "ROORG", chats: ["one", "two"])
    let writer = makeProfile("Claude-RO", account: "ROOWN", org: "ROOWNORG")
    let theirs = readonly.appending(path: "claude-code-sessions")
        .appending(path: "ROSRC").appending(path: "ROORG")
    let mine = writer.appending(path: "claude-code-sessions")
        .appending(path: "ROOWN").appending(path: "ROOWNORG")
    Graft.apply(GraftConfig(profileDir: writer.path, sourceDir: readonly.path))

    // Archived here, and the handover cannot land.
    try? Data("{\"isArchived\":true}".utf8).write(to: mine.appending(path: "local_one.json"))
    for store in Graft.chatStores {
        let dir = readonly.appending(path: store).appending(path: "ROSRC").appending(path: "ROORG")
        try? fm.setAttributes([.posixPermissions: 0o555], ofItemAtPath: dir.path)
    }
    Graft.apply(GraftConfig(profileDir: writer.path, sourceDir: nil))
    check((try? String(contentsOf: mine.appending(path: "local_one.json"), encoding: .utf8))
            == "{\"isArchived\":true}",
          "a record the other side could not be given stays where the profile wrote it")
    check((try? String(contentsOf: theirs.appending(path: "local_one.json"), encoding: .utf8)) == "{}",
          "and the stale copy over there is left as it was rather than counted as a handover")
    check(!Graft.exists(mine.appending(path: "local_two.json")),
          "while a record the other side really does hold, byte for byte, is taken out")
    for store in Graft.chatStores {
        let dir = readonly.appending(path: store).appending(path: "ROSRC").appending(path: "ROORG")
        try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: dir.path)
    }
}

do {
    // Two shortcuts on one account, grafted by a released version, which links
    // the whole store rather than one organization inside it. Converting has
    // to stand in for that link and going back has to undo it, and the store
    // is a level above everything the ungraft walk was looking at.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let elder = makeProfile("Claude-One-A", account: "ONE", org: "ONEORG", chats: ["shared"])
    let junior = makeProfile("Claude-One-B", account: "ONE", org: "ONEORG", chats: ["alone"])
    let store = junior.appending(path: "claude-code-sessions")
    let stash = junior.appending(path: ".claude-code-sessions\(Graft.stashSuffix)")

    linkTheOldWay(store, to: elder.appending(path: "claude-code-sessions"))
    check(Graft.isSymlink(store) && Graft.exists(stash),
          "a released version on one account links the whole store and stashes the whole store")

    Graft.apply(GraftConfig(profileDir: junior.path, sourceDir: elder.path))
    check(!Graft.isSymlink(store) && chatsVisible(to: junior) == ["local_alone.json", "local_shared.json"],
          "converting takes that link too, and merges the stashed store back in with the source's")
    check(Graft.exists(stash), "with the profile's own store still put away beside it")

    Graft.apply(GraftConfig(profileDir: junior.path, sourceDir: nil))
    check(chatsVisible(to: junior) == ["local_alone.json"],
          "and going back gives it the store it had before any of this")
}

do {
    // The source deleted out from under a profile that was copying from it.
    // The copies are all that is left of those chats, so they stay: whatever
    // else going back to its own chats means, it does not mean being the
    // second thing to delete them.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let doomed = makeProfile("Claude-Vanish-Src", account: "VANSRC", org: "VANORG", chats: ["theirs"])
    let survivor = makeProfile("Claude-Vanish", account: "VANOWN", org: "VANOWNORG", chats: ["ours"])
    Graft.apply(GraftConfig(profileDir: survivor.path, sourceDir: doomed.path))
    try! Graft.deleteProfile(doomed)
    check(Graft.loadMirrorState().pairs.isEmpty,
          "deleting the source ends the pair from its own side")

    Graft.apply(GraftConfig(profileDir: survivor.path, sourceDir: nil))
    check(chatsVisible(to: survivor).sorted() == ["local_ours.json", "local_theirs.json"],
          "and the profile keeps both what it owned and what it copied, having nowhere to hand it back")
}

do {
    // Deleting the shortcut but keeping the profile. Nothing runs its launcher
    // again, so nothing is left to undo the mirroring for it, and a pair that
    // outlives its shortcut goes on being squared up by whichever profile
    // opens next — for ever, and without anything on screen saying so.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Bye-Src", account: "BYSRC", org: "BYORG", chats: ["one"])
    _ = makeProfile("Claude-Bye", account: "BYOWN", org: "BYOWNORG")
    let shortcut = Shortcut(name: "Bye", folder: "Claude-Bye", source: .own)
    _ = try! Installer.install(shortcut, sourceDir: src)
    check(!Graft.loadMirrorState().pairs.isEmpty, "an installed mirrored shortcut leaves a pair")

    let store = ShortcutStore()
    store.shortcuts = [shortcut]
    store.delete(shortcut.id)
    check(Graft.loadMirrorState().pairs.isEmpty,
          "deleting the shortcut stops the two folders being squared up")
    check(chatsVisible(to: support.appending(path: "Claude-Bye")) == ["local_one.json"],
          "and the chats it had are left exactly where they are, since the folder was kept")
}

do {
    section("Keeping a bundle in step with the list")

    // What a shortcut does is written down twice, and the bundle is the copy
    // that runs: the Dock, Finder and Spotlight start the binary inside it and
    // ask Graft nothing. On this machine the two disagreed — the list said a
    // profile was back on its own chats while the bundle still named a source
    // — so opening it from the Dock grafted it again and put every one of the
    // profile's own chats in the stash a first mirror pass fills.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Saved-Src", account: "VSRC", org: "VORG", chats: ["one"])
    let profile = makeProfile("Claude-Saved", account: "VOWN", org: "VOWNORG")
    let shortcut = Shortcut(name: "Saved", folder: "Claude-Saved", source: .own)
    let bundle = try! Installer.install(shortcut, sourceDir: src)
    let configFile = bundle.appending(path: "Contents/Resources/graft.json")

    let written = try! JSONDecoder().decode(GraftConfig.self,
                                            from: Data(contentsOf: configFile))
    check(written.sourceDir == src.path, "an installed shortcut names its source in its bundle")
    check(chatsVisible(to: profile) == ["local_one.json"],
          "and the profile it describes has a copy of the source's chats")

    // The disagreement, made by hand the way a direct edit to the list makes
    // it: the bundle still borrowing, the list saying it stopped.
    check(!Installer.refreshConfig(for: shortcut, sourceDir: src),
          "a bundle that already agrees with the list is left alone")
    check(Installer.refreshConfig(for: shortcut, sourceDir: nil),
          "one that disagrees is brought back into line")

    let after = try! JSONDecoder().decode(GraftConfig.self,
                                          from: Data(contentsOf: configFile))
    check(after.sourceDir == nil,
          "so a shortcut put back on its own chats stops borrowing from the Dock too")
    check(after.profileDir == shortcut.profileDir.path,
          "and the profile it opens is untouched")

    // A bundle can fall out of step at any version, and it was the current one
    // that did, so this cannot be gated on the version the way the launcher is.
    try! Data("not json".utf8).write(to: configFile)
    check(Installer.refreshConfig(for: shortcut, sourceDir: nil),
          "a graft.json that will not parse is rewritten rather than trusted")
    Installer.uninstall(shortcut)
}

do {
    // Deleting the profile is the other way out of a pair: nothing is left to
    // settle, and a pair naming a folder that is gone would go on being tried
    // by every launcher that ran.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let src = makeProfile("Claude-Doom-Src", account: "DSRC", org: "DSORG", chats: ["one"])
    let doomed = makeProfile("Claude-Doom", account: "DOWN", org: "DOWNORG")
    Graft.apply(GraftConfig(profileDir: doomed.path, sourceDir: src.path))
    check(!Graft.loadMirrorState().pairs.isEmpty, "the pair is remembered while the profile is there")
    try! Graft.deleteProfile(doomed)
    check(Graft.loadMirrorState().pairs.isEmpty, "deleting the profile takes its pairs with it")
}

// MARK: - A folder this app emptied

section("A folder this app emptied")

do {
    // The loss this section exists for. A mirror's first pass moves the
    // profile's own chats to the hidden sibling and leaves a real, readable,
    // empty folder in their place, and nothing downstream could tell that apart
    // from a person clearing their sidebar. Two things acted on it at once: the
    // pass that keeps two folders in step deleted every record from the profile
    // the chats had been borrowed from, and the sweep withdrew the lot for good.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let one = support.appending(path: "Emptied-One")
    let other = support.appending(path: "Emptied-Other")
    for dir in [one, other] {
        try? fm.removeItem(at: dir)
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        try! "chat \(name)".write(to: other.appending(path: name), atomically: true, encoding: .utf8)
    }

    Graft.mirrorChatFolders(one, other)
    check(Graft.exists(one.appending(path: "local_b.json")),
          "a first pass gives the empty side the records the other holds")
    check(Graft.loadMirrorState().pairs[Graft.pairKey(one, other)]?.count == 3,
          "and writes down what the two of them agreed on")

    // The stash, as `openForMirror` performs it: everything out at once, a real
    // empty folder left standing in its place.
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        try! fm.removeItem(at: one.appending(path: name))
    }
    Graft.mirrorChatFolders(one, other)
    check(Graft.exists(other.appending(path: "local_a.json"))
          && Graft.exists(other.appending(path: "local_b.json"))
          && Graft.exists(other.appending(path: "local_c.json")),
          "a side emptied all at once is a folder that was moved, not a sidebar someone cleared")
    check(Graft.exists(one.appending(path: "local_a.json")),
          "so the emptied side is filled again rather than the full one emptied")

    // And the guard must not swallow the deletions the pass was written for.
    // Taken with `try?`, since a broken guard leaves nothing here to remove and
    // the checks below are how that should be reported, not a trap that takes
    // the rest of the suite down with it.
    try? fm.removeItem(at: one.appending(path: "local_b.json"))
    Graft.mirrorChatFolders(one, other)
    check(!Graft.exists(other.appending(path: "local_b.json")),
          "one chat deleted out of three still carries across")
    check(Graft.exists(other.appending(path: "local_a.json"))
          && Graft.exists(other.appending(path: "local_c.json")),
          "and takes none of the others with it")

    for dir in [one, other] { try? fm.removeItem(at: dir) }
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // Where a profile's chats live is `<account>/<org>`, and signing in again
    // can move both. The pair an earlier pass wrote down then names a folder
    // nobody writes to, which is a folder that looks emptied, which is the
    // deletion above arriving by another road.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let store = support.appending(path: "Claude-Moved/claude-code-sessions")
    let was = store.appending(path: "OLD-ACCOUNT/OLD-ORG")
    let now = store.appending(path: "NEW-ACCOUNT/NEW-ORG")
    let theirs = support.appending(path: "Claude-Moved-Src/claude-code-sessions/S/S-ORG")
    for dir in [was, now, theirs] {
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    var state = Graft.MirrorState()
    state.pairs[Graft.pairKey(was, theirs)] = ["local_1.json": "beef"]
    state.pairs[Graft.pairKey(now, theirs)] = ["local_2.json": "cafe"]
    Graft.saveMirrorState(state)

    Graft.forgetStalePairs(under: store, keeping: [(mine: now, theirs: theirs)])
    let left = Graft.loadMirrorState().pairs
    check(left[Graft.pairKey(now, theirs)] != nil,
          "the pair for the folder the profile actually writes to is left alone")
    check(left[Graft.pairKey(was, theirs)] == nil,
          "and the one naming where its chats used to live is dropped before it can be squared up")

    try? fm.removeItem(at: support.appending(path: "Claude-Moved"))
    try? fm.removeItem(at: support.appending(path: "Claude-Moved-Src"))
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // The other half of the same emptying, and the half that made it permanent.
    // The store is real, readable, and holds nothing, so a sweep read every
    // record remembered there as a chat deleted by hand. `mayFileRecords` has
    // asked about the stash on the way in all along; withdrawing is forever, so
    // it has to be asked on the way out too.
    let profile = support.appending(path: "Claude-Emptied")
    let store = profile.appending(path: "claude-code-sessions/EEEE/ORG-E")
    try? fm.removeItem(at: profile)
    try! fm.createDirectory(at: store, withIntermediateDirectories: true)
    try! fm.createDirectory(at: store.deletingLastPathComponent().appending(path: ".ORG-E.graft-own"),
                            withIntermediateDirectories: true)

    // A pass has already found it missing once, so the next one withdraws it.
    Graft.saveSessionRecordState(Graft.SessionRecordState(records: ["e-1": store.path],
                                                          vanished: ["e-1"]))
    Graft.fileMissingSessionRecords(filingInto: [profile], now: Date())
    check(!Graft.loadSessionRecordState().withdrawn.contains("e-1"),
          "a store with a stash beside it is one this app emptied, so nothing in it is withdrawn")
    check(Graft.loadSessionRecordState().records["e-1"] == store.path,
          "and where the record was is still remembered, for when the stash comes back")

    // With the stash gone the folder is the profile's own again, and an empty
    // one really is a sidebar someone cleared.
    try! fm.removeItem(at: store.deletingLastPathComponent().appending(path: ".ORG-E.graft-own"))
    Graft.saveSessionRecordState(Graft.SessionRecordState(records: ["e-1": store.path],
                                                          vanished: ["e-1"]))
    Graft.fileMissingSessionRecords(filingInto: [profile], now: Date())
    check(Graft.loadSessionRecordState().withdrawn.contains("e-1"),
          "with no stash beside it, a record twice missing from a store that was read is one somebody deleted")

    try? fm.removeItem(at: profile)
}

// MARK: - A store put away whole

section("A store put away whole")

do {
    // There are two shapes of stash and only one of them leaves a sibling. A
    // cross-account graft puts the organization folder away and the sibling
    // sits beside it; two profiles on one account put the whole store away, and
    // then there is no folder left for a sibling to stand next to. Everything
    // that asked only about the sibling waved the second shape through.
    let profile = support.appending(path: "Claude-Whole-Store")
    let org = profile.appending(path: "claude-code-sessions/WWWW/ORG-W")
    try? fm.removeItem(at: profile)
    try! fm.createDirectory(at: org, withIntermediateDirectories: true)

    check(!Graft.isStashedAway(org),
          "a folder with nothing put away above it is not a folder this app stashed")

    let sibling = org.deletingLastPathComponent().appending(path: ".ORG-W.graft-own")
    try! fm.createDirectory(at: sibling, withIntermediateDirectories: true)
    check(Graft.isStashedAway(org),
          "a stash beside the organization folder is the shape a cross-account graft leaves")
    try! fm.removeItem(at: sibling)

    // Exactly what `openForMirror` does to a store on a same-account first pass.
    try! fm.moveItem(at: profile.appending(path: "claude-code-sessions"),
                     to: profile.appending(path: ".claude-code-sessions.graft-own"))
    try! fm.createDirectory(at: profile.appending(path: "claude-code-sessions"),
                            withIntermediateDirectories: true)
    check(Graft.isStashedAway(org),
          "and a store put away whole is the shape a same-account graft leaves, two levels up")
    check(!Graft.mayFileRecords(inOrganisation: org, storesRead: []),
          "so nothing is filed into the folder standing in its place")

    try? fm.removeItem(at: profile)
}

do {
    // The same thing again, as a whole pass rather than one rule: the store put
    // away, a transcript on disk with nobody's record naming it, and a sweep
    // that used to answer that by rebuilding the folder and filing the lot into
    // it — every chat a second time, none of them archived, the real store
    // orphaned in the stash beside it.
    let account = "1e1e1e1e-1e1e-4e1e-8e1e-1e1e1e1e1e1e"
    let org = "1f1f1f1f-1f1f-4f1f-8f1f-1f1f1f1f1f1f"
    let session = "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a"
    let profile = makeProfile("Claude-Refiled", account: account, org: org, chats: ["mine"])
    makeTranscript(session: session, owner: account, org: org, title: "Borrowed")

    let store = profile.appending(path: "claude-code-sessions")
    try! fm.moveItem(at: store, to: profile.appending(path: ".claude-code-sessions.graft-own"))
    try! fm.createDirectory(at: store, withIntermediateDirectories: true)

    Graft.fileMissingSessionRecords(filingInto: [profile], now: Date())
    check(!Graft.exists(store.appending(path: account).appending(path: org)),
          "a sweep does not build an organization folder back up inside a store this app emptied")
    check(Graft.exists(profile.appending(path: ".claude-code-sessions.graft-own")
                        .appending(path: account).appending(path: org)
                        .appending(path: "local_mine.json")),
          "and the profile's real history stays where the graft put it")

    try? fm.removeItem(at: profile)
    try? fm.removeItem(at: Graft.claudeProjects.appending(path: "-Users-test-work")
                        .appending(path: "\(session).jsonl"))
}

// MARK: - A source with nothing under the account

section("A source with nothing to share")

do {
    // The store went to the stash before the source was consulted, so a source
    // that turned out to hold nothing under this account left the borrowing
    // profile's whole history hidden behind an empty folder — and wrote down no
    // pair, so every launch after did it again.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let account = "2a2a2a2a-2a2a-4a2a-8a2a-2a2a2a2a2a2a"
    let source = makeProfile("Claude-Empty-Source", account: account, org: "ORG-S", chats: ["theirs"])
    let borrower = makeProfile("Claude-Empty-Borrower", account: account, org: "ORG-B", chats: ["own-1", "own-2"])

    // One store the source can share and one it cannot: the account directory
    // under `local-agent-mode-sessions` is spelled some other way entirely.
    let agent = source.appending(path: "local-agent-mode-sessions")
    try! fm.removeItem(at: agent.appending(path: account))
    try! fm.createDirectory(at: agent.appending(path: "somebody-else/ORG-X"),
                            withIntermediateDirectories: true)

    Graft.graft(from: source, into: borrower)
    Graft.graft(from: source, into: borrower)

    let mine = borrower.appending(path: "local-agent-mode-sessions")
        .appending(path: account).appending(path: "ORG-B")
    check(Graft.exists(mine.appending(path: "local_own-1.json"))
          && Graft.exists(mine.appending(path: "local_own-2.json")),
          "a source holding nothing under this account leaves the profile's own chats where they are")
    check(!Graft.exists(borrower.appending(path: ".local-agent-mode-sessions.graft-own")),
          "and nothing of the profile's goes to the stash on the strength of a share that cannot happen")
    check(Graft.exists(borrower.appending(path: "claude-code-sessions")
                        .appending(path: account).appending(path: "ORG-S")
                        .appending(path: "local_theirs.json")),
          "while the store the source can share is shared as usual")

    try? fm.removeItem(at: source)
    try? fm.removeItem(at: borrower)
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // `<accountUuid>/<orgUuid>` is the shape, except where it is not: a profile
    // in local mode was measured naming both halves by their first eight
    // characters, in one store and not the other, for the same account.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let account = "ed417e0f-5edd-45dd-9a81-a9925b2c57dc"
    let source = support.appending(path: "Claude-Short-Source")
    let borrower = support.appending(path: "Claude-Short-Borrower")
    for (profile, chat) in [(source, "theirs"), (borrower, "own")] {
        try? fm.removeItem(at: profile)
        let dir = profile.appending(path: "local-agent-mode-sessions/ed417e0f/00000000")
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
        try! "{}".write(to: dir.appending(path: "local_\(chat).json"), atomically: true, encoding: .utf8)
        try! JSONSerialization.data(withJSONObject: ["lastKnownAccountUuid": account])
            .write(to: profile.appending(path: "config.json"))
    }

    check(Graft.counterpartDirectory(in: source.appending(path: "local-agent-mode-sessions"),
                                     for: account) == "ed417e0f",
          "a store spelling an account short is still that account's store")
    check(Graft.counterpartDirectory(in: source.appending(path: "local-agent-mode-sessions"),
                                     for: "ffffffff-5edd-45dd-9a81-a9925b2c57dc") == nil,
          "and an account no directory there begins with is not in it at all")

    Graft.graft(from: source, into: borrower)
    let mine = borrower.appending(path: "local-agent-mode-sessions/ed417e0f/00000000")
    check(Graft.exists(mine.appending(path: "local_own.json")),
          "a graft between two such profiles keeps what the borrower already had")
    check(Graft.exists(mine.appending(path: "local_theirs.json")),
          "and merges the source's chats into the folder the borrower actually reads")

    try? fm.removeItem(at: source)
    try? fm.removeItem(at: borrower)
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // Claude writes `<org>.profile-origin.json` beside the organization folders
    // as an organization is created, so for a moment it is the newest thing in
    // the account directory — and it was handed back as one.
    let account = support.appending(path: "Claude-Origin/claude-code-sessions/OOOO")
    try? fm.removeItem(at: support.appending(path: "Claude-Origin"))
    try! fm.createDirectory(at: account.appending(path: "ORG-O"), withIntermediateDirectories: true)
    try! "{\"mode\":\"local\"}".write(to: account.appending(path: "ORG-O.profile-origin.json"),
                                      atomically: true, encoding: .utf8)
    check(Graft.newestChild(of: account) == "ORG-O",
          "the newest child of an account directory is a directory, whatever else was written later")
    try? fm.removeItem(at: support.appending(path: "Claude-Origin"))
}

do {
    // It sat behind the same-account branch's early exit, so two profiles on
    // one account never shared it while two on different accounts did.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let account = "3a3a3a3a-3a3a-4a3a-8a3a-3a3a3a3a3a3a"
    let source = makeProfile("Claude-Skills-Source", account: account, org: "ORG-K", chats: ["k"])
    let borrower = makeProfile("Claude-Skills-Borrower", account: account, org: "ORG-K")
    try! fm.createDirectory(at: source.appending(path: "claude-code-sessions/skills-plugin/ORG-K"),
                            withIntermediateDirectories: true)

    Graft.graft(from: source, into: borrower)
    check(Graft.isSymlink(borrower.appending(path: "claude-code-sessions/skills-plugin")),
          "two profiles on one account share the plugin folder, as two on different accounts always did")
    check(Graft.exists(borrower.appending(path: "claude-code-sessions")
                        .appending(path: account).appending(path: "ORG-K")
                        .appending(path: "local_k.json")),
          "and the link is made after the store is opened, so it does not go to the stash with everything else")

    try? fm.removeItem(at: source)
    try? fm.removeItem(at: borrower)
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

// MARK: - Chats left behind by an account switch

section("Chats left in another profile")

do {
    func writeChat(_ id: String, titled title: String, at dir: URL,
                   age: TimeInterval = 0, lastActive: Date? = nil) {
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appending(path: "local_\(id).json")
        var record: [String: Any] = ["cliSessionId": id, "title": title]
        if let lastActive {
            record["lastActivityAt"] = lastActive.timeIntervalSince1970 * 1000
        }
        try! JSONSerialization.data(withJSONObject: record).write(to: file)
        try? fm.setAttributes([.modificationDate: Date().addingTimeInterval(-age)],
                              ofItemAtPath: file.path)
    }

    func workFolder(of profile: URL, store: String = "claude-code-sessions") -> URL {
        profile.appending(path: store).appending(path: "work").appending(path: "ORG-W")
    }

    // The shape switching accounts inside one Claude leaves behind: a single
    // profile holding both histories, keyed by account, with the one it is not
    // signed into sitting there untouched.
    let main = makeProfile("Claude", account: "personal", org: "ORG-P", chats: ["p1"])
    writeChat("w1", titled: "First look at the codebase", at: workFolder(of: main), age: 300)
    writeChat("w2", titled: "Invoice parser", at: workFolder(of: main), age: 120)
    writeChat("w3", titled: "Rewriting the deploy script", at: workFolder(of: main))
    writeChat("w4", titled: "A local agent chat",
              at: workFolder(of: main, store: "local-agent-mode-sessions"), age: 60)
    try! "1700000000".write(to: workFolder(of: main).appending(path: "deleted_gone"),
                            atomically: true, encoding: .utf8)

    let second = makeProfile("Claude-Work", account: "work")
    let found = Graft.chatsElsewhere(for: second, among: Graft.sessionStoreProfiles())

    check(found?.profile.lastPathComponent == "Claude",
          "a profile signed in with nothing of its own finds the chats another profile holds for its account")
    check(found?.account == "work", "and it is asked about the account this profile is signed into")
    check(found?.count == 4, "both chat stores are counted, not only the sessions one")
    check(found?.merging == false,
          "a profile with nothing of its own is told this is a history arriving, not two being merged")
    check(found?.chats.first?.title == "Rewriting the deploy script",
          "the newest chat is named first, since a count alone does not say which history this is")
    check(found?.chats.contains { $0.title == "A local agent chat" } == true,
          "and the titles reach across both stores")
    check(found?.chats.first?.lastActive != nil,
          "each one carries when it was last active, which is the other half of recognising it")

    check(Graft.chatsElsewhere(for: main, among: Graft.sessionStoreProfiles()) == nil,
          "a profile holding every chat the others have is offered nothing")

    let signedOut = makeProfile("Claude-New", account: nil)
    check(Graft.chatsElsewhere(for: signedOut, among: Graft.sessionStoreProfiles()) == nil,
          "a profile that has never been signed in is offered nothing")

    try! "{ truncated".write(to: signedOut.appending(path: "config.json"),
                             atomically: true, encoding: .utf8)
    check(Graft.chatsElsewhere(for: signedOut, among: Graft.sessionStoreProfiles()) == nil,
          "a config caught mid-rename is not read as an account with chats waiting for it")

    let busy = makeProfile("Claude-Busy", account: "work", org: "ORG-W", chats: ["own"])
    check(Graft.chatsElsewhere(for: busy, among: Graft.sessionStoreProfiles())?.count == 4,
          "a profile with a history of its own is offered the chats it has not got as well")
    check(Graft.chatsElsewhere(for: busy, among: Graft.sessionStoreProfiles())?.merging == true,
          "and is told the two sets are merged rather than one standing in for the other")
    writeChat("w1", titled: "First look at the codebase", at: workFolder(of: busy))
    check(Graft.chatsElsewhere(for: busy, among: Graft.sessionStoreProfiles())?.count == 3,
          "the count is only what is missing, so it is the number that will actually arrive")
    try? fm.removeItem(at: busy)

    // Ordered by the moment inside the record, not by the file it sits in.
    let stamped = makeProfile("Claude-Stamped", account: "stamps")
    let stampedSource = main.appending(path: "claude-code-sessions")
        .appending(path: "stamps").appending(path: "ORG-S")
    writeChat("late", titled: "Touched last, active first", at: stampedSource,
              age: 9000, lastActive: Date())
    writeChat("early", titled: "Touched first, active last", at: stampedSource,
              lastActive: Date().addingTimeInterval(-9000))
    check(Graft.chatsElsewhere(for: stamped, among: Graft.sessionStoreProfiles())?
            .chats.first?.title == "Touched last, active first",
          "a record says when it was last active, and that outranks when its file was written")
    try? fm.removeItem(at: stamped)

    let grafted = makeProfile("Claude-Grafted", account: "work")
    linkTheOldWay(grafted.appending(path: "claude-code-sessions"), to: workFolder(of: main))
    check(Graft.chatsElsewhere(for: grafted, among: Graft.sessionStoreProfiles()) == nil,
          "a grafted profile's emptiness is this app's own doing, so it is offered nothing")
    try? fm.removeItem(at: grafted)

    // The copy itself.
    let copied = Graft.adoptChats(from: main, into: second, account: "work")
    check(copied.copied == 4,
          "the count answers in chats, so it is the same number the offer put on screen")
    check(chatsVisible(to: second) == ["local_w1.json", "local_w2.json", "local_w3.json"],
          "the chats arrive in the profile that was signed into that account")
    check(Graft.exists(workFolder(of: second, store: "local-agent-mode-sessions")
                        .appending(path: "local_w4.json")),
          "and the other store is filled as well as the sessions one")
    check(Graft.exists(workFolder(of: second).appending(path: "deleted_gone")),
          "a chat deleted over there stays deleted here, rather than coming back as new")
    check(chatsVisible(to: main).contains("local_w3.json"),
          "the originals stay where they are, so the other profile is unchanged by this")
    check(Graft.exists(workFolder(of: main).appending(path: "local_w3.json")),
          "and a profile this app did not make has nothing taken out of it")

    check(Graft.chatsElsewhere(for: second, among: Graft.sessionStoreProfiles()) == nil,
          "once they are here the offer stops being made")

    try! JSONSerialization.data(withJSONObject: ["cliSessionId": "w3", "isArchived": true])
        .write(to: workFolder(of: second).appending(path: "local_w3.json"))
    check(Graft.adoptChats(from: main, into: second, account: "work").copied == 0,
          "a second run copies nothing, having nothing new to bring")
    let kept = (try? Data(contentsOf: workFolder(of: second).appending(path: "local_w3.json")))
        .flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
    check(kept?["isArchived"] as? Bool == true,
          "so a chat archived here since is not written back over with the copy it came from")

    check(Graft.adoptChats(from: main, into: main, account: "work").copied == 0,
          "a profile is never asked to copy its own chats onto themselves")
    try? fm.removeItem(at: second)
    try? fm.removeItem(at: signedOut)

    // Nothing moves while any Claude is up, this profile's or anybody's.
    let waiting = makeProfile("Claude-Waiting", account: "work")
    Graft.runningClaudesOverride = { [Graft.mainProfile] }
    let refused = Graft.adoptChats(from: main, into: waiting, account: "work")
    check(refused.running.map(\.lastPathComponent) == ["Claude"],
          "a copy asked for while a Claude is open says which one to quit")
    check(refused.copied == 0 && chatsVisible(to: waiting).isEmpty,
          "and copies nothing, rather than landing records under an instance that may write over them")
    Graft.runningClaudesOverride = { [] }
    check(Graft.adoptChats(from: main, into: waiting, account: "work").copied == 4,
          "the same press on a quiet machine brings them across")
    try? fm.removeItem(at: waiting)

    // A folder that is not the profile's to fill.
    let stashed = makeProfile("Claude-Stashed", account: "work")
    try! fm.createDirectory(at: Graft.stashURL(for: stashed.appending(path: "claude-code-sessions")
                                                .appending(path: "work")),
                            withIntermediateDirectories: true)
    let landed = Graft.adoptChats(from: main, into: stashed, account: "work").copied
    check(chatsVisible(to: stashed).isEmpty,
          "a folder this app has stashed away is left alone rather than filled behind the graft holding it")
    check(landed == 1 && Graft.exists(workFolder(of: stashed, store: "local-agent-mode-sessions")
                                        .appending(path: "local_w4.json")),
          "and the refusal is per store, so the one that is still the profile's own is filled")
    try? fm.removeItem(at: stashed)

    // The shortened spelling a profile in local mode uses.
    let short = makeProfile("Claude-Short", account: "workaccount-1234")
    let shortWork = short.appending(path: "claude-code-sessions").appending(path: "workacco")
    try! fm.createDirectory(at: shortWork.appending(path: "ORG-W"), withIntermediateDirectories: true)
    writeChat("s1", titled: "Long-named account",
              at: main.appending(path: "claude-code-sessions")
                  .appending(path: "workaccount-1234").appending(path: "ORG-W"))
    check(Graft.adoptChats(from: main, into: short, account: "workaccount-1234").copied == 1,
          "a store spelling the account short is filled where it really keeps it")
    check(Graft.exists(shortWork.appending(path: "ORG-W").appending(path: "local_s1.json")),
          "rather than beside it under the name the account was asked for")
    try? fm.removeItem(at: short)

    try? fm.removeItem(at: main)
}

// MARK: - Reading an absence

section("Reading an absence")

do {
    // A folder that is not there, with nothing stashed above it, is a folder
    // nobody writes to: the profile was deleted, or signing in again moved
    // <account>/<org>. Remembering it left the session out of sight on every
    // pass from then on — the transcript whole on disk and the chat in nobody's
    // sidebar, with no finding naming it either.
    let account = "4a4a4a4a-4a4a-4a4a-8a4a-4a4a4a4a4a4a"
    let org = "4b4b4b4b-4b4b-4b4b-8b4b-4b4b4b4b4b4b"
    let session = "4c4c4c4c-4c4c-4c4c-8c4c-4c4c4c4c4c4c"
    let was = makeProfile("Claude-Was-Here", account: account, org: org)
    makeTranscript(session: session, owner: account, org: org, title: "Moved on")

    Graft.fileMissingSessionRecords(filingInto: [was], now: Date())
    let filed = was.appending(path: "claude-code-sessions").appending(path: account)
        .appending(path: org).appending(path: "local_\(session).json")
    check(Graft.exists(filed), "a first pass files the record where the account lives")
    check(Graft.loadSessionRecordState().records[session] != nil,
          "and remembers which folder it put it in")

    // The profile goes, and the account turns up on another one.
    try? fm.removeItem(at: was)
    let now = makeProfile("Claude-Is-Here", account: account, org: org)
    Graft.fileMissingSessionRecords(filingInto: [now], now: Date())
    check(Graft.exists(now.appending(path: "claude-code-sessions").appending(path: account)
                        .appending(path: org).appending(path: "local_\(session).json")),
          "a record remembered at a folder that has gone is forgotten, so the session is filed where the account is now")
    check(!Graft.loadSessionRecordState().withdrawn.contains(session),
          "and forgetting where it was is not the same as deciding somebody deleted it")

    try? fm.removeItem(at: now)
    try? fm.removeItem(at: Graft.claudeProjects.appending(path: "-Users-test-work")
                        .appending(path: "\(session).jsonl"))
}

do {
    // The timing guess is for markers this app cannot read by name — a record
    // Claude wrote, under an id no transcript carries. It used to see every
    // marker on the machine, and they accumulate, so one deletion went on
    // suppressing whatever else had gone quiet in the minute before it.
    let account = "5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a"
    let org = "5b5b5b5b-5b5b-4b5b-8b5b-5b5b5b5b5b5b"
    let deleted = "5c5c5c5c-5c5c-4c5c-8c5c-5c5c5c5c5c5c"
    let neighbour = "5d5d5d5d-5d5d-4d5d-8d5d-5d5d5d5d5d5d"
    let profile = makeProfile("Claude-Marker", account: account, org: org)
    makeTranscript(session: deleted, owner: account, org: org, title: "The one deleted",
                   first: "2026-08-29T18:00:00.000Z", last: "2026-08-29T18:00:10.000Z")
    makeTranscript(session: neighbour, owner: account, org: org, title: "The one beside it",
                   first: "2026-08-29T17:59:40.000Z", last: "2026-08-29T17:59:50.000Z")

    // A marker naming the session outright, written twenty seconds after the
    // other one went quiet — which is well inside the window the guess uses.
    let store = profile.appending(path: "claude-code-sessions")
        .appending(path: account).appending(path: org)
    let when = Graft.sessionFacts(inTranscriptAt: Graft.claudeProjects
                                    .appending(path: "-Users-test-work")
                                    .appending(path: "\(deleted).jsonl"),
                                  cliSessionId: deleted)!.lastActivityAt
    try! "\(when)".write(to: store.appending(path: "deleted_\(deleted)"),
                         atomically: true, encoding: .utf8)

    Graft.fileMissingSessionRecords(filingInto: [profile], now: Date())
    check(!Graft.exists(store.appending(path: "local_\(deleted).json")),
          "a marker naming a session is read by name, and that session is not filed again")
    check(Graft.exists(store.appending(path: "local_\(neighbour).json")),
          "and it takes nothing else that happened to go quiet beside it")
    check(Graft.loadSessionRecordState().records[deleted] == nil,
          "a session withdrawn by a marker stops being remembered at the folder its record has gone from")

    try? fm.removeItem(at: profile)
    for session in [deleted, neighbour] {
        try? fm.removeItem(at: Graft.claudeProjects.appending(path: "-Users-test-work")
                            .appending(path: "\(session).jsonl"))
    }
}

do {
    // A plugin folder sits beside the account directories without being one. A
    // walk that took it for an account reported its organization folders as
    // chat stores of their own.
    let profile = makeProfile("Claude-Plugin-Walk", account: "6a6a6a6a", org: "6b6b6b6b", chats: ["p"])
    try! fm.createDirectory(at: profile.appending(path: "claude-code-sessions/skills-plugin/6b6b6b6b"),
                            withIntermediateDirectories: true)
    let read = Graft.sessionStoreContents().stores.filter { $0.hasPrefix(profile.path + "/") }
    check(read.count == 1, "a walk of the chat stores counts the account directories and not the plugin folder")
    try? fm.removeItem(at: profile)
}

// MARK: - What the mirror writes down

section("What the mirror writes down")

do {
    // The baseline is what makes an absence mean something, so it may only say
    // what the filesystem actually did. A removal was written down whether or
    // not it happened, and a removal that failed left a name on one side with
    // no baseline — which reads as a new chat, and put the deleted
    // conversation straight back where it had been deleted from.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let one = support.appending(path: "Refused/one")
    let other = support.appending(path: "Refused/other")
    for dir in [one, other] {
        try? fm.removeItem(at: dir)
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    for name in ["local_a.json", "local_b.json"] {
        try! "chat \(name)".write(to: one.appending(path: name), atomically: true, encoding: .utf8)
    }
    Graft.mirrorChatFolders(one, other)

    try! fm.removeItem(at: other.appending(path: "local_a.json"))
    try! fm.setAttributes([.posixPermissions: 0o555], ofItemAtPath: one.path)
    Graft.mirrorChatFolders(one, other)
    try! fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: one.path)

    check(Graft.loadMirrorState().pairs[Graft.pairKey(one, other)]?["local_a.json"] != nil,
          "a removal the filesystem refused is not written down as one")

    Graft.mirrorChatFolders(one, other)
    check(!Graft.exists(one.appending(path: "local_a.json")),
          "so the next pass carries the deletion across rather than undoing it")
    check(!Graft.exists(other.appending(path: "local_a.json")),
          "and the chat stays deleted on the side it was deleted from")

    for dir in [one, other] { try? fm.removeItem(at: dir) }
    try? fm.removeItem(at: support.appending(path: "Refused"))
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // A name neither side holds any more is never visited by a pass, so its
    // entry sat in the baseline for good — growing the file, and standing ready
    // to read the name coming back on one side alone as a deletion.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let one = support.appending(path: "Gone/one")
    let other = support.appending(path: "Gone/other")
    for dir in [one, other] {
        try? fm.removeItem(at: dir)
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    for name in ["local_a.json", "local_b.json", "local_c.json"] {
        try! "chat \(name)".write(to: one.appending(path: name), atomically: true, encoding: .utf8)
    }
    Graft.mirrorChatFolders(one, other)

    for dir in [one, other] { try! fm.removeItem(at: dir.appending(path: "local_a.json")) }
    Graft.mirrorChatFolders(one, other)
    let baseline = Graft.loadMirrorState().pairs[Graft.pairKey(one, other)] ?? [:]
    check(baseline.keys.sorted() == ["local_b.json", "local_c.json"],
          "a name gone from both sides is dropped from the baseline rather than kept for ever")

    // And what that entry used to do if the name ever came back.
    try! "chat local_a.json".write(to: one.appending(path: "local_a.json"),
                                   atomically: true, encoding: .utf8)
    Graft.mirrorChatFolders(one, other)
    check(Graft.exists(other.appending(path: "local_a.json")),
          "so a chat filed under that name again is a new chat, not a deletion to carry across")

    for dir in [one, other] { try? fm.removeItem(at: dir) }
    try? fm.removeItem(at: support.appending(path: "Gone"))
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // Two profiles on one account mirror every organization the account has, so
    // no single folder is the pair worth keeping.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let store = support.appending(path: "Claude-Many/claude-code-sessions")
    let first = store.appending(path: "ACC-M/ORG-1")
    let second = store.appending(path: "ACC-M/ORG-2")
    let stale = store.appending(path: "ACC-M/ORG-OLD")
    let theirs = support.appending(path: "Claude-Many-Src/claude-code-sessions/S/S-ORG")
    for dir in [first, second, stale, theirs] {
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    var state = Graft.MirrorState()
    for folder in [first, second, stale] {
        state.pairs[Graft.pairKey(folder, theirs)] = ["local_1.json": "beef"]
    }
    Graft.saveMirrorState(state)

    Graft.forgetStalePairs(under: store,
                           keeping: [(mine: first, theirs: theirs), (mine: second, theirs: theirs)])
    let left = Graft.loadMirrorState().pairs
    check(left[Graft.pairKey(first, theirs)] != nil && left[Graft.pairKey(second, theirs)] != nil,
          "every organization still being mirrored keeps its pair")
    check(left[Graft.pairKey(stale, theirs)] == nil,
          "and only the one naming a folder nobody writes to is dropped")

    try? fm.removeItem(at: support.appending(path: "Claude-Many"))
    try? fm.removeItem(at: support.appending(path: "Claude-Many-Src"))
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // A store lends as well as borrows. The pairs it lends are somebody else's
    // mirror, kept current by that profile's own launch, and dropping one here
    // reads as a first pass over there.
    try? fm.removeItem(at: Graft.mirrorStateFile)
    let store = support.appending(path: "Claude-Lent/claude-code-sessions")
    let mine = store.appending(path: "ACC-L/ORG-L")
    let theirs = support.appending(path: "Claude-Lent-Src/claude-code-sessions/S/S-ORG")
    let moved = support.appending(path: "Claude-Lent-Src/claude-code-sessions/S/S-ORG-NEW")
    let guest = support.appending(path: "Claude-Lent-Guest/claude-code-sessions/G/G-ORG")
    for dir in [mine, theirs, moved, guest] {
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
    }
    var state = Graft.MirrorState()
    state.pairs[Graft.pairKey(mine, theirs)] = ["local_1.json": "beef"]
    state.pairs[Graft.pairKey(guest, mine)] = ["local_2.json": "cafe"]
    Graft.saveMirrorState(state)

    check(Graft.pairHalves(Graft.pairKey(mine, theirs))?.borrower
            == mine.resolvingSymlinksInPath().path,
          "a pair is written with the borrowing folder first, which is the only record of which is which")
    check(Graft.mirrorPairs(borrowedBy: mine).count == 1,
          "so a folder is found by the half it borrows through")
    check(Graft.mirrorPairs(borrowedBy: theirs).isEmpty,
          "and the folder lent from borrows through nothing, however many pairs name it")
    check(Graft.mirrorPairs(under: theirs).count == 1,
          "though it is still under one, for the cases where both roles are equally finished")

    Graft.forgetStalePairs(under: store, keeping: [(mine: mine, theirs: theirs)])
    check(Graft.loadMirrorState().pairs[Graft.pairKey(mine, theirs)] != nil,
          "the pair this store borrows through is kept, being the one this pass is mirroring")

    // A source that moved leaves a pair as stale as a destination that moved,
    // and only the destination half was ever looked at.
    Graft.forgetStalePairs(under: store, keeping: [(mine: mine, theirs: moved)])
    check(Graft.loadMirrorState().pairs[Graft.pairKey(mine, theirs)] == nil,
          "so a pair naming the organization the source has stopped writing to is dropped as well")

    // And this profile signing in again moves the folder it lends from, which
    // says nothing about the guest borrowing through it: that pair is the
    // guest's mirror, its own launch is what squares it up, and dropping it
    // here is a first pass over there — the folder stashed, the merge fetched
    // back, the record of what the guest owns folded in with it.
    let elsewhere = store.appending(path: "ACC-L/ORG-NEW")
    try! fm.createDirectory(at: elsewhere, withIntermediateDirectories: true)
    Graft.forgetStalePairs(under: store, keeping: [(mine: elsewhere, theirs: moved)])
    check(Graft.loadMirrorState().pairs[Graft.pairKey(guest, mine)] != nil,
          "a pair this store only lends to is left to the profile that borrows through it")

    for name in ["Claude-Lent", "Claude-Lent-Src", "Claude-Lent-Guest"] {
        try? fm.removeItem(at: support.appending(path: name))
    }
    try? fm.removeItem(at: Graft.mirrorStateFile)
}

do {
    // Folding a stash back in is for a pair this can reason about. Two files
    // are one, and the live copy supersedes the stashed one; a directory
    // against a file is not, either way round, and guessing at it threw the
    // stashed one away.
    let profile = support.appending(path: "Claude-Odd-Stash")
    try? fm.removeItem(at: profile)
    try! fm.createDirectory(at: profile.appending(path: "claude_desktop_config.json"),
                            withIntermediateDirectories: true)
    let stash = profile.appending(path: ".claude_desktop_config.json.graft-own")
    try! "the profile's own".write(to: stash, atomically: true, encoding: .utf8)

    let source = support.appending(path: "Claude-Odd-Source")
    try? fm.removeItem(at: source)
    try! fm.createDirectory(at: source, withIntermediateDirectories: true)
    try! "the source's".write(to: source.appending(path: "claude_desktop_config.json"),
                              atomically: true, encoding: .utf8)
    Graft.relink(target: source.appending(path: "claude_desktop_config.json"),
                 at: profile.appending(path: "claude_desktop_config.json"))

    check((try? String(contentsOf: stash, encoding: .utf8)) == "the profile's own",
          "a stashed file against a live directory is left alone rather than guessed at")

    try? fm.removeItem(at: profile)
    try? fm.removeItem(at: source)
}


// MARK: - What the state report says

do {
    section("Reading the state of the stores")

    let login = support.appending(path: "claude.json")
    Graft.claudeConfigFileOverride = login
    defer { Graft.claudeConfigFileOverride = nil }

    // The command line keeps one login for the whole machine, and it is the
    // reason a chat typed into one profile turns up in another's sidebar.
    // Every report leads with it because every report is read by somebody who
    // has just watched that happen and concluded the profiles are syncing.
    let owner = "AAAAAAAA-0000-0000-0000-000000000001"
    let borrower = "BBBBBBBB-0000-0000-0000-000000000002"
    let org = "CCCCCCCC-0000-0000-0000-000000000003"
    try! JSONSerialization.data(withJSONObject: [
        "oauthAccount": ["accountUuid": owner, "organizationUuid": org],
    ]).write(to: login)

    check(Graft.commandLineLogin()?.account == owner,
          "the account every session on this machine is stamped with is read off the command line")

    let held = makeProfile("Report-Owner", account: owner, org: org, chats: ["r-1"])
    let other = makeProfile("Report-Borrower", account: borrower, org: org, chats: ["r-2"])

    let text = Graft.stateReportText(checkingRunning: false)
    check(text.contains("held by Report-Owner"),
          "and the report says which profile is holding it")
    check(text.contains("Report-Borrower is signed into BBBBBBBB"),
          "a profile signed into something else is named")
    check(text.contains("not two profiles syncing"),
          "and the report says outright that one shared login is not two profiles syncing")

    // The half-finished ungraft: a stash still holding what the profile owns
    // beside a folder holding almost nothing. Nothing was destroyed and
    // nothing looks wrong from inside the app, but the sidebar is short.
    let store = other.appending(path: "claude-code-sessions")
        .appending(path: borrower).appending(path: org)
    let stash = store.deletingLastPathComponent().appending(path: ".\(org).graft-own")
    try! fm.createDirectory(at: stash, withIntermediateDirectories: true)
    for chat in ["r-3", "r-4", "r-5"] {
        try! "{}".write(to: stash.appending(path: "local_\(chat).json"),
                        atomically: true, encoding: .utf8)
    }

    let short = Graft.stateReportText(checkingRunning: false)
    check(short.contains("3 stashed beside it"),
          "a stash beside a folder is counted rather than passed over")
    check(short.contains("the sidebar is short by 3"),
          "and a stash holding more than the folder reads as a graft undone without handing them back")

    // A device is a profile, so chats made under a previous one are shown as
    // coming from somewhere else however plainly they belong to the account.
    try! Data("QUFBQS1CQkJC".utf8).write(to: other.appending(path: "ant-did"))
    check(Graft.deviceIdentifier(of: other) == "AAAA-BBBB",
          "the device a profile registered as is read back from its own file")
    check(Graft.deviceIdentifier(of: held) == nil,
          "and a profile that has never registered one has none rather than a guess")

    try? fm.removeItem(at: held)
    try? fm.removeItem(at: other)
    try? fm.removeItem(at: login)
}

// MARK: - The log itself

do {
    section("Writing down what a pass did")

    try? fm.removeItem(at: Diagnostics.file)
    Diagnostics.who = "suite"
    Diagnostics.note("first", ["folder": support, "count": 2])
    Diagnostics.note("second", ["ok": true])

    let lines = ((try? String(contentsOf: Diagnostics.file, encoding: .utf8)) ?? "")
        .split(separator: "\n")
    check(lines.count == 2, "one line per event, so a log half-written by a killed pass still reads")

    let first = (try? JSONSerialization.jsonObject(with: Data(lines[0].utf8))) as? [String: Any]
    check(first?["event"] as? String == "first", "each line names its event")
    check(first?["who"] as? String == "suite",
          "and which process wrote it, since a launcher and the app run the same passes")
    check(first?["folder"] as? String == support.path,
          "a URL is written as its path rather than costing the whole line")

    // A field JSONSerialization refuses would otherwise take the event with
    // it, and the events worth having are the ones written mid-incident.
    Diagnostics.note("third", ["when": Date(), "what": URL(fileURLWithPath: "/tmp/x")])
    let all = ((try? String(contentsOf: Diagnostics.file, encoding: .utf8)) ?? "")
        .split(separator: "\n")
    check(all.count == 3, "an event carrying something unserialisable is still written")

    try? fm.removeItem(at: Diagnostics.file)
    Diagnostics.who = ProcessInfo.processInfo.processName
}

print("\n\(checks - failures)/\(checks) checks passed")
if failures > 0 {
    print("\(failures) FAILED")
    exit(1)
}
