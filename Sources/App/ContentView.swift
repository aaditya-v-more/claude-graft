import AppKit
import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var store: ShortcutStore
    @State private var selection: UUID?
    @State private var pendingDeletion: UUID?
    @State private var problem: String?

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 280)
                .toolbar {
                    ToolbarItem {
                        Button(action: add) {
                            Label("New Shortcut", systemImage: "plus")
                        }
                        .keyboardShortcut("n", modifiers: .command)
                        .help("New shortcut")
                    }
                }
        } detail: {
            if let selection, let index = store.shortcuts.firstIndex(where: { $0.id == selection }) {
                ShortcutDetail(shortcut: $store.shortcuts[index],
                               requestDelete: { pendingDeletion = selection })
                    .id(selection)
            } else {
                EmptyState(hasShortcuts: !store.shortcuts.isEmpty, add: add)
            }
        }
        .confirmationDialog(deletionTitle,
                            isPresented: Binding(get: { pendingDeletion != nil },
                                                 set: { if !$0 { pendingDeletion = nil } }),
                            titleVisibility: .visible) {
            Button("Delete Shortcut Only") { delete(alsoProfile: false) }
            Button("Delete Shortcut and Profile", role: .destructive) { delete(alsoProfile: true) }
            Button("Cancel", role: .cancel) { pendingDeletion = nil }
        } message: {
            Text(deletionMessage)
        }
        .alert("The shortcut was deleted", isPresented: Binding(get: { problem != nil },
                                                               set: { if !$0 { problem = nil } })) {
            Button("OK") { problem = nil }
        } message: {
            Text(problem ?? "")
        }
    }

    private func delete(alsoProfile: Bool) {
        guard let pendingDeletion else { return }
        if selection == pendingDeletion { selection = nil }
        problem = store.delete(pendingDeletion, deletingProfile: alsoProfile)
        self.pendingDeletion = nil
    }

    private var deletionMessage: String {
        guard let pendingDeletion, let shortcut = store.shortcut(pendingDeletion) else { return "" }
        return """
        The app is removed from \(Installer.installDirectory.path) either way.

        Its profile folder, \(shortcut.folder), holds that account's login and \
        chat history. Deleting it cannot be undone.
        """
    }

    private var deletionTitle: String {
        guard let pendingDeletion, let shortcut = store.shortcut(pendingDeletion) else {
            return "Delete this shortcut?"
        }
        return "Delete “\(shortcut.name)”?"
    }

    private var sidebar: some View {
        List(selection: $selection) {
            Section("Shortcuts") {
                ForEach(store.shortcuts) { shortcut in
                    Label {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(shortcut.name)
                            Text(store.label(for: shortcut.source))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: shortcut.source == .own ? "circle" : "link")
                            .foregroundStyle(.secondary)
                    }
                    .tag(shortcut.id)
                    .contextMenu {
                        Button("Delete Shortcut…", role: .destructive) {
                            pendingDeletion = shortcut.id
                        }
                    }
                }
            }

            Button(action: add) {
                Label("New Shortcut", systemImage: "plus")
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .padding(.vertical, 2)
        }
        .onDeleteCommand { if let selection { pendingDeletion = selection } }
    }

    private func add() {
        let shortcut = Shortcut(name: store.uniqueName())
        store.shortcuts.append(shortcut)
        selection = shortcut.id
    }
}

private struct EmptyState: View {
    let hasShortcuts: Bool
    let add: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "square.on.square.dashed")
                .font(.system(size: 34, weight: .light))
                .foregroundStyle(.tertiary)
            Text(hasShortcuts ? "Select a shortcut" : "No shortcuts yet")
                .font(.title3)
            Text("Each shortcut opens Claude on its own account,\nand can borrow another account's Claude Code chats.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            if !hasShortcuts {
                Button("New Shortcut", action: add)
                    .padding(.top, 4)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}
