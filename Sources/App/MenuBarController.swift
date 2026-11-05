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

        Shared.claudeUpdates.$availableVersion.combineLatest(Shared.claudeUpdates.$isUpdating)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateTitle() }
            .store(in: &watches)

        Shared.updater.$availableVersion.combineLatest(Shared.updater.$isHolding)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateTitle() }
            .store(in: &watches)

        // The shortcuts too, so a new icon or name reaches the bar without
        // waiting for the next pass.
        Publishers.CombineLatest4(settings.$showFrontIcon, settings.$showFrontName,
                                  Shared.frontmost.$profile, store.$shortcuts)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateTitle() }
            .store(in: &watches)
    }

    // MARK: - The item itself

    /// Whether there is actually something in the bar to go back to.
    ///
    /// Not `item != nil`. An item macOS had no room for is still an object and
    /// still reports itself visible, so that test says yes to a bar that has
    /// nowhere to put it — and cancelling a quit then leaves no window, no
    /// reachable item, and Force Quit as the only way out. `MenuBarPlacement`
    /// carries the rule and what it was measured against.
    var isShowing: Bool {
        guard let window = item?.button?.window else { return false }
        return MenuBarPlacement.isReachable(window.frame,
                                            onAnyOf: NSScreen.screens.map(\.frame))
    }

    func install() {
        guard item == nil else { updateTitle(); return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover)
        item.button?.imagePosition = .imageLeading
        item.button?.image = NSImage(systemSymbolName: "circle.lefthalf.filled",
                                     accessibilityDescription: "Claude Graft")
        // The only handle a script has on the item: its title is a figure
        // that changes on every pass, and the bar holds no other name for it.
        item.button?.setAccessibilityIdentifier("status-item")
        self.item = item
        updateTitle()
    }

    func remove() {
        popover.performClose(nil)
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
    }

    /// The five-hour figure for whichever Claude is open, or its week if it has
    /// no five-hour limit, with the rest behind the tooltip. With the Claude in
    /// front shown, the figure is that one's.
    private func updateTitle() {
        guard let button = item?.button else { return }
        let updates = Shared.claudeUpdates
        // Claude's own update outranks Graft's: it is the one that closes
        // work, and the one with a confirmation waiting behind it.
        let heldVersion = UpdatePolicy.showsInStatusItem(holding: Shared.updater.isHolding,
                                                         available: Shared.updater.availableVersion)
            ? Shared.updater.availableVersion : nil
        button.image = NSImage(systemSymbolName: updates.isUpdating ? "arrow.triangle.2.circlepath"
                               : updates.availableVersion != nil ? "exclamationmark.circle.fill"
                               : heldVersion != nil ? "arrow.down.circle.fill" : "circle.lefthalf.filled",
                               accessibilityDescription: "Claude Graft")
        let updateTip = updates.isUpdating ? L10n.text("Claude Desktop is updating")
            : updates.availableVersion.map { L10n.format("Claude Desktop %@ is available", $0) }
            ?? heldVersion.map { L10n.format("Claude Graft %@ is available", $0) }
        let wanted = settings.showFrontIcon || settings.showFrontName
        let named = usage.statusEntry(front: wanted ? Shared.frontmost.profile : nil)
            .flatMap { $0.named ? $0.entry : nil }
        let icon = named.flatMap { settings.showFrontIcon ? FrontIcon.image(for: preset(of: $0)) : nil }
        let tag = named.flatMap {
            settings.showFrontName ? FrontTag.image(name: $0.name, preset: preset(of: $0)) : nil
        }
        // A Claude nothing could be drawn for is not shown, and nor is its
        // figure, which would otherwise pass for the tightest window's.
        let front = icon != nil || tag != nil ? named : nil
        let headline = front ?? usage.statusEntry(front: nil)?.entry
        let figures = headline?.currentUsage
        // Built the same way with or without a Claude in front, so the figure
        // is lowered in both and does not jump as one comes forward.
        if front != nil || figures != nil {
            button.attributedTitle = FrontTitle.make(icon: icon, tag: tag, figure: figures?.headline,
                                                     font: button.font ?? NSFont.menuBarFont(ofSize: 0))
        } else {
            button.title = ""
        }
        // Pictures in a title read out as nothing, so the words go alongside:
        // whose figure it is, which on screen only the icon says, and the
        // update the symbol stands for, which only the symbol does.
        let spoken = headline.flatMap { entry in
            figures.map { FrontTitle.spoken(entry.name, $0) }
        } ?? front?.name
        button.setAccessibilityTitle([spoken, updateTip].compactMap { $0 }.joined(separator: ". "))
        guard let headline, let figures else {
            button.toolTip = updateTip ?? L10n.text("No usage reported yet")
            return
        }

        var lines = [FrontTitle.tooltipLine(headline.name, figures)]
        if headline.isRunning { lines[0] += L10n.text(" — open now") }
        for entry in usage.entries where entry.id != headline.id {
            guard let other = entry.usage else { continue }
            lines.append(FrontTitle.tooltipLine(entry.name, other))
        }
        if let updateTip { lines.append(updateTip) }
        button.toolTip = lines.joined(separator: "\n")
    }

    /// The main profile has no shortcut and so no icon of its own; it keeps
    /// Claude's, which is what the original preset is.
    private func preset(of entry: UsageMonitor.Entry) -> Shortcut.IconPreset {
        entry.shortcut.flatMap { store.shortcut($0) }?.iconPreset ?? .original
    }

    // MARK: - The popover

    @objc private func togglePopover() {
        if popover.isShown { popover.performClose(nil); return }
        guard let button = item?.button else { return }

        // Opening the dropdown is someone asking to see the figures, so it may
        // ask for the access those figures need — but not skip the backoff, or
        // every open would be another call to the endpoint.
        usage.mayPromptUnasked = true
        usage.refresh(store, prompting: .onceIfShut, freshness: .recent)
        // Cheap once the transcripts have been read once, and the dropdown is
        // where the line about what was recovered is shown.
        Shared.sessionRecords.sweep(store)
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
        Shared.usagePool.watch()
        guard let window = popover.contentViewController?.view.window else { return }
        window.makeKey()
        // The identifier is what a script asks for in every language.
        window.setAccessibilityIdentifier(ControlSurface.dropdown)
        // A popover has no title of its own, so VoiceOver announced a bare
        // "popover" and nothing could ask for this one by name. The window
        // server keeps its own copy of a window's name apart from the one
        // VoiceOver reads, and a client that compared the two refused to act
        // on a window where they disagreed, so both are set.
        let name = L10n.text("Claude Graft Menu")
        window.title = name
        window.setAccessibilityTitle(name)
        // AppKit hangs a popover under the status item and leaves it out of
        // the app's windows, which is the only list most assistive clients
        // search, so nothing inside the dropdown could be found or pressed.
        // Parented to the app it is listed there too while it is open, and
        // still reads as a popover. Replacing the list instead was measured
        // leaving the app with no windows at all once it was cleared again.
        window.setAccessibilityParent(NSApp)
    }

    /// Paired with the watch above here rather than in the view: a view's
    /// disappearance inside a popover is not reliably reported, and a watch
    /// that is never let go keeps the pool on its fast timer for good.
    func popoverDidClose(_ notification: Notification) {
        Shared.usagePool.unwatch()
    }
}
