import CryptoKit
import Foundation
import Network
import Security

/// The usage pool's relay: a proxy on loopback that Claude Code is pointed at
/// through its own environment settings. See `UsagePool` for what it changes
/// and why only that.
///
/// It is a process of its own, rather than part of the app, so that it goes
/// on answering while Graft is quit — a Claude told to use a proxy that is not
/// there cannot reach the service at all — and so that an update to Graft
/// replaces it deliberately rather than by accident.
final class Relay {
    struct Options {
        var port = UsagePool.port
        var socket: String?
        var upstream = Upstream.service
        /// How often the relay looks for its own file. A test shortens it.
        var removalCheck: TimeInterval = 60
    }

    private let options: Options
    private let state = PoolState()
    private let started = Date()
    private let queue = DispatchQueue(label: "relay")
    private var binary = ""
    private var front: NWListener?
    private var inner: NWListener?
    private var innerPort: NWEndpoint.Port?
    private var control: ControlServer?
    private var lock: Int32 = -1
    private var quitting = false
    private var watch: DispatchSourceTimer?
    private var missing = 0
    private var remembered: PoolEnvironment.Journal?

    init(options: Options) {
        self.options = options
    }

    func start() {
        let directory = UsagePool.directory
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        // One relay at a time, settled before anything is bound: the one
        // stepping aside lets go of this as it stops listening, and a second
        // copy started by mistake leaves without touching the socket the first
        // is answering on. Settled before the certificate as well: a second
        // copy that renewed it on its way to finding the lock taken would
        // leave the running relay presenting a certificate from an authority
        // every session started afterwards no longer trusts.
        lock = open(directory.appending(path: "relay.lock").path, O_CREAT | O_RDWR, 0o600)
        guard lock >= 0, flock(lock, LOCK_EX | LOCK_NB) == 0 else {
            Diagnostics.note("relay already running", [:])
            exit(0)
        }
        do {
            try PoolCertificates.ensure()
        } catch {
            fail("certificate", error)
        }
        binary = Relay.digest(of: Relay.executable())

        let identity: SecIdentity
        do {
            identity = try Relay.identity()
        } catch {
            fail("identity", error)
        }
        do {
            inner = try innerListener(identity)
        } catch {
            fail("inner listener", error)
        }
    }

    private func fail(_ stage: String, _ error: Error) -> Never {
        Diagnostics.note("relay could not start", ["stage": stage, "error": "\(error)"])
        exit(1)
    }

