import AppKit
import SwiftUI

/// What drops down from the menu bar: every account's two usage windows, when
/// each one resets, and the few controls worth having without opening the app.
struct MenuBarContent: View {
    @EnvironmentObject private var store: ShortcutStore
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var usage: UsageMonitor
    @ObservedObject private var updater = Shared.updater
    @ObservedObject private var sessionRecords = Shared.sessionRecords
    @ObservedObject private var pool = Shared.usagePool

    /// Passed in: this view is hosted by an NSPopover, outside any scene, so
    /// the openWindow environment action is not available here.
    let openMainWindow: () -> Void
    /// The most the dropdown may stand: the screen under the menu bar, less
    /// the arrow. Past it the account list scrolls, because a popover taller
    /// than its screen runs off the bottom and takes the switches and Quit
    /// with it.
    var maxHeight: CGFloat = .infinity

    @State private var starting: String?
    @State private var problem: String?

    var body: some View {
        DropdownColumn(maxHeight: maxHeight) {
            UpdateRecoveryNotice()
            ClaudeUpdateStatus(compact: true)
            Divider()
        } list: {
            if usage.entries.isEmpty {
                Text("No Claude profiles yet")
                    .foregroundStyle(.secondary)
                    .identified("no-profiles")
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
            } else {
                ForEach(usage.entries) { entry in
                    UsageRow(entry: entry,
                             starting: starting == entry.id,
                             open: { open(entry) },
                             start: { startSession(entry) },
                             refresh: { usage.refresh(store, interactive: true, focus: entry.profile) })
                    if entry.id != usage.entries.last?.id {
                        Divider().padding(.leading, 14)
                    }
                }
            }
            // An account that is also one of the Claudes above is that
            // Claude's row already: one allowance, one pair of bars.
            ForEach(pool.accountsAlone) { account in
                Divider().padding(.leading, 14)
                AccountUsageRow(account: account,
                                starting: starting == account.id,
                                start: { startSession(account) })
            }
            SharedChats()
        } foot: {
            Divider().padding(.vertical, 6)

            if let problem = problem ?? usage.liveProblem {
                Text(problem)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .identified("problem")
                    .padding(.horizontal, 14)
                    .padding(.bottom, 8)
            }
            LendingPausedNotice()
            // One key opens both, so the notice that says lending stopped is
            // the one shown, not two buttons for one dialog.
            if problem == nil, usage.liveProblem == nil, usage.needsKeychainAccess, !pool.lendingPaused {
                Button { usage.refresh(store, interactive: true) } label: {
                    Label("Turn on live usage", systemImage: "lock")
                        .font(.callout)
                }
                .buttonStyle(.link)
                .help("Reads each account's limits from Anthropic. macOS asks once for keychain access.")
                .identified("turn-on-live-usage")
                .padding(.horizontal, 14)
                .padding(.bottom, 8)
            }

            if sessionRecords.recovered.count == 1, let title = sessionRecords.recovered.first {
                Text("Recovered “\(title)”, which had closed without a record")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .identified("recovered")
                    .padding(.horizontal, 14)
                    .padding(.bottom, 8)
            } else if !sessionRecords.recovered.isEmpty {
                Text("Recovered \(sessionRecords.recovered.count) sessions that closed without records")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .identified("recovered")
                    .padding(.horizontal, 14)
                    .padding(.bottom, 8)
            }

            if let version = updater.availableVersion {
                Button { updater.install() } label: {
                    Label(updater.isHolding ? L10n.format("Install Claude Graft %@", version)
                                            : L10n.format("Claude Graft %@ is available", version),
                          systemImage: "arrow.down.circle")
                        .font(.callout)
                }
                .buttonStyle(.link)
                .help("Installs it and restarts. Nothing is asked.")
                .identified("graft-update")
                .padding(.horizontal, 14)
                .padding(.bottom, 8)
            }

            // Each switch explains itself on hover, never underneath: a note
            // shown while a switch is on stays for as long as it is, and the
            // dropdown grew a paragraph for every one somebody had turned on.
            VStack(alignment: .leading, spacing: 6) {
                Toggle("Open at Login", isOn: Binding(
                    get: { settings.openAtLogin },
                    set: { problem = settings.setOpenAtLogin($0) }))
                    .identified("open-at-login")
                Toggle("Show in Menu Bar", isOn: $settings.showInMenuBar)
                    .identified("show-in-menu-bar")
                FrontClaudeControl()
                ManualUpdateControl()
                HoldUpdatesControl()
            }
            .toggleStyle(.checkbox)
            .padding(.horizontal, 14)

            Divider().padding(.vertical, 6)

            VStack(alignment: .leading, spacing: 4) {
                MenuButton(usage.isRefreshing ? "Refreshing…" : "Refresh Usage", id: "refresh-usage") {
                    usage.refresh(store, interactive: true)
                }
                MenuButton(updater.canCheck ? "Check for Claude Graft Updates" : "Checking for Claude Graft Updates…",
                           id: "check-graft-updates") {
                    updater.checkForUpdates()
                }
                .disabled(!updater.canCheck)
                MenuButton("Open Claude Graft", id: "open-window", action: openMainWindow)
                MenuButton("Donate…", id: "support", icon: "heart.fill", iconTint: .donateHeart) { Links.open(Links.support) }
                MenuButton("Quit Claude Graft", id: "quit") { AppDelegate.quit() }
            }
            .padding(.horizontal, 8)
            .padding(.bottom, 8)
        }
        .onAppear {
            settings.refreshLoginItem()
            usage.refresh(store, prompting: .onceIfShut, freshness: .recent)
        }
        .environment(\.controlSurface, ControlSurface.dropdown)
    }

