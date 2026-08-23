import AppKit
import SwiftUI

@main
struct ClaudeGraftApp: App {
    static let mainWindowID = "graft-main"

    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    /// Held outside the App value. Observing them here would invalidate the
    /// whole scene on every usage poll, and rebuilding the scene rebuilds the
    /// main menu, which invalidates it again.
    private var store: ShortcutStore { Shared.store }
    private var settings: AppSettings { Shared.settings }
    private var usage: UsageMonitor { Shared.usage }

    var body: some Scene {
        WindowGroup("Claude Graft", id: Self.mainWindowID) {
            ContentView()
                .environmentObject(store)
                .environmentObject(settings)
                .environmentObject(usage)
                .frame(minWidth: 720, minHeight: 460)
                .onAppear {
                    // Not while starting hidden: the delegate is about to close
                    // this window, and showing a Dock icon would undo that.
                    if !Shared.startHidden { NSApp.setActivationPolicy(.regular) }
                }
        }
        .defaultSize(width: 820, height: 560)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView()
                .environmentObject(settings)
        }
    }
}

/// Long-lived objects, kept off the App value so scene rebuilds cannot
/// multiply them or be triggered by them.
enum Shared {
    /// Set at launch when the window was closed the last time the app quit.
    static var startHidden = false
    static let store = ShortcutStore()
    static let settings = AppSettings()
    static let usage = UsageMonitor()
}

struct SettingsView: View {
    @EnvironmentObject private var settings: AppSettings
    @State private var problem: String?

    var body: some View {
        Form {
            Section {
                Toggle("Open at Login", isOn: Binding(
                    get: { settings.openAtLogin },
                    set: { problem = settings.setOpenAtLogin($0) }))
                Toggle("Show in Menu Bar", isOn: $settings.showInMenuBar)
            } footer: {
                Text(problem ?? """
                     The menu bar item keeps reporting usage after this window is \
                     closed. Quit it from the menu bar itself.
                     """)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
            }
        }
        .formStyle(.grouped)
        .frame(width: 420)
        .onAppear { settings.refreshLoginItem() }
    }
}

/// Closing the window is not quitting: the status item carries on. The Dock
/// icon goes with the window so it behaves like the utility it is.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var menuBar: MenuBarController?
    /// Guards against hiding the app before its first window ever appears.
    private var hasShownWindow = false

    func applicationWillFinishLaunching(_ notification: Notification) {
        let wantsWindow = UserDefaults.standard.object(forKey: Self.windowOpenKey) as? Bool ?? true
        Shared.startHidden = !wantsWindow && Shared.settings.showInMenuBar
        Shared.usage.start(watching: Shared.store)
        menuBar = MenuBarController(store: Shared.store,
                                    settings: Shared.settings,
                                    usage: Shared.usage,
                                    openMainWindow: { [weak self] in self?.showMainWindow() })
        watchForWindowClose()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    /// Comes back the way it was left. Closing the window and quitting means
    /// the next launch — a login item among them — stays in the menu bar
    /// instead of putting a window in front of whatever is being done.
    ///
    /// The launch notification does carry a "was this a default launch" flag,
    /// but it reads false for an ordinary `open` from a shell too, so it cannot
    /// tell a login launch apart from a deliberate one.
    func applicationDidFinishLaunching(_ notification: Notification) {
        guard Shared.startHidden else { return }
        DispatchQueue.main.async {
            NSApp.windows.filter(\.canBecomeMain).forEach { $0.close() }
            NSApp.setActivationPolicy(.accessory)
            // Everything after launch behaves normally.
            Shared.startHidden = false
        }
    }

    static let windowOpenKey = "mainWindowOpen"

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        NSApp.setActivationPolicy(.regular)
        return true
    }

    func showMainWindow() {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { $0.canBecomeMain }) {
            window.makeKeyAndOrderFront(nil)
        } else {
            // Nothing left to raise: a reopen makes SwiftUI build one again.
            NSWorkspace.shared.open(Bundle.main.bundleURL)
        }
    }

    /// Only when a window actually closes. Doing this on resign-active hid the
    /// app the moment it launched behind something else.
    private func watchForWindowClose() {
        NotificationCenter.default.addObserver(forName: NSWindow.didBecomeMainNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            guard !Shared.startHidden else { return }
            self?.hasShownWindow = true
            UserDefaults.standard.set(true, forKey: Self.windowOpenKey)
        }
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            DispatchQueue.main.async { self?.hideDockIconIfWindowless() }
        }
    }

    private func hideDockIconIfWindowless() {
        guard Shared.settings.showInMenuBar, hasShownWindow else { return }
        let hasWindow = NSApp.windows.contains {
            $0.isVisible && $0.canBecomeMain && !$0.isMiniaturized
        }
        if !hasWindow {
            UserDefaults.standard.set(false, forKey: Self.windowOpenKey)
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
