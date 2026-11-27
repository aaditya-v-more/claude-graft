import Foundation

/// The rules of dictation that need no microphone, no network and no screen:
/// which key, what a press means, when a recording must end, what the service
/// said and how its words are put down. notes/dictation.md has the reasons.

/// A key that can be held without typing anything.
///
/// Only keys that put no character on the page are offered: a monitor can
/// watch a key from another app but cannot keep it from that app.
struct DictationKey: Equatable, Identifiable {
    let code: UInt16
    /// English, and the key it is localized by.
    let name: String
    /// The bit `flagsChanged` carries for this key alone. The ordinary
    /// Control flag stays set while either Control is down.
    let flag: UInt?

    var id: UInt16 { code }
    var isModifier: Bool { flag != nil }

    static let rightControl = DictationKey(code: 62, name: "Right Control", flag: 0x2000)
    static let rightOption = DictationKey(code: 61, name: "Right Option", flag: 0x40)
    static let rightCommand = DictationKey(code: 54, name: "Right Command", flag: 0x10)
    static let function = DictationKey(code: 63, name: "Fn (Globe)", flag: 0x800000)

    static let all: [DictationKey] = [
        rightCommand, rightOption, rightControl, function,
        DictationKey(code: 105, name: "F13", flag: nil),
        DictationKey(code: 107, name: "F14", flag: nil),
        DictationKey(code: 113, name: "F15", flag: nil),
        DictationKey(code: 106, name: "F16", flag: nil),
        DictationKey(code: 64, name: "F17", flag: nil),
        DictationKey(code: 79, name: "F18", flag: nil),
        DictationKey(code: 80, name: "F19", flag: nil),
    ]

    /// A code nobody offers reads as the default, so a hand-edited setting
    /// cannot leave dictation on a key that types.
    static func key(code: Int) -> DictationKey {
        all.first { Int($0.code) == code } ?? rightCommand
    }

    func isDown(flags: UInt) -> Bool {
        flag.map { flags & $0 != 0 } ?? false
    }
}

/// Why a recording ended.
enum DictationEnd: Equatable {
    case asked
    case tooLong
    case wentQuiet
    case microphoneLost
    /// A password field took the keyboard, and with it the key that would finish.
    case guarded
    case chord
    case escape
    case strayTap
    case away
    case switchedOff

    /// What was said is typed unless the person took the press back.
    var keepsWords: Bool {
        switch self {
        case .asked, .tooLong, .wentQuiet, .microphoneLost, .guarded: return true
        case .chord, .escape, .strayTap, .away, .switchedOff: return false
        }
    }
}

/// What presses of the one key mean: held to speak, tapped twice to go on
/// recording until the next press.
///
/// A press starts the microphone at once, since people speak as they press,
/// but nothing is sent until the press has outlasted a tap or been doubled:
/// the key is a modifier, and every shortcut that uses it begins the same way.
struct DictationGesture {
    enum State: Equatable {
        case idle
        case pressed(since: TimeInterval)
        case held
        case tapped(at: TimeInterval)
        case locking
        case locked
        /// The press has been used up; the release that follows means nothing.
        case spent
    }

    enum Action: Equatable {
        case begin
        /// The press is dictation and not a shortcut: start sending.
        case commit
        case lock
        case finish
        case cancel(DictationEnd)
    }

    static let tap: TimeInterval = 0.3
    static let secondTap: TimeInterval = 0.4

    private(set) var state = State.idle

    var isHandsFree: Bool { state == .locking || state == .locked }
    var keyIsExpectedDown: Bool {
        switch state {
        case .pressed, .held, .locking, .spent: return true
        case .idle, .tapped, .locked: return false
        }
    }

    /// When `passed` next has something to say.
    var deadline: TimeInterval? {
        switch state {
        case .pressed(let since): return since + Self.tap
        case .tapped(let at): return at + Self.secondTap
        default: return nil
        }
    }

    mutating func press(at now: TimeInterval) -> [Action] {
        switch state {
        case .idle:
            state = .pressed(since: now)
            return [.begin]
        case .tapped(let at):
            guard now - at <= Self.secondTap else {
                state = .pressed(since: now)
                return [.cancel(.strayTap), .begin]
            }
            state = .locking
            return [.commit, .lock]
        case .locked:
            state = .spent
            return [.finish]
        case .pressed, .held, .locking, .spent:
            return []
        }
    }

    mutating func release(at now: TimeInterval) -> [Action] {
        switch state {
        case .pressed(let since):
            guard now - since >= Self.tap else {
                state = .tapped(at: now)
                return []
            }
            state = .idle
            return [.commit, .finish]
        case .held:
            state = .idle
            return [.finish]
        case .locking:
            state = .locked
            return []
        case .spent:
            state = .idle
            return []
        case .idle, .tapped, .locked:
            return []
        }
    }

