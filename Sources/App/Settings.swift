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
/// An update is the other exception, and it was learned the hard way: Sparkle
/// swaps the bundle and starts the new copy by asking the app to terminate.
/// Refusing that left the old version running, the update never came up, and
/// the window was put away on the way past — which read as the app quitting
/// itself on launch and never coming back with a window again.
///
/// The last clause is the way out: with nothing in the bar to go back to,
/// cancelling a quit would leave no window, no item, and no way to stop the app
/// but Force Quit.
/// Whether a status item is somewhere a pointer can reach it.
///
/// macOS drops menu bar items it has no room for — a full bar, and a notch
/// taking the middle of it — without telling the app. The item is still an
/// object, and it still answers `isVisible` true; what gives it away is where
/// it was put. Filling the bar with throwaway items until they overflowed put
/// the last four at x of -71, -115, -160 and -204, which no pointer reaches.
///
/// Horizontal only, on purpose: the bar sits above `NSScreen.frame`, so asking
/// whether a screen contains the whole item rejects every item there is.
///
/// The notch is the other place an item goes unseen, and it is inside the
/// screen. A new conversation's text widened Graft's item to x 866–1054 on a
/// MacBook Air whose notch covers 751–959. The item was on the screen by every
/// other measure, but behind the notch, where no pointer reaches it. So any
/// overlap with a notch counts as out of reach.
enum MenuBarPlacement {
    static func isReachable(_ frame: CGRect, onAnyOf screens: [CGRect], notches: [CGRect] = []) -> Bool {
        screens.contains { frame.minX >= $0.minX && frame.maxX <= $0.maxX }
            && !notches.contains { frame.intersects($0) }
    }

    /// The part of the bar between the two clear corners at the top of the
    /// screen, in global coordinates, or nil for a screen without a notch.
    static func notch(of screen: NSScreen) -> CGRect? {
        guard let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea,
              right.minX > left.maxX else { return nil }
        let gap = CGRect(x: left.maxX, y: right.minY, width: right.minX - left.maxX, height: right.height)
        // The only notched screen measured sat at the origin, which cannot
        // say whether these corners are global or the screen's own. A gap
        // lying outside its screen must be the screen's own, so it is moved
        // onto the screen.
        return screen.frame.intersects(gap) ? gap
            : gap.offsetBy(dx: screen.frame.minX, dy: screen.frame.minY)
    }
}

enum QuitPolicy {
    static func endsTheApp(askedFor: Bool,
                           installingUpdate: Bool,
                           systemGoingDown: Bool,
                           menuBarShowing: Bool) -> Bool {
        askedFor || installingUpdate || systemGoingDown || !menuBarShowing
    }
}

/// When the window may go away and take the Dock icon with it.
///
/// With the window closed and the app an accessory, the status item is the
/// only way back in. So the setting is not enough: an item the bar had no room
/// for, or one sitting behind the notch, leaves an app with nothing to click,
/// and the only way to reach it again was to open it from Finder.
enum WindowPolicy {
    static func hidesDockIcon(showInMenuBar: Bool, hasShownWindow: Bool,
                              hasWindow: Bool, menuBarShowing: Bool) -> Bool {
        showInMenuBar && hasShownWindow && !hasWindow && menuBarShowing
    }

    /// A login launch closes its window before the status item has been
    /// placed, so reachability is asked again once the bar has had a moment.
    static let placementGrace: TimeInterval = 3

    static func showsWindowAfterHiddenLaunch(menuBarShowing: Bool, hasWindow: Bool) -> Bool {
        !menuBarShowing && !hasWindow
    }
}

/// What holding Graft's own updates changes, and what it leaves alone.
///
/// Off, which is where every copy starts and where every copy already out there
/// stays, an update is found on Sparkle's schedule and installs itself. On, the
/// schedule is Graft's instead: an hourly probe that learns a version exists
/// and fetches nothing, so the only thing that can start a download is a person
/// pressing the line that names it. A press installs without a further
/// question either way — holding is about when, not about being asked twice.
enum UpdatePolicy {
    /// Sparkle's own timer, which downloads and installs whatever it finds.
    static func sparkleChecksOnItsOwn(holding: Bool) -> Bool { !holding }

    /// The status item only says so while an update is waiting on somebody.
    /// Without a hold the version is known for the seconds before it installs
    /// itself, and an icon changing for that long is noise.
    static func showsInStatusItem(holding: Bool, available: String?) -> Bool {
        holding && available != nil
    }
}

/// The handful of preferences the menu bar exposes. Stored in UserDefaults,
/// except for the login item, which is state macOS owns.
final class AppSettings: ObservableObject {
    private enum Key {
        static let menuBar = "showInMenuBar"
        static let seeded = "defaultsSeeded"
        static let holdUpdates = "holdUpdates"
        static let frontIcon = "showFrontIcon"
        static let frontName = "showFrontName"
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

    /// Off unless somebody turns it on. Read with a plain `bool(forKey:)` on
    /// purpose: a key nobody has written is every machine that installed this
    /// release by updating, and those must go on updating exactly as before.
    @Published var holdUpdates: Bool {
        didSet { defaults.set(holdUpdates, forKey: Key.holdUpdates) }
    }

    /// On unless somebody turns it off, an update included, so a key nobody
    /// has written reads as on. It widens the item too, but by one icon, and
    /// only while a Claude is in front — the moment somebody running several
    /// of them needs to know which one it is.
    @Published var showFrontIcon: Bool {
        didSet { defaults.set(showFrontIcon, forKey: Key.frontIcon) }
    }

    /// Off unless somebody turns it on. The name can widen the item six times
    /// as far as the icon does, and a bar with no room left drops an item
    /// rather than squeezing it, so nobody's usage figure disappears because of
    /// a release they did not ask this of.
    @Published var showFrontName: Bool {
        didSet { defaults.set(showFrontName, forKey: Key.frontName) }
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
        holdUpdates = defaults.bool(forKey: Key.holdUpdates)
        showFrontIcon = defaults.object(forKey: Key.frontIcon) as? Bool ?? true
        showFrontName = defaults.bool(forKey: Key.frontName)
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
            return L10n.text("Opening at login needs macOS 13 or later.")
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
                return L10n.text("macOS needs this approved in System Settings › General › Login Items.")
            }
            return error.localizedDescription
        }
    }

    func refreshLoginItem() {
        openAtLogin = AppSettings.loginItemEnabled
    }
}
