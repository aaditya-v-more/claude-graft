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

print("\n\(checks - failures)/\(checks) checks passed")
if failures > 0 {
    print("\(failures) FAILED")
    exit(1)
}
