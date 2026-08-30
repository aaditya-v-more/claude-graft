import AppKit
import Combine
import SwiftUI

struct ShortcutDetail: View {
    @EnvironmentObject private var store: ShortcutStore
    /// The figures come from here rather than from this profile's own history
    /// file, so the window and the menu bar cannot disagree about one account.
    @EnvironmentObject private var usage: UsageMonitor
    @Binding var shortcut: Shortcut
    let requestDelete: () -> Void

    @State private var error: String?
    @State private var errorTitle = "Something went wrong"

    /// Looked up off the main thread and refreshed on a timer. Reading them
    /// during a view update would mean touching the filesystem and running
    /// pgrep inside the body, and blocking there re-enters AppKit layout.
    @State private var installedAt: URL?
    @State private var isRunning = false
    @State private var profileExists = false
    @State private var startingSession = false

    /// Other instances found open on the same chat store when Open was pressed.
    @State private var sharersOpen: [String] = []
    @State private var askAboutSharers = false

    /// Set once the folder is typed by hand, so renaming stops rewriting it.
    @State private var folderIsCustom = false

    private let clock = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    private var claudeMissing: Bool {
        !FileManager.default.fileExists(atPath: Graft.claudeApp.path)
    }

    static let sessionNote = """
        Sends one short message to this account so its five-hour window opens. \
        Nothing appears on screen.

        It uses that profile's own login, borrowed the same way the usage \
        figures are, so each account can be started separately.
        """

