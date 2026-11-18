import AppKit
import PDFKit
import ApplicationServices
import SwiftUI

// The real views run against empty profiles and an update service that never
// leaves this process. Nothing opens Claude or reads an account's credentials.
final class UpdateResponse: URLProtocol {
    static var available = false
    static var failing = false

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let code = Self.failing ? 503 : Self.available ? 200 : 204
        client?.urlProtocol(self, didReceive: HTTPURLResponse(
            url: request.url!, statusCode: code, httpVersion: nil, headerFields: nil)!,
            cacheStoragePolicy: .notAllowed)
        if code == 200 {
            client?.urlProtocol(self, didLoad: Data(#"{"currentRelease":"999.0.0"}"#.utf8))
        }
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

URLProtocol.registerClass(UpdateResponse.self)
let layoutSession: URLSession = {
    let configuration = URLSessionConfiguration.ephemeral
    configuration.protocolClasses = [UpdateResponse.self]
    return URLSession(configuration: configuration)
}()
let fm = FileManager.default
let temporary = fm.temporaryDirectory.appending(path: "graft-layout-\(UUID().uuidString)")
try fm.createDirectory(at: temporary, withIntermediateDirectories: true)
defer { try? fm.removeItem(at: temporary) }
Graft.applicationSupportOverride = temporary
Installer.installDirectoryOverride = temporary.appending(path: "Applications")
Installer.registersWithLaunchServices = false
Graft.runningClaudesOverride = { [] }
Shared.usage.mayPromptUnasked = false
// Claude 2 is created, borrows Claude's chats and draws on two accounts,
// which is the fullest a shortcut's pane gets; Claude 3 is a draft. A bundle
// carrying `graft.json` is all that makes a shortcut created.
var shortcut = Shortcut(name: "Claude 2")
shortcut.installedName = "Claude 2"
let draft = Shortcut(name: "Claude 3", source: .own)
Shared.store.shortcuts = [shortcut, draft]
let resources = Installer.installDirectoryOverride!.appending(path: "Claude 2.app/Contents/Resources")
try fm.createDirectory(at: resources, withIntermediateDirectories: true)
try JSONEncoder().encode(GraftConfig(profileDir: shortcut.profileDir.path, sourceDir: Graft.mainProfile.path))
    .write(to: resources.appending(path: "graft.json"))
// Claude's pane with accounts to draw on, one of them a Claude and one signed
// in here, written before the pool first reads them. Attached rather than
// started: nothing in here may run a relay or touch a Code environment.
let lentAccount = PoolAccount(id: UsagePoolController.accountPrefix + "lent-account", accountUuid: "lent-account",
                              email: "lent@example.com", organizationName: nil, scopes: ["user:inference"],
                              expires: Date().addingTimeInterval(3e7), added: Date(), sealed: "")
try fm.createDirectory(at: UsagePool.accountsFile.deletingLastPathComponent(), withIntermediateDirectories: true)
try JSONEncoder().encode([lentAccount]).write(to: UsagePool.accountsFile)
try JSONEncoder().encode(UsageSharing(lenders: [
    UsagePoolController.mainKey: [UsagePoolController.key(for: shortcut), lentAccount.id],
    UsagePoolController.key(for: shortcut): [UsagePoolController.mainKey, lentAccount.id],
])).write(to: UsagePool.settingsFile)
Shared.usagePool.attach(store: Shared.store, usage: Shared.usage)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
// Without it AppKit never starts answering accessibility requests, and the
// accessibility checks read every window from outside.
app.finishLaunching()
var checks = 0
var failures = 0
func check(_ condition: Bool, _ message: String) {
    checks += 1
    if !condition { failures += 1; print("FAIL  \(message)") }
}
func descendants(_ view: NSView) -> [NSView] {
    [view] + view.subviews.flatMap(descendants)
}
func settle() { RunLoop.main.run(until: Date().addingTimeInterval(0.3)) }

/// Each identified element's name as a reader in another process gets it:
/// its description, or its title when it has none. The run loop keeps turning
/// while the reader works, because every question it asks is answered here, on
/// the main thread, and waiting on it would leave nobody to answer.
func readFromOutside(_ window: NSWindow) -> [String: String] {
    guard let reader = Bundle.main.url(forResource: "outside-reader", withExtension: nil) else { return [:] }
    let process = Process()
    let pipe = Pipe()
    process.executableURL = reader
    process.arguments = [String(getpid()), window.title]
    process.standardOutput = pipe
    guard (try? process.run()) != nil else { return [:] }
    let deadline = Date().addingTimeInterval(20)
    while process.isRunning && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    if process.isRunning { process.terminate() }
    let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    var names: [String: String] = [:]
    for line in output.split(separator: "\n") {
        let fields = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
        guard fields.count == 4 else { continue }
        names[fields[0]] = fields[2].isEmpty ? fields[3] : fields[2]
    }
    return names
}

// MARK: - What VoiceOver and a script see

/// One element as VoiceOver and every other client of the accessibility API
/// sees it.
struct Seen {
    var role = "", subrole = "", label = "", title = "", titledBy = "", value = "", identifier = ""
    var frame = CGRect.zero
    /// What VoiceOver says the element is. A control in a grouped form has
    /// none of its own: AppKit links it to the label drawn beside it instead,
    /// and VoiceOver reads that.
    var name: String { [label, title, titledBy].first { !$0.isEmpty } ?? "" }
    var described: String { "\(role) “\(name)” \(identifier)" }
}

/// Reads `window` from outside, through the calls an assistive app makes,
/// from another thread while this one answers them.
///
/// Nothing asked from inside the process would do. SwiftUI builds a window's
/// accessibility tree only once a request has come in from outside, and the
/// elements it builds then do not adopt the protocol a local walk would go
/// through; both were measured, the first as an empty tree under every
/// window. Reading from outside is also the only way to see what VoiceOver
/// is actually given, which is the thing being checked.
func seen(_ window: NSWindow) -> [Seen]? {
    let title = window.title
    var result: [Seen]?
    var finished = false
    DispatchQueue.global(qos: .userInitiated).async {
        let app = AXUIElementCreateApplication(getpid())
        AXUIElementSetMessagingTimeout(app, 5)
        func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
            var value: CFTypeRef?
            return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
        }
        func text(_ element: AXUIElement, _ name: String) -> String {
            switch attribute(element, name) {
            case let value as String: return value
            case let value as NSNumber: return value.stringValue
            default: return ""
            }
        }
        func frame(_ element: AXUIElement) -> CGRect {
            var origin = CGPoint.zero, size = CGSize.zero
            if let value = attribute(element, kAXPositionAttribute) { AXValueGetValue(value as! AXValue, .cgPoint, &origin) }
            if let value = attribute(element, kAXSizeAttribute) { AXValueGetValue(value as! AXValue, .cgSize, &size) }
            return CGRect(origin: origin, size: size)
        }
        // A scroller's arrows belong to AppKit, which names them itself.
        func walk(_ element: AXUIElement) -> [Seen] {
            let role = text(element, kAXRoleAttribute)
            guard role != kAXScrollBarRole else { return [] }
            // A bar's value is a fraction, and what VoiceOver says is the
            // description that comes with it.
            let described = text(element, "AXValueDescription")
            let titledBy = attribute(element, kAXTitleUIElementAttribute)
                .flatMap { CFGetTypeID($0) == AXUIElementGetTypeID() ? ($0 as! AXUIElement) : nil }
                .map { text($0, kAXValueAttribute) } ?? ""
            let here = Seen(role: role, subrole: text(element, kAXSubroleAttribute),
                            label: text(element, kAXDescriptionAttribute),
                            title: text(element, kAXTitleAttribute),
                            titledBy: titledBy,
                            value: described.isEmpty ? text(element, kAXValueAttribute) : described,
                            identifier: text(element, "AXIdentifier"),
                            frame: frame(element))
            return [here] + ((attribute(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []).flatMap(walk)
        }
        let windows = (attribute(app, kAXWindowsAttribute) as? [AXUIElement]) ?? []
        // A subtitle is read as part of the title, after it.
        let found = windows.first { text($0, kAXTitleAttribute).hasPrefix(title) }.map(walk)
        DispatchQueue.main.async { result = found; finished = true }
    }
    let deadline = Date().addingTimeInterval(30)
    while !finished && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    return result
}

let controlRoles: Set<String> = [
    kAXButtonRole, kAXCheckBoxRole, kAXRadioButtonRole, kAXPopUpButtonRole, kAXMenuButtonRole,
    kAXTextFieldRole, kAXComboBoxRole, kAXSliderRole, kAXIncrementorRole, kAXDisclosureTriangleRole, "AXLink",
]
/// The window's own buttons, which AppKit draws and names.
let windowButtons: Set<String> = [kAXCloseButtonSubrole, kAXMinimizeButtonSubrole,
                                  kAXZoomButtonSubrole, kAXFullScreenButtonSubrole]

/// The rules every surface is held to in every state it is drawn in. A
/// control says what it is, can be asked for by a name this app gave it, and
/// is the only thing answering to that name; a picture is either part of
/// something that says what it shows or kept out of VoiceOver's way.
@discardableResult
func checkAccessibility(_ window: NSWindow, surface: String, _ place: String) -> [Seen] {
    guard let elements = seen(window), elements.count > 1 else {
        check(false, "\(place): the window can be read the way VoiceOver reads it, which needs Accessibility allowed for whatever runs this suite")
        return []
    }
    let ours = surface + "."
    let controls = elements.filter { controlRoles.contains($0.role) && !windowButtons.contains($0.subrole) }
    let unnamed = controls.filter { $0.name.isEmpty }
    check(unnamed.isEmpty, "\(place): every control says what it is \(unnamed.map(\.described))")
    // The sidebar's own toggle is the split view's, which AppKit names in
    // the person's language and this app does not build. It is known by the
    // toolbar item SwiftUI puts it in, never by its words.
    let systemToggles = Set((window.toolbar?.items ?? [])
        .filter { $0.itemIdentifier.rawValue.hasSuffix("navigationSplitView.toggleSidebar") }
        .map(\.label))
    let unidentified = controls.filter { !$0.identifier.hasPrefix(ours) && !systemToggles.contains($0.name) }
    check(unidentified.isEmpty,
          "\(place): every control can be asked for by a name this app gave it \(unidentified.map(\.described))")
    let roleless = elements.filter { $0.role == kAXUnknownRole && $0.identifier.hasPrefix(ours) }
    check(roleless.isEmpty, "\(place): nothing this app names is left without a role \(roleless.map(\.described))")
    let pictures = elements.filter { $0.role == kAXImageRole && !$0.identifier.hasPrefix(ours) }
    check(pictures.isEmpty, "\(place): no picture is read out under its symbol's own name \(pictures.map(\.described))")
    let names = elements.map(\.identifier).filter { $0.hasPrefix(ours) }
    let repeated = Dictionary(grouping: names, by: { $0 }).filter { $0.value.count > 1 }.keys.sorted()
    check(repeated.isEmpty, "\(place): no two elements answer to one identifier \(repeated)")
    if ProcessInfo.processInfo.environment["GRAFT_AX_DEBUG"] == "1" {
        print("TREE \(place)")
        for element in elements { print("    \(element.described) value=\(element.value)") }
    }
    return elements
}

func element(_ name: String, in elements: [Seen]) -> Seen? {
    elements.first { $0.identifier == name }
}

/// Brings the window on screen long enough for the window server to draw it
/// with its frame, and asks `screencapture` for that one window.
func capture(_ window: NSWindow, as name: String, in directory: String) {
    let wasVisible = window.isVisible && window.frame.minX > -5000
    let origin = window.frame.origin
    if !wasVisible { window.setFrameOrigin(NSPoint(x: 80, y: 120)) }
    window.orderFrontRegardless()
    RunLoop.main.run(until: Date().addingTimeInterval(1.2))
    try? fm.createDirectory(atPath: directory, withIntermediateDirectories: true)
    let shot = Process()
    shot.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    shot.arguments = ["-x", "-o", "-l", String(window.windowNumber), "\(directory)/\(name).png"]
    try? shot.run()
    shot.waitUntilExit()
    if !wasVisible {
        window.setFrameOrigin(origin)
        window.orderBack(nil)
        settle()
    }
}

/// The height a scroll view shows its document in: its clip view less what
/// sits over it, the toolbar above and the buttons along the bottom. Those
/// insets reach the clip view either through the scroll view, which hands
/// its own on, or set on the clip view alone; counting both counts the
/// toolbar twice, and counting neither passed with the last row hidden.
func room(in scroll: NSScrollView) -> CGFloat {
    let clip = scroll.contentView
    return clip.bounds.height
        - max(clip.contentInsets.top, scroll.contentInsets.top)
        - max(clip.contentInsets.bottom, scroll.contentInsets.bottom)
}

final class LayoutWindow: NSWindow {
    // CI's virtual display can be smaller than the largest test window. Keep
    // AppKit from shrinking that case to the screen instead of testing it.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        frameRect
    }
}

// Figures for the two Claudes, with all three bars, as an account on a plan
// with a Fable allowance shows them. A usage pass in here finds no login and
// would leave them empty, so they are put back before every measurement.
let taken = Date()
let pageFigures = [Graft.mainProfile, shortcut.profileDir].map { profile in
    UsageMonitor.Entry(name: profile == Graft.mainProfile ? "Claude" : "Claude 2", profile: profile,
                       usage: Graft.Usage(fiveHour: 6, week: 20, organization: nil, sampled: taken,
                                          fiveHourReset: taken.addingTimeInterval(4 * 3600),
                                          weekReset: taken.addingTimeInterval(4 * 86400),
                                          fable: 3, fableReset: taken.addingTimeInterval(4 * 86400)),
                       isRunning: false, shortcut: profile == Graft.mainProfile ? nil : shortcut.id,
                       isLive: true, plan: "Max", refreshed: taken)
}

func sampleArtifacts() -> (ArtifactCenter, [Artifact]) {
    MainActor.assumeIsolated {
        let first = ArtifactAccount(id: UUID(), email: "me@example.com", accountUuid: "a1", org: "o1")
        let second = ArtifactAccount(id: UUID(), email: "lent@example.com", accountUuid: "a2", org: "o2")
        func artifact(_ id: String, _ title: String, _ account: ArtifactAccount, _ audience: String,
                      _ rel: String) -> Artifact {
            Artifact(id: id, account: account.id, org: account.org!, title: title, kind: "page",
                     openURL: URL(string: "https://claude.ai/artifact/\(id)"), audience: audience, rel: rel,
                     sortTime: taken, ownerAccount: nil)
        }
        let samples = [
            artifact("11111111-a", "Quarterly plan with a title long enough to need cutting short somewhere", first, "public", "mine"),
            artifact("22222222-b", "Pool check", first, "owner", "mine"),
            artifact("33333333-c", "Design review", second, "users", "shared"),
        ]
        let groups = [ArtifactGroup(id: UUID(), name: "Design", emails: ["a@example.com", "b@example.com"])]
        return (ArtifactCenter(accounts: [first, second], artifacts: samples, groups: groups), samples)
    }
}

/// Every page the sidebar leads to, each in the state that fills it most.
struct Page {
    let name: String
    let selection: UUID
    let profile: URL?
}
let pages = [
    Page(name: "claude", selection: ContentView.mainProfileID, profile: Graft.mainProfile),
    Page(name: "claude-2", selection: shortcut.id, profile: shortcut.profileDir),
    Page(name: "claude-3-draft", selection: draft.id, profile: draft.profileDir),
    Page(name: "artifacts", selection: ContentView.artifactsID, profile: nil),
    Page(name: "projects", selection: ContentView.projectsID, profile: nil),
    Page(name: "search", selection: ContentView.searchID, profile: nil),
]
let (pageArtifacts, artifactSamples) = sampleArtifacts()

/// Projects and search results drawn from sample rows, over the same sample
/// accounts, for the same reason: the real ones are asked of claude.ai.
func sampleProjects(_ accounts: ArtifactCenter) -> ProjectCenter {
    MainActor.assumeIsolated {
        let first = accounts.accounts[0].id
        let second = accounts.accounts[1].id
        let projects = [
            ClaudeProject(id: "p1111111-a", account: first, name: "Claude Graft", summary: "The app",
                          isPrivate: true, isStarred: true, isExample: false, creator: nil, updated: taken),
            ClaudeProject(id: "p2222222-b", account: second, name: "A project with a name long enough to need cutting short",
                          summary: "", isPrivate: false, isStarred: false, isExample: false, creator: nil, updated: taken),
            ClaudeProject(id: "p3333333-c", account: first, name: "How to use Claude", summary: "",
                          isPrivate: true, isStarred: false, isExample: true, creator: nil, updated: taken),
        ]
        func hit(_ id: String, _ account: UUID, _ kind: SearchHit.Kind, _ title: String, _ snippet: String,
                 place: SearchHit.Place? = nil, archived: Bool = false, project: String? = nil) -> SearchHit {
            SearchHit(id: id, account: account, kind: kind, title: title, snippet: snippet,
                      snippetMatches: [0..<5], titleMatches: [0..<5], project: project, updated: taken,
                      isArchived: archived, isStarred: kind == .chat && project != nil, place: place,
                      folder: kind == .code ? snippet : nil,
                      transcript: place == .goneFromClaude ? URL(fileURLWithPath: "/tmp/\(id).jsonl") : nil,
                      rank: 0, openURL: place == .goneFromClaude ? nil : URL(string: "https://claude.ai/chat/\(id)"))
        }
        let results = [
            hit("c1111111-a", first, .chat, "Graft release plan", "Graft ships both architectures and a note for the floor version, which is long enough to wrap onto a second line here", project: "p1111111-a"),
            hit("c2222222-b", second, .chat, "Graft on a lent account", "Graft lends the week to the account with the soonest reset"),
            hit("cse_3333333", first, .code, "Graft layout fix", "/Users/Shared/Developer/claude-graft", place: .thisMac),
            hit("local-gone", first, .code, "Graft notes", "/Users/Shared/Developer/claude-graft", place: .goneFromClaude),
        ]
        return ProjectCenter(accounts: accounts, projects: projects, results: results, searched: "Graft")
    }
}
let pageProjects = sampleProjects(pageArtifacts)

for appearance in [NSAppearance.Name.aqua, .darkAqua] {
for page in pages {
    let root = ContentView(selection: page.selection, artifacts: pageArtifacts, projects: pageProjects)
        .environmentObject(Shared.store)
        .environmentObject(Shared.settings)
        .environmentObject(Shared.usage)
    // Built the way the app's own window is: the toolbar and title come from
    // the view, and the content runs up under the titlebar, which is where a
    // sidebar and a title can be seen to be one piece or two.
    let controller = NSHostingController(rootView: root)
    controller.sizingOptions = []
    if #available(macOS 14, *) { controller.sceneBridgingOptions = .all }
    let window = LayoutWindow(contentRect: NSRect(x: 100, y: 100, width: 820, height: 560),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
    window.contentViewController = controller
    window.setContentSize(NSSize(width: 820, height: 560))
    let hosting = controller.view
    window.appearance = NSAppearance(named: appearance)
    window.title = "Claude Graft"
    if ProcessInfo.processInfo.environment["GRAFT_LAYOUT_VISIBLE"] == "1" {
        window.orderFront(nil)
    } else {
        window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
        window.orderBack(nil)
    }
    settle()
    let pageName = "the \(page.name) page"
    for state in ["current", "available", "error", "current-again"] {
        UpdateResponse.available = state == "available"
        UpdateResponse.failing = state == "error"
        Shared.claudeUpdates.check()
        let deadline = Date().addingTimeInterval(5)
        repeat { settle() } while Shared.claudeUpdates.checking && Date() < deadline
        check(!Shared.claudeUpdates.checking, "the mocked update request completes")
        check(UpdateResponse.failing ? Shared.claudeUpdates.problem != nil
              : (Shared.claudeUpdates.availableVersion != nil) == UpdateResponse.available,
              "the header enters the requested \(state) state")
        // The dropdown is 320 points wide and this line sits on top of every
        // account in it.
        let dropdownHeader = NSHostingController(rootView: ClaudeUpdateStatus(compact: true))
            .sizeThatFits(in: CGSize(width: 320, height: 1000)).height
        check(dropdownHeader <= (state.hasPrefix("current") ? 32 : 52),
              "\(state): the dropdown's update line takes \(state.hasPrefix("current") ? "one line" : "two at most")")
        for size in [NSSize(width: 820, height: 560), NSSize(width: 1200, height: 800)] {
            Shared.usage.setEntriesForTesting(pageFigures)
            window.setContentSize(size)
            settle()
            if Shared.usage.entry(for: Graft.mainProfile)?.usage == nil {
                Shared.usage.setEntriesForTesting(pageFigures)
                settle()
            }
            hosting.layoutSubtreeIfNeeded()
            let place = "\(state), \(size), \(pageName)"
            check(abs(hosting.bounds.width - size.width) <= 1
                  && abs(hosting.bounds.height - size.height) <= 1,
                  "the window reaches the requested \(size) size")
            let views = descendants(hosting)
            if size.width == 820, state == "current" || state == "available" {
                let tree = checkAccessibility(window, surface: ControlSurface.window, "\(state), \(pageName)")
                check(element("\(ControlSurface.window).new-shortcut", in: tree) != nil,
                      "\(place): New Shortcut in the toolbar can be asked for by name")
                let toggle = window.toolbar?.items.first {
                    $0.itemIdentifier.rawValue.hasSuffix("navigationSplitView.toggleSidebar")
                }
                check(toggle.map { item in tree.contains { $0.role == kAXButtonRole && $0.name == item.label } } == true,
                      "\(place): the sidebar can be hidden from the toolbar, as any sidebar can")
                check(state != "available" || element("window.claude-update.install", in: tree) != nil,
                      "\(place): a waiting Claude update can be asked for by name")
                if let profileDir = page.profile {
                    let profile = profileDir.lastPathComponent
                    let scope = "\(ControlSurface.window).\(profile)."
                    check(element(scope + "usage-info", in: tree)?.name
                            == L10n.format("About %@", L10n.text("Plan usage")),
                          "\(place): the button explaining a section says which section, not “Error”")
                    check(element(scope + "show-in-finder", in: tree)?.name == L10n.text("Show in Finder"),
                          "\(place): the folder button says it shows the folder, not “Move”")
                    check(element(scope + "folder", in: tree)?.name == L10n.text("Profile folder"),
                          "\(place): the profile folder is read out under its own name")
                    check(element(scope + "open", in: tree)?.role == kAXButtonRole,
                          "\(place): Open can be asked for by the profile it opens")
                    check(element("\(ControlSurface.window).sidebar.\(profile)", in: tree) != nil,
                          "\(place): each sidebar row can be asked for by its profile")
                    if page.profile != draft.profileDir {
                        check(element(scope + "week", in: tree)?.role == kAXProgressIndicatorRole,
                              "\(place): the week's figure is drawn")
                        check(element(scope + "shared-usage.add", in: tree) != nil,
                              "\(place): adding an account can be asked for by name")
                    }
                    if page.selection == ContentView.mainProfileID {
                        let lent = scope + "lender.\(shortcut.profileDir.lastPathComponent)"
                        check(element(lent, in: tree)?.name == "Claude 2",
                              "\(place): an account Claude draws on is read out under its own name")
                        check(element(lent + ".remove", in: tree)?.name == L10n.format("Stop drawing on %@", "Claude 2"),
                              "\(place): the button taking an account away says which account, not “Remove”")
                        check(element(scope + "lender.lent-account.remove", in: tree)?.name
                                == L10n.format("Stop drawing on %@", "lent@example.com"),
                              "\(place): an account signed in here is named by its email")
                    }
                }
                if page.profile == nil {
                    // One sign-in for all three: each page heads itself with
                    // the same accounts, each offering to sign in again or out.
                    let scope = page.selection == ContentView.projectsID ? "projects"
                        : page.selection == ContentView.searchID ? "search" : "artifacts"
                    for account in MainActor.assumeIsolated({ pageArtifacts.accounts }) {
                        check(element("window.\(scope).account.\(account.id.uuidString.prefix(8))", in: tree) != nil,
                              "\(place): \(account.email ?? "") is shown as signed in here too")
                    }
                }
                if page.selection == ContentView.projectsID {
                    for control in ["refresh", "add-account", "filter", "count"] {
                        check(element("window.projects.\(control)", in: tree) != nil,
                              "\(place): projects \(control) can be asked for by name")
                    }
                    check(element("window.project.p1111111.open", in: tree)?.name
                            == L10n.format("Open %@", "Claude Graft"),
                          "\(place): opening a project says which project")
                    check(element("window.project.p3333333", in: tree)?.value.contains(L10n.text("Example")) == true,
                          "\(place): the example project says it is one")
                } else if page.selection == ContentView.searchID {
                    for control in ["query", "go", "filters", "add-account"] {
                        check(element("window.search.\(control)", in: tree) != nil,
                              "\(place): search \(control) can be asked for by name")
                    }
                    check(element("window.search.filters", in: tree)?.name == L10n.text("Filters"),
                          "\(place): the filter icon is read out as the filters, not as a picture")
                    check(element("window.search.kind", in: tree) == nil,
                          "\(place): the filters stay behind their icon rather than across the page")
                    check(element("window.hit.c1111111.open", in: tree)?.name
                            == L10n.format("Open %@", "Graft release plan"),
                          "\(place): opening a chat says which chat")
                    check(element("window.hit.local-go.open", in: tree)?.name
                            == L10n.format("Show %@ in Finder", "Graft notes"),
                          "\(place): a session gone from claude.ai offers Finder, not a dead link")
                    check(element("window.hit.3333333", in: tree)?.value.contains(L10n.text("On this Mac")) == true,
                          "\(place): a Code session says where it ran")
                } else if page.selection == ContentView.artifactsID {
                    for control in ["refresh", "add-account", "filter", "search", "groups"] {
                        check(element("window.artifacts.\(control)", in: tree) != nil,
                              "\(place): \(control) can be asked for by name")
                    }
                    let share = element("window.artifact.11111111.share", in: tree)
                    check(share?.name == L10n.format("Share %@ with a Group", artifactSamples[0].title),
                          "\(place): sharing says which artifact it shares \(share?.described ?? "missing")")
                }
            }
            if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_SNAPSHOTS"],
               state == "current", size.width == 820 {
                let output = URL(fileURLWithPath: directory)
                    .appending(path: Bundle.main.preferredLocalizations.first ?? "en")
                    .appending(path: appearance == .aqua ? "light" : "dark")
                try! fm.createDirectory(at: output, withIntermediateDirectories: true)
                let tree = views.map { "\(type(of: $0)) \(hosting.convert($0.bounds, from: $0))" }
                try! tree.joined(separator: "\n").write(
                    to: output.appending(path: "\(page.name)-views.txt"), atomically: true, encoding: .utf8)
            }
            // The whole window, titlebar and all, as a person sees it: what a
            // snapshot of the content leaves out is exactly the sidebar
            // running up under the title and the title reading as one piece.
            if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_WINDOWS"],
               state == "current", size.width == 820 {
                let language = Bundle.main.preferredLocalizations.first ?? "en"
                capture(window, as: "\(language)-\(appearance == .aqua ? "light" : "dark")-\(page.name)",
                        in: directory)
            }

            // A real sidebar: the split view's first item is a sidebar, and
            // it runs the full height of the window, up under the titlebar.
            let split = views.compactMap { $0 as? NSSplitView }.first!
            let splitBounds = hosting.convert(split.bounds, from: split)
            check(abs(splitBounds.minY) <= 1 && splitBounds.maxY <= hosting.bounds.maxY + 1,
                  "\(place): the split view fills the window from its top edge")
            // A page wider than the window does not make the window grow: it
            // pushes the sidebar off the left edge and itself off the right.
            check(splitBounds.minX >= -1 && splitBounds.maxX <= hosting.bounds.maxX + 1,
                  "\(place): the page fits the window's width \(Int(splitBounds.width)) in \(Int(hosting.bounds.width))")
            let controllerItem = (split.delegate as? NSSplitViewController)?.splitViewItems.first
            check(controllerItem?.behavior == .sidebar,
                  "\(place): the first column is a sidebar, not one half of a split")
            let column = split.arrangedSubviews.first.map { hosting.convert($0.bounds, from: $0) } ?? .zero
            check(column.minY <= 1 && column.maxY >= hosting.bounds.maxY - 1,
                  "\(place): the sidebar runs the full height of the window, under the titlebar")
            let titlebarHeight = window.frame.height - window.contentLayoutRect.height
            // One backdrop over each column at the very top, and none lower
            // down: one drawn inside the detail covered its first heading.
            let titlebars = views.filter {
                String(describing: type(of: $0)) == "NSTitlebarBackgroundView"
                    && !$0.isHiddenOrHasHiddenAncestor && $0.bounds.height > 1
            }.map { hosting.convert($0.bounds, from: $0) }
            check(titlebars.allSatisfy { $0.minY <= 1 && $0.height <= titlebarHeight + 1 },
                  "\(place): no titlebar backdrop sits below the titlebar \(titlebars)")
            // The title lies wholly on one side of the sidebar's edge: across
            // it, the split cut the title in two.
            let edge = split.arrangedSubviews.first.map { $0.convert($0.bounds, to: nil).maxX } ?? 0
            let titles = (window.contentView?.superview).map(descendants)?
                .compactMap { $0 as? NSTextField }
                .filter { $0.stringValue == window.title && !$0.isHiddenOrHasHiddenAncestor }
                .map { $0.convert($0.bounds, to: nil) } ?? []
            check(!titles.isEmpty && titles.allSatisfy { $0.minX >= edge - 1 || $0.maxX <= edge + 1 },
                  "\(place): the title reads as one piece, clear of the sidebar's edge \(titles) edge \(edge)")

            let sidebarList = split.arrangedSubviews.first.map(descendants)?
                .compactMap { $0 as? NSOutlineView }.first
            check(sidebarList != nil, "\(place): the sidebar lists the profiles")
            if let sidebarList {
                let account = hosting.convert(sidebarList.rect(ofRow: 1), from: sidebarList)
                check(account.minY >= titlebarHeight - 1 && account.maxY <= hosting.bounds.maxY,
                      "\(place): the main account row is fully visible, below the titlebar")
            }
            let sidebarLine = NSHostingController(rootView: ClaudeUpdateStatus(stacked: true))
                .sizeThatFits(in: CGSize(width: 220, height: 1000)).height
            check(sidebarLine <= (state == "available" ? 90 : 52),
                  "\(place): the sidebar's update line takes two lines at most, and its button one more")

            // Every page is done in one view, without scrolling: what does not
            // fit goes behind an ⓘ, never below the bottom edge or under the
            // toolbar. A horizontal strip of account chips is not a page.
            let sidebarScroll = sidebarList?.enclosingScrollView
            let pageScrolls = views.compactMap { $0 as? NSScrollView }.filter {
                $0 !== sidebarScroll && !$0.isHiddenOrHasHiddenAncestor && $0.bounds.height > 40
            }
            check(!pageScrolls.isEmpty, "\(place): the page is present")
            for scroll in pageScrolls {
                let frame = hosting.convert(scroll.bounds, from: scroll)
                let top = max(scroll.contentView.contentInsets.top, scroll.contentInsets.top)
                check(frame.minY + top >= titlebarHeight - 1,
                      "\(place): nothing on the page starts under the toolbar")
                guard size == NSSize(width: 820, height: 560), let document = scroll.documentView else { continue }
                // A list's table stretches to fill whatever it is shown in, so
                // what it holds is where its last row ends, not its own height.
                let table = document as? NSTableView
                let content = table.map { $0.numberOfRows == 0 ? 0 : $0.rect(ofRow: $0.numberOfRows - 1).maxY }
                    ?? document.frame.height
                let overflow = content - room(in: scroll)
                check(overflow <= 1, "\(place): fits without scrolling (\(Int(overflow)) points over)")
                // A page over on one macOS and not another is found by which
                // row grew, and the machine that sees it may be one nobody can
                // sit at, so the failure says where every row lies.
                if overflow > 1 || ProcessInfo.processInfo.environment["GRAFT_LAYOUT_ROWS"] == "1", state == "current" {
                    print("ROWS \(place): document \(Int(content)), room \(Int(room(in: scroll)))")
                    for row in seen(window) ?? [] where row.frame.height > 0 && (!row.identifier.isEmpty || row.role == kAXStaticTextRole) {
                        print("    y \(Int(row.frame.minY)) h \(Int(row.frame.height)) \(row.described) \(row.value.prefix(40))")
                    }
                }
                if ProcessInfo.processInfo.environment["GRAFT_LAYOUT_MARGINS"] == "1" {
                    print("MARGIN \(page.name) \(state) \(Int(-overflow))")
                }
            }
        }
    }
    window.orderOut(nil)
}
}
// The dropdown and Settings, held to the same rules. The rows are built from
// figures rather than from a usage pass, which in here has no account to ask.
func hosted<V: View>(_ view: V, title: String) -> NSWindow {
    let hosting = NSHostingView(rootView: view)
    let window = LayoutWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 700),
                              styleMask: [.titled], backing: .buffered, defer: false)
    window.title = title
    window.contentView = hosting
    window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
    window.orderBack(nil)
    settle()
    return window
}

let dropdown = hosted(MenuBarContent(openMainWindow: {})
    .environmentObject(Shared.store)
    .environmentObject(Shared.settings)
    .environmentObject(Shared.usage), title: "Dropdown check")
let dropdownTree = checkAccessibility(dropdown, surface: ControlSurface.dropdown, "the dropdown")
for control in ["open-at-login", "show-in-menu-bar", "front-icon", "front-name", "manual-updates",
                "hold-updates", "refresh-usage", "check-graft-updates", "open-window", "support", "quit",
                "claude-update.check"] {
    check(element("dropdown.\(control)", in: dropdownTree) != nil, "the dropdown's \(control) can be asked for by name")
}
dropdown.orderOut(nil)

// Someone with seven accounts found the dropdown running off the bottom of
// the screen, taking the switches and Quit with it. The list scrolls instead.
func column(accounts: Int) -> NSWindow {
    hosted(DropdownColumn(maxHeight: 400) {
        Text("Head").padding(10)
    } list: {
        ForEach(0..<accounts, id: \.self) { Text("Account \($0)").padding(.vertical, 20) }
    } foot: {
        Button("Quit") {}.identified("quit")
    }
    .environment(\.controlSurface, ControlSurface.dropdown), title: "Dropdown height check")
}
let crowded = column(accounts: 30)
settle()
let crowdedHeight = crowded.contentView?.fittingSize.height ?? 0
check(crowdedHeight > 0 && crowdedHeight <= 400,
      "a dropdown with more accounts than the screen holds stays within the screen (\(Int(crowdedHeight)))")
check(element("dropdown.quit", in: checkAccessibility(crowded, surface: ControlSurface.dropdown,
                                                      "a crowded dropdown")) != nil,
      "and what sits under the accounts can still be reached")
crowded.orderOut(nil)
let few = column(accounts: 3)
settle()
let fewHeight = few.contentView?.fittingSize.height ?? 0
check(fewHeight > 100 && fewHeight < 400,
      "a dropdown with a few accounts keeps the height it asks for (\(Int(fewHeight)))")
few.orderOut(nil)
check(DropdownColumn<EmptyView, EmptyView, EmptyView>.listHeight(list: 900, head: 600, foot: 300, limit: 800)
        == DropdownColumn<EmptyView, EmptyView, EmptyView>.leastList,
      "however crowded the rest, some of the account list stays in view")

let now = Date()
func sample(_ name: String, _ profile: URL, _ id: UUID?) -> UsageMonitor.Entry {
    UsageMonitor.Entry(name: name, profile: profile,
                       usage: Graft.Usage(fiveHour: 6, week: 20, organization: nil, sampled: now,
                                          fiveHourReset: now.addingTimeInterval(4 * 3600 + 120),
                                          weekReset: now.addingTimeInterval(4 * 86400),
                                          fable: 0, fableReset: now.addingTimeInterval(4 * 86400)),
                       isRunning: id == nil, shortcut: id, isLive: true, plan: "Max", refreshed: now)
}
// An account with no five-hour limit, as the service has answered for one
// since November 2026.
let unlimitedProfile = shortcut.profileDir.deletingLastPathComponent().appending(path: "Claude-Unlimited")
let unlimited = UsageMonitor.Entry(name: "Claude 3", profile: unlimitedProfile,
                                   usage: Graft.Usage(fiveHour: nil, week: 94, organization: nil, sampled: now,
                                                      weekReset: now.addingTimeInterval(3 * 86400)),
                                   isRunning: false, shortcut: UUID(), isLive: true, plan: "Max",
                                   refreshed: now)
let rows = hosted(VStack(spacing: 0) {
    UsageRow(entry: sample("Claude", Graft.mainProfile, nil), starting: false,
             open: {}, start: {}, refresh: {})
    UsageRow(entry: sample("Claude 2", shortcut.profileDir, shortcut.id), starting: false,
             open: {}, start: {}, refresh: {})
    UsageRow(entry: unlimited, starting: false, open: {}, start: {}, refresh: {})
}
.frame(width: 320)
.environment(\.controlSurface, ControlSurface.dropdown), title: "Account rows check")
let rowTree = checkAccessibility(rows, surface: ControlSurface.dropdown,
                                 "three accounts in the dropdown, one without a five-hour limit")
let third = "dropdown.\(unlimitedProfile.lastPathComponent)."
let noLimit = element(third + "no-five-hour-limit", in: rowTree)
check(noLimit.map { [$0.name, $0.value].contains(L10n.text("No 5-hour limit on this account")) } == true,
      "an account with no five-hour limit says so where its five-hour bar would be")
check(element(third + "five-hour", in: rowTree) == nil,
      "rather than drawing a bar stuck at nothing used")
check(element(third + "week", in: rowTree)?.role == kAXProgressIndicatorRole,
      "and its week is still a bar of its own")
check(element(third + "start-session", in: rowTree) == nil && element(third + "open", in: rowTree) != nil,
      "it offers no session to start, having no window for one to open, but can still be opened")
let second = "dropdown.\(shortcut.profileDir.lastPathComponent)."
check(element(second + "open", in: rowTree)?.name == L10n.format("Open %@", "Claude 2"),
      "each account's Open says which Claude it opens")
check(element(second + "start-session", in: rowTree)?.name
        == L10n.format("Start Session for %@", "Claude 2"),
      "each account's Start Session says which account it starts")
check(element(second + "refresh-usage", in: rowTree)?.name
        == L10n.format("Refresh usage for %@", "Claude 2"),
      "each account's refresh says which account it asks about")
let fiveHours = element(second + "five-hour", in: rowTree)
check(fiveHours?.role == kAXProgressIndicatorRole && fiveHours?.name == L10n.text("5 hours")
        && fiveHours?.value.hasPrefix(L10n.format("%1$ld%% used, resets in %2$@", 6, "")) == true,
      "a usage bar is read as one progress bar: what it measures, how much is used and when it resets")
let account = element(second + "account", in: rowTree)
check(account?.name == "Claude 2"
        && account?.value == "Max, \(L10n.text("Not running"))",
      "an account's heading says whether its Claude is open, which only the dot shows")
check(element("dropdown.\(Graft.mainProfile.lastPathComponent).account", in: rowTree)?.value
        == "Max, \(L10n.text("Running"))",
      "a running Claude's heading says so")
rows.orderOut(nil)

// Drawn from figures, since in here no relay has carried a conversation.
let claude2 = UsagePoolController.key(for: shortcut)
let conversations = hosted(ConversationList(chats: [
    UsagePoolController.Chat(id: "first-session", title: "Plan the next release", claude: UsagePoolController.mainKey,
                             servedBy: claude2, assigned: nil, choices: [UsagePoolController.mainKey, claude2],
                             lastAt: now, passedOver: false),
    UsagePoolController.Chat(id: "second-session", title: nil, claude: UsagePoolController.mainKey,
                             servedBy: UsagePoolController.mainKey, assigned: claude2,
                             choices: [UsagePoolController.mainKey, claude2],
                             lastAt: now.addingTimeInterval(-60), passedOver: true),
], front: UsagePoolController.mainKey, othersShown: true)
.frame(width: 320)
.environment(\.controlSurface, ControlSurface.dropdown), title: "Conversations check")
let conversationTree = checkAccessibility(conversations, surface: ControlSurface.dropdown, "conversations in the dropdown")
let moving = element("dropdown.conversation.first-session.account", in: conversationTree)
check(moving?.name == L10n.format("Account answering “%@”", "Plan the next release"),
      "the menu moving a conversation says which conversation it moves, not only an account")
check(moving?.value == L10n.format("Auto · %@", "Claude 2"),
      "and it says which account answers that conversation now, and that the ranking put it there")
check(element("dropdown.conversation.second-session.account", in: conversationTree)?.value == "Claude",
      "one moved by hand reads the account's name alone")
check(element("dropdown.conversation.second-session", in: conversationTree)?.value
        == L10n.format("%@ had no room", "Claude 2"),
      "a conversation the chosen account could not take says so out loud as well as in orange")
check(element("dropdown.conversations.others", in: conversationTree)?.value == L10n.text("Shown"),
      "the other conversations sit behind a line that says whether they are shown")
conversations.orderOut(nil)

let folded = hosted(ConversationList(chats: [
    UsagePoolController.Chat(id: "first-session", title: "Plan the next release", claude: UsagePoolController.mainKey,
                             servedBy: claude2, assigned: nil, choices: [UsagePoolController.mainKey, claude2],
                             lastAt: now, passedOver: false),
    UsagePoolController.Chat(id: "second-session", title: nil, claude: UsagePoolController.mainKey,
                             servedBy: UsagePoolController.mainKey, assigned: nil,
                             choices: [UsagePoolController.mainKey, claude2],
                             lastAt: now.addingTimeInterval(-60), passedOver: false),
], front: UsagePoolController.mainKey)
.frame(width: 320)
.environment(\.controlSurface, ControlSurface.dropdown), title: "Folded conversations check")
let foldedTree = checkAccessibility(folded, surface: ControlSurface.dropdown, "folded conversations")
check(element("dropdown.conversation.first-session", in: foldedTree) != nil
        && element("dropdown.conversation.second-session", in: foldedTree) == nil
        && element("dropdown.conversations.others", in: foldedTree)?.name
            == L10n.format("Other conversations · %ld", 1),
      "the conversation in front comes first, and the rest wait behind a count until asked for")
folded.orderOut(nil)

// Sized by its content, as the Settings scene sizes its window.
let settingsController = NSHostingController(rootView: SettingsView().environmentObject(Shared.settings))
let settingsWindow = LayoutWindow(contentViewController: settingsController)
settingsWindow.styleMask = [.titled, .closable]
settingsWindow.title = "Settings check"
settingsWindow.setFrameOrigin(NSPoint(x: -10000, y: -10000))
settingsWindow.orderBack(nil)
settle()
for scroll in descendants(settingsWindow.contentView!).compactMap({ $0 as? NSScrollView }) {
    guard let document = scroll.documentView else { continue }
    let overflow = document.frame.height - room(in: scroll)
    check(overflow <= 1, "Settings fits its window without scrolling (\(Int(overflow)) points over)")
}
if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_WINDOWS"] {
    capture(settingsWindow, as: "\(Bundle.main.preferredLocalizations.first ?? "en")-settings", in: directory)
}
let settingsTree = checkAccessibility(settingsWindow, surface: ControlSurface.settings, "Settings")
check(["open-at-login", "show-in-menu-bar", "front-icon", "front-name", "manual-updates", "hold-updates"]
        .allSatisfy { element("settings.\($0)", in: settingsTree) != nil },
      "every switch in Settings can be asked for by name, apart from the dropdown's copy of it")
settingsWindow.orderOut(nil)

// Artifacts, drawn from sample rows: the real list is asked of claude.ai,
// which a layout check must never reach.
MainActor.assumeIsolated {
    let first = ArtifactAccount(id: UUID(), email: "me@example.com", accountUuid: "a1", org: "o1")
    let second = ArtifactAccount(id: UUID(), email: "lent@example.com", accountUuid: "a2", org: "o2")
    func artifact(_ id: String, _ title: String, _ account: ArtifactAccount, _ audience: String,
                  _ rel: String) -> Artifact {
        Artifact(id: id, account: account.id, org: account.org!, title: title, kind: "page",
                 openURL: URL(string: "https://claude.ai/artifact/\(id)"), audience: audience, rel: rel,
                 sortTime: now, ownerAccount: nil)
    }
    let samples = [
        artifact("11111111-a", "Quarterly plan with a title long enough to need cutting short somewhere", first, "public", "mine"),
        artifact("22222222-b", "Pool check", first, "owner", "mine"),
        artifact("33333333-c", "Design review", second, "users", "shared"),
    ]
    let groups = [ArtifactGroup(id: UUID(), name: "Design", emails: ["a@example.com", "b@example.com"])]
    for (name, center) in [("artifacts", ArtifactCenter(accounts: [first, second], artifacts: samples, groups: groups)),
                           ("artifacts-signed-out", ArtifactCenter(accounts: [], artifacts: [], groups: []))] {
        // The window's own background, which a snapshot taken off screen
        // does not draw and which the split view gives it in the app.
        let hosting = NSHostingView(rootView: ArtifactsView(center: center)
            .background(Color(nsColor: .windowBackgroundColor))
            .environment(\.controlSurface, ControlSurface.window))
        hosting.sizingOptions = []
        let window = LayoutWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 560),
                                  styleMask: [.titled], backing: .buffered, defer: false)
        window.title = "Artifacts check \(name)"
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = hosting
        window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
        window.orderBack(nil)
        settle()
        let tree = checkAccessibility(window, surface: ControlSurface.window, name)
        if center.accounts.isEmpty {
            check(element("window.artifacts.sign-in", in: tree) != nil
                    && element("window.artifacts.sign-in-note-info", in: tree) != nil,
                  "\(name): with no account, one line says to sign in, and the rest is behind ⓘ")
            check(element("window.artifacts.sign-in-hint", in: tree)?.value == L10n.text(ArtifactsView.signInHint),
                  "\(name): under Sign In, one line says to use an emailed code")
        } else {
            for control in ["refresh", "add-account", "filter", "search", "groups"] {
                check(element("window.artifacts.\(control)", in: tree) != nil,
                      "\(name): \(control) can be asked for by name")
            }
            let share = element("window.artifact.11111111.share", in: tree)
            check(share?.name == L10n.format("Share %@ with a Group", samples[0].title),
                  "\(name): sharing says which artifact it shares \(share?.described ?? "missing")")
            let access = element("window.artifact.11111111.access", in: tree)
            check(access?.value == ArtifactAccess.anyoneWithLink.label
                    && access?.name == L10n.format("Who Can Open %@", samples[0].title),
                  "\(name): who can open an artifact is read out with it, not as “Globe” \(access?.described ?? "missing")")
            check(element("window.artifact.11111111.more", in: tree)?.name
                    == L10n.format("More for %@", samples[0].title)
                    && element("window.artifact.33333333.more", in: tree) != nil,
                  "\(name): every artifact has its menu of actions, named for the artifact")
            check(element("window.artifact.33333333.access", in: tree) == nil,
                  "\(name): an artifact someone else owns offers no change to who can open it")
            let row = element("window.artifact.33333333", in: tree)
            check(row?.value.contains(L10n.format("Shared with %@", "lent@example.com")) == true,
                  "\(name): an artifact shared with an account says which account")
        }
        let views = descendants(hosting)
        let scrolls = views.compactMap { $0 as? NSScrollView }.filter { !$0.hasHorizontalScroller && $0.bounds.height > 40 }
        for scroll in scrolls {
            guard let document = scroll.documentView else { continue }
            let shown = room(in: scroll)
            // A list's table stretches to fill whatever it is shown in, so
            // what it holds is where its last row ends, not its own height.
            let table = document as? NSTableView
            let content = table.map { $0.numberOfRows == 0 ? 0 : $0.rect(ofRow: $0.numberOfRows - 1).maxY }
                ?? document.frame.height
            check(content - shown <= 1,
                  "\(name): three artifacts fit beside the sidebar without scrolling (\(Int(content - shown)) points over)")
        }
        if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_SNAPSHOTS"] {
            let output = URL(fileURLWithPath: directory)
                .appending(path: Bundle.main.preferredLocalizations.first ?? "en")
            try! fm.createDirectory(at: output, withIntermediateDirectories: true)
            if let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) {
                hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
                try! bitmap.representation(using: .png, properties: [:])!
                    .write(to: output.appending(path: "\(name).png"))
            }
        }
        window.orderOut(nil)
    }
}

