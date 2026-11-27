import SwiftUI

final class WhatsNewPresenter: ObservableObject {
    static let seenKey = "whatsNewSeenVersion"
    static let askedKey = "donationAskedVersion"
    @Published var showing: WhatsNew?
    /// The donation sheet, which follows a release's list once.
    @Published var asking = false

    func start(usedBefore: Bool) {
        showing = WhatsNew.unseen(running: Installer.graftVersion,
                                  seen: UserDefaults.standard.string(forKey: Self.seenKey),
                                  usedBefore: usedBefore)
        if showing == nil { markSeen() }
    }

    func markSeen() {
        UserDefaults.standard.set(Installer.graftVersion, forKey: Self.seenKey)
    }

    func listClosed() {
        markSeen()
        let defaults = UserDefaults.standard
        guard WhatsNew.asksForSupport(running: Installer.graftVersion, asked: defaults.string(forKey: Self.askedKey))
        else { return }
        defaults.set(WhatsNew.line(of: Installer.graftVersion), forKey: Self.askedKey)
        // The next turn: a sheet cannot go up while the one before it is coming down.
        DispatchQueue.main.async { self.asking = true }
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
                DonateButton()
                    .identified("whats-new.donate")
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

struct DonateButton: View {
    var body: some View {
        Button { Links.open(Links.support) } label: {
            Label {
                Text("Donate")
            } icon: {
                Image(systemName: "heart.fill").foregroundStyle(Color.donateHeart)
            }
        }
        .help("Claude Graft is free. A tip on Ko-fi is what keeps it worked on.")
    }
}

/// Asked once after a release's list is put away.
struct DonateView: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "heart.fill")
                .font(.system(size: 38))
                .foregroundStyle(Color.donateHeart)
                .accessibilityHidden(true)
            Text("Claude Graft is free")
                .font(.title2.bold())
                .multilineTextAlignment(.center)
            Text("A tip on Ko-fi is what keeps it worked on. The app is the same whether you give one or not.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .identified("donate.why")
            HStack {
                // Escape and Return both leave: a key pressed out of habit
                // must not open a page that asks for money.
                Button("Not Now") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                    .identified("donate.not-now")
                Spacer()
                Button {
                    Links.open(Links.support)
                    dismiss()
                } label: {
                    Label("Donate", systemImage: "heart.fill")
                }
                .buttonStyle(.borderedProminent)
                .identified("donate.donate")
            }
            .padding(.top, 6)
        }
        .padding(26)
        .frame(width: 360)
    }
}
