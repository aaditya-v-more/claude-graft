import AppKit
import AVFoundation
import Combine
import SwiftUI

/// Dictation into whatever app has the cursor: a key held to speak, or tapped
/// twice to go on until it is pressed again.
///
/// The speech service is the one Claude's own dictation uses, asked with a
/// login borrowed from a Claude here the way the usage figures are. Sound
/// leaves this Mac only while a recording is on screen, and goes to Anthropic
/// alone. notes/dictation.md has what was measured and what is still unproven.
final class DictationController: ObservableObject {
    enum Phase: Equatable {
        case idle
        case listening(handsFree: Bool)
        case transcribing
        case notice(String)
    }

    /// What macOS has yet to allow before a press can do anything.
    enum Need: Equatable {
        case accessibility
        case microphone
    }

    @Published private(set) var phase = Phase.idle {
        didSet { if phase != oldValue { panel.show(phase != .idle) } }
    }
    @Published private(set) var level: Float = -120
    @Published private(set) var secondsLeft: Int?
    @Published private(set) var preview = ""
    @Published private(set) var need: Need?
    private(set) var key = DictationKey.rightCommand

    /// One recording. Its sound and its stream belong to `work`; the rest is
    /// the main thread's.
    private final class Recording {
        let began = ProcessInfo.processInfo.systemUptime
        let microphone = DictationMicrophone()
        var isCommitted = false
        var isEnding = false
        var transcript = DictationTranscript()
        var quiet = DictationQuiet(startedAt: ProcessInfo.processInfo.systemUptime)
        var microphoneFailure: String?
        /// The app in front when the recording ended, which is where its words belong.
        var app: pid_t?
        var awake: NSObjectProtocol?

        var stream: DictationStream?
        var backlog = Data()
        var wantsClose = false
        var isOver = false
    }

    private enum Refusal {
        case notice(String)
    }

    private weak var settings: AppSettings?
    private var gesture = DictationGesture()
    private var recording: Recording?
    private var refusal: Refusal?
    private var isDelivering = false
    private var lastTyped: (app: pid_t?, at: Date)?
    private var monitors: [Any] = []
    private var watching: Set<AnyCancellable> = []
    private var clock: Timer?
    private var trustWatch: Timer?
    private var missedReleases = 0
    private var noticeNumber = 0
    private let work = DispatchQueue(label: "graft.dictation", qos: .userInitiated)
    private lazy var panel = DictationPanel(dictation: self)

    private var now: TimeInterval { ProcessInfo.processInfo.systemUptime }