// Each person in a group with what they may do, and every control in the
// editor reachable from outside: a live run found its minus button drawn and
// working but missing from what assistive apps were given.
MainActor.assumeIsolated {
    var group = ArtifactGroup(id: UUID(), name: "Review",
                              emails: ["editor@example.com", "commenter@example.com", "viewer@example.com"])
    group.setRole(.editor, for: "editor@example.com")
    group.setRole(.commenter, for: "commenter@example.com")
    let center = ArtifactCenter(accounts: [], artifacts: [], groups: [group])
    let hosting = NSHostingView(rootView: GroupsEditor(center: center)
        .background(Color(nsColor: .windowBackgroundColor))
        .environment(\.controlSurface, ControlSurface.window))
    let window = LayoutWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 420),
                              styleMask: [.titled], backing: .buffered, defer: false)
    window.title = "Groups check"
    window.appearance = NSAppearance(named: .aqua)
    window.contentView = hosting
    window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
    window.orderBack(nil)
    settle()
    let tree = checkAccessibility(window, surface: ControlSurface.window, "groups")
    check(element("window.groups.add", in: tree)?.name == L10n.text("New Group")
            && element("window.groups.delete", in: tree)?.name == L10n.text("Delete Group"),
          "groups: adding and deleting a group can both be found and say what they do")
    // The same two read from another process. A reader in a second process is
    // what VoiceOver and every script is, and it was there, not here, that the
    // minus button turned up with its help and no name.
    let outside = readFromOutside(window)
    check(outside["window.groups.add"] == L10n.text("New Group")
            && outside["window.groups.delete"] == L10n.text("Delete Group"),
          "groups: read from another process, adding and deleting a group still say what they do")
    // And as the app shows it, a sheet over the window, which is where the live
    // run read it.
    window.contentView = NSView()
    let parent = LayoutWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 560),
                              styleMask: [.titled], backing: .buffered, defer: false)
    parent.title = "Groups sheet check"
    parent.appearance = NSAppearance(named: .aqua)
    parent.setFrameOrigin(NSPoint(x: -10000, y: -10000))
    parent.orderBack(nil)
    let sheet = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 420),
                         styleMask: [.titled], backing: .buffered, defer: false)
    sheet.contentView = hosting
    parent.beginSheet(sheet)
    settle()
    let inSheet = readFromOutside(parent)
    check(inSheet["window.groups.add"] == L10n.text("New Group")
            && inSheet["window.groups.delete"] == L10n.text("Delete Group"),
          "groups: in a sheet, read from another process, adding and deleting a group say what they do")
    if inSheet["window.groups.delete"] != L10n.text("Delete Group") {
        print("    outside read of the sheet: \(inSheet.filter { $0.key.hasPrefix("window.groups.") && !$0.key.contains("member") })")
    }
    parent.endSheet(sheet)
    parent.orderOut(nil)
    for position in 0..<3 {
        check(element("window.groups.member.\(position).role", in: tree) != nil
                && element("window.groups.member.\(position).remove", in: tree) != nil,
              "groups: person \(position + 1) has a role to choose and a way out of the group")
    }
    let editorRole = element("window.groups.member.0.role", in: tree)
    check(editorRole?.value == ArtifactRole.editor.label || editorRole?.title == ArtifactRole.editor.label,
          "groups: each person's role is read out as the one they have \(editorRole.map { "\($0.described) \($0.value)" } ?? "missing")")
    check(element("window.groups.everyone", in: tree) != nil && element("window.groups.new-addresses", in: tree) != nil,
          "groups: everyone can be given one role, and more addresses pasted in")
    if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_SNAPSHOTS"] {
        let output = URL(fileURLWithPath: directory).appending(path: Bundle.main.preferredLocalizations.first ?? "en")
        try! fm.createDirectory(at: output, withIntermediateDirectories: true)
        if let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) {
            hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
            try! bitmap.representation(using: .png, properties: [:])!.write(to: output.appending(path: "groups.png"))
        }
    }
    window.orderOut(nil)
}

