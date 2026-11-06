import Foundation

enum ClaudeUpdateFeed {
    enum Failure: LocalizedError {
        case invalid, unavailable
        var errorDescription: String? {
            switch self {
            case .invalid: return L10n.text("Claude's update service returned an unreadable version.")
            case .unavailable: return L10n.text("Could not check for Claude Desktop updates. Try again when connected.")
            }
        }
    }

    static func parts(_ version: String) -> [UInt]? {
        let strings = version.split(separator: ".", omittingEmptySubsequences: false)
        guard strings.count == 3, strings.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isASCII) && $0.allSatisfy(\.isNumber) })
        else { return nil }
        let numbers = strings.compactMap { UInt($0) }
        return numbers.count == 3 ? numbers : nil
    }

    static func isNewer(_ version: String, than installed: String) -> Bool {
        guard let a = parts(version), let b = parts(installed) else { return false }
        return b.lexicographicallyPrecedes(a)
    }

    static func available(in data: Data, installed: String) throws -> String? {
        guard parts(installed) != nil,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let version = (object["currentRelease"] ?? object["version"]) as? String,
              parts(version) != nil else { throw Failure.invalid }
        return isNewer(version, than: installed) ? version : nil
    }

    static func url(version: String, deviceID: String) -> URL {
        var url = URLComponents(string: "https://api.anthropic.com/api/desktop/darwin/universal/squirrel/update")!
        let os = ProcessInfo.processInfo.operatingSystemVersion
        url.queryItems = [URLQueryItem(name: "version", value: version),
                         URLQueryItem(name: "device_id", value: deviceID),
                         URLQueryItem(name: "os_version", value: "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)")]
        return url.url!
    }

    static func installedVersion(at bundle: URL = Graft.claudeApp) -> String? {
        // Bundle caches its Info.plist, including across an installation.
        guard let data = try? Data(contentsOf: bundle.appending(path: "Contents/Info.plist")),
              let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let version = info["CFBundleShortVersionString"] as? String,
              parts(version) != nil else { return nil }
        return version
    }
}

/// An update may start only after every instance from the button press has
/// exited. A reused pid or a newly opened Claude is not permission to quit it.
struct ClaudeUpdateFlow: Codable {
    struct Instance: Codable, Hashable {
        var pid: Int32
        var launched: Date
    }
    enum Phase: String, Codable { case quitting, starting, downloading, installing }
    enum Action: Equatable { case wait, launch, quitHelper, complete, fail(String) }
    struct Sample {
        var instances: Set<Instance>
        var helper: Instance?
        var installed: String?
        var staged: String?
        var installerRunning = false
    }

    var target: String
    var original: Set<Instance>
    var phase = Phase.quitting
    var phaseStarted: Date

    mutating func advance(_ sample: Sample, at now: Date) -> Action {
        let elapsed = now.timeIntervalSince(phaseStarted)
        func installed() -> Bool {
            guard let version = sample.installed else { return false }
            return version == target || ClaudeUpdateFeed.isNewer(version, than: target)
        }
        switch phase {
        case .quitting:
            guard sample.instances.isSubset(of: original) else {
                return .fail(L10n.text("Another Claude opened while preparing the update. Close it and try again."))
            }
            guard sample.instances.isEmpty else {
                return elapsed >= 90 ? .fail(L10n.text("Claude has not finished quitting. Finish closing it, then try again.")) : .wait
            }
            if sample.installerRunning { return elapsed >= 180 ? .fail(L10n.text("Claude's installer is still busy. Try again after it finishes.")) : .wait }
            if installed() { return .complete }
            phase = .starting; phaseStarted = now
            return .launch
        case .starting, .downloading:
            if installed() && sample.helper == nil && !sample.installerRunning { return .complete }
            guard sample.instances.allSatisfy({ $0 == sample.helper }) else {
                return .fail(L10n.text("Another Claude opened during the update. Keep Claude closed until the update finishes."))
            }
            if sample.helper != nil, let staged = sample.staged, staged == target || ClaudeUpdateFeed.isNewer(staged, than: target) {
                phase = .installing; phaseStarted = now
                return sample.helper == nil ? .wait : .quitHelper
            }
            if sample.installerRunning && sample.helper == nil {
                phase = .installing; phaseStarted = now
                return .wait
            }
            if phase == .starting {
                if sample.helper != nil { phase = .downloading; phaseStarted = now }
                else if elapsed >= 30 { return .fail(L10n.text("Claude's updater could not be started. Try again.")) }
            } else if sample.helper == nil {
                return .fail(L10n.text("Claude closed before preparing its update. Try again."))
            } else if elapsed >= 20 * 60 {
                return .fail(L10n.text("Claude did not finish downloading its update. Check your connection and try again."))
            }
        case .installing:
            if installed() && sample.helper == nil && !sample.installerRunning { return .complete }
            if elapsed >= 180 { return .fail(L10n.text("Claude's update did not finish installing. Check Claude and try again.")) }
        }
        return .wait
    }
}