    func start(settings: AppSettings) {
        self.settings = settings
        key = DictationKey.key(code: settings.dictationKey)
        // Published values arrive before they are stored, hence the hop.
        settings.$dictationEnabled.dropFirst().removeDuplicates().sink { [weak self] on in
            DispatchQueue.main.async { self?.switched(on: on) }
        }.store(in: &watching)
        settings.$dictationKey.dropFirst().removeDuplicates().sink { [weak self] code in
            DispatchQueue.main.async { self?.rekey(code) }
        }.store(in: &watching)

        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification,
                     NSWorkspace.sessionDidResignActiveNotification] {
            workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.end(.away, fromKey: false)
            }
        }
        DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name("com.apple.screenIsLocked"), object: nil, queue: .main) { [weak self] _ in
            self?.end(.away, fromKey: false)
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            self?.refreshNeed()
        }
        if settings.dictationEnabled { listen() }
        refreshNeed()
    }

    /// Asked again whenever Settings is looked at: a grant made in System
    /// Settings tells nobody.
    func refreshNeed() {
        guard settings?.dictationEnabled == true else {
            need = nil
            return
        }
        if !DictationTyping.isTrusted {
            need = .accessibility
        } else if [.denied, .restricted].contains(DictationMicrophone.permission) {
            need = .microphone
        } else if DictationMicrophone.permission == .notDetermined {
            need = .microphone
            // Graft is in front, so somebody is there to read the request.
            if NSApp.isActive { DictationMicrophone.askPermission { [weak self] _ in self?.refreshNeed() } }
        } else {
            need = nil
        }
    }

    // MARK: - The key

    private func switched(on: Bool) {
        if on {
            // Both requests come from the switch, where there is somebody to
            // read them, and never from a key pressed in another app.
            if !DictationTyping.isTrusted { DictationTyping.askForTrust() }
            if DictationMicrophone.permission == .notDetermined {
                DictationMicrophone.askPermission { [weak self] _ in self?.refreshNeed() }
            }
            listen()
        } else {
            end(.switchedOff, fromKey: false)
            unlisten()
        }
        refreshNeed()
    }

    private func rekey(_ code: Int) {
        end(.switchedOff, fromKey: false)
        key = DictationKey.key(code: code)
        gesture = DictationGesture()
    }

    private func listen() {
        unlisten()
        let keys: NSEvent.EventTypeMask = [.flagsChanged, .keyDown, .keyUp, .leftMouseDown, .rightMouseDown,
                                           .otherMouseDown, .scrollWheel]
        if let global = NSEvent.addGlobalMonitorForEvents(matching: keys, handler: { [weak self] event in
            self?.pressed(event)
        }) { monitors.append(global) }
        if let local = NSEvent.addLocalMonitorForEvents(matching: keys, handler: { [weak self] event in
            self?.pressed(event)
            return event
        }) { monitors.append(local) }

        // macOS hands a monitor no keys until the app is trusted, and one put
        // in place before the grant may never be handed any.
        guard !DictationTyping.isTrusted else { return }
        let watch = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            guard DictationTyping.isTrusted else { return }
            self?.listen()
            self?.refreshNeed()
        }
        RunLoop.main.add(watch, forMode: .common)
        trustWatch = watch
    }

    private func unlisten() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        trustWatch?.invalidate()
        trustWatch = nil
    }

    private func pressed(_ event: NSEvent) {
        switch event.type {
        case .leftMouseDown, .rightMouseDown, .otherMouseDown:
            perform(gesture.otherKey(escape: false))
            return
        case .scrollWheel:
            // A flick goes on scrolling after the fingers have left.
            if event.momentumPhase.isEmpty { perform(gesture.otherKey(escape: false)) }
            return
        default:
            break
        }
        guard event.cgEvent?.getIntegerValueField(.eventSourceUserData) != DictationTyping.mark else { return }
        switch event.type {
        case .flagsChanged where key.isModifier && event.keyCode == key.code:
            perform(key.isDown(flags: event.modifierFlags.rawValue) ? gesture.press(at: now) : gesture.release(at: now))
        case .keyDown where !key.isModifier && event.keyCode == key.code:
            if !event.isARepeat { perform(gesture.press(at: now)) }
        case .keyUp where !key.isModifier && event.keyCode == key.code:
            perform(gesture.release(at: now))
        case .keyDown where !event.isARepeat:
            if event.keyCode == 53, recording?.isEnding == true { end(.escape, fromKey: true) }
            perform(gesture.otherKey(escape: event.keyCode == 53))
        default:
            break
        }
    }

    private func perform(_ actions: [DictationGesture.Action]) {
        for action in actions {
            switch action {
            case .begin: begin()
            case .commit: commit()
            case .lock: if recording != nil { phase = .listening(handsFree: true) }
            case .finish: end(.asked, fromKey: true)
            case .cancel(let reason): end(reason, fromKey: true)
            }
        }
        if gesture.state != .idle { startClock() }
    }

    // MARK: - A recording

    /// The microphone opens with the press, since people speak as they press.
    /// Anything that has to be said about why it cannot waits for `commit`:
    /// this key begins every shortcut that uses it too.
    private func begin() {
        refusal = nil
        guard recording == nil, !isDelivering else {
            gesture.ended()
            return
        }
        if DictationTyping.keyboardIsGuarded {
            refusal = .notice(L10n.text("A password field has the keyboard, so dictation waits until you leave it."))
            return
        }
        switch DictationMicrophone.permission {
        case .authorized: break
        case .notDetermined:
            refusal = .notice(L10n.text("Dictation needs the microphone. Open Settings in Claude Graft to allow it."))
            return
        default:
            refusal = .notice(L10n.text("Dictation needs the microphone. Allow Claude Graft in System Settings › Privacy & Security › Microphone."))
            return
        }

        let recording = Recording()
        self.recording = recording
        work.async { [weak self] in
            do {
                try recording.microphone.start(heard: { sound in self?.heard(sound, in: recording) },
                                               lost: { DispatchQueue.main.async { self?.microphoneLost(recording) } })
            } catch {
                let message = L10n.text("No microphone could be started for dictation.")
                DispatchQueue.main.async {
                    recording.microphoneFailure = message
                    if recording.isCommitted { self?.fail(recording, message) }
                }
            }
        }
    }

    private func commit() {
        if let refusal {
            self.refusal = nil
            gesture.ended()
            switch refusal {
            case .notice(let text): show(text)
            }
            return
        }
        guard let recording, !recording.isCommitted else { return }
        recording.isCommitted = true
        if let failure = recording.microphoneFailure {
            fail(recording, failure)
            return
        }
        preview = ""
        phase = .listening(handsFree: gesture.isHandsFree)
        // Read aloud hands-free, nothing touches the Mac to keep its screen on.
        recording.awake = ProcessInfo.processInfo.beginActivity(options: .idleDisplaySleepDisabled, reason: "Dictation")
        Diagnostics.note("dictation.began", ["key": key.name, "handsFree": gesture.isHandsFree])
        dial(for: recording)
    }

    private func heard(_ sound: Data, in recording: Recording) {
        work.async {
            guard !recording.isOver, !recording.wantsClose else { return }
            if let stream = recording.stream {
                stream.send(sound)
            } else {
                recording.backlog.append(sound)
                let over = recording.backlog.count - DictationLimits.backlog
                if over > 0 { recording.backlog.removeFirst(over) }
            }
        }
        let level = DictationSound.level(of: sound)
        DispatchQueue.main.async { [weak self] in
            guard let self, self.recording === recording, !recording.isEnding else { return }
            recording.quiet.heard(level: level, at: self.now)
            if recording.isCommitted { self.level = level }
        }
    }

    private func dial(for recording: Recording) {
        var profiles = [Graft.mainProfile] + Shared.store.shortcuts.map(\.profileDir)
        if let front = Shared.frontmost.profile { profiles.insert(front, at: 0) }
        let accounts = Shared.usagePool.accounts
        let language = DictationWire.language(preferred: Locale.preferredLanguages)
        // A keychain dialog can sit unanswered, and `work` is where the microphone is stopped.
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let login = Self.login(profiles: profiles, accounts: accounts)
            self?.work.async { self?.open(recording, login: login, language: language) }
        }
    }

    private func open(_ recording: Recording, login: (token: String?, problem: String), language: String) {
        guard !recording.isOver else { return }
        guard let token = login.token else {
            let problem = login.problem
            DispatchQueue.main.async { self.fail(recording, problem) }
            return
        }
        let stream = DictationStream(request: DictationWire.request(token: token, language: language),
                                     queue: work)
        stream.onFrame = { frame in
            DispatchQueue.main.async {
                guard self.recording === recording else { return }
                recording.transcript.take(frame)
                self.preview = recording.transcript.preview
            }
        }
        stream.onEnd = { failure in
            DispatchQueue.main.async { self.streamEnded(recording, failure) }
        }
        recording.stream = stream
        stream.open()
        let backlog = recording.backlog
        recording.backlog = Data()
        var sent = 0
        while sent < backlog.count {
            let next = min(sent + 6400, backlog.count)
            stream.send(backlog.subdata(in: sent..<next))
            sent = next
        }
        if recording.wantsClose { stream.finish() }
    }

    /// The first login that may be used for this: the Claude in front, then
    /// Claude's own, then the others, then accounts signed in here.
    private static func login(profiles: [URL], accounts: [PoolAccount]) -> (token: String?, problem: String) {
        var seen: Set<String> = []
        for profile in profiles where seen.insert(profile.path).inserted {
            do {
                if let token = try ClaudeCredentials.token(for: profile, prompting: .onceIfShut,
                                                           requiring: [ClaudeCredentials.inferenceScope]) {
                    return (token.value, "")
                }
            } catch ClaudeCredentials.Failure.noKeychainAccess, ClaudeCredentials.Failure.keychainDeclined {
                return (nil, L10n.text("Dictation needs the “Claude Safe Storage” keychain item. Refresh Usage asks macOS for it again."))
            } catch {
                continue
            }
        }
        for account in accounts where account.scopes.contains(ClaudeCredentials.inferenceScope) {
            if let token = try? UsageMonitor.token(of: account, prompting: .onceIfShut) { return (token, "") }
        }
        return (nil, L10n.text("No Claude here is signed in with a login dictation can use. Open one and sign in."))
    }

    private func microphoneLost(_ recording: Recording) {
        guard self.recording === recording else { return }
        if recording.isCommitted {
            end(.microphoneLost, fromKey: false)
        } else {
            recording.microphoneFailure = L10n.text("The microphone went away.")
        }
    }

    /// `fromKey` says the gesture already knows: told again, it would throw
    /// away a press that came in the same breath.
    private func end(_ reason: DictationEnd, fromKey: Bool) {
        refusal = nil
        if !fromKey { gesture.ended() }
        guard let recording else { return }
        guard !recording.isEnding else {
            // Being written down, it can still be taken back and nothing else.
            guard [.escape, .away, .switchedOff].contains(reason) else { return }
            Diagnostics.note("dictation.taken-back", ["reason": "\(reason)"])
            discard(recording)
            preview = ""
            if case .notice = phase {} else { phase = .idle }
            return
        }
        recording.isEnding = true
        if recording.isCommitted {
            Diagnostics.note("dictation.ended", ["reason": "\(reason)", "seconds": Int(now - recording.began)])
        }
        level = -120
        secondsLeft = nil
        if reason.keepsWords, recording.isCommitted {
            recording.app = NSWorkspace.shared.frontmostApplication?.processIdentifier
            phase = .transcribing
            work.async {
                recording.microphone.stop()
                recording.wantsClose = true
                recording.stream?.finish()
            }
        } else {
            discard(recording)
            preview = ""
            if case .notice = phase {} else { phase = .idle }
        }
    }

    private func discard(_ recording: Recording) {
        if self.recording === recording { self.recording = nil }
        if let awake = recording.awake { ProcessInfo.processInfo.endActivity(awake) }
        recording.awake = nil
        work.async {
            recording.isOver = true
            recording.microphone.stop()
            recording.stream?.cancel()
            recording.backlog = Data()
        }
    }

    private func fail(_ recording: Recording, _ message: String) {
        guard self.recording === recording else { return }
        gesture.ended()
        discard(recording)
        level = -120
        secondsLeft = nil
        preview = ""
        Diagnostics.note("dictation.failed", ["why": message])
        show(message)
    }

    private func streamEnded(_ recording: Recording, _ failure: DictationStream.Failure?) {
        guard self.recording === recording else { return }
        let words = recording.transcript.settled
        gesture.ended()
        discard(recording)
        level = -120
        secondsLeft = nil
        preview = ""
        let length = words.count
        Diagnostics.note("dictation.answered", ["characters": length, "failure": failure?.kind ?? "none"])
        guard !words.isEmpty else {
            show(failure?.message ?? L10n.text("Nothing was heard."))
            return
        }
        // Written down while the person went elsewhere, they are not typed there.
        if let app = recording.app, app != NSWorkspace.shared.frontmostApplication?.processIdentifier {
            Diagnostics.note("dictation.delivered", ["typed": false, "moved": true])
            DictationTyping.copy(words)
            show(L10n.text("You moved to another app, so the words are on the clipboard."))
            return
        }
        deliver(words, trouble: failure?.message)
    }

    private func deliver(_ words: String, trouble: String?) {
        isDelivering = true
        phase = .transcribing
        let key = self.key
        let last = lastTyped
        work.async { [weak self] in
            // The press that ended a hands-free recording may still be down.
            let patience = Date().addingTimeInterval(1)
            while DictationTyping.isDown(key), Date() < patience { usleep(20_000) }
            let place = DictationTyping.place()
            let afterOwn = last.map { $0.app == place.app && Date().timeIntervalSince($0.at) < 45 } ?? false
            let text = DictationText.insertion(words, before: place.before, afterOwn: afterOwn)
            let typed = !place.isPassword && DictationTyping.type(text)
            DispatchQueue.main.async {
                guard let self else { return }
                self.isDelivering = false
                Diagnostics.note("dictation.delivered", ["typed": typed, "password": place.isPassword])
                if typed {
                    self.lastTyped = (place.app, Date())
                    if let trouble { self.show(trouble) } else { self.phase = .idle }
                } else {
                    // Nothing said is lost for want of somewhere to put it.
                    DictationTyping.copy(words)
                    self.show(L10n.text("Couldn't type here, so the words are on the clipboard."))
                }
            }
        }
    }

    private func show(_ notice: String) {
        noticeNumber += 1
        let number = noticeNumber
        phase = .notice(notice)
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) { [weak self] in
            guard let self, self.noticeNumber == number, case .notice = self.phase else { return }
            self.phase = .idle
        }
    }

    // MARK: - The clock

    private func startClock() {
        guard clock == nil else { return }
        let clock = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(clock, forMode: .common)
        self.clock = clock
    }

    private func tick() {
        let now = self.now
        perform(gesture.passed(now))

        // A release can go unseen: a password field takes the keyboard, the
        // screen locks. Three looks in a row, since one can catch the key
        // between two events.
        if gesture.keyIsExpectedDown {
            missedReleases = DictationTyping.isDown(key) ? 0 : missedReleases + 1
            if missedReleases >= 3 {
                missedReleases = 0
                perform(gesture.release(at: now))
            }
        } else {
            missedReleases = 0
        }

        if let recording, recording.isCommitted, !recording.isEnding, gesture.isHandsFree,
           DictationTyping.keyboardIsGuarded {
            end(.guarded, fromKey: false)
        }
        if let recording, recording.isCommitted, !recording.isEnding {
            let elapsed = now - recording.began
            let left = DictationLimits.secondsLeft(elapsed: elapsed)
            if left != secondsLeft { secondsLeft = left }
            if let reason = DictationLimits.end(elapsed: elapsed,
                                                quietFor: recording.quiet.lasted(until: now),
                                                handsFree: gesture.isHandsFree) {
                end(reason, fromKey: false)
            }
        }
        if recording == nil, gesture.state == .idle {
            clock?.invalidate()
            clock = nil
        }
    }
}

