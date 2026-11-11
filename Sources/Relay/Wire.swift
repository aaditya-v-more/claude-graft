import Foundation
import Network

enum RelayError: Error {
    case closed
    case timedOut
    case truncated
    case malformed
    case tooLarge
}

/// Runs a closure once, however many of a connection's callbacks race to it.
final class Once: @unchecked Sendable {
    private var done = false
    private let lock = NSLock()

    func run(_ body: () -> Void) {
        lock.lock()
        let first = !done
        done = true
        lock.unlock()
        if first { body() }
    }
}

/// One connection, read into a buffer and written with backpressure. Every
/// send waits for the network to take the bytes before the next is made, so a
/// slow reader on one side slows the other rather than filling memory.
///
/// The buffer has one reader at a time — whoever is awaiting `fill` — which
/// is what the unchecked conformance is vouching for.
final class Wire: @unchecked Sendable {
    let connection: NWConnection
    private let queue: DispatchQueue
    private(set) var buffer = Data()
    private(set) var ended = false
    /// Bytes ever received, so a reused connection that dies can be told
    /// apart from one that died partway through an answer.
    private(set) var received = 0

    init(_ connection: NWConnection, label: String) {
        self.connection = connection
        self.queue = DispatchQueue(label: label)
    }

    /// `waiting` counts as failure: it is how a refused or unroutable
    /// connection reports itself, and the relay has somebody waiting on it.
    func open(timeout: TimeInterval) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let once = Once()
            connection.stateUpdateHandler = { [weak connection] state in
                switch state {
                case .ready:
                    once.run { continuation.resume() }
                case .waiting(let error), .failed(let error):
                    once.run {
                        connection?.cancel()
                        continuation.resume(throwing: error)
                    }
                case .cancelled:
                    once.run { continuation.resume(throwing: RelayError.closed) }
                default:
                    break
                }
            }
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + timeout) { [weak connection] in
                once.run {
                    connection?.cancel()
                    continuation.resume(throwing: RelayError.timedOut)
                }
            }
        }
    }

    /// Reads whatever arrives next into the buffer. False once the other side
    /// has finished sending.
    func fill() async throws -> Bool {
        if ended { return false }
        return try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 18) { data, _, complete, error in
                let got = data?.count ?? 0
                if got > 0 {
                    self.buffer.append(data!)
                    self.received += got
                }
                if complete { self.ended = true }
                if let error, got == 0 {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume(returning: got > 0 || !complete)
                }
            }
        }
    }

    /// Re-based, so every index into the buffer starts at zero.
    func take(_ count: Int) -> Data {
        let taken = Data(buffer.prefix(count))
        buffer = buffer.advanced(by: min(count, buffer.count))
        return taken
    }

    func takeAll() -> Data { take(buffer.count) }

    /// Nil on a clean end before a single byte of a new head.
    func readHead(limit: Int) async throws -> (head: HTTPHead, raw: Data)? {
        while true {
            if let length = HTTPHead.length(in: buffer) {
                let raw = take(length)
                guard let head = HTTPHead.parse(raw) else { throw RelayError.malformed }
                return (head, raw)
            }
            guard buffer.count <= limit else { throw RelayError.tooLarge }
            guard try await fill() else {
                if buffer.isEmpty { return nil }
                throw RelayError.truncated
            }
        }
    }

    /// A request is read whole, because it may have to be sent twice.
    func readBody(_ framing: HTTPBody, limit: Int) async throws -> Data {
        switch framing {
        case .none:
            return Data()
        case .length(let count):
            guard count <= limit else { throw RelayError.tooLarge }
            while buffer.count < count {
                guard try await fill() else { throw RelayError.truncated }
            }
            return take(count)
        case .chunked:
            var framing = ChunkedFraming()
            var body: Data? = Data()
            while true {
                if !buffer.isEmpty { _ = take(try framing.consume(buffer, collecting: &body)) }
                if framing.isDone { return body ?? Data() }
                guard (body?.count ?? 0) <= limit else { throw RelayError.tooLarge }
                guard try await fill() else { throw RelayError.truncated }
            }
        case .untilClose:
            throw RelayError.malformed
        }
    }

    func send(_ data: Data) async throws {
        guard !data.isEmpty else { return }
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            })
        }
    }

    /// Says this side has nothing more to send, leaving the other direction
    /// open for whatever is still on its way back.
    func finish() {
        connection.send(content: nil, contentContext: .finalMessage, isComplete: true,
                        completion: .idempotent)
    }

    func close() {
        connection.cancel()
    }
}

/// Bytes both ways until both sides are done. A tunnel the relay does not
/// open is passed on untouched, and so is the TLS inside the one it does,
/// on its way to the listener that takes it apart.
func splice(_ a: Wire, _ b: Wire) async {
    await withTaskGroup(of: Void.self) { group in
        group.addTask { await pump(from: a, to: b) }
        group.addTask { await pump(from: b, to: a) }
    }
    a.close()
    b.close()
}

private func pump(from source: Wire, to sink: Wire) async {
    do {
        if !source.buffer.isEmpty { try await sink.send(source.takeAll()) }
        while try await source.fill() {
            try await sink.send(source.takeAll())
        }
        sink.finish()
    } catch {
        source.close()
        sink.close()
    }
}
