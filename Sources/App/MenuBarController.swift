import AppKit
import Combine
import SwiftUI

/// The status item, built with AppKit rather than SwiftUI's `MenuBarExtra`.
///
/// `MenuBarExtra` alongside a `WindowGroup` sends this version of SwiftUI into
/// a loop: every scene update rebuilds the main menu, and rebuilding it
/// invalidates the scene again. With a window open that pinned a core and grew
/// by roughly a gigabyte a minute. An `NSStatusItem` has no scene to churn.
@MainActor
final class MenuBarController: NSObject, NSPopoverDelegate {
    private var item: NSStatusItem?
    private let popover = NSPopover()
    private var watches: Set<AnyCancellable> = []

    private let store: ShortcutStore
    private let settings: AppSettings
    private let usage: UsageMonitor
    private let openMainWindow: () -> Void

    init(store: ShortcutStore,
         settings: AppSettings,
         usage: UsageMonitor,
         openMainWindow: @escaping () -> Void) {
        self.store = store
        self.settings = settings
        self.usage = usage
        self.openMainWindow = openMainWindow
        super.init()

        settings.$showInMenuBar
            .receive(on: RunLoop.main)
            .sink { [weak self] wanted in wanted ? self?.install() : self?.remove() }
            .store(in: &watches)

        usage.$entries
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateTitle() }
            .store(in: &watches)
    }

    // MARK: - The item itself

    /// Whether there is actually something in the bar. The setting says what
    /// was wanted; this says what happened.
    var isShowing: Bool { item != nil }

    func install() {
        guard item == nil else { updateTitle(); return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover)
        item.button?.imagePosition = .imageLeading
        item.button?.image = NSImage(systemSymbolName: "circle.lefthalf.filled",
                                     accessibilityDescription: "Claude Graft")
        self.item = item
        updateTitle()
    }

    func remove() {
        popover.performClose(nil)
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
    }

    /// The five-hour figure for whichever Claude is open, with the rest behind
    /// the tooltip.
    private func updateTitle() {
        guard let button = item?.button else { return }
        guard let headline = usage.headlineEntry, let figures = headline.usage else {
            button.title = ""
            button.toolTip = "No usage reported yet"
            return
        }

        button.title = " \(figures.fiveHour)%"

        var lines = ["\(headline.name): \(figures.fiveHour)% of 5 hours, \(figures.week)% of the week"]
        if headline.isRunning { lines[0] += " — open now" }
        for entry in usage.entries where entry.id != headline.id {
            guard let other = entry.usage else { continue }
            lines.append("\(entry.name): \(other.fiveHour)% of 5 hours, \(other.week)% of the week")
        }
        button.toolTip = lines.joined(separator: "\n")
    }

    // MARK: - The popover

    @objc private func togglePopover() {
        if popover.isShown { popover.performClose(nil); return }
        guard let button = item?.button else { return }

        // Opening the dropdown is someone asking to see the figures, so it may
        // ask for the access those figures need — but not skip the backoff, or
        // every open would be another call to the endpoint.
        usage.mayPromptUnasked = true
        usage.refresh(store, prompting: .onceIfShut)
        let content = MenuBarContent(openMainWindow: { [weak self] in
            self?.popover.performClose(nil)
            self?.openMainWindow()
        })
        .environmentObject(store)
        .environmentObject(settings)
        .environmentObject(usage)

        popover.contentViewController = NSHostingController(rootView: content)
        popover.behavior = .transient
        popover.delegate = self
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
    }
}
