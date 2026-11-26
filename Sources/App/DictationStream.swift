import Foundation

/// One recording's connection to the speech service: sound goes up as it is
/// captured, words come back, and the last of them arrive after the close.
///
/// Everything here runs on the queue it was given.
final class DictationStream {
    enum Failure: Equatable {
        case unreachable
        case refused
        case lost
        case service(String?)

        var message: String {
            switch self {
            case .unreachable:
                return L10n.text("Couldn't reach Anthropic for dictation.")
            case .refused:
                return L10n.text("That login was refused for dictation. Open its Claude once so it can renew it.")
            case .lost:
                return L10n.text("Dictation lost its connection.")
            case .service(let detail):
                return detail.map { L10n.format("Dictation stopped: %@", $0) }
                    ?? L10n.text("The dictation service ran into a problem.")
            }
        }
    }

    var onFrame: ((DictationWire.Frame) -> Void)?
    /// Called once, when nothing more will arrive.
    var onEnd: ((Failure?) -> Void)?

    private let queue: DispatchQueue
    private let task: URLSessionWebSocketTask
    private var isOpen = false
    private var isClosing = false
    private var hasEnded = false
    private var failure: Failure?
    private var keepAlive: DispatchSourceTimer?

    /// A borrowed login goes out through the session that writes nothing down.
    init(request: URLRequest, session: URLSession = UsageAPI.session, queue: DispatchQueue) {
        self.queue = queue
        task = session.webSocketTask(with: request)
    }

    func open() {
        task.resume()
        receive()
        queue.asyncAfter(deadline: .now() + DictationLimits.openWait) { [weak self] in
            guard let self, !self.isOpen else { return }
            self.end(.unreachable)
        }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + DictationLimits.keepAlive, repeating: DictationLimits.keepAlive)
        timer.setEventHandler { [weak self] in self?.say(DictationWire.keepAlive) }
        timer.resume()
        keepAlive = timer
    }

    func send(_ sound: Data) {
        guard !hasEnded, !isClosing else { return }
        task.send(.data(sound)) { [weak self] error in
            self?.queue.async { self?.sent(error) }
        }
    }

    /// Says no more sound is coming and waits for the words still owed.
    func finish() {
        guard !hasEnded, !isClosing else { return }
        isClosing = true
        say(DictationWire.close)
        queue.asyncAfter(deadline: .now() + DictationLimits.answerWait) { [weak self] in
            self?.end(nil)
        }
    }

    func cancel() {
        onFrame = nil
        onEnd = nil
        end(nil)
    }

    private func say(_ text: String) {
        guard !hasEnded else { return }
        task.send(.string(text)) { [weak self] error in
            self?.queue.async { self?.sent(error) }
        }
    }

    /// The first write to go through is the only word there is that the
    /// connection opened: this session has no delegate to be told.
    private func sent(_ error: Error?) {
        if error == nil { isOpen = true } else { end(failure ?? (isOpen ? .lost : .unreachable)) }
    }

    private func receive() {
        task.receive { [weak self] result in
            self?.queue.async { self?.received(result) }
        }
    }

    private func received(_ result: Result<URLSessionWebSocketTask.Message, Error>) {
        guard !hasEnded else { return }
        switch result {
        case .success(let message):
            isOpen = true
            if case .string(let text) = message {
                let frame = DictationWire.frame(text)
                if case .failure(let detail, let code) = frame {
                    end(code == "account_session_invalid" ? .refused : .service(detail))
                    return
                }
                onFrame?(frame)
            }
            receive()
        case .failure:
            if task.closeCode == .policyViolation { failure = failure ?? .refused }
            let closedCleanly = task.closeCode == .normalClosure || isClosing
            end(failure ?? (closedCleanly ? nil : (isOpen ? .lost : .unreachable)))
        }
    }

    private func end(_ failure: Failure?) {
        guard !hasEnded else { return }
        hasEnded = true
        keepAlive?.cancel()
        keepAlive = nil
        task.cancel(with: .normalClosure, reason: nil)
        let finished = onEnd
        onFrame = nil
        onEnd = nil
        finished?(failure)
    }
}
