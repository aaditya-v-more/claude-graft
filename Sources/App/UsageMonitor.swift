import Foundation

/// Polls each profile's recorded plan usage. Claude writes those numbers only
/// while it is running, so an entry can be current, stale, or absent, and the
/// interface has to be able to say which.
final class UsageMonitor: ObservableObject {
    struct Entry: Identifiable, Equatable {
        var id: String { profile.path }
        var name: String
        var profile: URL
        var usage: Graft.Usage?
        var isRunning: Bool
        /// Nil for the main profile, which has no shortcut behind it.
        var shortcut: UUID?
    }

    @Published private(set) var entries: [Entry] = []

    private var timer: Timer?
    private let queue = DispatchQueue(label: "graft.usage", qos: .utility)
    private var inFlight = false

    /// The number worth putting in the menu bar: the closest five-hour window
    /// to its limit among everything not stale.
    var headline: Int? {
        entries
            .compactMap { entry -> Int? in
                guard let usage = entry.usage, !usage.isStale else { return nil }
                return usage.fiveHour
            }
            .max()
    }

    /// Idempotent: the window and the menu bar item both ask for it, and only
    /// one of the two may ever appear.
    func start(watching store: ShortcutStore, every seconds: TimeInterval = 30) {
        guard timer == nil else { refresh(store); return }
        refresh(store)
        let timer = Timer(timeInterval: seconds, repeats: true) { [weak self, weak store] _ in
            guard let store else { return }
            self?.refresh(store)
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// Reads the store on the calling thread — it must be the main one — then
    /// does the filesystem and pgrep work away from it.
    func refresh(_ store: ShortcutStore) {
        var targets: [(name: String, profile: URL, shortcut: UUID?)] = [
            (name: "Claude", profile: Graft.mainProfile, shortcut: nil)
        ]
        for shortcut in store.shortcuts where shortcut.installedName != nil {
            targets.append((name: shortcut.name, profile: shortcut.profileDir, shortcut: shortcut.id))
        }

        guard !inFlight else { return }
        inFlight = true
        queue.async { [weak self] in
            let fresh = targets.map { target in
                Entry(name: target.name,
                      profile: target.profile,
                      usage: Graft.usage(of: target.profile),
                      isRunning: Graft.isRunning(profile: target.profile),
                      shortcut: target.shortcut)
            }
            DispatchQueue.main.async {
                guard let self else { return }
                self.inFlight = false
                if self.entries != fresh { self.entries = fresh }
            }
        }
    }
}
