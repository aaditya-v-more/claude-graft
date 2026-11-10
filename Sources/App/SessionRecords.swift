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
/// moment a Claude quits, which is when the changes the mirror held back
/// from it can land.
final class SessionRecords: ObservableObject {
    /// Titles filed by the last pass, for a line in the dropdown. Empty the
    /// rest of the time: the session list is the history, and the line is
    /// only there to say what just happened.
    @Published private(set) var recovered: [String] = []

    private var inFlight = false
    private var askedAgain = false
    private var quitObserver: NSObjectProtocol?

    /// A Claude quitting is the moment the changes held back from it can land.
    /// A running Claude never reads its records again and writes them back
    /// from memory, so the mirror leaves its folder alone until it has quit,
    /// and this is the pass that catches it up before anything opens it
    /// again — including Claude opened straight from the Dock, which asks
    /// Graft nothing on the way in.
    func watchForClaudeQuitting(_ store: ShortcutStore) {
        guard quitObserver == nil else { return }
        quitObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                  app.bundleURL.map({ Graft.samePath($0, Graft.claudeApp) }) == true else { return }
            // Its helper processes follow it out a moment later, and a profile
            // with any of them left still reads as running.
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
