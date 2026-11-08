import Foundation

/// Graft's half of `PoolEnvironment`: reading a Claude's Code environment,
/// deciding what it should hold, and leaving both finished values in the
/// journal for whichever opens that Claude next.
enum ClaudeCodeEnvironment {
    enum State: Equatable {
        case off
        case on
        /// Wanted, and that Claude is open: it takes the lines when it next
        /// starts, since a running one would never read them.
        case waiting
        /// No longer wanted, and that Claude is open: they come out when it
        /// closes. The relay stays up until then.
        case leaving
        /// The person already sends this Claude's Claude Code somewhere of
        /// their own, or trusts a certificate of their own. Both are theirs;
        /// replacing either would quietly break what they set up. `holding`
        /// is whether a line of Graft's is still there beside it, in a Claude
        /// that is open, which keeps the relay up until it closes.
        case conflict(String, holding: Bool)
        case unreadable

        var holdsLines: Bool {
            switch self {
            case .on, .leaving: return true
            case .conflict(_, let holding): return holding
            case .off, .waiting, .unreadable: return false
            }
        }
    }

    struct Reading: Equatable {
        var entry: PoolEnvironment.Entry
        /// The field as read, byte for byte.
        var current: String?
        /// Some name holds exactly a value of Graft's, so Claude Code there
        /// may be pointed at the relay or trusting its authority.
        var holding: Bool
        /// One of Graft's names set to a value of the person's own.
        var conflict: String?
    }

    /// What the profile's environment holds now, and the two values to swap
    /// between. The value that matches what is on disk is the one on disk,
    /// byte for byte, so a launcher acting on this later recognises it. Nil
    /// is an environment that cannot be read.
    ///
    /// A name holding exactly one of Graft's values is Graft's whatever sits
    /// beside it. A line used to stop counting as Graft's once a value of the
    /// person's own appeared under the other name, and then nothing took it
    /// out: a proxy left without the certificate that makes it work, and a
    /// profile read as holding nothing while it still pointed at the relay.
    static func read(_ profile: URL, wanted: Bool, key: Data) -> Reading? {
        guard let outer = PoolEnvironment.outer(in: profile) else { return nil }
        let current: String?
        switch outer[PoolEnvironment.field] {
        case nil, is NSNull: current = nil
        case let text as String: current = text
        default: return nil
        }
        var variables: [String: String] = [:]
        if let current {
            guard let plain = ClaudeCredentials.unseal(current, key: key),
                  let object = try? JSONSerialization.jsonObject(with: plain) as? [String: Any]
            else { return nil }
            for (name, value) in object {
                guard let value = value as? String else { return nil }
                variables[name] = value
            }
        }
        var conflict = conflict(in: variables)
        // Scoped values are laid over these when a session starts, so one
        // naming either variable would win over the lines without a word.
        if let scoped = outer[PoolEnvironment.scopedField] as? String {
            guard let plain = ClaudeCredentials.unseal(scoped, key: key),
                  let object = try? JSONSerialization.jsonObject(with: plain)
            else { return nil }
            conflict = conflict ?? self.conflict(inNested: object)
        }

        let lines = PoolEnvironment.lines
        let base = variables.filter { !isLine($0.key, $0.value) }
        let holding = base.count < variables.count
        let exact = variables == base.merging(lines) { _, ours in ours }
        let plain: String?
        if holding {
            plain = base.isEmpty ? nil : seal(base, key: key)
            if !base.isEmpty, plain == nil { return nil }
        } else {
            plain = current
        }
        let pooled: String?
        if conflict != nil {
            // The lines never go in beside a value of the person's own, so
            // the only swap left is the one taking Graft's out.
            pooled = holding ? current : seal(base, key: key)
        } else {
            pooled = exact ? current : seal(base.merging(lines) { _, ours in ours }, key: key)
        }
        guard let pooled else { return nil }
        return Reading(entry: PoolEnvironment.Entry(wanted: wanted && conflict == nil, plain: plain, pooled: pooled),
                       current: current, holding: holding, conflict: conflict)
    }

    /// One of Graft's names, whatever its case, holding exactly Graft's value.
    static func isLine(_ name: String, _ value: String) -> Bool {
        PoolEnvironment.lines.contains {
            name.caseInsensitiveCompare($0.key) == .orderedSame && value == $0.value
        }
    }

    /// Either name set to anything but the relay's value. Names are matched
    /// without regard to case, since a lower-case proxy variable is read too.
    static func conflict(in variables: [String: String]) -> String? {
        for (name, value) in variables {
            for (ours, wanted) in PoolEnvironment.lines
            where name.caseInsensitiveCompare(ours) == .orderedSame && value != wanted {
                return ours
            }
        }
        return nil
    }

    private static func conflict(inNested object: Any) -> String? {
        if let strings = object as? [String: String] { return conflict(in: strings) }
        if let nested = object as? [String: Any] {
            for value in nested.values {
                if let found = conflict(inNested: value) { return found }
            }
        }
        return nil
    }

    /// Compact and in a fixed order, the way `JSON.stringify` writes it, so the
    /// same variables always seal to the same value.
    static func seal(_ variables: [String: String], key: Data) -> String? {
        guard let json = try? JSONSerialization.data(withJSONObject: variables,
                                                     options: [.sortedKeys, .withoutEscapingSlashes])
        else { return nil }
        return ClaudeCredentials.seal(json, key: key)
    }