    /// The dropdown is where a Claude actually gets opened most of the time,
    /// and it was the one route that never checked who else was already on
    /// those chats — it went straight to `openApplication`. The window asked
    /// and this did not, for the same click.
    private func open(_ entry: UsageMonitor.Entry) {
        let shortcut = entry.shortcut.flatMap { store.shortcut($0) }
        let neighbours = store.chatStoreNeighbours(of: shortcut)
        problem = nil

        DispatchQueue.global(qos: .userInitiated).async {
            let openNow = ChatConflict.openSharers(of: entry.profile, among: neighbours)
            DispatchQueue.main.async {
                guard openNow.isEmpty || ChatConflict.askInPopover(sharers: openNow) else { return }
                ProfileOpener.open(profile: entry.profile, shortcut: shortcut,
                                   among: store.shortcuts) { error in
                    problem = error?.localizedDescription
                }
            }
        }
    }

    private func startSession(_ entry: UsageMonitor.Entry) {
        guard starting == nil else { return }
        starting = entry.id
        problem = nil
        let profile = entry.profile
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = SessionStarter.start(profile: profile, interactive: true)
            // The window this just opened is exactly what the stored reading
            // predates, so it is dropped rather than waited out.
            usage.invalidate(profile)
            DispatchQueue.main.async {
                starting = nil
                problem = failure?.errorDescription
                // Starting the session already asked the keychain for this
                // press; asking here too was a second dialog after a Deny, and
                // without a focus one for every signed-in Claude.
                usage.refresh(store, interactive: true, prompting: .no, focus: profile)
            }
        }
    }
}

extension MenuBarContent {
    fileprivate func startSession(_ account: PoolAccount) {
        guard starting == nil else { return }
        starting = account.id
        problem = nil
        pool.startSession(account: account.id) { failure in
            starting = nil
            problem = failure
        }
    }
}

/// The dropdown's three parts stacked, with only the middle one ever
/// scrolling. The list is what grows with every account added; the switches
/// and Quit underneath are what someone opened the dropdown to reach, so they
/// stay put and the list gives up the height instead.
struct DropdownColumn<Head: View, Rows: View, Foot: View>: View {
    let maxHeight: CGFloat
    @ViewBuilder let head: Head
    @ViewBuilder let list: Rows
    @ViewBuilder let foot: Foot

    @State private var heights: [DropdownPart: CGFloat] = [:]

    /// However crowded the rest is, this much of the list stays in view, or a
    /// long notice could leave no account showing at all.
    static var leastList: CGFloat { 160 }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 0) { head }.measured(.head)
            // Unscrolled until it has to be: a list that fits keeps the height
            // it asks for, which a scroll view never states for it.
            if let room = Self.listHeight(list: heights[.list] ?? 0, head: heights[.head] ?? 0,
                                          foot: heights[.foot] ?? 0, limit: maxHeight) {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 0) { list }.measured(.list)
                }
                .frame(height: room)
            } else {
                VStack(alignment: .leading, spacing: 0) { list }.measured(.list)
            }
            VStack(alignment: .leading, spacing: 0) { foot }.measured(.foot)
        }
        .frame(width: 320)
        .onPreferenceChange(DropdownPartHeights.self) { heights = $0 }
    }

    /// The height the list is held to, or nil when the whole of it fits.
    static func listHeight(list: CGFloat, head: CGFloat, foot: CGFloat, limit: CGFloat) -> CGFloat? {
        let room = limit - head - foot
        guard list > room else { return nil }
        return max(room, min(list, leastList))
    }

}

