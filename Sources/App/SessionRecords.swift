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
/// Graft itself: when it starts, and when the dropdown opens.
final class SessionRecords: ObservableObject {
    /// Titles filed by the last pass, for a line in the dropdown. Empty the
    /// rest of the time: the session list is the history, and the line is
    /// only there to say what just happened.
    @Published private(set) var recovered: [String] = []

    private var inFlight = false

    /// Reads the store on the main thread, does the scanning away from it —
    /// transcripts run to megabytes, and none of that belongs on a run loop
    /// that is also holding a menu bar together. A pass still running when
    /// another is asked for is left to finish; the write it is headed for
    /// refuses to land twice.
    func sweep(_ store: ShortcutStore) {
        guard !inFlight else { return }
        inFlight = true

        var filing: [URL] = [Graft.mainProfile]
        for shortcut in store.shortcuts where shortcut.installedName != nil {
            filing.append(shortcut.profileDir)
        }

        DispatchQueue.global(qos: .utility).async { [weak self] in
            // Ahead of the filing for the reason `Graft.run` puts it there:
            // a record that arrives by mirror should be on disk before the
            // sweep decides whether anyone has filed one. This is also the
            // only pass that covers Claude opened straight from the Dock,
            // which runs no launcher and asks Graft nothing.
            Graft.mirrorKnownPairs()
            let filed = Graft.fileMissingSessionRecords(filingInto: filing)
            // After the pass rather than before it, so the snapshot describes
            // what the stores were left in rather than what they started as.
            Graft.writeStateReport()
            DispatchQueue.main.async {
                guard let self else { return }
                self.inFlight = false
                let titles = filed.map(\.title)
                if self.recovered != titles { self.recovered = titles }
            }
        }
    }
}
