import AppKit
import SwiftUI

/// What drops down from the menu bar: every account's two usage windows, when
/// each one resets, and the few controls worth having without opening the app.
struct MenuBarContent: View {
    @EnvironmentObject private var store: ShortcutStore
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var usage: UsageMonitor

    /// Passed in: this view is hosted by an NSPopover, outside any scene, so
    /// the openWindow environment action is not available here.
    let openMainWindow: () -> Void

    @State private var starting: String?
    @State private var problem: String?

    private static let sessionNote = """
        Sends one short message to this account so its five-hour window opens. \
        Nothing appears on screen.

        It uses that profile's own login, borrowed the same way the usage \
        figures are, so each account can be started separately.
        """

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
                MenuButton("Refresh Usage") { usage.refresh(store, interactive: true) }
                MenuButton("Open Claude Graft", action: openMainWindow)
                MenuButton("Quit Claude Graft") { NSApp.terminate(nil) }
            }
            .padding(.horizontal, 8)
            .padding(.bottom, 8)
        }
        .frame(width: 320)
        .onAppear {
            settings.refreshLoginItem()
            usage.refresh(store)
        }
    }

    private func open(_ entry: UsageMonitor.Entry) {
        if let id = entry.shortcut,
           let shortcut = store.shortcut(id),
           let bundle = Installer.installedBundle(for: shortcut) {
            NSWorkspace.shared.openApplication(at: bundle, configuration: NSWorkspace.OpenConfiguration())
        } else {
            // The main profile has no shortcut of its own to go through.
            NSWorkspace.shared.openApplication(at: Graft.claudeApp,
                                               configuration: NSWorkspace.OpenConfiguration())
        }
    }

    private func startSession(_ entry: UsageMonitor.Entry) {
        guard starting == nil else { return }
        starting = entry.id
        problem = nil
        let profile = entry.profile
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = SessionStarter.start(profile: profile)
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