private enum DropdownPart: Hashable { case head, list, foot }

private struct DropdownPartHeights: PreferenceKey {
    static let defaultValue: [DropdownPart: CGFloat] = [:]
    static func reduce(value: inout [DropdownPart: CGFloat], nextValue: () -> [DropdownPart: CGFloat]) {
        value.merge(nextValue()) { $1 }
    }
}

private extension View {
    func measured(_ part: DropdownPart) -> some View {
        background(GeometryReader { proxy in
            Color.clear.preference(key: DropdownPartHeights.self, value: [part: proxy.size.height])
        })
    }
}

/// A row that reads like a menu item rather than a button.
struct MenuButton: View {
    let title: String
    /// Asked for apart from the title, which changes while the action runs.
    let id: String
    let icon: String?
    let iconTint: Color?
    let action: () -> Void
    @State private var hovering = false

    init(_ title: String, id: String, icon: String? = nil, iconTint: Color? = nil,
         action: @escaping () -> Void) {
        self.title = L10n.text(title)
        self.id = id
        self.icon = icon
        self.iconTint = iconTint
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if let icon {
                    Image(systemName: icon)
                        .foregroundStyle(iconTint.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.primary))
                }
                Text(title)
            }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 6)
                .padding(.vertical, 4)
                .background(hovering ? Color.accentColor.opacity(0.15) : .clear,
                            in: RoundedRectangle(cornerRadius: 5))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .identified(id)
    }
}

struct UsageRow: View {
    let entry: UsageMonitor.Entry
    let starting: Bool
    let open: () -> Void
    let start: () -> Void
    let refresh: () -> Void

    /// The dot is the only thing saying whether that Claude is open, and a
    /// dot says nothing to somebody who cannot see it.
    private var status: String {
        [entry.plan, L10n.text(entry.isRunning ? "Running" : "Not running")]
            .compactMap { $0 }.joined(separator: ", ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            // A long name of the button, as in Russian, left no room for the
            // rest: the name wrapped and the button was cut short.
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 6) { title(plan: entry.plan); Spacer(); buttons }
                HStack(spacing: 6) { title(plan: nil); Spacer(); buttons }
                VStack(alignment: .leading, spacing: 6) {
                    title(plan: entry.plan)
                    HStack(spacing: 6) { Spacer(); buttons }
                }
            }

