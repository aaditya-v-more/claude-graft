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

    /// Where a shortcut actually reads its chats from, following one hop.
    func sourceDir(for shortcut: Shortcut) -> URL? {
        switch shortcut.source {
        case .own: return nil
        case .main: return Graft.mainProfile
        case .shortcut(let id): return self.shortcut(id)?.profileDir
        }
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
