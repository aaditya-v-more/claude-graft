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
    @State private var errorTitle = L10n.text("Something went wrong")

    /// Looked up off the main thread and refreshed on a timer. Reading them
    /// during a view update would mean touching the filesystem and running
    /// pgrep inside the body, and blocking there re-enters AppKit layout.
    @State private var installedAt: URL?
    @State private var isRunning = false
    @State private var profileExists = false
    @State private var startingSession = false
    @State private var iconPreviews: [Shortcut.IconPreset: NSImage] = [:]

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
    @State private var sidebarNote: String?

    /// Set once the folder is typed by hand, so renaming stops rewriting it.
    @State private var folderIsCustom = false
    @State private var choosingIcon = false

    private let clock = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    private var claudeMissing: Bool {
        !FileManager.default.fileExists(atPath: Graft.claudeApp.path)
    }

    static let sessionNote = L10n.text("""
        Sends one short message to this account so its five-hour window opens. \
        Nothing appears on screen.

        It uses that profile's own login, borrowed the same way the usage \
        figures are, so each account can be started separately.
        """)

    var body: some View {
        Form {
            identitySection

            if let found = elsewhere {
                Section {
                    // The chats themselves are named behind the ⓘ: three rows
                    // of titles put the button below the bottom of the window.
                    HStack(spacing: 8) {
                        OneLineNote(text: L10n.format("%@ holds %ld chats for this account", store.name(ofProfile: found.profile), found.count),
                                    more: foundMore(found),
                                    topic: "Chats found elsewhere", id: "elsewhere-note")
                        Spacer(minLength: 8)
                        if copying { ProgressView().controlSize(.small) }
                        Button(L10n.text(found.merging ? "Merge Them Here" : "Copy Them Here")) {
                            adopt(found)
                        }
                        .disabled(copying)
                        .identified("copy-chats")
                    }
                } header: {
                    SectionHeader(title: "Chats found elsewhere", info: Self.elsewhereNote,
                                  id: "elsewhere-info")
                }
            }

            Section {
                UsageSummary(entry: plan)
            } header: {
                UsageSummary.header(for: plan) {
                    usage.refresh(store, interactive: true, focus: shortcut.profileDir)
                }
            }

            // A shortcut not created yet has no profile for anything to reach.
            if installedAt != nil {
                SharedUsageSection(claude: UsagePoolController.key(for: shortcut))
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) { bottomBar }
        .navigationSubtitle(status)
        .confirmationDialog(ChatConflict.title,
                            isPresented: $askAboutSharers,
                            titleVisibility: .visible) {
            Button("Open Anyway") { launch() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(ChatConflict.message(sharers: sharersOpen))
        }
        .confirmationDialog(L10n.text(elsewhere?.merging == true
                                ? "Merge this account's other chats in first?"
                                : "Bring this account's chats across first?"),
                            isPresented: $askAboutElsewhere,
                            titleVisibility: .visible) {
            Button(L10n.text(elsewhere?.merging == true ? "Merge Them Here" : "Copy Them Here")) {
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
            loadIconPreviews()
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
        .controls(profile: shortcut.profileDir)
    }

    /// What the shortcut is and where its chats come from.
    private var identitySection: some View {
        Section {
            // Icon, name and folder share one row: the three say what the
            // shortcut is, and stacked they pushed everything else below
            // the bottom of the window. Whether it is created and running
            // is the window's subtitle.
            HStack(spacing: 10) {
                iconButton
                TextField("Name", text: $shortcut.name)
                    .textFieldStyle(.roundedBorder)
                    .labelsHidden()
                    .onChange(of: shortcut.name) { _ in
                        guard installedAt == nil, !folderIsCustom else { return }
                        shortcut.folder = Shortcut.folderName(for: shortcut.name)
                    }
                    .identified("name")
                // The field carries no title of its own, so without this
                // VoiceOver reads only whatever has been typed.
                TextField("", text: $shortcut.folder)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .frame(width: 130)
                    .onChange(of: shortcut.folder) { _ in folderIsCustom = true }
                    .help("Profile folder")
                    .accessibilityLabel(L10n.text("Profile folder"))
                    .identified("folder")
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([shortcut.profileDir])
                } label: {
                    Image(systemName: "folder")
                }
                .buttonStyle(.borderless)
                .help("Show in Finder")
                // SwiftUI reads the folder symbol out as "Move".
                .accessibilityLabel(L10n.text("Show in Finder"))
                .identified("show-in-finder")
                InfoButton(Self.shortcutNote, about: "Shortcut", id: "shortcut-info")
            }
            if let copiedNote {
                OneLineNote(text: copiedNote, id: "copied-note")
            }
            // Where chats come from, and under it whether they are shared,
            // in one row: a section of its own for one picker cost the
            // height of a heading and a gap.
            LabeledContent {
                Picker("Reads chats from", selection: $shortcut.source) {
                    ForEach(store.availableSources(for: shortcut), id: \.self) { source in
                        Text(store.label(for: source)).tag(source)
                    }
                }
                .labelsHidden()
                .fixedSize()
                .accessibilityLabel(L10n.text("Reads chats from"))
                .identified("chat-source")
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 5) {
                        Text("Reads chats from")
                        InfoButton(sourceExplanation, about: "Chats", id: "chats-info")
                    }
                    if shortcut.source != .own {
                        OneLineNote(text: L10n.format("Shared both ways with %@", store.label(for: shortcut.source)),
                                    more: mergeMore,
                                    topic: "Chats", id: "merge-note")
                    }
                }
            }
        }
    }

    /// Delete on the left, what the shortcut is for on the right.
    private var bottomBar: some View {
        HStack(spacing: 8) {
            Button(L10n.text(isDraft ? "Discard" : "Delete Shortcut…"),
                   role: .destructive, action: requestDelete)
                .identified("delete")
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
                InfoButton(Self.sessionNote, about: "Start Session", id: "session-info")
            }

            Button("Open", action: open)
                .disabled(installedAt == nil)
                .identified("open")
            Button(L10n.text(installedAt == nil ? "Create Shortcut" : "Update Shortcut"),
                   action: install)
                .keyboardShortcut(.defaultAction)
                .disabled(shortcut.name.trimmingCharacters(in: .whitespaces).isEmpty || claudeMissing)
                // Where it was put, which the status line used to carry.
                .help(installedAt?.path ?? "")
                .identified("install")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.bar)
    }

    /// Created or not, and running or not, in one line; where the shortcut
    /// was put is on its tooltip. A missing Claude is said here rather than
    /// in a section of its own: that section was the 46 points that made the
    /// fullest page scroll on a Mac without Claude, and nothing can be running
    /// without it anyway.
    private var status: String {
        [L10n.text(installedAt == nil ? "Not created yet" : "Created"),
         L10n.text(claudeMissing ? "Claude.app was not found." : isRunning ? "Running" : "Not running")]
            .joined(separator: " · ")
    }

    /// The icon in use, opening the rest only when asked: twelve of them laid
    /// out in the window took more room than every other setting together.
    private var iconButton: some View {
        Button { choosingIcon.toggle() } label: {
            iconImage(shortcut.iconPreset).frame(width: 28, height: 28)
        }
        .buttonStyle(.plain)
        .help("Shortcut icon")
        .accessibilityLabel(L10n.text("Shortcut icon"))
        .accessibilityValue(Text(shortcut.iconPreset.title))
        .identified("icon")
        .popover(isPresented: $choosingIcon, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 8) {
                LazyVGrid(columns: Array(repeating: GridItem(.fixed(46), spacing: 6), count: 6),
                          spacing: 6) {
                    ForEach(Shortcut.IconPreset.allCases) { preset in
                        Button {
                            shortcut.iconPreset = preset
                            choosingIcon = false
                        } label: {
                            iconImage(preset)
                                .frame(width: 38, height: 38)
                                .padding(4)
                                .background(RoundedRectangle(cornerRadius: 8)
                                    .fill(preset == shortcut.iconPreset
                                          ? Color.accentColor.opacity(0.12) : .clear))
                                .overlay(RoundedRectangle(cornerRadius: 8)
                                    .stroke(preset == shortcut.iconPreset
                                            ? Color.accentColor : Color.secondary.opacity(0.2),
                                            lineWidth: preset == shortcut.iconPreset ? 2 : 1))
                        }
                        .buttonStyle(.plain)
                        .help(preset.title)
                        .accessibilityLabel(Text(preset.title))
                        .accessibilityValue(Text(preset == shortcut.iconPreset
                                                 ? L10n.text("Selected") : ""))
                        .identified("icon.\(preset.rawValue)")
                    }
                }
                Text("Used in Finder and the Dock; a running Claude shows its own.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(12)
        }
    }

    @ViewBuilder
    private func iconImage(_ preset: Shortcut.IconPreset) -> some View {
        if let image = iconPreviews[preset] {
            Image(nsImage: image).resizable().scaledToFit()
        } else {
            Image(systemName: "app.dashed").resizable().scaledToFit().padding(4)
        }
    }

    private static let shortcutNote = L10n.text("""
            The name is what the installed shortcut app is called. The profile folder is where \
            this account's login, chats and settings are kept, inside \
            ~/Library/Application Support.

            Naming an existing folder adopts that profile instead of starting an \
            empty one.
            """)

    private func loadIconPreviews() {
        guard iconPreviews.isEmpty else { return }
        DispatchQueue.global(qos: .userInitiated).async {
            let previews = Dictionary(uniqueKeysWithValues:
                Shortcut.IconPreset.allCases.compactMap { preset in
                    Installer.previewIcon(for: preset).map { (preset, $0) }
                })
            DispatchQueue.main.async { iconPreviews = previews }
        }
    }

    private var sourceExplanation: String {
        switch shortcut.source {
        case .own:
            return L10n.text("This profile keeps its own Claude Code history, connectors and preferences. Nothing is shared.")
        default:
            let name = store.label(for: shortcut.source)
            return L10n.format("""
                Merges this profile's Claude Code chats with %@'s, and shares \
                extensions and window state. Missing local MCP servers are copied; \
                existing server settings are kept. Logins and permission choices \
                stay separate. Choose permission modes in Claude's settings for \
                each account; organization restrictions still apply.
                """, name)
        }
    }

    /// Named rather than described, because the consequence worth reading twice
    /// is the one about the other profile, and "the other profile" is not what
    /// anybody calls it.
    private var mergeNote: String {
        let name = store.label(for: shortcut.source)
        return L10n.format("""
            The two histories are merged rather than swapped. This profile keeps \
            the chats it already had and gains %1$@'s, and both sidebars end up \
            showing the combined set. Graft carries changes both ways each time \
            either Claude is opened, which is what lets this profile archive, \
            rename and delete them at all.

            Sharing goes both ways, so this profile's existing chats are copied \
            into %1$@ as well. Switching back to its own chats returns this profile \
            to exactly what it had, but the copies already in %1$@ stay there — \
            merging a history is not something Graft can take back.

            Only the small record files are copied; the messages themselves already \
            live in ~/.claude and are shared either way. Archive the same conversation \
            differently in both between two openings and the one touched last wins.
            """, name)
    }

    private static let elsewhereNote = L10n.text("""
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
        """)

    /// Named, counted and quoted, because none of the three answers the
    /// question on its own. Which profile says where they went, the count says
    /// whether it is the history being missed, and the titles and dates say it
    /// in the only terms anybody recognises their own chats by.
    private var mergeMore: String {
        let sidebar = sidebarNote ?? L10n.text("Pinned chats and sort order sync when the linked Claude apps are closed and you open a shortcut.")
        return mergeNote + "\n\n" + sidebar
    }

    /// The note, then the newest of the chats it found, by name and date.
    private func foundMore(_ found: Graft.ChatsElsewhere) -> String {
        let chats: [String] = found.chats.prefix(5).map { chat in
            chat.title + " · " + Self.chatDate.string(from: chat.lastActive)
        }
        return ([foundNote(found)] + chats).joined(separator: "\n\n")
    }

    private func foundNote(_ found: Graft.ChatsElsewhere) -> String {
        L10n.format("%@ is holding chats for this account that this profile does not have. Chats found: %ld. %@",
                    store.name(ofProfile: found.profile), found.count,
                    L10n.text(found.merging
                        ? "This profile has chats of its own too, so they are merged rather than either set being replaced."
                        : "This profile has none of its own yet."))
    }

    private func openNote(_ found: Graft.ChatsElsewhere) -> String {
        L10n.format("%@\n\nClaude builds its sidebar as it starts, so bringing them over now is what puts them in the window about to open. Every other Claude has to be closed for that.", foundNote(found))
    }

    /// Reads as a sentence for any number of them.
    private func names(_ profiles: [URL]) -> String {
        let all = profiles.map(store.name(ofProfile:))
        switch all.count {
        case 0: return ""
        case 1: return all[0]
        case 2: return all.joined(separator: " \(L10n.text("and")) ")
        default: return all.dropLast().joined(separator: ", ") + " \(L10n.text("and")) " + (all.last ?? "")
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
            errorTitle = L10n.text("Could not create the shortcut")
            self.error = error.localizedDescription
        }
    }

    /// Off the main thread: these hit the filesystem and run pgrep, neither of
    /// which belongs in a view update.
    private func refresh() {
        let target = shortcut
        DispatchQueue.global(qos: .utility).async {
            let bundle = ProfileOpener.bundle(for: target)
            let running = Graft.isRunning(profile: target.profileDir)
            let sidebar = SidebarSync.status(for: target.profileDir)
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
                sidebarNote = sidebar
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
                    errorTitle = L10n.text("Could not start a session")
                    error = failure.errorDescription
                }
                refresh()
                // Starting the session already asked the keychain for this
                // press, so this asks nobody, and only about this account.
                usage.refresh(store, interactive: true, prompting: .no, focus: profile)
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
                    copiedNote = L10n.format("Nothing copied — quit %@ first", names(result.running))
                } else if result.copied == 0 {
                    copiedNote = L10n.text("Nothing was copied")
                } else {
                    copiedNote = L10n.format("Chats copied from %@: %ld",
                                            store.name(ofProfile: found.profile), result.copied)
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
        guard installedAt != nil else { return }
        ProfileOpener.open(profile: shortcut.profileDir, shortcut: shortcut) { failure in
            error = failure?.localizedDescription
        }
    }
}

