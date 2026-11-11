import SwiftUI

/// Claude Desktop's version and its update, at the top of the dropdown and the
/// foot of the window's sidebar.
///
/// It sits above everything either of them is opened for, so it gets one line,
/// two when the words need them, and explains the rest on hover. Red is kept
/// for a release waiting to be installed, the one state asking somebody to do
/// something; a line coloured all the time stops being read.
struct ClaudeUpdateStatus: View {
    var compact = false
    /// In the sidebar, which is too narrow for the line and an install button
    /// side by side, so the button goes under the line.
    var stacked = false
    @ObservedObject private var updater: ClaudeDesktopUpdater
    private let requestUpdate: () -> Void

    init(compact: Bool = false, stacked: Bool = false,
         updater: ClaudeDesktopUpdater = Shared.claudeUpdates, requestUpdate: (() -> Void)? = nil) {
        self.compact = compact || stacked
        self.stacked = stacked
        self.updater = updater
        self.requestUpdate = requestUpdate ?? updater.confirmInstall
    }

    /// A release ready to install, while nothing is under way or has failed.
    private var waiting: String? {
        updater.isUpdating || updater.problem != nil ? nil : updater.availableVersion
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            row
            if stacked, waiting != nil, !updater.isUpdating {
                installButton.frame(maxWidth: .infinity)
            }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, compact ? 6 : 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        // In the sidebar it stands on the sidebar's own material; a bar
        // there reads as a panel pasted over it.
        .background(stacked ? AnyShapeStyle(.clear) : AnyShapeStyle(.bar))
        .onAppear { updater.check(force: false) }
    }

    private var row: some View {
        HStack(spacing: 8) {
            // The line beside it says everything the symbol does, and SwiftUI
            // reads this one out as "Syncing".
            Image(systemName: waiting == nil ? "arrow.triangle.2.circlepath" : "arrow.down.circle.fill")
                .foregroundColor(waiting == nil ? .secondary : .red)
                .accessibilityHidden(true)
            line
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
                .help(ifAny: updater.problem ?? updater.status)
                .identified("claude-update.status")
            Spacer(minLength: 8)
            action
        }
    }

    private var installButton: some View {
        Button("Update Claude…", action: requestUpdate)
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .controlSize(compact ? .small : .regular)
            .disabled(updater.checking)
            .help("Closes all Claude instances and ends their running workflows.")
            .identified("claude-update.install")
    }

    /// The product's name leads only where the words after it do not already
    /// say it, or a finished update would read "Claude Desktop Claude Desktop".
    private var line: Text {
        let name = Text("Claude Desktop").fontWeight(.medium)
        if updater.isUpdating {
            return Text(updater.status ?? L10n.text("Preparing the update…"))
        }
        if let problem = updater.problem { return Text(problem).foregroundColor(.red) }
        if let version = updater.availableVersion {
            return Text(L10n.format("Claude Desktop %@ is available", version))
                .fontWeight(.medium).foregroundColor(.red)
        }
        if updater.checking {
            return name + Text(verbatim: stacked ? "\n" : " ") + Text("Checking for updates…").foregroundColor(.secondary)
        }
        if let status = updater.status { return Text(status).foregroundColor(.secondary) }
        if let installed = updater.installedVersion {
            // In the sidebar the version goes under the name rather than
            // breaking wherever the width runs out.
            return name + Text(verbatim: stacked ? "\n" : " ")
                + Text("\(installed) · Up to date").foregroundColor(.secondary)
        }
        return name
    }

    @ViewBuilder private var action: some View {
        if updater.isUpdating {
            ProgressView().controlSize(.small)
        } else if updater.availableVersion != nil, !stacked {
            installButton
        } else if compact {
            Button { updater.check() } label: {
                if updater.checking {
                    ProgressView().controlSize(.mini)
                } else {
                    Image(systemName: "arrow.clockwise")
                }
            }
            .buttonStyle(.borderless)
            .disabled(updater.checking)
            .help("Check for Claude Updates")
            .accessibilityLabel(L10n.text("Check for Claude Updates"))
            .identified("claude-update.check")
        } else {
            Button("Check for Claude Updates") { updater.check() }
                .disabled(updater.checking)
                .identified("claude-update.check")
        }
    }
}

private extension View {
    /// A tooltip only where there is something to put in it.
    @ViewBuilder func help(ifAny text: String?) -> some View {
        if let text, !text.isEmpty { help(Text(verbatim: text)) } else { self }
    }
}
