import Foundation

/// What a release shows of itself the first time it runs after an update.
struct WhatsNew: Identifiable, Equatable {
    struct Item: Identifiable, Equatable {
        let symbol: String
        let title: String
        let detail: String
        var id: String { title }
    }

    /// Major and minor: a patch release announces nothing of its own.
    let version: String
    let items: [Item]
    var id: String { version }

    static let releases = [
        WhatsNew(version: "2.3", items: [
            Item(symbol: "mic",
                 title: "Dictation in any app",
                 detail: "Hold the right Command key and speak, and the words are typed where the cursor is. Off until you switch it on under Dictation."),
            Item(symbol: "person.crop.circle",
                 title: "A page for every account",
                 detail: "What each account has used, when its windows reset and which Claudes draw on it, with the order accounts are used in under Shared usage."),
            Item(symbol: "menubar.arrow.down.rectangle",
                 title: "A shorter dropdown",
                 detail: "Each account’s usage side by side on one line, listed by Claude or by account."),
            Item(symbol: "play.circle",
                 title: "Auto-start",
                 detail: "Opens an account’s next five-hour window as soon as the last one closes. Off until you switch it on."),
            Item(symbol: "bubble.left.and.bubble.right",
                 title: "A conversation stays on its account",
                 detail: "Under Shared usage a conversation keeps the account it started on until that account runs out, and a side chat follows it."),
            Item(symbol: "building.2",
                 title: "More than one organization",
                 detail: "An address in several organizations chooses which one Artifacts, Projects and Search Chats show."),
        ]),
    ]

    static func line(of version: String) -> String {
        version.split(separator: ".").prefix(2).joined(separator: ".")
    }

    /// A first install has nothing to compare with and is shown nothing;
    /// `usedBefore` is what tells it from an update out of a version that
    /// kept no record.
    static func unseen(running: String, seen: String?, usedBefore: Bool) -> WhatsNew? {
        guard seen != nil || usedBefore, seen.map(line) != line(of: running) else { return nil }
        return releases.first { $0.version == line(of: running) }
    }
}
