import AppKit
import Foundation

/// Keeps session records whole.
///
/// A Claude Desktop signed into one account will not write the record for a
/// Claude Code session owned by another, and the command line holds one
/// login for the whole machine — so every session started from a grafted
/// profile has been closing without a record and vanishing from every
/// sidebar while its transcript sat whole on disk. Each pass finds those
/// transcripts and files the missing record where the owner's account lives,
/// which is where the session's own window and everything grafted onto it
/// reads. `Graft.fileMissingSessionRecords` decides what gets filed and what
/// gets left; this only says when.
///
/// There is no timer. A record is read when a Claude starts and at no other
/// moment, so the pass that matters is the one a shortcut runs on its way to
/// opening one — and that happens in the launcher, in a process this app
/// never sees. What is left here are the two times somebody is looking at
/// Graft itself — when it starts, and when the dropdown opens — and the
/// moment a Claude quits, when what it wrote can reach the profiles sharing
/// its chats before any of them opens.
final class SessionRecords: ObservableObject {
    /// Titles filed by the last pass, for a line in the dropdown. Empty the
    /// rest of the time: the session list is the history, and the line is
    /// only there to say what just happened.
    @Published private(set) var recovered: [String] = []

    private var inFlight = false
    private var askedAgain = false
    private var quitObserver: NSObjectProtocol?

    /// A Claude that has just quit has written everything it is going to, and
    /// a profile sharing its chats may be opened next straight from the Dock,
    /// which asks Graft nothing on the way in. A Claude reads its records once,
    /// as it starts, so the pass has to land before that: without one here, an
    /// archive made in one profile waited for somebody to open the dropdown or
    /// a shortcut before any other profile could see it.
    func watchForClaudeQuitting(_ store: ShortcutStore) {
        guard quitObserver == nil else { return }
        quitObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                  app.bundleURL.map({ Graft.samePath($0, Graft.claudeApp) }) == true else { return }
            // A moment's grace for the last of its writes to land.
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self?.sweep(store) }
        }
    }

    /// Reads the store on the main thread, does the scanning away from it —
    /// transcripts run to megabytes, and none of that belongs on a run loop
    /// that is also holding a menu bar together. A pass still running when
    /// another is asked for is left to finish, and one more follows it: the
    /// pass under way may have read a Claude as running that has since quit.
    func sweep(_ store: ShortcutStore) {
        guard !inFlight else { askedAgain = true; return }
        inFlight = true

        var filing: [URL] = [Graft.mainProfile]
        for shortcut in store.shortcuts where shortcut.installedName != nil {
            filing.append(shortcut.profileDir)
        }

        DispatchQueue.global(qos: .utility).async { [weak self] in
            // The only pass that covers Claude opened straight from the
            // Dock, which runs no launcher and asks Graft nothing.
            let filed = Graft.squareUp(filingInto: filing)
            DispatchQueue.main.async {
                guard let self else { return }
                self.inFlight = false
                let titles = filed.map(\.title)
                if self.recovered != titles { self.recovered = titles }
                if self.askedAgain {
                    self.askedAgain = false
                    self.sweep(store)
                }
            }
        }
    }
}
