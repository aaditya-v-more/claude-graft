import CommonCrypto
import Foundation
import LocalAuthentication
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
/// macOS asks for permission the first time the `Claude Safe Storage` keychain
/// item is read. Background polls never prompt: they ask non-interactively and
/// give up quietly, leaving the prompt to a refresh the user asked for.
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
        case notSignedIn
        case expired

        var errorDescription: String? {
            switch self {
            case .noKeychainAccess:
                return """
                    macOS has not granted access to the “Claude Safe Storage” keychain \
                    item. Choose Always Allow when it asks, and live usage starts working.
                    """
            case .notSignedIn:
                return "No Claude Desktop login was found for this profile."
            case .expired:
                return "That profile's login has expired. Open it once so Claude can renew it."
            }
        }
    }

    static let usageScope = "user:profile"
    static let inferenceScope = "user:inference"

    private static let keychainService = "Claude Safe Storage"
    private static let keychainAccount = "Claude Key"
    private static let cacheKeys = ["oauth:tokenCacheV2", "oauth:tokenCache"]

    // MARK: - The key

    /// Cached for the process: the keychain read is the part that can prompt.
    private static var cachedKey: Data?
    private static let keyLock = NSLock()

    static func safeStorageKey(allowInteraction: Bool) throws -> Data? {
        keyLock.lock()
        if let cachedKey { keyLock.unlock(); return cachedKey }
        keyLock.unlock()

        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainAccount,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecReturnData as String: true,
        ]
        if !allowInteraction {
            let context = LAContext()
            context.interactionNotAllowed = true
            query[kSecUseAuthenticationContext as String] = context
        }

        var result: CFTypeRef?
        switch SecItemCopyMatching(query as CFDictionary, &result) {
        case errSecSuccess:
            guard let data = result as? Data,
                  let password = String(data: data, encoding: .utf8),
                  let key = derive(password: password)
            else { throw Failure.noKeychainAccess }
            keyLock.lock()
            cachedKey = key
            keyLock.unlock()
            return key
        case errSecItemNotFound:
            return nil
        default:
            throw Failure.noKeychainAccess
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
                      allowInteraction: Bool,
                      requiring scopes: [String] = [usageScope]) throws -> Token? {
        let config = Graft.configJSON(of: profile)
        let encoded = cacheKeys.compactMap { config[$0] as? String }.first
        guard let encoded, let blob = Data(base64Encoded: encoded) else { return nil }

        guard let key = try safeStorageKey(allowInteraction: allowInteraction) else {
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
