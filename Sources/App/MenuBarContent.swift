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

    @State private var starting = false
    @State private var problem: String?

    private static let sessionNote = """
        Sends a one-word message through Claude Code's command line, which \
        opens a five-hour window without putting a window on screen.

        It uses the account Claude Code is signed into. That is the only login \
        reachable from outside Claude — the desktop keeps each profile's token \
        encrypted in that profile — so it cannot be aimed at one shortcut's \
        account in particular.
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
                    UsageRow(entry: entry, open: { open(entry) })
                    if entry.id != usage.entries.last?.id {
                        Divider().padding(.leading, 14)
                    }
                }
            }

            Divider().padding(.vertical, 6)

            if let problem {
                Text(problem)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 14)
                    .padding(.bottom, 8)
            }

            HStack(spacing: 6) {
                Button(action: startSession) {
                    if starting {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Start Session")
                    }
                }
                .controlSize(.small)
                .disabled(starting || !SessionStarter.isAvailable)
                InfoButton(Self.sessionNote)
                Spacer()
            }
            .padding(.horizontal, 14)
            .padding(.bottom, 8)

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

    private func startSession() {
        guard !starting else { return }
        starting = true
        problem = nil
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = SessionStarter.start()
            DispatchQueue.main.async {
                starting = false
                problem = failure?.errorDescription
                usage.refresh(store)
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
    let open: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Circle()
                    .fill(entry.isRunning ? Color.green : Color.secondary.opacity(0.35))
                    .frame(width: 6, height: 6)
                Text(entry.name).fontWeight(.medium)
                Spacer()
                Button("Open", action: open)
                    .controlSize(.small)
            }

            if let usage = entry.usage {
                UsageBar(label: "5 hours",
                         percent: usage.fiveHour,
                         resets: usage.fiveHourReset,
                         dimmed: usage.isStale)
                UsageBar(label: "Week",
                         percent: usage.week,
                         resets: usage.weekReset,
                         dimmed: usage.isStale)
                if usage.isStale {
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
