import SwiftUI

final class WhatsNewPresenter: ObservableObject {
    static let seenKey = "whatsNewSeenVersion"
    @Published var showing: WhatsNew?

    func start(usedBefore: Bool) {
        showing = WhatsNew.unseen(running: Installer.graftVersion,
                                  seen: UserDefaults.standard.string(forKey: Self.seenKey),
                                  usedBefore: usedBefore)
        if showing == nil { markSeen() }
    }

    func markSeen() {
        UserDefaults.standard.set(Installer.graftVersion, forKey: Self.seenKey)
    }

    /// From the Help menu. False when this version announced nothing.
    func showAgain() -> Bool {
        guard let release = WhatsNew.releases.first(where: { $0.version == WhatsNew.line(of: Installer.graftVersion) })
        else { return false }
        showing = release
        // A reopen makes SwiftUI build the window the sheet hangs on.
        if !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
            NSWorkspace.shared.open(Bundle.main.bundleURL)
        }
        return true
    }
}

struct WhatsNewView: View {
    let release: WhatsNew
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 22) {
            VStack(spacing: 4) {
                Text("What’s New in Claude Graft")
                    .font(.title.bold())
                    .multilineTextAlignment(.center)
                Text(L10n.format("Version %@", release.version))
                    .foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 16) {
                ForEach(release.items) { item in
                    HStack(alignment: .top, spacing: 14) {
                        Image(systemName: item.symbol)
                            .font(.title)
                            .foregroundStyle(.tint)
                            .frame(width: 36)
                            .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L10n.text(item.title)).font(.headline)
                            Text(L10n.text(item.detail))
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .accessibilityElement(children: .combine)
                }
            }
            HStack {
                Button(L10n.text("All Release Notes")) { Links.open(Links.releases) }
                    .identified("whats-new.release-notes")
                Spacer()
                Button(L10n.text("Continue")) { dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .identified("whats-new.continue")
            }
        }
        .padding(28)
        .frame(width: 480)
    }
}
