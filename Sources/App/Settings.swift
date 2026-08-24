import AppKit
import Foundation
import ServiceManagement

/// Whether a terminate really means terminate.
///
/// ⌘Q is the reflex for putting a window away, and it used to take the menu bar
/// item with it — the part of Graft that does the reporting, and the part
/// nobody asked to close. So only Quit in the dropdown ends the app, along with
/// macOS on its way to a logout, which stalls on a dialog if it is refused.
///
/// The last clause is the way out: with nothing in the bar to go back to,
/// cancelling a quit would leave no window, no item, and no way to stop the app
/// but Force Quit.
enum QuitPolicy {
    static func endsTheApp(askedFor: Bool, systemGoingDown: Bool, menuBarShowing: Bool) -> Bool {
        askedFor || systemGoingDown || !menuBarShowing
    }
}

/// The handful of preferences the menu bar exposes. Stored in UserDefaults,
/// except for the login item, which is state macOS owns.
final class AppSettings: ObservableObject {
    private enum Key {
        static let menuBar = "showInMenuBar"
        static let seeded = "defaultsSeeded"
    }

    private let defaults: UserDefaults

    @Published var showInMenuBar: Bool {
        didSet {
            defaults.set(showInMenuBar, forKey: Key.menuBar)
            // Without the menu bar item and without a window, there would be
            // nothing left to click — not even a way to quit.
            if !showInMenuBar { NSApp?.setActivationPolicy(.regular) }
        }
    }

    /// Reflects what macOS reports, not a value of our own.
    @Published private(set) var openAtLogin: Bool

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // On by default, but a later change to false has to survive a restart,
        // hence a marker rather than a plain `bool(forKey:)`.
        if defaults.bool(forKey: Key.seeded) == false {
            defaults.set(true, forKey: Key.seeded)
            defaults.set(true, forKey: Key.menuBar)
        }
        showInMenuBar = defaults.bool(forKey: Key.menuBar)
        openAtLogin = AppSettings.loginItemEnabled
    }

    // MARK: - Login item

    static var loginItemEnabled: Bool {
        guard #available(macOS 13, *) else { return false }
        return SMAppService.mainApp.status == .enabled
    }

    /// Returns a message when macOS refused, so the caller can say why.
    @discardableResult
    func setOpenAtLogin(_ wanted: Bool) -> String? {
        guard #available(macOS 13, *) else {
            return "Opening at login needs macOS 13 or later."
        }
        do {
            if wanted {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
            } else if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            }
            openAtLogin = SMAppService.mainApp.status == .enabled
            return nil
        } catch {
            openAtLogin = SMAppService.mainApp.status == .enabled
            if SMAppService.mainApp.status == .requiresApproval {
                return "macOS needs this approved in System Settings › General › Login Items."
            }
            return error.localizedDescription
        }
    }

    func refreshLoginItem() {
        openAtLogin = AppSettings.loginItemEnabled
    }
}
