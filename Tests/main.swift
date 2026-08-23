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
check(Graft.processIDs(of: support.appending(path: "Claude-NeverLaunched")).isEmpty,
      "an unused profile has no processes")

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
    check(monitor.headline == 77, "the headline is the tightest five-hour window")

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

print("\n\(checks - failures)/\(checks) checks passed")
if failures > 0 {
    print("\(failures) FAILED")
    exit(1)
}
