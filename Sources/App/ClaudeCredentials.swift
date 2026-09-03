import CommonCrypto
import Foundation
import Security

/// Borrows the access token Claude Desktop already holds for a profile, so
/// usage can be read for that account from Anthropic's own API.
///
/// Read-only, and deliberately narrow:
///
/// * only the access token is decoded — never the refresh token, because
///   Anthropic rotates those and using one would sign Claude Desktop out;
/// * nothing is ever written back to Claude's config, cookies or keychain;
/// * the token goes to `api.anthropic.com` and nowhere else.
///
/// The `Claude Safe Storage` item is guarded by an ACL naming the exact builds
/// allowed to decrypt it, and each name is a code hash rather than a developer.
/// An ad-hoc signature gives every build its own hash, so every new version
/// arrives as a stranger and has to be let in again.
///
/// `Prompting` says who may raise that dialog. Every path reads silently first
/// and escalates only when that read comes back shut, so a build already on the
/// list never asks at all — and one that is not asks once, rather than quietly
/// showing worse figures.
enum ClaudeCredentials {
    struct Token {
        var value: String
        var expires: Date
        var plan: String?
        var scopes: [String]

        var isCurrent: Bool { expires > Date().addingTimeInterval(120) }

        /// An entry whose scopes could not be read from its cache key is not
        /// ruled out: the key's shape is Claude's to change, and the service
        /// refuses the request anyway if the token really is insufficient.
        func covers(_ required: [String]) -> Bool {
            scopes.isEmpty || required.allSatisfy(scopes.contains)
        }
    }

    enum Failure: LocalizedError, Equatable {
        case noKeychainAccess
        case keychainDeclined
        case notSignedIn
        case expired

        var errorDescription: String? {
            switch self {
            case .noKeychainAccess:
                return L10n.text("""
                    macOS has not granted access to the “Claude Safe Storage” keychain \
                    item. Choose Always Allow when it asks, and live usage starts working.
                    """)
            case .keychainDeclined:
                return L10n.text("""
                    Access to the “Claude Safe Storage” keychain item was declined, so usage \
                    is coming from disk. Refresh Usage asks again.
                    """)
            case .notSignedIn:
                return L10n.text("No Claude Desktop login was found for this profile.")
            case .expired:
                return L10n.text("That profile's login has expired. Open it once so Claude can renew it.")
            }
        }
    }

    /// Who may raise the macOS keychain dialog on a given read.
    ///
    /// Every case tries the silent read first, so none of them prompts while
    /// this build is still on the item's ACL. The distinction only matters once
    /// that read comes back shut.
    enum Prompting {
        /// Never. Whatever is on disk is shown instead.
        case no
        /// Once for the life of the process, and never again after a decline.
        /// A thirty-second timer must not turn one lost grant into a dialog
        /// every thirty seconds.
        case onceIfShut
        /// Someone pressed something and is waiting for the answer.
        case yes
    }

    static let usageScope = "user:profile"
    static let inferenceScope = "user:inference"

    private static let keychainService = "Claude Safe Storage"
    private static let keychainAccount = "Claude Key"
    private static let cacheKeys = ["oauth:tokenCacheV2", "oauth:tokenCache"]

    // MARK: - The key

    /// Cached for the process: the keychain read is the part that can prompt.
    private static var cachedKey: Data?
    /// Whether the one unasked-for prompt has been spent, and whether the
    /// answer was no. Both last only as long as the process, so a relaunch is
    /// allowed to ask once more.
    private static var hasAskedUnprompted = false
    private static var wasDeclined = false
    private static let keyLock = NSLock()

    /// Whether a read that nobody asked for is still allowed to open a dialog.
    /// Separate from the read itself so the rule can be read, and tested, on
    /// its own.
    static func mayRaiseDialog(_ prompting: Prompting,
                               alreadyAsked: Bool,
                               declined: Bool) -> Bool {
        switch prompting {
        case .no: return false
        case .onceIfShut: return !alreadyAsked && !declined
        case .yes: return true
        }
    }

    /// The lock is held across the dialog on purpose: two profiles finding the
    /// keychain shut in the same pass would otherwise stack two dialogs for one
    /// item, and the second would be answered by someone who has already
    /// answered the first.
    static func safeStorageKey(prompting: Prompting) throws -> Data? {
        keyLock.lock()
        defer { keyLock.unlock() }
        if let cachedKey { return cachedKey }

        switch read(allowingDialog: false) {
        case .password(let password):
            return try remember(password)
        case .missing:
            return nil
        case .shut:
            break
        }

        guard mayRaiseDialog(prompting, alreadyAsked: hasAskedUnprompted, declined: wasDeclined)
        else { throw wasDeclined ? Failure.keychainDeclined : Failure.noKeychainAccess }
        if case .onceIfShut = prompting { hasAskedUnprompted = true }

        switch read(allowingDialog: true) {
        case .password(let password):
            wasDeclined = false
            return try remember(password)
        case .missing:
            return nil
        case .shut:
            // The dialog was up and the answer was no. Asking again on the next
            // tick is the nagging this whole path exists to avoid.
            wasDeclined = true
            throw Failure.keychainDeclined
        }
    }

