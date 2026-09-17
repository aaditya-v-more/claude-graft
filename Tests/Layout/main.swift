import AppKit
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
        for size in [NSSize(width: 720, height: 460), NSSize(width: 820, height: 560),
                     NSSize(width: 1200, height: 800)] {
            window.setContentSize(size)
            settle()
            hosting.layoutSubtreeIfNeeded()
            check(abs(hosting.bounds.width - size.width) <= 1
                  && abs(hosting.bounds.height - size.height) <= 1,
                  "the window reaches the requested \(size) size")
            let views = descendants(hosting)
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
            // A split view drawn underneath an inset still starts at y=0.
            // Its own bounds must instead begin below the entire status bar.
            check(splitBounds.minY >= 45, "\(state), \(size): profiles start below the update header")
            if state == "current" { currentOffsets[size.width] = splitBounds.minY }
            if let original = currentOffsets[size.width], state != "current" {
                check(state == "current-again" ? abs(splitBounds.minY - original) <= 1
                      : splitBounds.minY > original + 8,
                      "\(state), \(size): profiles follow the changing header height")
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
print("\(checks - failures)/\(checks) layout checks passed (\(Bundle.main.preferredLocalizations.joined(separator: ",")))")
exit(failures == 0 ? 0 : 1)