/// What is on screen while a recording is. It never takes the keyboard: the
/// words are going to the app that has it.
final class DictationPanel {
    private let panel: NSPanel

    init(dictation: DictationController) {
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 480, height: 64),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = NSHostingView(rootView: DictationIndicator(dictation: dictation))
    }

    func show(_ visible: Bool) {
        guard visible else {
            panel.orderOut(nil)
            return
        }
        let pointer = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(pointer, $0.frame, false) } ?? NSScreen.main
        if let area = screen?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: area.midX - panel.frame.width / 2, y: area.minY + 40))
        }
        panel.orderFrontRegardless()
    }
}

struct DictationIndicator: View {
    @ObservedObject var dictation: DictationController

    var body: some View {
        HStack(spacing: 8) {
            symbol
            Text(words)
                .font(.callout)
                .lineLimit(2)
                .truncationMode(.head)
                .multilineTextAlignment(.leading)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .background(.regularMaterial, in: Capsule())
        .overlay(Capsule().strokeBorder(Color.primary.opacity(0.15)))
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder private var symbol: some View {
        switch dictation.phase {
        case .listening:
            Image(systemName: "mic.fill")
                .foregroundStyle(.red)
                // Loudness, so a microphone that hears nothing shows it.
                .scaleEffect(1 + 0.4 * CGFloat(max(0, min(1, (dictation.level + 50) / 40))))
                .animation(.easeOut(duration: 0.1), value: dictation.level)
        case .transcribing:
            ProgressView().controlSize(.small)
        case .notice, .idle:
            Image(systemName: "info.circle").foregroundStyle(.secondary)
        }
    }

    private var words: String {
        switch dictation.phase {
        case .idle:
            return ""
        case .listening(let handsFree):
            if let left = dictation.secondsLeft { return L10n.format("Dictation stops in %ld s", left) }
            if !dictation.preview.isEmpty { return dictation.preview }
            return handsFree
                ? L10n.format("Listening. Press %@ to finish.", L10n.text(dictation.key.name))
                : L10n.text("Listening…")
        case .transcribing:
            return L10n.text("Writing it down…")
        case .notice(let text):
            return text
        }
    }
}

/// Dictation's rows in Settings: the switch, the key, and whatever macOS has
/// yet to allow.
struct DictationControl: View {
    @EnvironmentObject private var settings: AppSettings
    @ObservedObject private var dictation = Shared.dictation

    static let how = """
        Hold the key and speak; let go, and the words are typed where the cursor is. \
        Tap it twice to keep recording until you press it again; Escape discards it. \
        Your voice goes to Anthropic, the way Claude's own dictation sends it, with the login of a Claude here.
        """

    var body: some View {
        Group {
            Toggle("Dictate into any app", isOn: $settings.dictationEnabled)
                .help(L10n.text(Self.how))
                .identified("dictation")
            Picker("Dictation key", selection: $settings.dictationKey) {
                ForEach(DictationKey.all) { key in
                    Text(L10n.text(key.name)).tag(Int(key.code))
                }
            }
            .disabled(!settings.dictationEnabled)
            .identified("dictation-key")
            if settings.dictationEnabled {
                HStack(spacing: 8) {
                    note
                    Spacer(minLength: 0)
                    if let need = dictation.need {
                        Button("Open System Settings…") {
                            DictationTyping.openSettings(need == .accessibility
                                ? "Privacy_Accessibility" : "Privacy_Microphone")
                        }
                        .controlSize(.small)
                        .identified("dictation-allow")
                    }
                }
            }
        }
        .font(.callout)
        .onAppear { dictation.refreshNeed() }
    }

    @ViewBuilder private var note: some View {
        switch dictation.need {
        case .accessibility:
            OneLineNote(text: L10n.text("Needs Accessibility to hear the key and type"),
                        more: L10n.text("macOS lets an app watch a key and type into other apps only once it is allowed under Privacy & Security › Accessibility. After an update macOS may ask for this again."),
                        topic: "Dictation", attention: true, id: "dictation-note")
        case .microphone:
            OneLineNote(text: L10n.text("Needs the microphone"),
                        more: L10n.text("Allow Claude Graft under Privacy & Security › Microphone."),
                        topic: "Dictation", attention: true, id: "dictation-note")
        case nil:
            OneLineNote(text: L10n.text("Hold to speak, tap twice to keep recording"),
                        more: L10n.text(Self.how), topic: "Dictation", id: "dictation-note")
        }
    }
}
