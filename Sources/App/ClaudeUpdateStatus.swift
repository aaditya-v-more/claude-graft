import SwiftUI

struct ClaudeUpdateStatus: View {
    var compact = false
    @ObservedObject private var updater: ClaudeDesktopUpdater
    private let requestUpdate: () -> Void

    init(compact: Bool = false, updater: ClaudeDesktopUpdater = Shared.claudeUpdates, requestUpdate: (() -> Void)? = nil) {
        self.compact = compact
        self.updater = updater
        self.requestUpdate = requestUpdate ?? updater.confirmInstall
    }

    var body: some View {
        Group {
            if compact {
                VStack(alignment: .leading, spacing: 8) { information; actions }
            } else {
                HStack(alignment: .center, spacing: 16) {
                    information
                    Spacer(minLength: 12)
                    actions
                }
            }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.bar)
        .onAppear { updater.check(force: false) }
    }

    private var information: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label("Claude Desktop", systemImage: updater.availableVersion == nil ? "arrow.triangle.2.circlepath" : "arrow.down.circle")
                .fontWeight(.medium)
            if updater.isUpdating {
                Text(updater.status ?? L10n.text("Preparing the update…"))
            } else if let version = updater.availableVersion {
                Text("Version \(version) is available")
                Text("Closes all Claude instances and ends their running workflows.")
                    .foregroundStyle(.secondary)
            } else if updater.checking {
                Text("Checking for updates…").foregroundStyle(.secondary)
            } else if let installed = updater.installedVersion, updater.problem == nil {
                Text("Version \(installed) · Up to date").foregroundStyle(.secondary)
                if let status = updater.status { Text(status).foregroundStyle(.secondary) }
            }
            if let problem = updater.problem { Text(problem).foregroundStyle(.red) }
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder private var actions: some View {
        if updater.isUpdating {
            ProgressView().controlSize(.small)
        } else if updater.availableVersion != nil {
            Button("Update Claude…", action: requestUpdate)
                .buttonStyle(.borderedProminent)
                .disabled(updater.checking)
        } else {
            Button("Check for Claude Updates") { updater.check() }
                .disabled(updater.checking)
        }
    }
}
