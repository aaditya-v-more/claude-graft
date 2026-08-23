import AppKit
import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var store: ShortcutStore
    @State private var selection: UUID?

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 280)
        } detail: {
            if let selection, let index = store.shortcuts.firstIndex(where: { $0.id == selection }) {
                ShortcutDetail(shortcut: $store.shortcuts[index])
                    .id(selection)
            } else {
                EmptyState(hasShortcuts: !store.shortcuts.isEmpty, add: add)
            }
        }
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
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            HStack(spacing: 2) {
                Button(action: add) { Image(systemName: "plus") }
                    .help("Add a shortcut")
                Button(action: removeSelected) { Image(systemName: "minus") }
                    .help("Remove the selected shortcut")
                    .disabled(selection == nil)
                Spacer()
            }
            .buttonStyle(.borderless)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(.bar)
        }
    }

    private func add() {
        let shortcut = Shortcut(name: store.uniqueName())
        store.shortcuts.append(shortcut)
        selection = shortcut.id
    }

    private func removeSelected() {
        guard let selection, let shortcut = store.shortcut(selection) else { return }
        Installer.uninstall(shortcut)
        store.shortcuts.removeAll { $0.id == selection }
        self.selection = store.shortcuts.first?.id
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
                Button("Add Shortcut", action: add)
                    .padding(.top, 4)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}
