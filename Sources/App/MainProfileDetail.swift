import AppKit
import Combine
import SwiftUI

/// Claude's own profile, shown alongside the shortcuts so its usage and state
/// are in the same place. Nothing here is editable: this is the installation
/// Graft borrows from, not something it created.
struct MainProfileDetail: View {
    @EnvironmentObject private var store: ShortcutStore
    /// The figures come from here rather than from Claude's own history file,
    /// so the window and the menu bar cannot disagree about one account.
    @EnvironmentObject private var usage: UsageMonitor
    @State private var isRunning = false
    @State private var startingSession = false
    @State private var error: String?
    @State private var sharersOpen: [String] = []
    @State private var askAboutSharers = false

    private let clock = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    private var claudeMissing: Bool {
        !FileManager.default.fileExists(atPath: Graft.claudeApp.path)
    }

    var body: some View {
        Form {
            if claudeMissing {
                Section {
                    Label("Claude.app was not found.", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                LabeledContent("Name", value: "Claude")
                LabeledContent("Profile folder") {
                    HStack(spacing: 6) {
                        Text("Claude")
                            .font(.callout.monospaced())
                            .foregroundStyle(.secondary)
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([Graft.mainProfile])
                        } label: {
                            Image(systemName: "folder")
                        }
                        .buttonStyle(.borderless)
                        .help("Show in Finder")
                    }
                }
            } header: {
                SectionHeader(title: "Claude", info: """
                    The Claude you installed, launched the ordinary way. Graft does \
                    not create or modify it — shortcuts borrow from it, and it can \
                    never be renamed, re-pointed or deleted from here.
                    """)
            }

            Section("Status") {
                LabeledContent("Claude") {
                    Text(L10n.text(isRunning ? "Running on this profile" : "Not running"))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                UsageSummary(entry: plan)
            } header: {
                SectionHeader(title: "Plan usage", info: UsageSummary.explanation(for: plan))
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            HStack(spacing: 8) {
                Spacer()
                Button(action: startSession) {
                    if startingSession {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Start Session")
                    }
                }
                .disabled(startingSession)
                InfoButton(ShortcutDetail.sessionNote)

                Button("Open", action: open)
                .keyboardShortcut(.defaultAction)
                .disabled(claudeMissing)
            }
            .padding(14)
            .background(.bar)
        }
        .onAppear {
            refresh()
            // Selecting a profile is someone looking at its figures, which is the
            // rung below a press: current within the minute, and allowed to ask
            // for keychain access once if that is what stands in the way.
            usage.refresh(store, prompting: .onceIfShut, freshness: .recent)
        }
        .onReceive(clock) { _ in refresh() }
        .confirmationDialog(ChatConflict.title,
                            isPresented: $askAboutSharers,
                            titleVisibility: .visible) {
            Button("Open Anyway") { launch() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(ChatConflict.message(sharers: sharersOpen))
        }
        .alert("Could not start a session", isPresented: .constant(error != nil)) {
            Button("OK") { error = nil }
        } message: {
            Text(error ?? "")
        }
    }

    /// Every shortcut grafted from main reads the files this is about to open,
    /// so this is the warning that matters most — and the one profile that
    /// never had it, because the check went through a shortcut and main has
    /// none.
    private func open() {
        let neighbours = store.chatStoreNeighbours(of: nil)
        DispatchQueue.global(qos: .userInitiated).async {
            let openNow = ChatConflict.openSharers(of: Graft.mainProfile, among: neighbours)
            DispatchQueue.main.async {
                if openNow.isEmpty {
                    launch()
                } else {
                    sharersOpen = openNow
                    askAboutSharers = true
                }
            }
        }
    }

    /// Not `openApplication`, which is handed a bundle and picks an instance
    /// of it for itself — and with a shortcut running, every instance of that
    /// bundle is somebody else's profile.
    private func launch() {
        DispatchQueue.global(qos: .userInitiated).async {
            Graft.open(profile: Graft.mainProfile)
        }
    }

    private var plan: UsageMonitor.Entry? { usage.entry(for: Graft.mainProfile) }

    private func refresh() {
        DispatchQueue.global(qos: .utility).async {
            let running = Graft.isRunning(profile: Graft.mainProfile)
            DispatchQueue.main.async { isRunning = running }
        }
    }

    private func startSession() {
        guard !startingSession else { return }
        startingSession = true
        DispatchQueue.global(qos: .userInitiated).async {
            let profile = Graft.mainProfile
            let failure = SessionStarter.start(profile: profile, interactive: true)
            // The window this just opened is exactly what the stored reading
            // predates, so it is dropped rather than waited out.
            usage.invalidate(profile)
            DispatchQueue.main.async {
                startingSession = false
                error = failure?.errorDescription
                refresh()
                usage.refresh(store, interactive: true)
            }
        }
    }
}
