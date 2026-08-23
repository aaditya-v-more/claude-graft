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

    /// The tightest five-hour window, so the number in the bar is the one that
    /// runs out first.
    private func updateTitle() {
        guard let button = item?.button else { return }
        if let headline = usage.headline {
            button.title = " \(headline)%"
            button.toolTip = usage.entries
                .compactMap { entry -> String? in
                    guard let usage = entry.usage else { return nil }
                    return "\(entry.name): \(usage.fiveHour)% of 5 hours, \(usage.week)% of the week"
                }
                .joined(separator: "\n")
        } else {
            button.title = ""
            button.toolTip = "No usage reported yet"
        }
    }

    // MARK: - The popover

    @objc private func togglePopover() {
        if popover.isShown { popover.performClose(nil); return }
        guard let button = item?.button else { return }

        usage.refresh(store)
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
