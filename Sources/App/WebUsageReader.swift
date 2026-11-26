import Foundation

/// Asks claude.ai for the figures `WebUsage` describes, from the page the
/// account's sign-in already loads for Artifacts.
@MainActor
final class WebUsageReader {
    private var asked: [String: Date] = [:]
    private var asking: Set<String> = []

    func ask(_ accounts: [PoolAccount], center: ArtifactCenter, pressed: Bool,
             found: @escaping (String, WebUsage.Held) -> Void) {
        let now = Date()
        for account in accounts {
            guard let route = WebUsage.route(for: account, among: center.accounts),
                  !asking.contains(account.id),
                  WebUsage.mayAsk(last: asked[account.id], pressed: pressed, now: now)
            else { continue }
            asking.insert(account.id)
            asked[account.id] = now
            Task {
                defer { asking.remove(account.id) }
                let answer = try? await center.session(route.signIn)
                    .send("GET", WebUsage.path(organization: route.organization))
                guard let answer, answer.status == 200,
                      let body = try? JSONSerialization.jsonObject(with: answer.data) as? [String: Any],
                      let reading = UsageAPI.reading(from: body)
                else {
                    Diagnostics.note("usage.web unanswered", ["status": answer?.status ?? 0])
                    return
                }
                found(account.id, WebUsage.Held(reading: reading, fetched: Date()))
            }
        }
    }
}

extension UsagePoolController {
    /// Called when the dropdown opens and when somebody refreshes it.
    func askSignIns(pressed: Bool = false) {
        let wanted = accounts.filter {
            !$0.scopes.contains(ClaudeCredentials.usageScope) && claude(sameAccountAs: $0.id) == nil
        }
        Task { @MainActor in
            let center = Shared.artifacts
            learn(addresses: Dictionary(center.accounts.compactMap { signIn in
                signIn.accountUuid.flatMap { uuid in signIn.email.map { (uuid, $0) } }
            }, uniquingKeysWith: { first, _ in first }))
            Shared.webUsage.ask(wanted, center: center, pressed: pressed) { id, held in
                self.learn(web: held, for: id)
            }
        }
    }
}