    /// Where the TLS inside an intercepted tunnel is taken apart. A listener
    /// cannot take over a connection already accepted as plain TCP, so the
    /// tunnel's bytes are handed across loopback to this one instead.
    private func innerListener(_ identity: SecIdentity) throws -> NWListener {
        let tls = NWProtocolTLS.Options()
        let security = tls.securityProtocolOptions
        sec_protocol_options_set_local_identity(security, sec_identity_create(identity)!)
        sec_protocol_options_add_tls_application_protocol(security, "http/1.1")
        sec_protocol_options_set_min_tls_protocol_version(security, .TLSv12)
        let parameters = NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
        parameters.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: .any)
        parameters.acceptLocalOnly = true
        let listener = try NWListener(using: parameters)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            let exchange = Exchange(client: Wire(connection, label: "relay.exchange"),
                                    state: self.state, upstream: self.options.upstream)
            Task { await exchange.run() }
        }
        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.innerPort = listener.port
                self.startFront()
            case .failed(let error):
                self.fail("inner listener", error)
            default:
                break
            }
        }
        listener.start(queue: queue)
        return listener
    }

    private func startFront() {
        guard front == nil else { return }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback),
                                                     port: NWEndpoint.Port(rawValue: options.port)!)
        parameters.acceptLocalOnly = true
        // Connections closed by the relay sit in TIME_WAIT on this port for a
        // while, and the relay that replaces this one must still be able to
        // bind it. The lock, not the port, is what keeps there to one.
        parameters.allowLocalEndpointReuse = true
        do {
            let listener = try NWListener(using: parameters)
            listener.newConnectionHandler = { [weak self] connection in
                guard let self else { return }
                Task { await self.tunnel(connection) }
            }
            listener.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    self.startControl()
                case .failed(let error):
                    self.fail("front listener", error)
                default:
                    break
                }
            }
            listener.start(queue: queue)
            front = listener
        } catch {
            fail("front listener", error)
        }
    }

    private func startControl() {
        guard control == nil else { return }
        do {
            control = try ControlServer(path: options.socket ?? UsagePool.socketFile.path) { [weak self] command in
                guard let self else { return PoolStatus.empty }
                return await self.handle(command)
            }
            Diagnostics.note("relay started", ["port": Int(options.port), "binary": String(binary.prefix(12))])
        } catch {
            fail("control socket", error)
        }
        watchForRemoval()
    }

    /// The relay outlives Graft, so it is the one left to notice Graft has
    /// gone: trashed, uninstalled, or replaced by a version from before
    /// sharing, whose bundle carries no relay. Each Claude that borrowed still
    /// holds the lines, and once this relay goes — at the next logout, since
    /// nothing will start it again — every Claude Code request there fails.
    /// So once its own file has been missing at two looks a minute apart,
    /// which an update swapping the bundle never is, it stops lending, puts
    /// back each closed Claude's environment as it was, and leaves once none
    /// still points at it.
    private func watchForRemoval() {
        let path = Relay.executable().path
        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "relay.removal"))
        timer.schedule(deadline: .now() + options.removalCheck, repeating: options.removalCheck)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if FileManager.default.fileExists(atPath: path) {
                self.missing = 0
                // Kept for the zap that follows an uninstall, which takes the
                // journal with it while the Claudes it describes are still open.
                if let journal = PoolEnvironment.journalOnDisk() { self.remembered = journal }
                return
            }
            self.missing += 1
            guard self.missing >= 2 else { return }
            if self.missing == 2 {
                Task { await self.state.configure(PoolConfiguration(enabled: false, members: [])) }
            }
            let left = PoolEnvironment.takeOutLines(self.remembered, running: Graft.isRunning(profile:))
            guard left.isEmpty else { return }
            Task {
                guard await self.state.inFlight == 0 else { return }
                self.control?.stop()
                exit(0)
            }
        }
        timer.resume()
        watch = timer
    }

    private func handle(_ command: PoolCommand) async -> PoolStatus {
        if let configuration = command.configure {
            await state.configure(configuration)
        }
        if command.quit == true {
            await stepAside()
        }
        return await state.status(binary: binary, pid: getpid(), started: started)
    }

    /// Stops listening at once and lets go of the lock, so the relay replacing
    /// this one can start the moment Graft hears back; then leaves when the
    /// last request in flight has finished, or after a quarter of an hour,
    /// whichever comes first. An idle connection Claude Code keeps open is not
    /// in flight, and it reconnects to the new relay on its next request. An
    /// upgraded one is, for as long as it lasts, so a socket Remote Control
    /// holds open is cut no sooner than the quarter hour, and then reconnects
    /// to the new relay the way it would after any dropped network.
    private func stepAside() async {
        guard !quitting else { return }
        quitting = true
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let once = Once()
            front?.stateUpdateHandler = { state in
                if case .cancelled = state { once.run { continuation.resume() } }
            }
            queue.asyncAfter(deadline: .now() + 2) { once.run { continuation.resume() } }
            if let front { front.cancel() } else { once.run { continuation.resume() } }
        }
        inner?.cancel()
        control?.stop()
        close(lock)
        Diagnostics.note("relay stepping aside", [:])
        let deadline = Date().addingTimeInterval(15 * 60)
        Task.detached { [state] in
            try? await Task.sleep(nanoseconds: 500_000_000)
            while await state.inFlight > 0, Date() < deadline {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
            exit(0)
        }
    }

    /// A CONNECT for the service is opened; every other is passed through
    /// untouched, and so is an `http://` request in absolute form, for a
    /// client that sends everything to the one proxy it was given.
    private func tunnel(_ connection: NWConnection) async {
        let client = Wire(connection, label: "relay.front")
        do {
            try await client.open(timeout: 10)
            guard let (head, raw) = try await client.readHead(limit: 16384) else {
                client.close()
                return
            }
            if head.method == "CONNECT" {
                guard let (host, port) = Relay.authority(head.target) else {
                    try? await client.send(HTTPMessage.failure(400, "Bad Request", "The relay could not read that address."))
                    client.close()
                    return
                }
                let intercept = host.lowercased() == UsagePool.host && port == 443
                guard let far = await reach(intercept ? nil : (host, port)) else {
                    try? await client.send(HTTPMessage.failure(502, "Bad Gateway", "The relay could not reach \(host)."))
                    client.close()
                    return
                }
                try await client.send(Data("HTTP/1.1 200 Connection Established\r\n\r\n".utf8))
                await splice(client, far)
            } else if let url = URL(string: head.target), url.scheme == "http", let host = url.host {
                guard let far = await reach((host, UInt16(url.port ?? 80))) else {
                    try? await client.send(HTTPMessage.failure(502, "Bad Gateway", "The relay could not reach \(host)."))
                    client.close()
                    return
                }
                try await far.send(raw)
                await splice(client, far)
            } else {
                try? await client.send(HTTPMessage.failure(400, "Bad Request", "The relay only forwards CONNECT."))
                client.close()
            }
        } catch {
            client.close()
        }
    }

    /// Nil asks for the listener that takes the service's TLS apart.
    private func reach(_ target: (String, UInt16)?) async -> Wire? {
        let connection: NWConnection
        if let (host, port) = target {
            guard let port = NWEndpoint.Port(rawValue: port) else { return nil }
            connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
        } else {
            guard let innerPort else { return nil }
            connection = NWConnection(host: .ipv4(.loopback), port: innerPort, using: .tcp)
        }
        let wire = Wire(connection, label: "relay.far")
        do {
            try await wire.open(timeout: 20)
            return wire
        } catch {
            return nil
        }
    }

    static func authority(_ target: String) -> (String, UInt16)? {
        if target.hasPrefix("["), let close = target.firstIndex(of: "]") {
            let host = String(target[target.index(after: target.startIndex)..<close])
            let rest = target[target.index(after: close)...]
            guard rest.hasPrefix(":"), let port = UInt16(rest.dropFirst()) else { return nil }
            return (host, port)
        }
        guard let colon = target.lastIndex(of: ":"), let port = UInt16(target[target.index(after: colon)...])
        else { return nil }
        return (String(target[..<colon]), port)
    }

    /// Asked of the loader rather than of `Bundle.main`, which for a tool
    /// sitting in an app's Resources answers for the app around it.
    static func executable() -> URL {
        var size: UInt32 = 0
        _NSGetExecutablePath(nil, &size)
        var buffer = [CChar](repeating: 0, count: Int(size) + 1)
        guard _NSGetExecutablePath(&buffer, &size) == 0 else {
            return URL(fileURLWithPath: CommandLine.arguments[0])
        }
        return URL(fileURLWithPath: String(cString: buffer)).resolvingSymlinksInPath()
    }

    static func digest(of file: URL) -> String {
        guard let data = try? Data(contentsOf: file, options: .alwaysMapped) else { return "" }
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    /// Imported into memory alone. Without that flag the import writes the
    /// private key into the login keychain, which is somebody else's to fill,
    /// and on an older system there is no way to have one without the other.
    static func identity() throws -> SecIdentity {
        guard #available(macOS 15.0, *) else { throw RelayError.malformed }
        let data = try Data(contentsOf: UsagePool.identityFile)
        let options: [String: Any] = [
            kSecImportExportPassphrase as String: UsagePool.identityPassphrase,
            kSecImportToMemoryOnly as String: true,
        ]
        var items: CFArray?
        let status = SecPKCS12Import(data as CFData, options as CFDictionary, &items)
        guard status == errSecSuccess,
              let first = (items as? [[String: Any]])?.first,
              let identity = first[kSecImportItemIdentity as String]
        else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
        return identity as! SecIdentity
    }
}

