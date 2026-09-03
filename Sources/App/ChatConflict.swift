import AppKit
import Foundation

/// Two Claudes on one chat store.
///
/// Sharing chats is the point of this app, and it is also the one way it can
/// lose you something: both instances write the same files, so the same
/// conversation open in two of them can drop messages. So opening a profile
/// that something else is already reading is worth a question.
///
/// This lives on its own because every button that opens a profile has to ask
/// it, and only one of the three used to. The window asked; the dropdown did
/// not, and Claude's own profile could not — it has no shortcut to stand for
/// it, so nothing ever looked. That last case is the common one: every shortcut
/// grafted from main reads exactly the files main is about to open.
enum ChatConflict {
    static let title = L10n.text("Another Claude is open on these chats")

    /// Reads as a sentence for any number of them, which is why it is one
    /// function rather than a format string at each call site.
    static func message(sharers: [String]) -> String {
        let list: String
        switch sharers.count {
        case 0: list = ""
        case 1: list = sharers[0]
        case 2: list = sharers.joined(separator: " \(L10n.text("and")) ")
        default:
            list = sharers.dropLast().joined(separator: ", ")
                + " \(L10n.text("and")) " + (sharers.last ?? "")
        }
        let key = sharers.count == 1
            ? "%@ is already open on the same Claude Code chats.\n\nBoth instances write to the same chat files. Opening the same conversation in two of them at once can lose messages."
            : "%@ are already open on the same Claude Code chats.\n\nBoth instances write to the same chat files. Opening the same conversation in two of them at once can lose messages."
        return L10n.format(key, list)
    }

    /// Who among `neighbours` is running, and whether that is worth asking
    /// about at all. One `pgrep` each, so never on the main thread.
    static func openSharers(of profile: URL,
                            among neighbours: [(name: String, profile: URL)]) -> [String] {
        sharersToAskAbout(profileIsOpen: Graft.isRunning(profile: profile),
                          openNeighbours: neighbours
                            .filter { Graft.isRunning(profile: $0.profile) }
                            .map(\.name))
    }

    /// A profile that is already open is not about to be opened: the Claude
    /// sitting on those chats is only being brought forward, and it has been
    /// reading them all along. Warning there describes a situation rather than
    /// one the press is about to create, and it arrives attached to a button
    /// that will not create one.
    static func sharersToAskAbout(profileIsOpen: Bool, openNeighbours: [String]) -> [String] {
        profileIsOpen ? [] : openNeighbours
    }

    /// The dropdown's version of the question.
    ///
    /// An `NSAlert` rather than SwiftUI's `confirmationDialog`, because the
    /// dropdown is an `NSPopover`: it gives up key window as soon as anything
    /// else appears, taking any sheet it was hosting with it. The app is
    /// brought forward first for the same reason a panel would be — an
    /// accessory app that merely orders an alert front puts it behind whatever
    /// the person is actually looking at.
    @MainActor
    static func askInPopover(sharers: [String]) -> Bool {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = message(sharers: sharers)
        alert.addButton(withTitle: L10n.text("Open Anyway"))
        alert.addButton(withTitle: L10n.text("Cancel"))
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }
}
