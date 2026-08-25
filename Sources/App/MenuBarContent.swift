import AppKit
import SwiftUI

/// What drops down from the menu bar: every account's two usage windows, when
/// each one resets, and the few controls worth having without opening the app.
struct MenuBarContent: View {
    @EnvironmentObject private var store: ShortcutStore
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var usage: UsageMonitor
    @ObservedObject private var updater = Shared.updater

    /// Passed in: this view is hosted by an NSPopover, outside any scene, so
    /// the openWindow environment action is not available here.
    let openMainWindow: () -> Void

    @State private var starting: String?
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if usage.entries.isEmpty {
                Text("No Claude profiles yet")
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
            } else {
                ForEach(usage.entries) { entry in
                    UsageRow(entry: entry,
                             starting: starting == entry.id,
                             open: { open(entry) },
                             start: { startSession(entry) })
                    if entry.id != usage.entries.last?.id {
                        Divider().padding(.leading, 14)
                    }
                }
            }

            Divider().padding(.vertical, 6)

            if let problem = problem ?? usage.liveProblem {
                Text(problem)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 14)
                    .padding(.bottom, 8)
            } else if usage.needsKeychainAccess {
                Button { usage.refresh(store, interactive: true) } label: {
                    Label("Turn on live usage", systemImage: "lock")
                        .font(.callout)
                }
                .buttonStyle(.link)
                .help("Reads each account's limits from Anthropic. macOS asks once for keychain access.")
                .padding(.horizontal, 14)
                .padding(.bottom, 8)
            }

            if let version = updater.availableVersion {
                Button { updater.checkForUpdates() } label: {
                    Label("Version \(version) is available", systemImage: "arrow.down.circle")
                        .font(.callout)
                }
                .buttonStyle(.link)
                .help("Installs it and restarts. Nothing is asked.")
                .padding(.horizontal, 14)
                .padding(.bottom, 8)
            }

            VStack(alignment: .leading, spacing: 6) {
                Toggle("Open at Login", isOn: Binding(
                    get: { settings.openAtLogin },
                    set: { problem = settings.setOpenAtLogin($0) }))
                Toggle("Show in Menu Bar", isOn: $settings.showInMenuBar)
            }
            .toggleStyle(.checkbox)
            .padding(.horizontal, 14)

            Divider().padding(.vertical, 6)

            VStack(alignment: .leading, spacing: 4) {
                MenuButton(usage.isRefreshing ? "Refreshing…" : "Refresh Usage") {
                    usage.refresh(store, interactive: true)
                }
                MenuButton(updater.canCheck ? "Check for Updates" : "Checking for Updates…") {
                    updater.checkForUpdates()
                }
                .disabled(!updater.canCheck)
                MenuButton("Open Claude Graft", action: openMainWindow)
                MenuButton("Sponsor Claude Graft…") { Links.open(Links.sponsor) }
                MenuButton("Quit Claude Graft") { AppDelegate.quit() }
            }
            .padding(.horizontal, 8)
            .padding(.bottom, 8)
        }
        .frame(width: 320)
        .onAppear {
            settings.refreshLoginItem()
            usage.refresh(store, prompting: .onceIfShut, freshness: .recent)
        }
    }

    /// The dropdown is where a Claude actually gets opened most of the time,
    /// and it was the one route that never checked who else was already on
    /// those chats — it went straight to `openApplication`. The window asked
    /// and this did not, for the same click.
    private func open(_ entry: UsageMonitor.Entry) {
        let shortcut = entry.shortcut.flatMap { store.shortcut($0) }
        let bundle = shortcut.flatMap { Installer.installedBundle(for: $0) }
        let neighbours = store.chatStoreNeighbours(of: shortcut)

        DispatchQueue.global(qos: .userInitiated).async {
            let openNow = ChatConflict.openSharers(of: entry.profile, among: neighbours)
            DispatchQueue.main.async {
                guard openNow.isEmpty || ChatConflict.askInPopover(sharers: openNow) else { return }
                // A shortcut goes through its own launcher, which is what
                // re-establishes the links before anything opens. Claude's own
                // profile has no shortcut to go through, and cannot be opened
                // by handing its bundle to LaunchServices either — that
                // reopens whichever instance started first, which with a
                // shortcut running is a grafted profile, not this one.
                if let bundle {
                    NSWorkspace.shared.openApplication(at: bundle,
                                                       configuration: NSWorkspace.OpenConfiguration())
                } else {
                    DispatchQueue.global(qos: .userInitiated).async {
                        Graft.open(profile: Graft.mainProfile)
                    }
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
                usage.refresh(store, interactive: true)
            }
        }
    }
}

/// A row that reads like a menu item rather than a button.
struct MenuButton: View {
    let title: String
    let action: () -> Void
    @State private var hovering = false

    init(_ title: String, action: @escaping () -> Void) {
        self.title = title
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 6)
                .padding(.vertical, 4)
                .background(hovering ? Color.accentColor.opacity(0.15) : .clear,
                            in: RoundedRectangle(cornerRadius: 5))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

struct UsageRow: View {
    let entry: UsageMonitor.Entry
    let starting: Bool
    let open: () -> Void
    let start: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Circle()
                    .fill(entry.isRunning ? Color.green : Color.secondary.opacity(0.35))
                    .frame(width: 6, height: 6)
                Text(entry.name).fontWeight(.medium)
                if let plan = entry.plan {
                    Text(plan)
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
                Spacer()
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
                Button("Open", action: open)
                    .controlSize(.small)
            }

            if let usage = entry.usage {
                UsageBar(label: "5 hours",
                         percent: usage.fiveHour,
                         resets: usage.fiveHourReset,
                         dimmed: !entry.isLive && usage.isStale)
                UsageBar(label: "Week",
                         percent: usage.week,
                         resets: usage.weekReset,
                         dimmed: !entry.isLive && usage.isStale)
                if !entry.isLive, usage.isStale {
                    Text("Last seen \(Self.relative.localizedString(for: usage.sampled, relativeTo: Date()))")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
            } else {
                Text("No usage reported yet")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }

    private static let relative: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .short
        return formatter
    }()
}

struct UsageBar: View {
    let label: String
    let percent: Int
    var resets: Date?
    var dimmed = false

    private var fraction: Double { min(max(Double(percent) / 100, 0), 1) }

    private var tint: Color {
        switch percent {
        case ..<70: return .green
        case ..<90: return .orange
        default: return .red
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 8) {
                Text(label)
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
            }
            if let resets, let remaining = Graft.countdown(to: resets) {
                Text("Resets in \(remaining)")
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(.tertiary)
                    .padding(.leading, 60)
            }
        }
        .opacity(dimmed ? 0.45 : 1)
    }
}
