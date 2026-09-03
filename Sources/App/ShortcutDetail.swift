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

    /// Chats for this profile's account that another profile is still holding.
    @State private var elsewhere: Graft.ChatsElsewhere?
    @State private var askAboutElsewhere = false
    @State private var copying = false
    /// What the last copy did, kept on screen because Claude may not be open
    /// to show the answer for itself.
    @State private var copiedNote: String?

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

            if let found = elsewhere {
                Section {
                    Text(foundNote(found))
                        .font(.callout)
                        .fixedSize(horizontal: false, vertical: true)
                    ForEach(found.chats, id: \.self) { chat in
                        LabeledContent {
                            Text(Self.chatDate.string(from: chat.lastActive))
                                .font(.callout)
                                .foregroundStyle(.secondary)
                        } label: {
                            Text(chat.title)
                                .font(.callout)
                                .lineLimit(1)
                                .truncationMode(.tail)
                        }
                    }
                    HStack(spacing: 8) {
                        Button(found.merging ? "Merge Them Here" : "Copy Them Here") {
                            adopt(found)
                        }
                        .disabled(copying)
                        if copying { ProgressView().controlSize(.small) }
                    }
                } header: {
                    SectionHeader(title: "Chats found elsewhere", info: Self.elsewhereNote)
                }
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
                if let copiedNote {
                    LabeledContent("Chats") {
                        Text(copiedNote)
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .multilineTextAlignment(.trailing)
                    }
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
        .confirmationDialog(elsewhere?.merging == true
                                ? "Merge this account's other chats in first?"
                                : "Bring this account's chats across first?",
                            isPresented: $askAboutElsewhere,
                            titleVisibility: .visible) {
            Button(elsewhere?.merging == true ? "Merge Them Here" : "Copy Them Here") {
                if let found = elsewhere { adopt(found) { checkSharers() } }
            }
            Button("Open Without Them") {
                store.askedAboutChats.insert(shortcut.id)
                checkSharers()
            }
            Button("Do Not Show Again") {
                if let found = elsewhere { shortcut.stopAskingChatsFor = found.account }
                checkSharers()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(elsewhere.map(openNote) ?? "")
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

    private static let elsewhereNote = """
        Every Claude has to be closed first, this profile's and every other \
        one. An instance builds its sidebar as it starts and rewrites records \
        in its own shape as it runs, so chats copied underneath a running one \
        are invisible at best and written over at worst.

        Copying leaves the originals where they are, so the other profile is \
        unchanged and still has them if that account is ever signed back into \
        over there. Nothing already here is written over either, so a chat \
        this profile has archived or renamed keeps the shape it was given.

        Only the small record files are copied; the messages themselves \
        already live in ~/.claude and are shared either way.
        """

    /// Named, counted and quoted, because none of the three answers the
    /// question on its own. Which profile says where they went, the count says
    /// whether it is the history being missed, and the titles and dates say it
    /// in the only terms anybody recognises their own chats by.
    private func foundNote(_ found: Graft.ChatsElsewhere) -> String {
        """
        \(store.name(ofProfile: found.profile)) is holding \(chats(found.count)) for \
        the account this profile is signed into that this one has not got. \
        \(found.merging
            ? "This profile has chats of its own too, so they are merged rather than either set being replaced."
            : "This profile has none of its own yet.")
        """
    }

    private func openNote(_ found: Graft.ChatsElsewhere) -> String {
        """
        \(foundNote(found))

        Claude builds its sidebar as it starts, so bringing them over now is \
        what puts them in the window about to open. Every other Claude has to \
        be closed for that.
        """
    }

    private func chats(_ count: Int) -> String {
        count == 1 ? "1 chat" : "\(count) chats"
    }

    /// Reads as a sentence for any number of them.
    private func names(_ profiles: [URL]) -> String {
        let all = profiles.map(store.name(ofProfile:))
        switch all.count {
        case 0: return ""
        case 1: return all[0]
        case 2: return all.joined(separator: " and ")
        default: return all.dropLast().joined(separator: ", ") + " and " + (all.last ?? "")
        }
    }

    private static let chatDate: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .none
        return formatter
    }()

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
            // Only for a profile keeping its own chats. One reading from a
            // source is having its sidebar filled by the graft already, and
            // the offer would be describing chats it is about to be handed.
            let found = target.source == .own
                ? Graft.chatsElsewhere(for: target.profileDir,
                                       among: Graft.sessionStoreProfiles())
                : nil
            DispatchQueue.main.async {
                installedAt = bundle
                isRunning = running
                profileExists = hasProfile
                // Kept whatever the answer was last time. Saying no to being
                // asked silences the question at the door, not the offer in
                // the window, which is the only place left to change your
                // mind from.
                elsewhere = found
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

    /// Off the main thread, because it copies files and asks what is running.
    ///
    /// The continuation runs only when something was actually brought over. A
    /// copy refused for a Claude still being open has not done what the press
    /// asked for, and carrying on to open a window would bury the one line
    /// saying which Claude to quit.
    private func adopt(_ found: Graft.ChatsElsewhere, then: (() -> Void)? = nil) {
        guard !copying else { return }
        copying = true
        let profile = shortcut.profileDir
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Graft.adoptChats(from: found.profile, into: profile,
                                          account: found.account)
            DispatchQueue.main.async {
                copying = false
                if !result.running.isEmpty {
                    copiedNote = "Nothing copied — quit \(names(result.running)) first"
                } else if result.copied == 0 {
                    copiedNote = "Nothing was copied"
                } else {
                    copiedNote = "\(chats(result.copied)) copied from "
                        + store.name(ofProfile: found.profile)
                }
                refresh()
                if result.running.isEmpty, result.copied > 0 { then?() }
            }
        }
    }

    /// Asked before the conflict question, not after it: copying is what
    /// changes the sidebar of the window about to open, and Claude builds that
    /// sidebar as it starts.
    ///
    /// Silenced two ways, both meaning "carry on". One is for good and is kept
    /// with the shortcut; the other lasts as long as the app is up, so a press
    /// that changed nothing — a copy refused because something was still open
    /// — is asked about again the next time.
    private func open() {
        guard installedAt != nil else { return }
        if let found = elsewhere,
           shortcut.stopAskingChatsFor != found.account,
           !store.askedAboutChats.contains(shortcut.id) {
            askAboutElsewhere = true
            return
        }
        checkSharers()
    }

    /// Checking who else is open means one pgrep per neighbour, so it happens
    /// off the main thread and the window opens once the answer is back.
    private func checkSharers() {
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

    private var dimmed: Bool { entry?.isDimmed ?? false }

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