/// The bars, one line each and all in one row, or a line saying there is
/// nothing to show yet. When the figure is from sits in the section's header
/// (`UsageSummary.header`), not in a row under the bars.
///
/// Fed from `UsageMonitor`, never from disk, so the figure a window shows is
/// the one the menu bar is showing for the same account.
struct UsageSummary: View {
    let entry: UsageMonitor.Entry?

    private var dimmed: Bool { entry?.isDimmed ?? false }

    var body: some View {
        if let usage = entry?.usage {
            VStack(alignment: .leading, spacing: 7) {
                if let fiveHour = usage.fiveHour {
                    UsageBar(label: "5 hours", percent: fiveHour,
                             resets: usage.fiveHourReset, dimmed: dimmed, inline: true)
                        .identified("five-hour")
                } else {
                    NoFiveHourLimit(font: .callout, dimmed: dimmed)
                }
                UsageBar(label: "Week", percent: usage.week,
                         resets: usage.weekReset, dimmed: dimmed, inline: true)
                    .identified("week")
                if let fable = usage.fable {
                    UsageBar(label: "Fable", percent: fable,
                             resets: usage.fableReset, dimmed: dimmed, inline: true)
                        .identified("fable")
                }
            }
        } else {
            Text("No usage reported yet")
                .font(.callout)
                .foregroundStyle(.secondary)
                .identified("no-usage")
        }
    }

