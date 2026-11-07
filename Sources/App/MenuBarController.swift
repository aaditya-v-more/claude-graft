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

    /// What the title was last built from, kept so the ticker can move
    /// without the usage figures being worked out again on every tick.
    private var parts: (icon: NSImage?, tag: NSImage?, figure: Graft.Usage.Headline?)?
    private var spokenTitle = ""
    /// The conversation running across the bar, if one has just begun.
    private var newChat: UsagePoolController.Chat?
    private var tickerText: String?
    /// What the text would say, drawn or not, so a conversation whose text
    /// gave way still has its account kept current for the spoken title.
    private var tickerWords: String?
    /// The conversation whose text was taken out of the bar for lack of room.
    private var tickerRefused: String?
    private var tickerOffset: CGFloat = 0
    private var tickerHeld = 0
    private var ticker: Timer?

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

        // Published values arrive before they are stored, so the check waits
        // a turn of the run loop to read what changed.
        Shared.usagePool.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateNewChat() }
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
        let screens = NSScreen.screens
        return MenuBarPlacement.isReachable(window.frame, onAnyOf: screens.map(\.frame),
                                            notches: screens.compactMap(MenuBarPlacement.notch))
    }

    func install() {
        guard item == nil else { updateTitle(); return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover)
        // A right click moves a conversation that has just begun.
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
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
        ticker?.invalidate()
        ticker = nil
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
        parts = front != nil || figures != nil ? (icon, tag, figures?.headline) : nil
        // Pictures in a title read out as nothing, so the words go alongside:
        // whose figure it is, which on screen only the icon says, and the
        // update the symbol stands for, which only the symbol does.
        let spoken = headline.flatMap { entry in
            figures.map { FrontTitle.spoken(entry.name, $0) }
        } ?? front?.name
        spokenTitle = [spoken, updateTip].compactMap { $0 }.joined(separator: ". ")
        applyTitle()
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

    /// Built the same way with or without a Claude in front, so the figure is
    /// lowered in both and does not jump as one comes forward.
    private func applyTitle() {
        guard let button = item?.button else { return }
        let font = button.font ?? NSFont.menuBarFont(ofSize: 0)
        let picture = tickerText.map { NewChatTicker.image($0, offset: tickerOffset, font: font) }
        if parts != nil || picture != nil {
            button.attributedTitle = FrontTitle.make(icon: parts?.icon, tag: parts?.tag,
                                                     figure: parts?.figure, font: font, ticker: picture)
        } else {
            button.title = ""
        }
        let chat = newChat.map { NewChatTicker.spoken(title: $0.title, account: answering($0)) }
        button.setAccessibilityTitle([spokenTitle, chat].compactMap { $0 }.filter { !$0.isEmpty }
            .joined(separator: ". "))
    }

    // MARK: - A conversation that has just begun

    /// Who answered it last, or who it was moved to and has not answered yet,
    /// as the dropdown says it.
    private func answering(_ chat: UsagePoolController.Chat) -> String {
        let pool = Shared.usagePool
        if let served = chat.servedBy { return pool.name(of: served) }
        if let assigned = chat.assigned { return pool.name(of: assigned) }
        return L10n.text("Automatic")
    }

    private func updateNewChat() {
        DispatchQueue.main.async { [weak self] in self?.showNewChat() }
    }

    private func showNewChat() {
        let chat = item == nil ? nil : Shared.usagePool.newChat(front: Shared.frontmost.profile)
        let words = chat.map { NewChatTicker.text(title: $0.title, account: answering($0)) }
        guard chat?.id != newChat?.id || words != tickerWords else { return }
        let text = chat?.id == tickerRefused ? nil : words
        if chat?.id != newChat?.id {
            tickerOffset = 0
            tickerHeld = 0
        }
        // Measured before the text goes in: an item already out of reach is
        // not one the text pushed there.
        let wasShowing = text != nil && text != tickerText && isShowing
        newChat = chat
        tickerWords = words
        tickerText = text
        if chat == nil {
            ticker?.invalidate()
            ticker = nil
        } else if ticker == nil {
            let timer = Timer(timeInterval: NewChatTicker.tick, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.advanceTicker() }
            }
            RunLoop.main.add(timer, forMode: .common)
            ticker = timer
        }
        applyTitle()
        if wasShowing, let id = chat?.id { keepReachable(id) }
    }

    /// The text makes the item wider, and a bar with no room for the wider
    /// item does not refuse it: macOS puts it past the edge of the screen,
    /// where it still reports itself visible. On a full bar the item vanished
    /// for the two minutes the text ran and came back when it went, so the
    /// count, the dropdown and the right click went with it. A figure that can
    /// be reached matters more than the text, so the text gives way for that
    /// conversation; its words stay in the item's spoken title, and the right
    /// click still moves it.
    private func keepReachable(_ id: String) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            guard let self, self.newChat?.id == id, self.tickerText != nil, !self.isShowing
            else { return }
            self.tickerRefused = id
            self.tickerText = nil
            self.applyTitle()
            Diagnostics.note("menu.ticker withdrawn", ["reason": "no room in the menu bar"])
        }
    }

    private func advanceTicker() {
        // A conversation stops being new on the clock, not only when the
        // relay next has something to say.
        if let started = newChat?.startedAt,
           Date().timeIntervalSince(started) >= UsagePoolController.newChatShown {
            showNewChat()
            return
        }
        guard let text = tickerText, let button = item?.button,
              let cycle = NewChatTicker.cycle(text, font: button.font ?? NSFont.menuBarFont(ofSize: 0))
        else { return }
        if tickerOffset == 0, tickerHeld < NewChatTicker.pause {
            tickerHeld += 1
            return
        }
        tickerHeld = 0
        tickerOffset += NewChatTicker.step
        if tickerOffset >= cycle { tickerOffset = 0 }
        applyTitle()
    }

    /// The same choices as the conversation's row in the dropdown, reached
    /// without opening it.
    private func showNewChatMenu(_ chat: UsagePoolController.Chat, from button: NSStatusBarButton) {
        let pool = Shared.usagePool
        let menu = NSMenu()
        let heading = NSMenuItem(title: chat.title ?? L10n.text("New chat"), action: nil, keyEquivalent: "")
        heading.isEnabled = false
        menu.addItem(heading)
        menu.addItem(.separator())
        let automatic = NSMenuItem(title: L10n.text("Automatic"), action: #selector(moveNewChat(_:)),
                                   keyEquivalent: "")
        automatic.target = self
        automatic.representedObject = [chat.id, ""]
        automatic.state = chat.assigned == nil ? .on : .off
        menu.addItem(automatic)
        menu.addItem(.separator())
        for choice in chat.choices {
            let entry = NSMenuItem(title: pool.name(of: choice), action: #selector(moveNewChat(_:)),
                                   keyEquivalent: "")
            entry.target = self
            entry.representedObject = [chat.id, choice]
            entry.state = chat.assigned == choice ? .on : .off
            entry.setAccessibilityIdentifier("new-chat.account.\(choice)")
            menu.addItem(entry)
        }
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height + 5), in: button)
    }

    @objc private func moveNewChat(_ sender: NSMenuItem) {
        guard let pair = sender.representedObject as? [String], pair.count == 2 else { return }
        Shared.usagePool.assign(pair[0], to: pair[1].isEmpty ? nil : pair[1])
    }

    /// The main profile has no shortcut and so no icon of its own; it keeps
    /// Claude's, which is what the original preset is.
    private func preset(of entry: UsageMonitor.Entry) -> Shortcut.IconPreset {
        entry.shortcut.flatMap { store.shortcut($0) }?.iconPreset ?? .original
    }

    // MARK: - The popover

    @objc private func togglePopover() {
        if let event = NSApp.currentEvent, event.type == .rightMouseUp,
           let chat = newChat, let button = item?.button {
            popover.performClose(nil)
            showNewChatMenu(chat, from: button)
            return
        }
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