/// Claude updates itself just as well signed out, and that is how the profile
/// Graft starts it in is meant to stay. An account signed into it acts as that
/// account for as long as the update runs: a second device on the account, its
/// scheduled tasks and its plugins starting in a window nobody is watching.
enum UpdaterProfile {
    static func isSignedIn(_ profile: URL) -> Bool {
        // A config that will not parse cannot show that nobody signed in, and
        // no Claude is writing it while the updater is closed.
        guard let config = Graft.readableConfigJSON(of: profile) else { return true }
        if config["oauth:tokenCacheV2"] != nil || config["lastKnownAccountUuid"] != nil { return true }
        for store in ["claude-code-sessions", "local-agent-mode-sessions"] {
            let folder = profile.appending(path: store)
            let names = (try? Graft.fm.contentsOfDirectory(atPath: folder.path)) ?? []
            if names.contains(where: { $0 != "skills-plugin" && Graft.isDirectory(folder.appending(path: $0)) }) {
                return true
            }
        }
        return false
    }

    /// The old profile goes to the Trash rather than away, because somebody
    /// signed in there and what they did in it is theirs to look through. The
    /// device identifier carries over so the feed keeps placing this machine
    /// in the same rollout.
    static func reset(_ profile: URL, keeping deviceID: String,
                      discard: (URL) throws -> Void = { try Graft.fm.trashItem(at: $0, resultingItemURL: nil) }) throws {
        guard !Graft.isSymlink(profile) else { throw ManualUpdates.Failure.invalidProfile }
        if Graft.exists(profile) { try discard(profile) }
        try writeDeviceID(deviceID, into: profile)
    }

    /// Hiding from outside can't beat Claude to the screen: it puts its
    /// window up before anything watching it can react, and a live run
    /// counted two such flashes per update. Claude already has a way to start
    /// without showing anything: when it relaunches itself for an update it
    /// leaves this marker, and the launch that reads it keeps its window
    /// hidden ("Window was not visible, staying hidden") and deletes the file.
    /// That is exactly what this launch is. The profile is Graft's own, so no
    /// person's window state is overridden. A Claude that stops reading the
    /// marker starts normally and is hidden from outside instead.
    static func markHiddenLaunch(_ profile: URL, at now: Date = Date()) throws {
        guard !Graft.isSymlink(profile) else { throw ManualUpdates.Failure.invalidProfile }
        let marker = profile.appending(path: "stealth-relaunch")
        guard !Graft.isSymlink(marker) else { throw ManualUpdates.Failure.invalidProfile }
        let body: [String: Any] = ["ts": (now.timeIntervalSince1970 * 1000).rounded(), "windowVisible": false]
        try JSONSerialization.data(withJSONObject: body).write(to: marker, options: .atomic)
    }

    static func writeDeviceID(_ id: String, into profile: URL) throws {
        try Graft.fm.createDirectory(at: profile, withIntermediateDirectories: true)
        let file = profile.appending(path: "ant-did")
        try Data(id.utf8).base64EncodedData().write(to: file, options: .atomic)
        try Graft.fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