    /// "Plan usage", its explanation, and on the right when the figure was
    /// taken with the button that asks again.
    static func header(for entry: UsageMonitor.Entry?, refresh: @escaping () -> Void) -> some View {
        SectionHeader(title: "Plan usage", info: explanation(for: entry), id: "usage-info") {
            if let entry {
                UsageFreshness(entry: entry, refresh: refresh)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    /// Which of the two sources the figure came from. They answer differently
    /// enough to be worth naming: the endpoint is current whether or not that
    /// Claude is running, the file is as old as the last time it ran.
    static func explanation(for entry: UsageMonitor.Entry?) -> String {
        guard let entry, let usage = entry.usage else {
            return L10n.text("""
                Claude records how much of each window it has spent while it runs, \
                and the account itself is asked directly once its login is readable. \
                Neither has answered yet.
                """)
        }
        if entry.isLive {
            return L10n.text("""
                Asked of this account directly, using the login it already holds — \
                the same figure the menu bar is showing. It stays current whether or \
                not that Claude is running.
                """)
        }
        if usage.isStale, usage.fiveHour == nil {
            return L10n.text("""
                Recorded while this profile was last open, which was hours ago, so \
                the week may have moved since. Turn on live usage from the menu bar \
                to have the account asked instead.
                """)
        }
        if usage.isStale {
            return L10n.text("""
                Recorded while this profile was last open, which was long enough ago \
                that the five-hour window has since reset. Turn on live usage from the \
                menu bar to have the account asked instead.
                """)
        }
        return L10n.format("""
            Recorded by this profile at %@. It only updates while that Claude is \
            running, and the reset times are worked out from its own history.
            """, time.string(from: usage.sampled))
    }

    private static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter
    }()
}