    mutating func passed(_ now: TimeInterval) -> [Action] {
        switch state {
        case .pressed(let since) where now - since >= Self.tap:
            state = .held
            return [.commit]
        case .tapped(let at) where now - at >= Self.secondTap:
            state = .idle
            return [.cancel(.strayTap)]
        default:
            return []
        }
    }

    /// Another key went down, or the pointer was clicked or scrolled. With
    /// the key held that is a shortcut; hands-free, only Escape means anything.
    mutating func otherKey(escape: Bool) -> [Action] {
        switch state {
        case .pressed, .held:
            state = .spent
            return [.cancel(escape ? .escape : .chord)]
        case .tapped:
            state = .idle
            return [.cancel(escape ? .escape : .chord)]
        case .locking where escape:
            state = .spent
            return [.cancel(.escape)]
        case .locked where escape:
            state = .idle
            return [.cancel(.escape)]
        default:
            return []
        }
    }

    /// The recording ended for a reason the key had no part in.
    mutating func ended() {
        switch state {
        case .pressed, .held, .locking: state = .spent
        case .tapped, .locked: state = .idle
        case .idle, .spent: break
        }
    }
}

enum DictationLimits {
    /// The service names no limit; ten minutes is a long thought and a short
    /// accident.
    static let longest: TimeInterval = 600
    static let countdown: TimeInterval = 30
    /// Hands-free only: a held key is its own sign that somebody is there.
    static let quiet: TimeInterval = 45
    static let quietLevel: Float = -50
    static let openWait: TimeInterval = 8
    static let answerWait: TimeInterval = 15
    static let keepAlive: TimeInterval = 4
    /// Sound kept while the connection opens: fifteen seconds of it.
    static let backlog = 16_000 * 2 * 15

    static func end(elapsed: TimeInterval, quietFor: TimeInterval, handsFree: Bool) -> DictationEnd? {
        if elapsed >= longest { return .tooLong }
        if handsFree, quietFor >= quiet { return .wentQuiet }
        return nil
    }

    /// Seconds left, once there are few enough to be worth showing.
    static func secondsLeft(elapsed: TimeInterval) -> Int? {
        let left = longest - elapsed
        return left <= countdown ? max(0, Int(left.rounded(.up))) : nil
    }
}

/// Sixteen-bit samples, as they are sent.
enum DictationSound {
    static let sampleRate = 16_000.0

    /// Loudness in decibels below full scale; silence is -120.
    static func level(of samples: Data) -> Float {
        let count = samples.count / 2
        guard count > 0 else { return -120 }
        var sum = 0.0
        samples.withUnsafeBytes { raw in
            for index in 0..<count {
                let sample = Double(Int16(littleEndian: raw.loadUnaligned(fromByteOffset: index * 2, as: Int16.self)))
                sum += sample * sample
            }
        }
        let rms = (sum / Double(count)).squareRoot() / 32_768
        return rms > 0 ? max(-120, Float(20 * log10(rms))) : -120
    }
}

/// How long it has been since anything was loud enough to be a voice.
struct DictationQuiet {
    private var lastSound: TimeInterval

    init(startedAt now: TimeInterval) { lastSound = now }

    mutating func heard(level: Float, at now: TimeInterval) {
        if level > DictationLimits.quietLevel { lastSound = now }
    }

    func lasted(until now: TimeInterval) -> TimeInterval { max(0, now - lastSound) }
}

/// The speech stream Claude's own dictation uses, asked with a borrowed login.
enum DictationWire {
    static let endpoint = URL(string: "wss://api.anthropic.com/api/ws/speech_to_text/voice_stream")!
    static let keepAlive = #"{"type":"KeepAlive"}"#
    static let close = #"{"type":"CloseStream"}"#

    /// The login rides in a header alone: an address is logged along the way.
    static func request(token: String, language: String) -> URLRequest {
        var address = URLComponents(url: endpoint, resolvingAgainstBaseURL: false)!
        address.queryItems = [
            URLQueryItem(name: "encoding", value: "linear16"),
            URLQueryItem(name: "sample_rate", value: "16000"),
            URLQueryItem(name: "channels", value: "1"),
            URLQueryItem(name: "endpointing_ms", value: "300"),
            URLQueryItem(name: "utterance_end_ms", value: "1000"),
            URLQueryItem(name: "language", value: language),
            // Without it the service sends nothing until an utterance is over.
            URLQueryItem(name: "forward_interims", value: "typed"),
        ]
        // The stream's own clock gives up on a connection that will not open.
        // This one also counts silence from the service, which sends nothing while it listens.
        var request = URLRequest(url: address.url!, timeoutInterval: DictationLimits.longest
                                    + DictationLimits.openWait + DictationLimits.answerWait)
        request.setValue("Bearer \(token.trimmingCharacters(in: .whitespacesAndNewlines))",
                         forHTTPHeaderField: "Authorization")
        return request
    }

