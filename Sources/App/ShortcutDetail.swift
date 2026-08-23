import AppKit
import Combine
import SwiftUI

struct ShortcutDetail: View {
    @EnvironmentObject private var store: ShortcutStore
    @Binding var shortcut: Shortcut
    let requestDelete: () -> Void

    @State private var error: String?
    @State private var errorTitle = "Something went wrong"
    /// Both are looked up off the main thread and refreshed on a timer. Reading
    /// them during a view update would mean touching the filesystem and running
    /// pgrep inside the body, and blocking there re-enters AppKit layout.
    @State private var installedAt: URL?
    @State private var isRunning = false
    @State private var profileExists = false
    @State private var usage: Graft.Usage?
    @State private var startingSession = false

    /// Other instances found open on the same chat store when Open was pressed.
    @State private var sharersOpen: [String] = []
    @State private var askAboutSharers = false

    private let clock = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    /// Set once the folder is typed by hand, so renaming stops rewriting it.
    /// Pointing it at an existing folder adopts that profile, login and all.
    @State private var folderIsCustom = false

    private var claudeMissing: Bool {
        !FileManager.default.fileExists(atPath: Graft.claudeApp.path)
    }

    var body: some View {
        Form {
            if claudeMissing {
                Section {
                    Label("Claude.app was not found in /Applications.", systemImage: "exclamationmark.triangle")
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
                Text("Shortcut")
            } footer: {
                Text("The name is what the app in \(Installer.installDirectory.path) is called. The profile folder is where this account's login, chats and settings are kept, inside ~/Library/Application Support. Naming an existing folder adopts that profile instead of starting an empty one.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
            }

            Section {
                Picker("Claude Code chats", selection: $shortcut.source) {
                    ForEach(store.availableSources(for: shortcut), id: \.self) { source in
                        Text(store.label(for: source)).tag(source)
                    }
                }
            } footer: {
                Text(sourceExplanation)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
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
                if let usage {
                    UsageBar(label: "5 hours", percent: usage.fiveHour, dimmed: usage.isStale)
                    UsageBar(label: "Week", percent: usage.week, dimmed: usage.isStale)
                } else {
                    Text("No usage reported yet")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            } header: {
                Text("Plan usage")
            } footer: {
                Text(usageExplanation)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            HStack {
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
                .disabled(installedAt == nil || startingSession || claudeMissing)
                .help("Sends “hi” in that Claude to open its five-hour window")

                Button("Open", action: open)
                    .disabled(installedAt == nil)
                Button(installedAt == nil ? "Create Shortcut" : "Update Shortcut", action: install)
                    .keyboardShortcut(.defaultAction)
                    .disabled(shortcut.name.trimmingCharacters(in: .whitespaces).isEmpty || claudeMissing)
            }
            .padding(14)
            .background(.bar)
        }
        .confirmationDialog("Another Claude is open on these chats",
                            isPresented: $askAboutSharers,
                            titleVisibility: .visible) {
            Button("Open Anyway") { launch() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(sharersMessage)
        }
        .onAppear { refresh() }
        .onReceive(clock) { _ in refresh() }
        .alert(errorTitle, isPresented: .constant(error != nil)) {
            Button("OK") { error = nil }
        } message: {
            Text(error ?? "")
        }
    }

    private var sourceExplanation: String {
        switch shortcut.source {
        case .own:
            return "This profile keeps its own Claude Code history, connectors and preferences. Nothing is shared."
        default:
            let name = store.label(for: shortcut.source)
            return "Opens \(name)'s Claude Code chats, connectors, extensions and window state. Logins stay separate, so this shortcut can sign into a different account."
        }
    }

    private func install() {
        shortcut.name = shortcut.name.trimmingCharacters(in: .whitespaces)
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

    /// Off the main thread: `Installer.installedBundle` hits the filesystem and
    /// `Graft.isRunning` spawns pgrep, neither of which belongs in a view update.
    private func refresh() {
        let target = shortcut
        DispatchQueue.global(qos: .utility).async {
            let bundle = Installer.installedBundle(for: target)
            let running = Graft.isRunning(profile: target.profileDir)
            let hasProfile = FileManager.default.fileExists(atPath: target.profileDir.path)
            let plan = Graft.usage(of: target.profileDir)
            DispatchQueue.main.async {
                installedAt = bundle
                isRunning = running
                profileExists = hasProfile
                usage = plan
            }
        }
    }

    private var usageExplanation: String {
        guard let usage else {
            return "Claude records how much of each window it has spent while it runs. Open this profile once and the numbers appear."
        }
        if usage.isStale {
            return "Recorded while this profile was last open, which was long enough ago that the five-hour window has since reset."
        }
        return "Recorded by this profile at \(Self.time.string(from: usage.sampled)). It only updates while that Claude is running."
    }

    private static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter
    }()

    private func startSession() {
        guard !startingSession else { return }
        if !SessionStarter.hasAccessibility { SessionStarter.requestAccessibility() }
        let profile = shortcut.profileDir
        let bundle = installedAt
        startingSession = true
        DispatchQueue.global(qos: .userInitiated).async {
            let failure = SessionStarter.start(profile: profile, bundle: bundle)
            DispatchQueue.main.async {
                startingSession = false
                if let failure {
                    errorTitle = "Could not start a session"
                    error = failure.errorDescription
                }
                refresh()
            }
        }
    }

    private var isDraft: Bool {
        installedAt == nil && !profileExists && shortcut.installedName == nil
    }

    private var sharersMessage: String {
        let list: String
        switch sharersOpen.count {
        case 0: list = ""
        case 1: list = sharersOpen[0] + " is"
        case 2: list = sharersOpen.joined(separator: " and ") + " are"
        default:
            list = sharersOpen.dropLast().joined(separator: ", ")
                + " and " + (sharersOpen.last ?? "") + " are"
        }
        return """
        \(list) already open on the same Claude Code chats.

        Both instances write to the same chat files. Opening the same \
        conversation in two of them at once can lose messages.
        """
    }

    /// Checking who else is open means one pgrep per neighbour, so it happens
    /// off the main thread and the window is opened once the answer is back.
    private func open() {
        guard installedAt != nil else { return }
        let neighbours = store.chatStoreNeighbours(of: shortcut)
        DispatchQueue.global(qos: .userInitiated).async {
            let openNow = neighbours.filter { Graft.isRunning(profile: $0.profile) }.map(\.name)
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