            UsageWindows(entry: entry)
            UsageFreshness(entry: entry, refresh: refresh)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .controls(profile: entry.profile)
    }

    private func title(plan: String?) -> some View {
        HStack(spacing: 6) {
            Circle()
                .fill(entry.isRunning ? Color.green : Color.secondary.opacity(0.35))
                .frame(width: 6, height: 6)
            Text(entry.name).fontWeight(.medium).lineLimit(1)
            if let plan {
                Text(plan)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .spoken(entry.name, value: status)
        .accessibilityAddTraits(.isHeader)
        .identified("account")
    }

    // Named for their account: every row has a pair, and a list of the
    // dropdown's controls would otherwise read "Open" once per account with
    // nothing to tell them apart.
    @ViewBuilder private var buttons: some View {
        if entry.mayStartSession {
            Button(action: start) {
                if starting {
                    ProgressView().controlSize(.small)
                } else {
                    Text("Start Session")
                }
            }
            .controlSize(.small)
            .disabled(starting)
            .help("Sends one short message to this account to open its five-hour window")
            .accessibilityLabel(L10n.format("Start Session for %@", entry.name))
            .identified("start-session")
            .fixedSize()
        }
        Button("Open", action: open)
            .controlSize(.small)
            .accessibilityLabel(L10n.format("Open %@", entry.name))
            .identified("open")
            .fixedSize()
    }
}

/// An account's windows as the dropdown draws them, for a Claude's row and a
/// signed-in account's alike.
struct UsageWindows: View {
    let entry: UsageMonitor.Entry

    var body: some View {
        if let usage = entry.usage {
            if let fiveHour = usage.fiveHour {
                UsageBar(label: "5 hours",
                         percent: fiveHour,
                         resets: usage.fiveHourReset,
                         dimmed: entry.isDimmed,
                         keepsLine: true)
                    .identified("five-hour")
                    // Outside the bar, which is read out as one thing, and on
                    // its reset line: the buttons' row has no width to give.
                    .overlay(alignment: .bottomTrailing) {
                        AutoSessionToggle(key: entry.key, name: entry.name)
                            .toggleStyle(.checkbox)
                            .controlSize(.mini)
                            .font(.caption2)
                    }
            } else {
                NoFiveHourLimit(font: .caption, dimmed: entry.isDimmed)
            }
            UsageBar(label: "Week",
                     percent: usage.week,
                     resets: usage.weekReset,
                     dimmed: entry.isDimmed)
                .identified("week")
            if let fable = usage.fable {
                UsageBar(label: "Fable", percent: fable,
                         resets: usage.fableReset, dimmed: entry.isDimmed)
                    .identified("fable")
            }
        } else {
            Text("No usage reported yet")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .identified("no-usage")
        }
    }
}

/// An account signed in through Graft, under the Claudes: the same windows,
/// and no Open, since it has no Claude to open.
struct AccountUsageRow: View {
    let account: PoolAccount
    let starting: Bool
    let start: () -> Void

    @EnvironmentObject private var store: ShortcutStore
    @EnvironmentObject private var usage: UsageMonitor
    @ObservedObject private var pool = Shared.usagePool

    var body: some View {
        let name = pool.name(of: account.id)
        let condition = pool.condition(of: account.id)
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                HStack(spacing: 6) {
                    // The colour it wears on the bar when it is answering.
                    if let icon = FrontIcon.image(for: pool.preset(of: account.id)) {
                        Image(nsImage: icon).resizable().frame(width: 14, height: 14)
                    }
                    Text(name).fontWeight(.medium).lineLimit(1)
                    if let plan = entry?.plan {
                        Text(plan)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                }
                .spoken(name, value: [entry?.plan, condition].compactMap { $0 }.joined(separator: ", "))
                .accessibilityAddTraits(.isHeader)
                .identified("account")
                Spacer()
                if pool.lending[account.id] == .ready, entry?.mayStartSession ?? true {
                    Button(action: start) {
                        if starting {
                            ProgressView().controlSize(.small)
                        } else {
                            Text("Start Session")
                        }
                    }
                    .controlSize(.small)
                    .disabled(starting)
                    .help("Sends one short message to this account to open its five-hour window")
                    .accessibilityLabel(L10n.format("Start Session for %@", name))
                    .identified("start-session")
                }
            }
            if pool.needsAttention(account.id) {
                Text(condition)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .lineLimit(1)
                    .help(condition)
                    .identified("state")
            }
            if let entry {
                UsageWindows(entry: entry)
                if account.scopes.contains(ClaudeCredentials.usageScope) {
                    UsageFreshness(entry: entry) {
                        usage.refresh(store, interactive: true, account: account.id)
                    }
                } else if entry.refreshed != nil {
                    // Nothing to ask again: its figures come with its answers.
                    Text(entry.freshness())
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .identified("freshness")
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .controls(account: account.id)
    }

    private var entry: UsageMonitor.Entry? { pool.entry(forAccount: account.id) }
}

/// Where the five-hour bar would be, on an account that has no such window.
/// A bar at 0% reads as a window with nothing spent in it yet, which is not
/// the same thing, and it never moved however hard the account was used.
struct NoFiveHourLimit: View {
    let font: Font
    var dimmed = false

    var body: some View {
        Text("No 5-hour limit on this account")
            .font(font)
            .foregroundStyle(.secondary)
            .opacity(dimmed ? 0.45 : 1)
            .identified("no-five-hour-limit")
    }
}

struct UsageBar: View {
    let label: String
    let percent: Int
    var resets: Date?
    var dimmed = false
    /// The countdown beside the figure rather than under it, as the window
    /// draws it: three bars with a line each under them were what pushed a
    /// Claude's pane past the bottom of the smallest window.
    var inline = false
    /// Holds the line under the bar while no reset is known, for what is
    /// laid over its far end.
    var keepsLine = false

    private var fraction: Double { min(max(Double(percent) / 100, 0), 1) }

    private var tint: Color {
        switch percent {
        case ..<70: return .green
        case ..<90: return .orange
        default: return .red
        }
    }

    private func countdown(_ remaining: String?) -> some View {
        Text(remaining ?? "")
            .font(.caption2.monospacedDigit())
            .foregroundStyle(.tertiary)
            .lineLimit(1)
            .frame(width: 76, alignment: .trailing)
            .help(remaining.map { L10n.format("Resets in %@", $0) } ?? "")
    }

    var body: some View {
        // On a clock of its own because SwiftUI only redraws a bar whose
        // inputs changed. A Fable bar whose figures stayed put sat at one
        // countdown for ten minutes beside a weekly one that kept counting,
        // and a window drawn earlier disagreed with the dropdown by two. The
        // whole bar rather than the line under it, so that what VoiceOver
        // reads counts down with what is on screen.
        TimelineView(.periodic(from: .now, by: 30)) { context in
            let remaining = resets.flatMap { Graft.countdown(to: $0, from: context.date) }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    Text(L10n.text(label))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .frame(width: 52, alignment: .leading)
                    GeometryReader { geometry in
                        ZStack(alignment: .leading) {
                            Capsule().fill(Color.secondary.opacity(0.2))
                            Capsule()
                                .fill(tint)
                                .frame(width: max(2, geometry.size.width * fraction))
                        }
                    }
                    .frame(height: 5)
                    Text("\(percent)%")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: 36, alignment: .trailing)
                    if inline { countdown(remaining) }
                }
                if let remaining, !inline {
                    Text("Resets in \(remaining)")
                        .font(.caption2.monospacedDigit())
                        .foregroundStyle(.tertiary)
                        .padding(.leading, 60)
                } else if keepsLine, !inline {
                    Text(verbatim: " ").font(.caption2.monospacedDigit())
                }
            }
            // One progress indicator, where it was three bits of text and a
            // shape: the figure read out with no word of what it was a figure
            // of. The sentence is the value's description, which is what
            // VoiceOver says in place of the bare fraction a bar carries.
            .accessibilityRepresentation {
                ProgressView(value: fraction) { Text(L10n.text(label)) }
            }
            .accessibilityValue(remaining.map {
                L10n.format("%1$ld%% used, resets in %2$@", percent, $0)
            } ?? L10n.format("%ld%% used", percent))
        }
        .opacity(dimmed ? 0.45 : 1)
    }
}