    /// One pass over every profile that wants the lines or may still hold
    /// them. Writes the journal, then settles each closed profile under the
    /// launch lock — the same lock and the same swap a launcher uses, so the
    /// two can never both be writing one file.
    ///
    /// `relayReady` says whether the relay answered in this pass; a profile
    /// is never given the lines on the strength of a relay that is not there.
    /// Answers by `PoolEnvironment.key(for:)`, since a profile read back out
    /// of the journal is a path, and one URL for a folder need not equal
    /// another for the same folder.
    static func settle(wanted: [URL: Bool], key: Data, relay: String?, relayReady: Bool,
                       running: (URL) -> Bool) -> [String: State] {
        var journal = PoolEnvironment.readJournal()
        journal.relay = relay
        var profiles: [String: (url: URL, wants: Bool)] = [:]
        for (url, wants) in wanted { profiles[PoolEnvironment.key(for: url)] = (url, wants) }
        for path in journal.profiles.keys where profiles[path] == nil {
            profiles[path] = (URL(fileURLWithPath: path), false)
        }

        var readings: [String: Reading] = [:]
        for (path, profile) in profiles {
            guard let reading = read(profile.url, wanted: profile.wants, key: key) else { continue }
            readings[path] = reading
            journal.profiles[path] = reading.entry
        }
        PoolEnvironment.writeJournal(journal)

        var states: [String: State] = [:]
        for (path, profile) in profiles {
            guard let reading = readings[path] else {
                states[path] = .unreadable
                continue
            }
            var holds = reading.holding || scopedCopies(profile.url, key: key)
            if !running(profile.url) {
                SidebarSync.withLaunchLock {
                    guard !running(profile.url) else { return }
                    // Part of the lines, left by an edit: neither finished
                    // value matches it, so a launcher's swap could never take
                    // them out. Graft read this very value and holds the key,
                    // so it puts the value without them in its place first.
                    let entry = reading.entry
                    if reading.holding, reading.current != entry.plain, reading.current != entry.pooled,
                       PoolEnvironment.field(in: profile.url) == reading.current.map(PoolEnvironment.Field.value) ?? .absent {
                        let written = PoolEnvironment.write(entry.plain, into: profile.url)
                        Diagnostics.note("pool.environment partial lines removed",
                                         ["profile": profile.url.lastPathComponent, "ok": written])
                    }
                    PoolEnvironment.settle(beforeOpening: profile.url) { relayReady }
                    removeScopedCopies(profile.url, key: key)
                }
                holds = holdsLines(profile.url, key: key)
            }
            if let name = reading.conflict, profile.wants {
                states[path] = .conflict(name, holding: holds)
                continue
            }
            switch (profile.wants, holds) {
            case (true, true): states[path] = .on
            case (true, false): states[path] = .waiting
            case (false, true): states[path] = .leaving
            case (false, false): states[path] = .off
            }
        }

        // A profile that wants nothing and holds nothing is finished with, and
        // a launcher has nothing left to do for it.
        let finished = states.filter { $0.value == .off }.map(\.key)
        if !finished.isEmpty {
            var cleaned = PoolEnvironment.readJournal()
            for path in finished { cleaned.profiles[path] = nil }
            PoolEnvironment.writeJournal(cleaned)
        }
        return states
    }

    /// Whether this Claude's Claude Code may still be pointed at the relay,
    /// which is what keeps the relay up.
    static func holdsLines(_ profile: URL, key: Data) -> Bool {
        if scopedCopies(profile, key: key) { return true }
        return read(profile, wanted: false, key: key)?.holding ?? false
    }

    /// Claude keeps what a folder's own environment inherited from this one
    /// under `:machine:<scope>` in `scopedEnvVars`, and lays it over `envVars`
    /// as a session starts. A copy of the lines made there would outlive their
    /// removal from `envVars` and keep pointing Claude Code at a relay that
    /// may since have stopped, and the swap a launcher makes cannot reach it,
    /// since only `envVars` has finished values waiting in the journal.
    static func scopedCopies(_ profile: URL, key: Data) -> Bool {
        guard let object = scopedObject(profile, key: key) else { return false }
        return withoutLines(object).found
    }

    /// Takes every copy of the exact lines out of the scoped values, so that
    /// `envVars` alone decides. A value of the person's own under either name
    /// is theirs and stays where it is. Graft's to do, with the key, and only
    /// with the profile closed under the launch lock.
    @discardableResult
    static func removeScopedCopies(_ profile: URL, key: Data) -> Bool {
        guard let object = scopedObject(profile, key: key) else { return false }
        let (kept, found) = withoutLines(object)
        guard found,
              let json = try? JSONSerialization.data(withJSONObject: kept,
                                                     options: [.sortedKeys, .withoutEscapingSlashes]),
              let sealed = ClaudeCredentials.seal(json, key: key)
        else { return false }
        let written = PoolEnvironment.write(sealed, field: PoolEnvironment.scopedField, into: profile)
        Diagnostics.note("pool.environment scoped copies removed",
                         ["profile": profile.lastPathComponent, "ok": written])
        return written
    }

    private static func scopedObject(_ profile: URL, key: Data) -> Any? {
        guard let outer = PoolEnvironment.outer(in: profile),
              let scoped = outer[PoolEnvironment.scopedField] as? String,
              let plain = ClaudeCredentials.unseal(scoped, key: key)
        else { return nil }
        return try? JSONSerialization.jsonObject(with: plain)
    }

    private static func withoutLines(_ object: Any) -> (kept: Any, found: Bool) {
        guard let map = object as? [String: Any] else { return (object, false) }
        var kept: [String: Any] = [:]
        var found = false
        for (name, value) in map {
            if let text = value as? String, isLine(name, text) {
                found = true
                continue
            }
            let inner = withoutLines(value)
            found = found || inner.found
            kept[name] = inner.kept
        }
        return (kept, found)
    }
}
