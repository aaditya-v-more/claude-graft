import Foundation

/// Launchers outlive the menu bar app. A file lock gives them the same answer
/// about an update in progress and is released even if its owner crashes.
enum ClaudeUpdateGate {
    static var file: URL { Graft.applicationSupport.appending(path: "ClaudeGraft/desktop-update.lock") }

    enum Failure: LocalizedError {
        case busy, unavailable
        var errorDescription: String? {
            switch self {
            case .busy: return L10n.text("Claude is updating. Wait for the update to finish before opening a shortcut.")
            case .unavailable: return L10n.text("Claude's update lock could not be opened.")
            }
        }
    }

    final class Lease {
        private let descriptor: Int32
        fileprivate init(_ descriptor: Int32) { self.descriptor = descriptor }
        deinit { flock(descriptor, LOCK_UN); close(descriptor) }
    }

    static func acquire() throws -> Lease {
        try Graft.fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        let descriptor = open(file.path, O_RDWR | O_CREAT | O_NOFOLLOW, 0o600)
        guard descriptor >= 0 else { throw Failure.unavailable }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            close(descriptor)
            throw Failure.busy
        }
        return Lease(descriptor)
    }

    static func requireLaunchAllowed() throws {
        guard Graft.exists(file) else { return }
        let descriptor = open(file.path, O_RDONLY | O_NOFOLLOW)
        guard descriptor >= 0 else { throw Failure.unavailable }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_SH | LOCK_NB) == 0 else { throw Failure.busy }
        flock(descriptor, LOCK_UN)
    }
}