/// When an account's figures were taken, where from, and a way to ask again.
///
/// Said under every account rather than only once a figure has gone stale:
/// a bar that is quietly five minutes behind looks exactly like one that is
/// current, and a figure read off the profile's own record when the service
/// would not answer looks exactly like a live one.
struct UsageFreshness: View {
    let entry: UsageMonitor.Entry
    let refresh: () -> Void
    @ObservedObject private var monitor = Shared.usage

    private var spinning: Bool {
        monitor.isRefreshing && (monitor.refreshingProfile == nil || monitor.refreshingProfile == entry.id)
    }

    var body: some View {
        // Time and reason share a line: under each other they were the line
        // that pushed the main window past its height.
        HStack(spacing: 4) {
                // Ticks on its own, so "2 min ago" does not sit there for an
                // hour between passes that changed nothing.
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    Text(entry.freshness(now: context.date))
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .identified("freshness")
                        .lineLimit(1)
                        .layoutPriority(1)
                }
                if let note = entry.note {
                    Text("·").font(.caption2).foregroundStyle(.tertiary)
                    Text(note)
                        .font(.caption2)
                        .foregroundStyle(.orange)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .help(note)
                        .identified("usage-note")
                }
                Spacer(minLength: 4)
                Button(action: refresh) {
                    if spinning {
                        ProgressView().controlSize(.mini)
                    } else {
                        Image(systemName: "arrow.clockwise")
                            .font(.caption2)
                    }
                }
                .buttonStyle(.borderless)
                .disabled(spinning)
                .help("Ask Anthropic for this account's usage now")
                .accessibilityLabel(L10n.format("Refresh usage for %@", entry.name))
                .identified("refresh-usage")
        }
    }

}

/// The Claude in front, put in the bar because the Dock gives every one of them
/// Claude's own name and icon and macOS will not let anything else change that.
struct FrontClaudeControl: View {
    @EnvironmentObject private var settings: AppSettings

    /// A group rather than a stack, so that Settings gives each switch a row
    /// of its own like every other one there.
    var body: some View {
        Group {
            Toggle("Show the icon of the Claude in front", isOn: $settings.showFrontIcon)
                .identified("front-icon")
            Toggle("Show the name of the Claude in front", isOn: $settings.showFrontName)
                .identified("front-name")
        }
        .help("The menu bar shows which Claude you're using, beside that account's usage. The Dock can't: macOS calls every running Claude “Claude”.")
        .font(.callout)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// Graft's own updates, beside the switch for Claude's. The two are separate on
/// purpose: one holds back the app doing the reporting, the other every Claude.
struct HoldUpdatesControl: View {
    @EnvironmentObject private var settings: AppSettings

    var body: some View {
        Toggle("Hold Claude Graft updates", isOn: $settings.holdUpdates)
            .help("Graft still checks every hour and says when a version is ready, in this menu and on its icon. Nothing downloads until you press Install.")
            .identified("hold-updates")
            .font(.callout)
            .fixedSize(horizontal: false, vertical: true)
    }
}
