import AppKit
import Combine
import Sparkle

/// In-app updates, through Sparkle.
///
/// Two things shape this. A scheduled check must not throw a panel over
/// whatever is in front: Graft usually has no window of its own open, so an
/// update window arriving on its own belongs to no visible app and interrupts
/// something else's work. And a window shown deliberately has to be asked for
/// first — a dockless app that just orders a panel front puts it behind
/// whatever is already there.
///
/// So a background check that finds something says so in the dropdown and stops
/// there. The panel appears when someone presses the line offering it, which is
/// the same rule the keychain prompt follows.
@MainActor
final class Updater: NSObject, ObservableObject {
    /// The version a background check found, if any. What the dropdown offers.
    @Published private(set) var availableVersion: String?
    /// False while a check is already running, so the menu line can dim rather
    /// than stacking a second check on the first.
    @Published private(set) var canCheck = false

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
                                                     userDriverDelegate: self)
        self.controller = controller
        canCheck = controller.updater.canCheckForUpdates
        watch = controller.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.canCheck = $0 }
    }

    /// Only ever from something a person pressed.
    func checkForUpdates() {
        guard let controller else { return }
        NSApp.activate(ignoringOtherApps: true)
        availableVersion = nil
        controller.updater.checkForUpdates()
    }

    var automaticallyChecks: Bool {
        get { controller?.updater.automaticallyChecksForUpdates ?? false }
        set { controller?.updater.automaticallyChecksForUpdates = newValue }
    }
}

extension Updater: SPUUpdaterDelegate {
    nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        Task { @MainActor in self.availableVersion = item.displayVersionString }
    }
}

extension Updater: SPUStandardUserDriverDelegate {
    /// Saying yes here is what buys the right to answer no below. Sparkle only
    /// lets a scheduled update be handled quietly if something has claimed it
    /// will be shown some other way.
    nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

    /// No, unless the app is already in front. A check nobody asked for gets a
    /// line in the dropdown; it does not get to interrupt.
    nonisolated func standardUserDriverShouldHandleShowingScheduledUpdate(
        _ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        immediateFocus
    }

    /// Sparkle is taking over the telling, so the dropdown stops offering it and
    /// the two cannot disagree about what is on screen.
    nonisolated func standardUserDriverWillHandleShowingUpdate(
        _ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
        state: SPUUserUpdateState) {
        guard handleShowingUpdate else { return }
        Task { @MainActor in self.availableVersion = nil }
    }
}
