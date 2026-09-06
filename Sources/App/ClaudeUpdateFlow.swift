import Foundation

enum ClaudeUpdateFeed {
    enum Failure: LocalizedError {
        case invalid, unavailable
        var errorDescription: String? {
            switch self {
            case .invalid: return "Claude's update service returned an unreadable version."
            case .unavailable: return "Could not check for Claude Desktop updates. Try again when connected."
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
                return .fail("Another Claude opened while preparing the update. Close it and try again.")
            }
            guard sample.instances.isEmpty else {
                return elapsed >= 90 ? .fail("Claude has not finished quitting. Finish closing it, then try again.") : .wait
            }
            if sample.installerRunning { return elapsed >= 180 ? .fail("Claude's installer is still busy. Try again after it finishes.") : .wait }
            if installed() { return .complete }
            phase = .starting; phaseStarted = now
            return .launch
        case .starting, .downloading:
            if installed() && sample.helper == nil && !sample.installerRunning { return .complete }
            guard sample.instances.allSatisfy({ $0 == sample.helper }) else {
                return .fail("Another Claude opened during the update. Keep Claude closed until the update finishes.")
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
                else if elapsed >= 30 { return .fail("Claude's updater could not be started. Try again.") }
            } else if sample.helper == nil {
                return .fail("Claude closed before preparing its update. Try again.")
            } else if elapsed >= 20 * 60 {
                return .fail("Claude did not finish downloading its update. Check your connection and try again.")
            }
        case .installing:
            if installed() && sample.helper == nil && !sample.installerRunning { return .complete }
            if elapsed >= 180 { return .fail("Claude's update did not finish installing. Check Claude and try again.") }
        }
        return .wait
    }
}