    private static func remember(_ password: String) throws -> Data {
        guard let key = derive(password: password) else { throw Failure.noKeychainAccess }
        cachedKey = key
        return key
    }

    private enum Read {
        case password(String)
        /// No such item at all: Claude has never stored one on this machine.
        case missing
        /// The item is there and this build is not on its list.
        case shut
    }

    /// `SecKeychainSetUserInteractionAllowed` is what actually governs this
    /// dialog. The `LAContext` that used to stand in for it covers items backed
    /// by an access control, not an ACL of trusted applications, and left a
    /// background poll free to prompt — measured on a build the item had never
    /// seen, which sailed past the suppression and put a dialog on screen.
    ///
    /// It is deprecated and process-wide, so it is put back the moment the read
    /// returns, under the lock every caller of this file already holds.
    private static func read(allowingDialog: Bool) -> Read {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainAccount,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecReturnData as String: true,
        ]
        if !allowingDialog {
            query[kSecUseAuthenticationUI as String] = kSecUseAuthenticationUIFail
            SecKeychainSetUserInteractionAllowed(false)
        }
        defer { if !allowingDialog { SecKeychainSetUserInteractionAllowed(true) } }

        var result: CFTypeRef?
        switch SecItemCopyMatching(query as CFDictionary, &result) {
        case errSecSuccess:
            guard let data = result as? Data,
                  let password = String(data: data, encoding: .utf8)
            else { return .shut }
            return .password(password)
        case errSecItemNotFound:
            return .missing
        default:
            // A refusal comes back as errSecAuthFailed whether the dialog was
            // suppressed or answered with Deny, so the two are told apart by
            // which call made it rather than by the status.
            return .shut
        }
    }

    /// Chromium's safe-storage derivation, which Electron inherits.
    private static func derive(password: String) -> Data? {
        let salt = Data("saltysalt".utf8)
        var key = Data(count: kCCKeySizeAES128)
        let length = key.count
        let status = key.withUnsafeMutableBytes { keyBytes in
            Data(password.utf8).withUnsafeBytes { passwordBytes in
                salt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        passwordBytes.bindMemory(to: Int8.self).baseAddress, password.utf8.count,
                        saltBytes.bindMemory(to: UInt8.self).baseAddress, salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), 1003,
                        keyBytes.bindMemory(to: UInt8.self).baseAddress, length)
                }
            }
        }
        return status == kCCSuccess ? key : nil
    }

    private static func decrypt(_ blob: Data, key: Data) -> Data? {
        guard blob.count > 3, blob.prefix(3) == Data("v10".utf8) else { return nil }
        let payload = blob.dropFirst(3)
        let iv = Data(repeating: 0x20, count: kCCBlockSizeAES128)
        var output = Data(count: payload.count + kCCBlockSizeAES128)
        var written = 0
        let capacity = output.count
        let status = output.withUnsafeMutableBytes { out in
            payload.withUnsafeBytes { input in
                key.withUnsafeBytes { keyBytes in
                    iv.withUnsafeBytes { ivBytes in
                        CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES),
                                CCOptions(kCCOptionPKCS7Padding),
                                keyBytes.baseAddress, key.count,
                                ivBytes.baseAddress,
                                input.baseAddress, payload.count,
                                out.baseAddress, capacity, &written)
                    }
                }
            }
        }
        guard status == kCCSuccess else { return nil }
        output.count = written
        return output
    }

    // MARK: - The token

    /// Nil when this profile has no login cached at all.
    static func token(for profile: URL,
                      prompting: Prompting,
                      requiring scopes: [String] = [usageScope]) throws -> Token? {
        let config = Graft.configJSON(of: profile)
        let encoded = cacheKeys.compactMap { config[$0] as? String }.first
        guard let encoded, let blob = Data(base64Encoded: encoded) else { return nil }

        guard let key = try safeStorageKey(prompting: prompting) else {
            throw Failure.noKeychainAccess
        }
        guard let plain = decrypt(blob, key: key),
              let cache = try? JSONSerialization.jsonObject(with: plain) as? [String: Any]
        else { throw Failure.notSignedIn }

        // The cache holds one entry per client, organization and scope set. The
        // key carries the scopes; the entry carries the token itself.
        var best: Token?
        for (composite, value) in cache {
            guard let entry = value as? [String: Any],
                  let token = entry["token"] as? String,
                  let expires = entry["expiresAt"] as? Double
            else { continue }
            let candidate = Token(value: token,
                                  expires: Date(timeIntervalSince1970: expires / 1000),
                                  plan: entry["subscriptionType"] as? String,
                                  scopes: Self.scopes(in: composite))
            guard candidate.isCurrent, candidate.covers(scopes) else { continue }
            if best == nil || candidate.expires > best!.expires { best = candidate }
        }
        guard let best else { throw Failure.expired }
        return best
    }

    /// Scopes are appended to the cache key after the api host.
    private static func scopes(in composite: String) -> [String] {
        guard let marker = composite.range(of: "https://") else { return [] }
        let tail = composite[marker.upperBound...]
        guard let separator = tail.firstIndex(of: ":") else { return [] }
        return tail[tail.index(after: separator)...]
            .split(separator: " ")
            .map(String.init)
    }
}
