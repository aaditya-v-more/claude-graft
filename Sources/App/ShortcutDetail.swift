import AppKit
import SwiftUI

struct ShortcutDetail: View {
    @EnvironmentObject private var store: ShortcutStore
    @Binding var shortcut: Shortcut

    @State private var installedAt: URL?
    @State private var error: String?
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
                    Text(Graft.isRunning(profile: shortcut.profileDir) ? "Running on this profile" : "Not running")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom) {
            HStack {
                if installedAt != nil {
                    Button("Remove Shortcut", role: .destructive, action: remove)
                }
                Spacer()
                Button("Open", action: open)
                    .disabled(installedAt == nil)
                Button(installedAt == nil ? "Create Shortcut" : "Update Shortcut", action: install)
                    .keyboardShortcut(.defaultAction)
                    .disabled(shortcut.name.trimmingCharacters(in: .whitespaces).isEmpty || claudeMissing)
            }
            .padding(14)
            .background(.bar)
        }
        .onAppear { refresh() }
        .alert("Could not create the shortcut", isPresented: .constant(error != nil)) {
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

    private func refresh() {
        installedAt = Installer.installedBundle(for: shortcut)
    }

    private func install() {
        shortcut.name = shortcut.name.trimmingCharacters(in: .whitespaces)
        do {
            let previous = installedAt?.deletingPathExtension().lastPathComponent
            installedAt = try Installer.install(shortcut,
                                                sourceDir: store.sourceDir(for: shortcut),
                                                previousName: previous)
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func open() {
        guard let installedAt else { return }
        NSWorkspace.shared.openApplication(at: installedAt, configuration: NSWorkspace.OpenConfiguration())
    }

    private func remove() {
        Installer.uninstall(shortcut)
        installedAt = nil
    }
}
