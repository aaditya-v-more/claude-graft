import Foundation

/// What was dictated, for somebody who asked for it to be kept: the words and
/// when, on this Mac, and never the sound. notes/dictation.md says what it
/// leaves out.
final class DictationHistory: ObservableObject {
    struct Entry: Codable, Equatable, Identifiable {
        var id = UUID()
        var at: Date
        var words: String
    }

    /// How many may be kept; none is the setting nobody has touched.
    static let sizes = [10, 50, 200, 1000]

    /// Newest first.
    @Published private(set) var entries: [Entry]
    private let file: URL

    static var file: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/dictation-history.json") }

    init(file: URL = DictationHistory.file) {
        self.file = file
        entries = (try? Data(contentsOf: file))
            .flatMap { try? JSONDecoder().decode([Entry].self, from: $0) } ?? []
    }

    func add(_ words: String, keeping limit: Int, at: Date = Date()) {
        guard limit > 0, !words.isEmpty else { return }
        entries.insert(Entry(at: at, words: words), at: 0)
        keep(limit)
    }

    /// Also how it is switched off: keeping none leaves no file behind.
    func keep(_ limit: Int) {
        let limit = max(0, limit)
        if entries.count > limit { entries.removeLast(entries.count - limit) }
        save()
    }

    func remove(_ id: UUID) {
        entries.removeAll { $0.id == id }
        save()
    }

    private func save() {
        let manager = FileManager.default
        guard !entries.isEmpty else {
            try? manager.removeItem(at: file)
            return
        }
        try? manager.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(entries) else { return }
        try? data.write(to: file, options: .atomic)
        // Somebody's own words: nobody else on this Mac reads them.
        try? manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
