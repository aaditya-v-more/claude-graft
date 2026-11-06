import AppKit
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
let shortcut = Shortcut(name: "Claude 2")
Shared.store.shortcuts = [shortcut, Shortcut(name: "Claude 3", source: .own)]
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

// MARK: - What VoiceOver and a script see

/// One element as VoiceOver and every other client of the accessibility API
/// sees it.
struct Seen {
    var role = "", subrole = "", label = "", title = "", titledBy = "", value = "", identifier = ""
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
                            identifier: text(element, "AXIdentifier"))
            return [here] + ((attribute(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []).flatMap(walk)
        }
        let windows = (attribute(app, kAXWindowsAttribute) as? [AXUIElement]) ?? []
        let found = windows.first { text($0, kAXTitleAttribute) == title }.map(walk)
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
    let unidentified = controls.filter { !$0.identifier.hasPrefix(ours) }
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

final class LayoutWindow: NSWindow {
    // CI's virtual display can be smaller than the largest test window. Keep
    // AppKit from shrinking that case to the screen instead of testing it.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        frameRect
    }
}

for appearance in [NSAppearance.Name.aqua, .darkAqua] {
for selection in [ContentView.mainProfileID, shortcut.id] {
    let root = ContentView(selection: selection)
        .environmentObject(Shared.store)
        .environmentObject(Shared.settings)
        .environmentObject(Shared.usage)
    let hosting = NSHostingView(rootView: root)
    hosting.sizingOptions = []
    let window = LayoutWindow(contentRect: NSRect(x: 100, y: 100, width: 820, height: 560),
                          styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
    window.contentView = hosting
    window.appearance = NSAppearance(named: appearance)
    window.title = "Claude Graft layout check"
    if ProcessInfo.processInfo.environment["GRAFT_LAYOUT_VISIBLE"] == "1" {
        window.orderFront(nil)
    } else {
        window.setFrameOrigin(NSPoint(x: -10000, y: -10000))
        window.orderBack(nil)
    }
    settle()
    var currentOffsets: [CGFloat: CGFloat] = [:]
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
        for size in [NSSize(width: 720, height: 460), NSSize(width: 820, height: 560),
                     NSSize(width: 1200, height: 800)] {
            window.setContentSize(size)
            settle()
            hosting.layoutSubtreeIfNeeded()
            check(abs(hosting.bounds.width - size.width) <= 1
                  && abs(hosting.bounds.height - size.height) <= 1,
                  "the window reaches the requested \(size) size")
            let views = descendants(hosting)
            if size.width == 820, state == "current" || state == "available" {
                let main = selection == ContentView.mainProfileID
                let place = "\(state), the \(main ? "main profile's" : "shortcut's") window"
                let tree = checkAccessibility(window, surface: ControlSurface.window, place)
                let profile = main ? Graft.mainProfile.lastPathComponent : shortcut.profileDir.lastPathComponent
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
                check(state != "available" || element("window.claude-update.install", in: tree) != nil,
                      "\(place): a waiting Claude update can be asked for by name")
            }
            if let directory = ProcessInfo.processInfo.environment["GRAFT_LAYOUT_SNAPSHOTS"],
               state == "current", size.width == 820 {
                let output = URL(fileURLWithPath: directory)
                    .appending(path: Bundle.main.preferredLocalizations.first ?? "en")
                    .appending(path: appearance == .aqua ? "light" : "dark")
                try! fm.createDirectory(at: output, withIntermediateDirectories: true)
                let name = selection == ContentView.mainProfileID ? "main" : "shortcut"
                if let bitmap = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) {
                    hosting.cacheDisplay(in: hosting.bounds, to: bitmap)
                    try! bitmap.representation(using: .png, properties: [:])!
                        .write(to: output.appending(path: "\(name).png"))
                }
                let tree = views.map { "\(type(of: $0)) \(hosting.convert($0.bounds, from: $0))" }
                try! tree.joined(separator: "\n").write(
                    to: output.appending(path: "\(name)-views.txt"), atomically: true, encoding: .utf8)
            }
            let split = views.compactMap { $0 as? NSSplitView }.first!
            let splitBounds = hosting.convert(split.bounds, from: split)
            let titlebars = views.filter {
                String(describing: type(of: $0)) == "NSTitlebarBackgroundView"
                    && !$0.isHiddenOrHasHiddenAncestor && $0.bounds.height > 1
            }
            check(titlebars.isEmpty,
                  "\(state), \(size): no extra titlebar covers the profile section heading")
            // A split view drawn underneath an inset still starts at y=0. Its
            // own bounds must instead begin below the whole update line, at
            // whatever height the line has in this state, and its divider.
            let header = NSHostingController(rootView: ClaudeUpdateStatus())
                .sizeThatFits(in: CGSize(width: size.width, height: 1000)).height
            check(header >= 30 && abs(splitBounds.minY - (header + 1)) <= 1,
                  "\(state), \(size): profiles start right below the update header")
            check(header <= 52, "\(state), \(size): the update header takes two lines at most")
            if state == "current" { currentOffsets[size.width] = splitBounds.minY }
            if let original = currentOffsets[size.width], state == "available" || state == "current-again" {
                check(abs(splitBounds.minY - original) <= 1,
                      "\(state), \(size): the header keeps to one line, a waiting update included")
            }
            check(splitBounds.maxY <= hosting.bounds.maxY + 1,
                  "\(state), \(size): profiles fit inside the window")
            let list = views.compactMap { $0 as? NSOutlineView }.first!
            let account = hosting.convert(list.rect(ofRow: 1), from: list)
            check(account.minY >= splitBounds.minY && account.maxY <= splitBounds.maxY,
                  "\(state), \(size): the main account row is fully visible")
            let scrolls = views.compactMap { $0 as? NSScrollView }
            check(scrolls.count >= 2, "both the sidebar and profile form are present")
            for scroll in scrolls {
                let bounds = hosting.convert(scroll.bounds, from: scroll)
                check(bounds.minY >= splitBounds.minY - 1,
                      "\(state), \(size): a scroll view cannot extend under the header")
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

let settingsWindow = hosted(SettingsView().environmentObject(Shared.settings), title: "Settings check")
let settingsTree = checkAccessibility(settingsWindow, surface: ControlSurface.settings, "Settings")
check(["open-at-login", "show-in-menu-bar", "front-icon", "front-name", "manual-updates", "hold-updates"]
        .allSatisfy { element("settings.\($0)", in: settingsTree) != nil },
      "every switch in Settings can be asked for by name, apart from the dropdown's copy of it")
settingsWindow.orderOut(nil)

print("\(checks - failures)/\(checks) layout checks passed (\(Bundle.main.preferredLocalizations.joined(separator: ",")))")
exit(failures == 0 ? 0 : 1)
