import Foundation
import SwiftUI

final class ManualUpdateSettings: ObservableObject {
    @Published private(set) var enabled = false
    @Published private(set) var changing = true
    @Published private(set) var problem: String?
    private weak var store: ShortcutStore?
    private let queue = DispatchQueue(label: "graft.manual-updates", qos: .utility)

    func start(watching store: ShortcutStore) {
        self.store = store
        update(nil)
    }

    func setEnabled(_ wanted: Bool) {
        guard !changing else { return }
        update(wanted)
    }

    private func update(_ wanted: Bool?) {
        changing = true
        let profiles = [Graft.mainProfile] + (store?.shortcuts ?? [])
            .filter { $0.installedName != nil }.map(\.profileDir)
        queue.async {
            var problem: String?
            do {
                if let wanted { try ManualUpdates.setEnabled(wanted, profiles: profiles) }
                else { try ManualUpdates.synchronize(profiles) }
            } catch { problem = error.localizedDescription }
            let state = try? ManualUpdates.readState()
            DispatchQueue.main.async {
                self.enabled = state?.enabled ?? false
                self.problem = problem
                self.changing = false
            }
        }
    }
}

struct ManualUpdateControl: View {
    @ObservedObject private var settings = Shared.manualUpdates
    @ObservedObject private var updates = Shared.claudeUpdates

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Toggle("Update Claude Desktop manually", isOn: Binding(
                get: { settings.enabled }, set: { settings.setEnabled($0) }))
                .disabled(settings.changing || updates.isUpdating)
            if let problem = settings.problem {
                Text(problem).foregroundStyle(.red)
            } else if settings.enabled {
                Text("Stops automatic updates for Claude and every shortcut from their next launch. Graft checks for new versions; use Update Claude when your work is finished.")
                    .foregroundStyle(.secondary)
            }
        }
        .font(.callout)
        .fixedSize(horizontal: false, vertical: true)
    }
}
