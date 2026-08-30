import Foundation

/// One extra Claude Desktop shortcut: a name, a profile folder of its own, and
/// where its Claude Code chats come from.
struct Shortcut: Codable, Identifiable, Hashable {
    enum Source: Codable, Hashable {
        case own
        case main
        case shortcut(UUID)
    }

    var id = UUID()
    var name: String
    var folder: String
    var source: Source = .main
    /// Name the bundle was last installed under, so a rename can clean up.
    var installedName: String?
    init(name: String, folder: String? = nil, source: Source = .main) {
        self.name = name
        self.folder = folder ?? Shortcut.folderName(for: name)
        self.source = source
    }

    /// "Work Account" -> "Claude-Work-Account", "Claude 2" -> "Claude-2".
    static func folderName(for name: String) -> String {
        var words = name
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
        if words.first?.lowercased() == "claude" { words.removeFirst() }
        let cleaned = words.joined(separator: "-")
        return "Claude-" + (cleaned.isEmpty ? "Profile" : cleaned)
    }

    var profileDir: URL { Graft.applicationSupport.appending(path: folder) }
}

final class ShortcutStore: ObservableObject {
    @Published var shortcuts: [Shortcut] = [] { didSet { save() } }

    private let file: URL = Graft.applicationSupport
        .appending(path: "ClaudeGraft")
        .appending(path: "shortcuts.json")

    init() {
        if let data = try? Data(contentsOf: file),
           let decoded = try? JSONDecoder().decode([Shortcut].self, from: data) {
            shortcuts = decoded
        }
    }

    private func save() {
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(shortcuts) else { return }
        try? data.write(to: file)
    }

    func shortcut(_ id: UUID) -> Shortcut? { shortcuts.first { $0.id == id } }

    /// Removes the shortcut and the app it installed. The profile folder — a
    /// login and a chat history — only goes when explicitly asked for, and
    /// never while another shortcut still points at it.
    /// Returns a message when the profile could not be removed.
    @discardableResult
    func delete(_ id: UUID, deletingProfile: Bool = false) -> String? {
        guard let shortcut = shortcut(id) else { return nil }
        Installer.uninstall(shortcut)

        var problem: String?
        if deletingProfile {
            let sharedWithAnother = shortcuts.contains { $0.id != id && $0.folder == shortcut.folder }
            if sharedWithAnother {
                problem = "The profile folder was kept: another shortcut still uses it."
            } else {
                do { try Graft.deleteProfile(shortcut.profileDir) }
                catch { problem = error.localizedDescription }
            }
        }

        // Nothing runs this shortcut's launcher again, so nothing is left to
        // undo its mirroring for it, and a pair outliving the shortcut that
        // made it goes on syncing whenever any other profile opens. The copies
        // stay — they are chats, and the person kept the folder — but the two
        // folders stop being squared up against each other.
        if !shortcuts.contains(where: { $0.id != id && $0.folder == shortcut.folder }) {
            Graft.forgetMirrors(of: shortcut.profileDir)
        }
        shortcuts.removeAll { $0.id == id }

        // Anything that borrowed from it would silently fall back to its own
        // chats, un-grafting on the next launch without a word.
        for index in shortcuts.indices where shortcuts[index].source == .shortcut(id) {
            shortcuts[index].source = .own
        }
        return problem
    }

    /// Never created, and holding no data: safe to drop without asking.
    func isDraft(_ shortcut: Shortcut) -> Bool {
        shortcut.installedName == nil
            && Installer.installedBundle(for: shortcut) == nil
            && !FileManager.default.fileExists(atPath: shortcut.profileDir.path)
    }

    /// Where a shortcut actually reads its chats from, following one hop.
    func sourceDir(for shortcut: Shortcut) -> URL? {
        switch shortcut.source {
        case .own: return nil
        case .main: return Graft.mainProfile
        case .shortcut(let id): return self.shortcut(id)?.profileDir
        }
    }

    /// The profile whose chat store a shortcut ends up reading, following the
    /// chain of sources to its end.
    func chatRoot(for shortcut: Shortcut) -> URL {
        var seen: Set<UUID> = []
        var current = shortcut
        while true {
            switch current.source {
            case .own:
                return current.profileDir
            case .main:
                return Graft.mainProfile
            case .shortcut(let id):
                guard seen.insert(current.id).inserted, let next = self.shortcut(id) else {
                    return current.profileDir
                }
                current = next
            }
        }
    }

    /// Everything else reading the same chat store, as name-and-profile pairs.
    /// Whether any of them is open is a separate, slower question best asked
    /// off the main thread.
    ///
    /// Nil asks on behalf of Claude's own profile, which has no shortcut to
    /// stand for it and was therefore the one profile that could never find out
    /// who else was on its chats — the case the question gets asked about most,
    /// since every shortcut grafted from main reads exactly those files.
    func chatStoreNeighbours(of shortcut: Shortcut?) -> [(name: String, profile: URL)] {
        let root = shortcut.map(chatRoot(for:)) ?? Graft.mainProfile
        var found: [(name: String, profile: URL)] = []
        if shortcut != nil, Graft.samePath(root, Graft.mainProfile) {
            found.append((name: "Claude", profile: Graft.mainProfile))
        }
        for other in shortcuts where other.id != shortcut?.id {
            guard Graft.samePath(chatRoot(for: other), root) else { continue }
            found.append((name: other.name, profile: other.profileDir))
        }
        return found
    }

    func label(for source: Shortcut.Source) -> String {
        switch source {
        case .own: return "Its own chats"
        case .main: return "Main Claude"
        case .shortcut(let id): return shortcut(id)?.name ?? "Removed shortcut"
        }
    }

    /// Sources that will not form a loop back to `shortcut`.
    func availableSources(for shortcut: Shortcut) -> [Shortcut.Source] {
        var options: [Shortcut.Source] = [.own, .main]
        for other in shortcuts where other.id != shortcut.id {
            if !leadsBack(from: other.id, to: shortcut.id) {
                options.append(.shortcut(other.id))
            }
        }
        return options
    }

    private func leadsBack(from start: UUID, to target: UUID, depth: Int = 0) -> Bool {
        guard depth < shortcuts.count + 1, let node = shortcut(start) else { return false }
        if case .shortcut(let next) = node.source {
            return next == target || leadsBack(from: next, to: target, depth: depth + 1)
        }
        return false
    }

    /// Numbering starts at two, since the stock app is the first one.
    func uniqueName(base: String = "Claude") -> String {
        var n = 2
        var candidate = "\(base) \(n)"
        while shortcuts.contains(where: { $0.name == candidate }) {
            n += 1
            candidate = "\(base) \(n)"
        }
        return candidate
    }
}
