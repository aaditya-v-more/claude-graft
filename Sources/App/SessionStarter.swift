import Foundation

/// Opens a five-hour window by sending one cheap message through Claude Code's
/// command line — no window, no keystrokes, no permission prompts.
///
/// It uses the account Claude Code itself is signed into. That is the only set
/// of credentials reachable from outside Claude: the desktop keeps each
/// profile's token encrypted in that profile, and the command line keeps one
/// login in the keychain for the whole machine. So this cannot be aimed at a
/// chosen shortcut's account, and the interface says so rather than pretending.
enum SessionStarter {
    enum Failure: LocalizedError {
        case notInstalled
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .notInstalled:
                return """
                Claude Code's command line was not found. Install it, or sign in \
                with `claude` once, and this will work.
                """
            case .failed(let detail):
                return detail.isEmpty
                    ? "Claude Code could not start a session."
                    : "Claude Code could not start a session: \(detail)"
            }
        }
    }

    /// Where the command line usually lands, in the order worth trying.
    static let searchPaths = [
        "/opt/homebrew/bin/claude",
        "/usr/local/bin/claude",
        NSHomeDirectory() + "/.local/bin/claude",
        NSHomeDirectory() + "/.claude/local/claude",
        "/usr/bin/claude",
    ]

    static var executable: String? {
        searchPaths.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static var isAvailable: Bool { executable != nil }

    /// Blocking; call it off the main thread. Nil means the message went.
    static func start(prompt: String = "hi", model: String = "haiku") -> Failure? {
        guard let executable else { return .notInstalled }
        let output = Graft.output(executable, ["-p", prompt, "--model", model])
        return output.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? .failed("")
            : nil
    }
}
