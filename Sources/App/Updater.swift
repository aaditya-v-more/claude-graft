import AppKit
import Combine
import Sparkle

/// In-app updates, through Sparkle, and by default they look after themselves.
///
/// A new version is found on a schedule, downloaded, installed and started
/// without anyone being asked. That is a deliberate choice for something that
/// lives in the menu bar: there is usually no window to put a question in
/// front of, and an update that waits to be noticed is one that never gets
/// installed. The visible cost is the app disappearing and coming back, which
/// for a status item is a flicker.
///
/// Holding updates is the one way round that, and it is off unless somebody
/// turns it on. `UpdatePolicy` says what it changes. What it does not change is
/// the install itself: a press goes down exactly the path the schedule would
/// have taken, so there is still only one way a new version arrives.
///
/// What makes it possible at all is `applicationShouldTerminate` letting the
/// update through. Sparkle starts the new version by ending this one, and this
/// app refuses a terminate nobody asked for. Refusing that is what left 1.0.0
/// running with 1.0.1 already staged, so the flag is set at every hook Sparkle
/// offers before it installs — `updaterWillRelaunchApplication` alone is not
/// enough, its own header says it may not be called.
@MainActor
final class Updater: NSObject, ObservableObject {
    /// The version a check found. Without a hold, only in the moment before
    /// it installs itself; with one, until somebody presses it.
    @Published private(set) var availableVersion: String?
    /// False while a check is running, so the menu line can dim rather than
    /// stacking a second check on the first.
    @Published private(set) var canCheck = false
    @Published private(set) var isHolding = false

    /// Read by `applicationShouldTerminate`, which is the only thing standing
    /// between Sparkle and the new version starting. Nothing clears it: by the
    /// time it is set, the process is on its way out.
    nonisolated(unsafe) private(set) static var isRelaunchingForUpdate = false

    /// Checked this often, and at launch when this much has passed since the
    /// last one — so a Mac that only ever runs Graft as a login item still
    /// keeps up. A hold probes on the same interval.
    static let checkInterval: TimeInterval = 60 * 60

    private var controller: SPUStandardUpdaterController?
    private var watches = Set<AnyCancellable>()
    private var probe: Timer?

    /// `Shared` builds this before anything is on the main actor's books, so
    /// construction has to be allowed from outside it. Nothing here touches
    /// Sparkle — `start()` does, and that is called from the main thread.
    nonisolated override init() { super.init() }

    /// Nothing starts until this is called, so the tests can build one without
    /// a bundle to read a feed URL out of.
    func start(holding settings: AppSettings) {
        guard controller == nil else { return }
        // Configured before it starts rather than after: Sparkle schedules
        // its launch check as it starts, and a hold that arrived a moment late
        // would let that check download and install the very thing being held.
        let controller = SPUStandardUpdaterController(startingUpdater: false,
                                                     updaterDelegate: self,
                                                     userDriverDelegate: nil)
        self.controller = controller

        // Set every launch rather than seeded once: this is how the app
        // behaves, not a default someone was offered and might have changed.
        // Downloading stays on under a hold too, since the only check a hold
        // lets through to Sparkle is a press asking for the install.
        controller.updater.automaticallyDownloadsUpdates = true
        controller.updater.updateCheckInterval = Self.checkInterval
        apply(holding: settings.holdUpdates)
        controller.startUpdater()
        if isHolding { lookForUpdate() }

        canCheck = controller.updater.canCheckForUpdates
        controller.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.canCheck = $0 }
            .store(in: &watches)

        settings.$holdUpdates
            .dropFirst()
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] holding in
                self?.apply(holding: holding)
                if holding { self?.lookForUpdate() }
            }
            .store(in: &watches)
    }

    /// Sparkle's own timer downloads whatever it finds, so a hold turns it off
    /// and keeps a timer of its own that only ever asks what exists. Turning a
    /// hold off hands the schedule back, and Sparkle resets its cycle on the
    /// change by itself.
    private func apply(holding: Bool) {
        isHolding = holding
        controller?.updater.automaticallyChecksForUpdates =
            UpdatePolicy.sparkleChecksOnItsOwn(holding: holding)
        probe?.invalidate()
        probe = nil
        guard holding else { return }
        probe = Timer.scheduledTimer(withTimeInterval: Self.checkInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.lookForUpdate() }
        }
    }

    /// A probe: the feed is read and the delegate told what it holds, and
    /// nothing is downloaded or offered.
    private func lookForUpdate() {
        controller?.updater.checkForUpdateInformation()
    }

    /// The dropdown's Check for Updates. Under a hold it looks and reports,
    /// since a check is not the press a hold is waiting for.
    ///
    /// Otherwise `checkForUpdatesInBackground` rather than `checkForUpdates`,
    /// which is the difference between an update installing and an update
    /// asking. The interactive one puts up Sparkle's own panel and waits on
    /// Install and Relaunch — so the scheduled check installed silently,
    /// exactly as intended, while pressing the button in the dropdown did not,
    /// which is a strange thing for the same app to do two ways.
    func checkForUpdates() {
        if isHolding { lookForUpdate() } else { install() }
    }

    /// The dropdown's "version N is available" line. Pressing a line that
    /// already names the version is not a request to be asked whether you
    /// want it, held or not: this is the same background check the schedule
    /// runs, and it downloads, installs and restarts.
    ///
    /// Nothing is brought to the front either: there is no panel to order
    /// forward, and stealing focus for something invisible is worse than
    /// doing nothing.
    func install() {
        guard let controller else { return }
        controller.updater.checkForUpdatesInBackground()
    }
}

extension Updater: SPUUpdaterDelegate {
    nonisolated func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        Task { @MainActor in self.availableVersion = item.displayVersionString }
    }

    /// A version that was offered and has since been pulled from the feed is
    /// not one to go on offering.
    nonisolated func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        Task { @MainActor in self.availableVersion = nil }
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
    /// calling the handler installs it now instead. Under a hold this is only
    /// reached from a press, because nothing else downloads.
    nonisolated func updater(_ updater: SPUUpdater,
                             willInstallUpdateOnQuit item: SUAppcastItem,
                             immediateInstallationBlock immediateInstallHandler: @escaping () -> Void) -> Bool {
        Updater.isRelaunchingForUpdate = true
        immediateInstallHandler()
        return true
    }

    nonisolated func updaterShouldRelaunchApplication(_ updater: SPUUpdater) -> Bool { true }
}