    var body: some View {
        Form {
            if claudeMissing {
                Section {
                    Label("Claude.app was not found.", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                TextField("Name", text: $shortcut.name)
                    .onChange(of: shortcut.name) { _ in
                        guard installedAt == nil, !folderIsCustom else { return }
                        shortcut.folder = Shortcut.folderName(for: shortcut.name)
                    }

                LabeledContent("Profile folder") {
                    HStack(spacing: 6) {
                        TextField("", text: $shortcut.folder)
                            .textFieldStyle(.plain)
                            .multilineTextAlignment(.trailing)
                            .font(.callout.monospaced())
                            .onChange(of: shortcut.folder) { _ in folderIsCustom = true }
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([shortcut.profileDir])
                        } label: {
                            Image(systemName: "folder")
                        }
                        .buttonStyle(.borderless)
                        .help("Show in Finder")
                    }
                }
            } header: {
                SectionHeader(title: "Shortcut", info: Self.shortcutNote)
            }

            Section {
                Picker("Reads chats from", selection: $shortcut.source) {
                    ForEach(store.availableSources(for: shortcut), id: \.self) { source in
                        Text(store.label(for: source)).tag(source)
                    }
                }
                if shortcut.source != .own {
                    Text(mergeNote)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } header: {
                SectionHeader(title: "Chats", info: sourceExplanation)
            }

            Section("Status") {
                LabeledContent("Shortcut") {
                    Text(installedAt.map { $0.path } ?? "Not created yet")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                LabeledContent("Claude") {
                    Text(isRunning ? "Running on this profile" : "Not running")
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
                Button(isDraft ? "Discard" : "Delete Shortcut…",
                       role: .destructive, action: requestDelete)
                Spacer()
                Button(action: startSession) {
                    if startingSession {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Start Session")
                    }
                }
                .disabled(startingSession)
                InfoButton(Self.sessionNote)

                Button("Open", action: open)
                    .disabled(installedAt == nil)
                Button(installedAt == nil ? "Create Shortcut" : "Update Shortcut", action: install)
                    .keyboardShortcut(.defaultAction)
                    .disabled(shortcut.name.trimmingCharacters(in: .whitespaces).isEmpty || claudeMissing)
            }
            .padding(14)
            .background(.bar)
        }
        .confirmationDialog(ChatConflict.title,
                            isPresented: $askAboutSharers,
                            titleVisibility: .visible) {
            Button("Open Anyway") { launch() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(ChatConflict.message(sharers: sharersOpen))
        }
        .onAppear {
            refresh()
            // Selecting a profile is someone looking at its figures, which is the
            // rung below a press: current within the minute, and allowed to ask
            // for keychain access once if that is what stands in the way.
            usage.refresh(store, prompting: .onceIfShut, freshness: .recent)
        }
        .onReceive(clock) { _ in refresh() }
        .alert(errorTitle, isPresented: .constant(error != nil)) {
            Button("OK") { error = nil }
        } message: {
            Text(error ?? "")
        }
    }

    private static let shortcutNote = """
        The name is what the app in \(Installer.installDirectory.path) is called. \
        The profile folder is where this account's login, chats and settings are \
        kept, inside ~/Library/Application Support.

        Naming an existing folder adopts that profile instead of starting an \
        empty one.
        """

    private var sourceExplanation: String {
        switch shortcut.source {
        case .own:
            return "This profile keeps its own Claude Code history, connectors and preferences. Nothing is shared."
        default:
            let name = store.label(for: shortcut.source)
            return """
                Merges this profile's Claude Code chats with \(name)'s, and shares \
                its connectors, extensions and window state. Logins stay separate, \
                so this shortcut can sign into a different account.
                """
        }
    }

    /// Named rather than described, because the consequence worth reading twice
    /// is the one about the other profile, and "the other profile" is not what
    /// anybody calls it.
    private var mergeNote: String {
        let name = store.label(for: shortcut.source)
        return """
            The two histories are merged rather than swapped. This profile keeps \
            the chats it already had and gains \(name)'s, and both sidebars end up \
            showing the combined set. Graft carries changes both ways each time \
            either Claude is opened, which is what lets this profile archive, \
            rename and delete them at all.

            Sharing goes both ways, so this profile's existing chats are copied \
            into \(name) as well. Switching back to its own chats returns this \
            profile to exactly what it had, but the copies already in \(name) stay \
            there — merging a history is not something Graft can take back.

            Only the small record files are copied; the messages themselves \
            already live in ~/.claude and are shared either way. Archive the same \
            conversation differently in both between two openings and the one \
            touched last wins.
            """
    }

    private var plan: UsageMonitor.Entry? { usage.entry(for: shortcut.profileDir) }

    private var isDraft: Bool {
        installedAt == nil && !profileExists && shortcut.installedName == nil
    }

    private func install() {
        shortcut.name = shortcut.name.trimmingCharacters(in: .whitespaces)
        shortcut.folder = shortcut.folder.trimmingCharacters(in: .whitespaces)
        do {
            _ = try Installer.install(shortcut,
                                      sourceDir: store.sourceDir(for: shortcut),
                                      previousName: shortcut.installedName)
            shortcut.installedName = shortcut.name
            refresh()
        } catch {
            errorTitle = "Could not create the shortcut"
            self.error = error.localizedDescription
        }
    }

    /// Off the main thread: these hit the filesystem and run pgrep, neither of
    /// which belongs in a view update.
    private func refresh() {
        let target = shortcut
        DispatchQueue.global(qos: .utility).async {
            let bundle = Installer.installedBundle(for: target)
            let running = Graft.isRunning(profile: target.profileDir)
            let hasProfile = FileManager.default.fileExists(atPath: target.profileDir.path)
            DispatchQueue.main.async {
                installedAt = bundle
                isRunning = running
                profileExists = hasProfile
            }
        }
    }

    private func startSession() {
        guard !startingSession else { return }
        startingSession = true
        let profile = shortcut.profileDir
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = SessionStarter.start(profile: profile, interactive: true)
            // The window this just opened is exactly what the stored reading
            // predates, so it is dropped rather than waited out.
            usage.invalidate(profile)
            DispatchQueue.main.async {
                startingSession = false
                if let failure {
                    errorTitle = "Could not start a session"
                    error = failure.errorDescription
                }
                refresh()
                usage.refresh(store, interactive: true)
            }
        }
    }

    /// Checking who else is open means one pgrep per neighbour, so it happens
    /// off the main thread and the window opens once the answer is back.
    private func open() {
        guard installedAt != nil else { return }
        let neighbours = store.chatStoreNeighbours(of: shortcut)
        DispatchQueue.global(qos: .userInitiated).async {
            let openNow = ChatConflict.openSharers(of: shortcut.profileDir, among: neighbours)
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

    private func launch() {
        guard let installedAt else { return }
        NSWorkspace.shared.openApplication(at: installedAt,
                                           configuration: NSWorkspace.OpenConfiguration())
    }
}

/// The two bars plus a note when there is nothing to show yet.
///
/// Fed from `UsageMonitor`, never from disk, so the figure a window shows is
/// the one the menu bar is showing for the same account.
struct UsageSummary: View {
    let entry: UsageMonitor.Entry?

    /// A live figure was taken just now, so only one read off disk can be old
    /// enough to be worth warning about.
    private var dimmed: Bool {
        guard let entry, let usage = entry.usage else { return false }
        return !entry.isLive && usage.isStale
    }

    var body: some View {
        if let usage = entry?.usage {
            UsageBar(label: "5 hours", percent: usage.fiveHour,
                     resets: usage.fiveHourReset, dimmed: dimmed)
            UsageBar(label: "Week", percent: usage.week,
                     resets: usage.weekReset, dimmed: dimmed)
        } else {
            Text("No usage reported yet")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    /// Which of the two sources the figure came from. They answer differently
    /// enough to be worth naming: the endpoint is current whether or not that
    /// Claude is running, the file is as old as the last time it ran.
    static func explanation(for entry: UsageMonitor.Entry?) -> String {
        guard let entry, let usage = entry.usage else {
            return """
                Claude records how much of each window it has spent while it runs, \
                and the account itself is asked directly once its login is readable. \
                Neither has answered yet.
                """
        }
        if entry.isLive {
            return """
                Asked of this account directly, using the login it already holds — \
                the same figure the menu bar is showing. It stays current whether or \
                not that Claude is running.
                """
        }
        if usage.isStale {
            return """
                Recorded while this profile was last open, which was long enough ago \
                that the five-hour window has since reset. Turn on live usage from the \
                menu bar to have the account asked instead.
                """
        }
        return """
            Recorded by this profile at \(time.string(from: usage.sampled)). It only \
            updates while that Claude is running, and the reset times are worked out \
            from its own history.
            """
    }

    private static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter
    }()
}
