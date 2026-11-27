import Foundation

/// The figures of an account whose login here cannot ask the usage endpoint,
/// read through its claude.ai sign-in instead: the answer claude.ai's own
/// usage page draws, each model's week included.
enum WebUsage {
    struct Held: Equatable {
        var reading: UsageAPI.Reading
        var fetched: Date
    }

    /// A page of claude.ai is loaded to ask from, so this is asked while
    /// somebody is looking and never on a clock.
    static let rest: TimeInterval = 5 * 60
    static let pressedRest: TimeInterval = 30

    static func path(organization: String) -> String {
        "/api/organizations/\(organization)/usage?skip_spend=1"
    }

    /// The sign-in to ask through and the organization to ask about: the
    /// login's own where it names one, and only one that sign-in can see.
    static func route(for account: PoolAccount,
                      among signIns: [ArtifactAccount]) -> (signIn: UUID, organization: String)? {
        guard let uuid = account.accountUuid,
              let signIn = signIns.first(where: { $0.accountUuid == uuid }) else { return nil }
        guard let wanted = account.organizationUuid else { return signIn.org.map { (signIn.id, $0) } }
        let seen = (signIn.organizations ?? []).map(\.id) + [signIn.org].compactMap { $0 }
        return seen.first { $0.lowercased() == wanted }.map { (signIn.id, $0) }
    }

    static func mayAsk(last: Date?, pressed: Bool, now: Date) -> Bool {
        guard let last else { return true }
        return now.timeIntervalSince(last) >= (pressed ? pressedRest : rest)
    }
}
