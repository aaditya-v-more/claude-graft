import AppKit
import ApplicationServices
import Foundation

/// Starts a profile's five-hour window by sending it a one-word message.
///
/// There is no way to do this behind the scenes. Claude Desktop keeps each
/// account's token encrypted in its own profile, and Claude Code's command line
/// holds a single set of credentials in the keychain for the whole machine, so
/// an outside process cannot make an authenticated request as a chosen account
/// without prising those credentials out. Driving the instance that is already
/// signed in is the honest route: bring its window up and type into it.
enum SessionStarter {
    enum Failure: LocalizedError {
        case needsAccessibility
        case didNotStart
        case scriptFailed

        var errorDescription: String? {
            switch self {
            case .needsAccessibility:
                return """
                Claude Graft needs Accessibility permission to type into another \
                app. Grant it in System Settings › Privacy & Security › \
                Accessibility, then try again.
                """
            case .didNotStart:
                return "That Claude did not come up in time."
            case .scriptFailed:
                return "Could not reach that Claude's window. Bring it to the front and send a message yourself."
            }
        }
    }

    static var hasAccessibility: Bool { AXIsProcessTrusted() }

    /// Shows the system prompt once, so the user has somewhere to say yes.
    static func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    /// Blocking; call it off the main thread. Returns nil when the message went.
    static func start(profile: URL, bundle: URL?, prompt: String = "hi") -> Failure? {
        guard hasAccessibility else { return .needsAccessibility }

        if Graft.processIDs(of: profile).isEmpty {
            if let bundle {
                NSWorkspace.shared.openApplication(at: bundle, configuration: NSWorkspace.OpenConfiguration())
            } else {
                Graft.launch(profile: profile)
            }
        }

        // Electron takes a moment to put a window up, and typing before then
        // goes nowhere.
        var pid: Int32?
        for _ in 0..<30 {
            if let found = Graft.processIDs(of: profile).first { pid = found; break }
            Thread.sleep(forTimeInterval: 0.5)
        }
        guard let pid else { return .didNotStart }
        Thread.sleep(forTimeInterval: 2.5)

        let escaped = prompt.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        let script = """
        tell application "System Events"
            set target to first process whose unix id is \(pid)
            set frontmost of target to true
            delay 1.0
            keystroke "\(escaped)"
            delay 0.4
            key code 36
        end tell
        """
        return Graft.runTool("/usr/bin/osascript", ["-e", script]) == 0 ? nil : .scriptFailed
    }
}