// An export drawn from files on disk, as one read from claude.ai would be,
// without reaching it. The page builds part of itself with script, which is
// the part an export of the source alone would miss.
MainActor.assumeIsolated {
    let folder = fm.temporaryDirectory.appending(path: "graft-export-check-\(UUID().uuidString)")
    try! fm.createDirectory(at: folder, withIntermediateDirectories: true)
    defer { try? fm.removeItem(at: folder) }
    let page = """
        <!doctype html><html><head><title>Plan</title><style>.gone{display:none}</style></head><body>
        <h1>Quarterly plan</h1>
        <p>Ship <strong>roles</strong>, <em>exports</em> and <code>a_b</code>. See <a href="https://example.com/x">the notes</a>.</p>
        <ul><li><input type="checkbox" checked> done</li><li><input type="checkbox"> next
          <ol><li>first</li><li>second</li></ol></li></ul>
        <table><tr><th>Name</th><th>Role</th></tr><tr><td>Ana</td><td>Editor</td></tr></table>
        <pre>let x = 1</pre>
        <p class="gone">hidden words</p>
        <button>Press</button>
        <div id="later"></div>
        <script>document.getElementById("later").innerHTML = "<p>Drawn by script</p>";</script>
        </body></html>
        """
    try! Data(page.utf8).write(to: folder.appending(path: "index.html"))
    var results: [ArtifactExportFormat: Data] = [:]
    var failed: [ArtifactExportFormat: String] = [:]
    var finished = false
    Task { @MainActor in
        for format in [ArtifactExportFormat.markdown, .pdf, .word] {
            do {
                results[format] = try await ArtifactRenderer.render(folder.appending(path: "index.html"),
                                                                    in: folder, as: format)
            } catch {
                failed[format] = "\(error)"
            }
        }
        finished = true
    }
    let deadline = Date().addingTimeInterval(90)
    while !finished && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    let markdown = results[.markdown].map { String(decoding: $0, as: UTF8.self) } ?? ""
    let expected = [
        "# Quarterly plan",
        "Ship **roles**, *exports* and `a_b`. See [the notes](https://example.com/x).",
        "- [x] done\n- [ ] next\n  1. first\n  2. second",
        "| Name | Role |\n| --- | --- |\n| Ana | Editor |",
        "```\nlet x = 1\n```",
        "Drawn by script",
    ]
    let missing = expected.filter { !markdown.contains($0) }
    check(failed[.markdown] == nil && missing.isEmpty,
          "export: Markdown keeps headings, emphasis, links, checkboxes, nested lists, tables and code \(failed[.markdown] ?? "") missing \(missing) in \(markdown.debugDescription)")
    check(!markdown.contains("hidden words") && !markdown.contains("Press") && !markdown.contains("getElementById"),
          "export: Markdown leaves out what is hidden, controls and scripts, as claude.ai's own does")
    let pdf = results[.pdf].flatMap { PDFDocument(data: $0) }
    check(failed[.pdf] == nil && pdf?.string?.contains("Quarterly plan") == true
            && pdf?.string?.contains("Drawn by script") == true,
          "export: the PDF holds the page as drawn \(failed[.pdf] ?? "")")
    let word = results[.word]
    let reread = word.flatMap { try? NSAttributedString(data: $0, options: [.documentType: NSAttributedString.DocumentType.officeOpenXML],
                                                        documentAttributes: nil) }
    check(failed[.word] == nil && word?.prefix(2) == Data("PK".utf8) && reread?.string.contains("Quarterly plan") == true,
          "export: the Word document opens and holds the page's text \(failed[.word] ?? "")")
}

