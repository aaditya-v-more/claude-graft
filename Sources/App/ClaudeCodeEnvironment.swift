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
        /// replacing either would quietly break what they set up.
        case conflict(String)
        case unreadable

        var holdsLines: Bool { self == .on || self == .leaving }
    }

    enum Reading: Equatable {
        case entry(PoolEnvironment.Entry, holding: Bool)
        case conflict(String)
        case unreadable
    }

    /// What the profile's environment holds now, and the two values to swap
    /// between. The value that matches what is on disk is the one on disk,
    /// byte for byte, so a launcher acting on this later recognises it.
    static func read(_ profile: URL, wanted: Bool, key: Data) -> Reading {
        guard let outer = PoolEnvironment.outer(in: profile) else { return .unreadable }
        let current: String?
        switch outer[PoolEnvironment.field] {
        case nil, is NSNull: current = nil
        case let text as String: current = text
        default: return .unreadable
        }
        var variables: [String: String] = [:]
        if let current {
            guard let plain = ClaudeCredentials.unseal(current, key: key),
                  let object = try? JSONSerialization.jsonObject(with: plain) as? [String: Any]
            else { return .unreadable }
            for (name, value) in object {
                guard let value = value as? String else { return .unreadable }
                variables[name] = value
            }
        }
        if let conflict = conflict(in: variables) { return .conflict(conflict) }
        // Scoped values are laid over these when a session starts, so one
        // naming either variable would win over the lines without a word.
        if let scoped = outer[PoolEnvironment.scopedField] as? String {
            guard let plain = ClaudeCredentials.unseal(scoped, key: key),
                  let object = try? JSONSerialization.jsonObject(with: plain)
            else { return .unreadable }
            if let conflict = conflict(inNested: object) { return .conflict(conflict) }
        }

        let lines = PoolEnvironment.lines
        let holding = lines.allSatisfy { variables[$0.key] == $0.value }
        var base = variables
        for name in lines.keys { base[name] = nil }
        if holding {
            guard let current else { return .unreadable }
            let plain = base.isEmpty ? nil : seal(base, key: key)
            if !base.isEmpty, plain == nil { return .unreadable }
            return .entry(PoolEnvironment.Entry(wanted: wanted, plain: plain, pooled: current), holding: true)
        }
        guard let pooled = seal(base.merging(lines) { _, ours in ours }, key: key) else { return .unreadable }
        return .entry(PoolEnvironment.Entry(wanted: wanted, plain: current, pooled: pooled), holding: false)
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
            let reading = read(profile.url, wanted: profile.wants, key: key)
            readings[path] = reading
            if case .entry(let entry, _) = reading { journal.profiles[path] = entry }
        }
        PoolEnvironment.writeJournal(journal)

        var states: [String: State] = [:]
        for (path, profile) in profiles {
            guard case .entry(_, let held) = readings[path] else {
                if case .conflict(let name) = readings[path] {
                    states[path] = profile.wants ? .conflict(name) : .off
                } else {
                    states[path] = .unreadable
                }
                continue
            }
            var holds = held || scopedCopies(profile.url, key: key)
            if !running(profile.url) {
                SidebarSync.withLaunchLock {
                    guard !running(profile.url) else { return }
                    PoolEnvironment.settle(beforeOpening: profile.url) { relayReady }
                    removeScopedCopies(profile.url, key: key)
                }
                holds = holdsLines(profile.url, key: key)
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
        guard case .entry(_, let holding) = read(profile, wanted: false, key: key) else { return false }
        return holding
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
    /// is a conflict and never gets this far. Graft's to do, with the key, and
    /// only with the profile closed under the launch lock.
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
            if let text = value as? String,
               PoolEnvironment.lines.contains(where: {
                   name.caseInsensitiveCompare($0.key) == .orderedSame && text == $0.value
               }) {
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