    /// The service takes a bare language, and falls back to English.
    static func language(preferred: [String]) -> String {
        let first = preferred.first?.split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map(String.init)
        guard let first, (2...3).contains(first.count), first.allSatisfy(\.isLetter) else { return "en" }
        return first.lowercased()
    }

    enum Frame: Equatable {
        case interim(String)
        case final(String)
        case endpoint
        case failure(message: String?, code: String?)
        case other
    }

    static func frame(_ text: String) -> Frame {
        guard let object = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any],
              let type = object["type"] as? String
        else { return .other }
        switch type {
        case "TranscriptInterim": return (object["data"] as? String).map(Frame.interim) ?? .other
        case "TranscriptText": return (object["data"] as? String).map(Frame.final) ?? .other
        case "TranscriptEndpoint": return .endpoint
        case "TranscriptError", "error":
            let error = object["error"] as? [String: Any]
            let details = error?["details"] as? [String: Any]
            return .failure(message: error?["message"] as? String ?? object["reason"] as? String
                                ?? object["data"] as? String,
                            code: details?["error_code"] as? String ?? object["error_code"] as? String)
        default: return .other
        }
    }
}

/// What has been heard so far, in the order it was said. Kept as Claude
/// Desktop keeps it: each guess, then its correction, replaces the utterance
/// being said, and the service's endpoint settles it.
struct DictationTranscript: Equatable {
    private(set) var settled = ""
    private(set) var open = ""
    private var openIsCorrected = false
    /// How many of each arrived, which is all the log may say of them.
    private(set) var guesses = 0
    private(set) var corrections = 0

    /// Everything said, the open utterance included: what is shown, and what is typed.
    var spoken: String {
        guard !open.isEmpty else { return settled }
        // A service that repeats everything so far must not be typed twice.
        return Self.repeats(settled, in: open) ? open : Self.tidy(settled + " " + open)
    }

    mutating func take(_ frame: DictationWire.Frame) {
        switch frame {
        case .interim(let text):
            // A guess after a correction is the next utterance beginning.
            if openIsCorrected { settle() }
            open = Self.tidy(text)
            guesses += 1
        case .final(let text):
            let words = Self.tidy(text)
            guard !words.isEmpty else { return }
            open = words
            openIsCorrected = true
            corrections += 1
        case .endpoint:
            settle()
        case .failure, .other:
            break
        }
    }

    private mutating func settle() {
        settled = spoken
        open = ""
        openIsCorrected = false
    }

    /// "No" is not said again by "Nothing else": the repeat has to end on a word.
    private static func repeats(_ settled: String, in words: String) -> Bool {
        guard !settled.isEmpty, words.hasPrefix(settled) else { return false }
        return words.dropFirst(settled.count).first.map { !$0.isLetter && !$0.isNumber } ?? true
    }

    /// One line: a line break typed into a chat box sends the message.
    static func tidy(_ text: String) -> String {
        text.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).joined(separator: " ")
    }
}

enum DictationText {
    private static let opens: Set<Character> = ["(", "[", "{", "“", "‘", "«", "\"", "'", "/", "-", "_", "@", "#", "$", "\\"]
    private static let closes: Set<Character> = [",", ".", ";", ":", "!", "?", ")", "]", "}", "’", "”", "…"]

    /// What to type, with a space in front where the words would otherwise
    /// run into what is there. `before` is the text up to the caret when the
    /// app says, and `afterOwn` whether dictation was the last thing typed
    /// there when it does not.
    static func insertion(_ words: String, before: String?, afterOwn: Bool) -> String {
        guard let first = words.first, !closes.contains(first) else { return words }
        if let before {
            guard let last = before.last, !last.isWhitespace, !last.isNewline, !opens.contains(last)
            else { return words }
            return " " + words
        }
        return afterOwn ? " " + words : words
    }

    /// A synthetic key press carries twenty UTF-16 units at most, and half a
    /// character is not a character.
    static func strokes(_ text: String, limit: Int = 20) -> [[UInt16]] {
        var strokes: [[UInt16]] = []
        var current: [UInt16] = []
        for character in text {
            let units = Array(String(character).utf16)
            if !current.isEmpty, current.count + units.count > limit {
                strokes.append(current)
                current = []
            }
            current += units
        }
        if !current.isEmpty { strokes.append(current) }
        return strokes
    }
}