// MARK: - Naming a new shortcut

// A shortcut's page typed into the way a person types, a key at a time. The
// folder follows the name until one is typed by hand, never lands on another
// shortcut's folder or a profile still on disk, and never moves once the
// shortcut is created. Backspacing the 2 of "Claude 2" passes through an
// empty name, whose folder is Claude-Profile. On a Mac with one, the new
// shortcut stopped there, showed that account's figures and offered its
// Start Session, and once its app was looked up by the folder alone, it read
// as that shortcut and Open started the other account's Claude.
struct NamingPage: View {
    @EnvironmentObject var store: ShortcutStore
    let id: UUID
    var body: some View {
        if let index = store.shortcuts.firstIndex(where: { $0.id == id }) {
            ShortcutDetail(shortcut: $store.shortcuts[index], requestDelete: {}).id(id)
                .environment(\.controlSurface, ControlSurface.window)
        }
    }
}
do {
    let saved = Shared.store.shortcuts
    var holder = Shortcut(name: "Holder", folder: "Claude-Profile", source: .own)
    holder.installedName = holder.name
    let holderResources = Installer.installDirectoryOverride!.appending(path: "Holder.app/Contents/Resources")
    try fm.createDirectory(at: holderResources, withIntermediateDirectories: true)
    try JSONEncoder().encode(GraftConfig(profileDir: holder.profileDir.path, sourceDir: nil))
        .write(to: holderResources.appending(path: "graft.json"))
    try fm.createDirectory(at: holder.profileDir, withIntermediateDirectories: true)
    let leftover = Graft.applicationSupport.appending(path: "Claude-Old")
    try fm.createDirectory(at: leftover, withIntermediateDirectories: true)
    // Added after the pages above, which still hold places in the list.
    Shared.store.shortcuts.append(holder)
    let taken = Date()
    let holderFigures = UsageMonitor.Entry(
        name: holder.name, profile: holder.profileDir,
        usage: Graft.Usage(fiveHour: 37, week: 61, organization: nil, sampled: taken,
                           fiveHourReset: taken.addingTimeInterval(3 * 3600),
                           weekReset: taken.addingTimeInterval(4 * 86400)),
        isRunning: false, shortcut: holder.id, isLive: true, plan: "Max", refreshed: taken)

    let keys: [Character: UInt16] = ["a": 0, "s": 1, "d": 2, "e": 14, "t": 17, "o": 31, "i": 34,
                                     "p": 35, "l": 37, "k": 40, "n": 45, "m": 46, "w": 13, "r": 15,
                                     "f": 3, "c": 8, "u": 32, " ": 49, "-": 27]
    var trail: [String] = []
    var pages = 0
    func send(_ characters: String, to window: NSWindow) {
        let code = characters == "\u{7F}" ? 51 : keys[Character(characters.lowercased())] ?? 0
        for type in [NSEvent.EventType.keyDown, .keyUp] {
            guard let event = NSEvent.keyEvent(with: type, location: .zero, modifierFlags: [],
                                               timestamp: ProcessInfo.processInfo.systemUptime,
                                               windowNumber: window.windowNumber, context: nil,
                                               characters: characters,
                                               charactersIgnoringModifiers: characters.lowercased(),
                                               isARepeat: false, keyCode: code) else { continue }
            window.sendEvent(event)
        }
    }
    /// Each key followed by a turn of the run loop, as between two keystrokes,
    /// and the folder written down after it.
    func type(_ text: String, in window: NSWindow, id: UUID) {
        for character in text {
            send(String(character), to: window)
            RunLoop.main.run(until: Date().addingTimeInterval(0.08))
            trail.append(Shared.store.shortcut(id)?.folder ?? "?")
        }
    }
    func focus(_ showing: String, in window: NSWindow, selectAll: Bool) {
        let field = descendants(window.contentView!).compactMap { $0 as? NSTextField }
            .first { $0.isEditable && $0.stringValue == showing }
        if let field, window.makeFirstResponder(field), let editor = window.firstResponder as? NSTextView {
            if selectAll { editor.selectAll(nil) }
            else { editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0)) }
        }
        trail = []
    }
    func page(_ id: UUID) -> NSWindow {
        pages += 1
        let window = hosted(AnyView(NamingPage(id: id).environmentObject(Shared.store)
            .environmentObject(Shared.usage)), title: "Naming check \(pages)")
        RunLoop.main.run(until: Date().addingTimeInterval(0.7))
        return window
    }
    /// The page as VoiceOver reads it, with Holder's figures on hand for it
    /// to borrow if it took Holder's folder for its own.
    func read(_ window: NSWindow, _ place: String) -> [Seen] {
        for _ in 0..<2 { Shared.usage.setEntriesForTesting([holderFigures]); settle() }
        // Asked again when a read comes back empty: one run in six here lost
        // the answer for one window, and every check after it then failed on
        // nothing at all.
        var tries = 0
        while (seen(window)?.count ?? 0) <= 1 && tries < 3 { tries += 1; settle() }
        return checkAccessibility(window, surface: ControlSurface.window, place)
    }
    /// The page goes before its shortcut does: it holds a binding into the list.
    func close(_ window: NSWindow) {
        window.makeFirstResponder(nil)
        (window.contentView as? NSHostingView<AnyView>)?.rootView = AnyView(EmptyView())
        settle()
        window.orderOut(nil)
    }
    func newShortcut() -> UUID {
        let shortcut = Shortcut(name: Shared.store.uniqueName(), source: .own)
        Shared.store.shortcuts.append(shortcut)
        return shortcut.id
    }
    func someoneElses(_ folder: String, besides id: UUID) -> Bool {
        Shared.store.shortcuts.contains { $0.id != id && Graft.isNamed($0.folder, folder) }
            || Graft.isNamed(folder, leftover.lastPathComponent)
    }

    /// The name and folder boxes on one line, the folder box at its full
    /// width. With an empty label kept, the folder box sat 11 points below
    /// the name on a created shortcut's page and lost 20 points of width on
    /// a new one's.
    func boxesInLine(_ window: NSWindow, _ shortcut: Shortcut?, _ place: String) {
        let fields = descendants(window.contentView!).compactMap { $0 as? NSTextField }.filter(\.isEditable)
        let name = fields.first { $0.stringValue == shortcut?.name }.map { $0.convert($0.bounds, to: nil) }
        let folder = fields.first { $0.stringValue == shortcut?.folder }.map { $0.convert($0.bounds, to: nil) }
        check(name != nil && folder != nil && abs(name!.minY - folder!.minY) <= 1 && abs(folder!.width - 130) <= 1,
              "naming, \(place): the folder box sits level with the name, 130 points wide (name \(name.map { "\($0)" } ?? "?"), folder \(folder.map { "\($0)" } ?? "?"))")
    }
    do {
        var long = Shortcut(name: "CRM-Kai-Longname@example.com", folder: "Claude-Longname-example-com", source: .own)
        long.installedName = long.name
        let resources = Installer.installDirectoryOverride!.appending(path: "\(long.name).app/Contents/Resources")
        try fm.createDirectory(at: resources, withIntermediateDirectories: true)
        try JSONEncoder().encode(GraftConfig(profileDir: long.profileDir.path, sourceDir: nil))
            .write(to: resources.appending(path: "graft.json"))
        try fm.createDirectory(at: long.profileDir, withIntermediateDirectories: true)
        Shared.store.shortcuts.append(long)
        let window = page(long.id)
        for width in [420.0, 790.0] {
            window.setContentSize(NSSize(width: width, height: 700))
            settle()
            boxesInLine(window, Shared.store.shortcut(long.id), "a created shortcut's page \(Int(width)) wide")
        }
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == long.id }
    }

    // What a new shortcut's page offers, read before anything is typed, so
    // the checks below that something is missing are known to be looking in
    // the right place.
    do {
        let id = newShortcut()
        let window = page(id)
        boxesInLine(window, Shared.store.shortcut(id), "a new shortcut's page")
        let scope = "\(ControlSurface.window).\(Shared.store.shortcut(id)?.folder ?? "")."
        let tree = read(window, "naming, a new shortcut's page")
        check(element(scope + "start-session", in: tree) != nil
                && element(scope + "install", in: tree)?.name == L10n.text("Create Shortcut")
                && element(scope + "delete", in: tree)?.name == L10n.text("Discard")
                && element(scope + "no-usage", in: tree) != nil
                && element(scope + "folder-taken-note", in: tree) == nil,
              "naming: a new shortcut's page offers Start Session, Create Shortcut and Discard, with no figures and no note")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
    }

    let typings: [(name: String, selectAll: Bool, erase: Bool, typed: String, folder: String)] = [
        ("backspacing the number and typing", false, true, "Tas", "Claude-Tas"),
        ("typing over the name", true, false, "Tas", "Claude-Tas"),
        ("clearing the name and typing", true, true, "Tas", "Claude-Tas"),
        ("typing a name whose folder another shortcut has", true, false, "Profile", "Claude-Profile-2"),
        ("typing a name whose folder is a profile on disk", true, false, "Old", "Claude-Old-2"),
    ]
    for typing in typings {
        let id = newShortcut()
        let window = page(id)
        focus(Shared.store.shortcut(id)?.name ?? "", in: window, selectAll: typing.selectAll)
        if typing.erase { type("\u{7F}", in: window, id: id) }
        type(typing.typed, in: window, id: id)
        let typedName = Shared.store.shortcut(id)?.name ?? ""
        check(typedName.hasSuffix(typing.typed), "naming, \(typing.name): the keys reach the name (\(typedName))")
        check(Shared.store.shortcut(id)?.folder == typing.folder,
              "naming, \(typing.name): the folder follows the name to \(typing.folder) (\(trail))")
        check(!trail.contains { someoneElses($0, besides: id) },
              "naming, \(typing.name): no key puts it on another shortcut's folder or a profile on disk (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
    }

    do {
        let id = newShortcut()
        var window = page(id)
        focus(Shared.store.shortcut(id)?.folder ?? "", in: window, selectAll: true)
        type("Claude-Mine", in: window, id: id)
        focus(Shared.store.shortcut(id)?.name ?? "", in: window, selectAll: true)
        type("Tas", in: window, id: id)
        check(Shared.store.shortcut(id)?.folder == "Claude-Mine",
              "naming: a folder typed by hand stays when the name changes (\(trail))")
        close(window)
        window = page(id)
        focus(Shared.store.shortcut(id)?.name ?? "", in: window, selectAll: true)
        type("Work", in: window, id: id)
        check(Shared.store.shortcut(id)?.folder == "Claude-Mine",
              "naming: and when it changes after the page is opened again (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
    }

    // Created ones: one whose app is not where Graft looks, and one typed
    // into before its page has looked its app up at all.
    for (name, withApp) in [("Made", false), ("Fresh", true)] {
        var made = Shortcut(name: name, folder: "Claude-\(name)", source: .own)
        made.installedName = name
        try fm.createDirectory(at: made.profileDir, withIntermediateDirectories: true)
        if withApp {
            let resources = Installer.installDirectoryOverride!.appending(path: "\(name).app/Contents/Resources")
            try fm.createDirectory(at: resources, withIntermediateDirectories: true)
            try JSONEncoder().encode(GraftConfig(profileDir: made.profileDir.path, sourceDir: nil))
                .write(to: resources.appending(path: "graft.json"))
        }
        Shared.store.shortcuts.append(made)
        let place = withApp ? "created and typed into as its page opens" : "created with its app not found"
        let window: NSWindow
        if withApp {
            pages += 1
            let hosting = NSHostingView(rootView: AnyView(NamingPage(id: made.id).environmentObject(Shared.store)
                .environmentObject(Shared.usage)))
            window = LayoutWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 700),
                                  styleMask: [.titled], backing: .buffered, defer: false)
            window.title = "Naming check \(pages)"
            window.contentView = hosting
            window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
            window.orderBack(nil)
            hosting.layoutSubtreeIfNeeded()
            focus(name, in: window, selectAll: false)
            for character in " Two" { send(String(character), to: window) }
            RunLoop.main.run(until: Date().addingTimeInterval(1))
        } else {
            window = page(made.id)
            focus(name, in: window, selectAll: false)
            type(" Two", in: window, id: made.id)
        }
        check(Shared.store.shortcut(made.id)?.name == "\(name) Two", "naming, \(place): the keys reach the name")
        check(Shared.store.shortcut(made.id)?.folder == made.folder,
              "naming, \(place): renaming keeps its folder (\(Shared.store.shortcut(made.id)?.folder ?? ""))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == made.id }
    }

    // On Holder's folder, left there by an earlier version or typed there by
    // hand: nothing on the page is Holder's, and it says whose folder it is.
    func onHoldersFolder(_ tree: [Seen], _ place: String) {
        let scope = "\(ControlSurface.window).\(holder.folder)."
        check(element(scope + "start-session", in: tree) == nil,
              "naming, \(place): no Start Session, which would be Holder's account")
        check(element(scope + "five-hour", in: tree) == nil && element(scope + "no-usage", in: tree) != nil,
              "naming, \(place): none of Holder's figures")
        check(element(scope + "install", in: tree)?.name == L10n.text("Create Shortcut"),
              "naming, \(place): not taken for Holder's app, so Create Shortcut, and Open stays off")
        check(element(scope + "delete", in: tree)?.name == L10n.text("Discard"),
              "naming, \(place): Discard, since what is in the folder is Holder's")
        let note = element(scope + "folder-taken-note", in: tree)
        check(note.map { [$0.value, $0.label, $0.title].contains { $0.contains(holder.name) && $0.contains(holder.folder) } } == true,
              "naming, \(place): says whose folder it is, by name (\(note.map { [$0.value, $0.label, $0.title] } ?? []))")
    }
    do {
        let stuck = Shortcut(name: "Tas", folder: holder.folder, source: .own)
        Shared.store.shortcuts.append(stuck)
        let window = page(stuck.id)
        onHoldersFolder(read(window, "naming, one left on Holder's folder"), "one left on Holder's folder")
        focus("Tas", in: window, selectAll: false)
        type("k", in: window, id: stuck.id)
        check(Shared.store.shortcut(stuck.id)?.folder == "Claude-Task",
              "naming: one left on Holder's folder gets its own at the next key (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == stuck.id }
    }
    do {
        let id = newShortcut()
        let window = page(id)
        focus(Shared.store.shortcut(id)?.folder ?? "", in: window, selectAll: true)
        type(holder.folder, in: window, id: id)
        check(Shared.store.shortcut(id)?.folder == holder.folder,
              "naming: a folder typed by hand is taken as typed, Holder's included")
        // The page looks its app and folder up every 5 seconds; read before
        // that, it still describes the folder it had.
        RunLoop.main.run(until: Date().addingTimeInterval(5.5))
        onHoldersFolder(read(window, "naming, typed onto Holder's folder"), "typed onto Holder's folder")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
    }
    do {
        // Named like the app Holder was created as: found by that name, it
        // was taken for Holder's own.
        let namesake = Shortcut(name: holder.name, folder: holder.folder, source: .own)
        Shared.store.shortcuts.append(namesake)
        let window = page(namesake.id)
        onHoldersFolder(read(window, "naming, one named like Holder's app"), "one named like Holder's app")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == namesake.id }
    }
    do {
        // A number after the name's folder is the name's choice too, and
        // goes on following the name once the folder that forced it is gone.
        let second = Shortcut(name: "Second", folder: "Claude-Second", source: .own)
        Shared.store.shortcuts.append(second)
        let id = newShortcut()
        var window = page(id)
        focus(Shared.store.shortcut(id)?.name ?? "", in: window, selectAll: true)
        type("Second", in: window, id: id)
        check(Shared.store.shortcut(id)?.folder == "Claude-Second-2",
              "naming: a name whose folder another shortcut has is numbered (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == second.id }
        window = page(id)
        focus("Second", in: window, selectAll: false)
        type("s", in: window, id: id)
        check(Shared.store.shortcut(id)?.folder == "Claude-Seconds",
              "naming: and once that shortcut is gone, the numbered folder still follows the name (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
    }
    do {
        // A numbered folder still on disk, adopted by typing it: it holds a
        // login, and the name never takes the shortcut off it.
        let adopted = Graft.applicationSupport.appending(path: "Claude-Adopt-2")
        try fm.createDirectory(at: adopted, withIntermediateDirectories: true)
        let id = newShortcut()
        var window = page(id)
        focus(Shared.store.shortcut(id)?.name ?? "", in: window, selectAll: true)
        type("Adopt", in: window, id: id)
        focus(Shared.store.shortcut(id)?.folder ?? "", in: window, selectAll: true)
        type("Claude-Adopt-2", in: window, id: id)
        close(window)
        window = page(id)
        focus("Adopt", in: window, selectAll: false)
        type(" Mac", in: window, id: id)
        check(Shared.store.shortcut(id)?.name == "Adopt Mac" && Shared.store.shortcut(id)?.folder == "Claude-Adopt-2",
              "naming: a numbered folder adopted by hand stays when the name changes after the page is opened again (\(trail))")
        close(window)
        Shared.store.shortcuts.removeAll { $0.id == id }
        try? fm.removeItem(at: adopted)
    }
    Shared.store.shortcuts = saved
}

print("\(checks - failures)/\(checks) layout checks passed (\(Bundle.main.preferredLocalizations.joined(separator: ",")))")
exit(failures == 0 ? 0 : 1)
