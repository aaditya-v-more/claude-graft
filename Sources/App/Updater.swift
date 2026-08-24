import AppKit
import Combine
import Sparkle

/// In-app updates, through Sparkle, and they look after themselves.
///
/// A new version is found on a schedule, downloaded, installed and started
/// without anyone being asked. That is a deliberate choice for something that
/// lives in the menu bar: there is usually no window to put a question in
/// front of, and an update that waits to be noticed is one that never gets
/// installed. The visible cost is the app disappearing and coming back, which
/// for a status item is a flicker.
///
/// What makes it possible at all is `applicationShouldTerminate` letting the
/// update through. Sparkle starts the new version by ending this one, and this
/// app refuses a terminate nobody asked for. Refusing that is what left 1.0.0
/// running with 1.0.1 already staged, so the flag is set at every hook Sparkle
/// offers before it installs — `updaterWillRelaunchApplication` alone is not
/// enough, its own header says it may not be called.
@MainActor
final class Updater: NSObject, ObservableObject {
    /// The version a check found, in the moment before it installs itself.
    @Published private(set) var availableVersion: String?
    /// False while a check is running, so the menu line can dim rather than
    /// stacking a second check on the first.
    @Published private(set) var canCheck = false

    /// Read by `applicationShouldTerminate`, which is the only thing standing
    /// between Sparkle and the new version starting. Nothing clears it: by the
    /// time it is set, the process is on its way out.
    nonisolated(unsafe) private(set) static var isRelaunchingForUpdate = false

    /// Checked this often, and at launch when this much has passed since the
    /// last one — so a Mac that only ever runs Graft as a login item still
    /// keeps up.
    static let checkInterval: TimeInterval = 60 * 60

    private var controller: SPUStandardUpdaterController?
    private var watch: AnyCancellable?

    /// `Shared` builds this before anything is on the main actor's books, so
    /// construction has to be allowed from outside it. Nothing here touches
    /// Sparkle — `start()` does, and that is called from the main thread.
    nonisolated override init() { super.init() }

    /// Nothing starts until this is called, so the tests can build one without
    /// a bundle to read a feed URL out of.
    func start() {
        guard controller == nil else { return }
        let controller = SPUStandardUpdaterController(startingUpdater: true,
                                                     updaterDelegate: self,
                                                     userDriverDelegate: nil)
        self.controller = controller

        // Set every launch rather than seeded once: this is how the app
        // behaves, not a default someone was offered and might have changed.
        controller.updater.automaticallyChecksForUpdates = true
        controller.updater.automaticallyDownloadsUpdates = true
        controller.updater.updateCheckInterval = Self.checkInterval

        canCheck = controller.updater.canCheckForUpdates
        watch = controller.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.canCheck = $0 }
    }

    /// The dropdown's Check for Updates. Everything else happens on its own.
    func checkForUpdates() {
        guard let controller else { return }
        // A dockless app that just orders a panel front puts it behind whatever
        // is already there.
        NSApp.activate(ignoringOtherApps: true)
        controller.updater.checkForUpdates()
    }
}

extension Updater: SPUUpdaterDelegate {
    nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        Task { @MainActor in self.availableVersion = item.displayVersionString }
    }

    /// The earliest hook before the bundle is replaced.
    nonisolated func updater(_ updater: SPUUpdater, willInstallUpdate item: SUAppcastItem) {
        Updater.isRelaunchingForUpdate = true
    }

    nonisolated func updaterWillRelaunchApplication(_ updater: SPUUpdater) {
        Updater.isRelaunchingForUpdate = true
    }

    /// Sparkle would rather wait for a quit that may never come — Graft is
    /// meant to sit in the menu bar for weeks. Taking responsibility here and
    /// calling the handler installs it now instead.
    nonisolated func updater(_ updater: SPUUpdater,
                             willInstallUpdateOnQuit item: SUAppcastItem,
                             immediateInstallationBlock immediateInstallHandler: @escaping () -> Void) -> Bool {
        Updater.isRelaunchingForUpdate = true
        immediateInstallHandler()
        return true
    }

    nonisolated func updaterShouldRelaunchApplication(_ updater: SPUUpdater) -> Bool { true }
}