extension PoolStatus {
    static let empty = PoolStatus(binary: "", pid: getpid(), started: Date(), enabled: false,
                                  configured: false, members: [], recent: [], sessions: [],
                                  openExchanges: 0)
}

// A client that goes away mid-answer is heard about from the failed write; the
// signal would end the process instead.
signal(SIGPIPE, SIG_IGN)

var options = Relay.Options()
var arguments = CommandLine.arguments.dropFirst().makeIterator()
while let argument = arguments.next() {
    switch argument {
    case "--support":
        if let path = arguments.next() { Graft.applicationSupportOverride = URL(fileURLWithPath: path) }
    case "--port":
        if let value = arguments.next().flatMap(UInt16.init) { options.port = value }
    case "--socket":
        // Relative on purpose for a test: a socket path longer than 103 bytes
        // cannot be bound, and a scratch directory easily runs to that.
        if let path = arguments.next() { options.socket = path }
    case "--upstream-loopback-port":
        if let value = arguments.next().flatMap(UInt16.init) { options.upstream = .loopback(value) }
    case "--removal-check":
        if let value = arguments.next().flatMap(TimeInterval.init), value > 0 { options.removalCheck = value }
    default:
        break
    }
}
Diagnostics.who = "graft-relay"
let relay = Relay(options: options)
relay.start()
dispatchMain()
