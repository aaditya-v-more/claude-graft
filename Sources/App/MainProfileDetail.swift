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
                        .identified("claude-missing")
                }
            }

            Section {
                LabeledContent("Name", value: "Claude")
                    .spoken(L10n.text("Name"), value: "Claude")
                    .identified("name")
                LabeledContent {
                    HStack(spacing: 6) {
                        Text("Claude")
                            .font(.callout.monospaced())
                            .foregroundStyle(.secondary)
                            .spoken(L10n.text("Profile folder"), value: Graft.mainProfile.lastPathComponent)
                            .identified("folder")
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([Graft.mainProfile])
                        } label: {
                            Image(systemName: "folder")
                        }
                        .buttonStyle(.borderless)
                        .help("Show in Finder")
                        // SwiftUI reads the folder symbol out as "Move".
                        .accessibilityLabel(L10n.text("Show in Finder"))
                        .identified("show-in-finder")
                    }
                } label: {
                    // Said with the folder's name instead, which is not a row
                    // of its own and so cannot carry it the way Name does.
                    Text("Profile folder").accessibilityHidden(true)
                }
            } header: {
                SectionHeader(title: "Claude", info: L10n.text("""
                    The Claude you installed, launched the ordinary way. Graft does \
                    not create or modify it — shortcuts borrow from it, and it can \
                    never be renamed, re-pointed or deleted from here.
                    """), id: "profile-info")
            }

            Section("Status") {
                LabeledContent("Claude") {
                    Text(L10n.text(isRunning ? "Running on this profile" : "Not running"))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                .spoken("Claude", value: L10n.text(isRunning ? "Running on this profile" : "Not running"))
                .identified("running")
            }

            Section {
                UsageSummary(entry: plan) {
                    usage.refresh(store, interactive: true, focus: Graft.mainProfile)
                }
            } header: {
                SectionHeader(title: "Plan usage", info: UsageSummary.explanation(for: plan),
                              id: "usage-info")
            }

            SharedUsageSection(claude: UsagePoolController.mainKey)
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            HStack(spacing: 8) {
                Spacer()
                if plan?.mayStartSession ?? true {
                    Button(action: startSession) {
                        if startingSession {
                            ProgressView().controlSize(.small)
                        } else {
                            Text("Start Session")
                        }
                    }
                    .disabled(startingSession)
                    // Named apart from its text, which a spinner replaces while
                    // the session is being started.
                    .accessibilityLabel(L10n.text("Start Session"))
                    .identified("start-session")
                    InfoButton(ShortcutDetail.sessionNote, about: "Start Session", id: "session-info")
                }

                Button("Open", action: open)
                .keyboardShortcut(.defaultAction)
                .disabled(claudeMissing)
                .identified("open")
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
        .controls(profile: Graft.mainProfile)
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
