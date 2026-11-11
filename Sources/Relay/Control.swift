import Foundation

/// The relay's door for Graft: a Unix socket in a folder only this user can
/// open, checked again against the caller's user id, taking one line of JSON
/// and answering with one. Tokens arrive this way and no other.
final class ControlServer {
    typealias Handler = (PoolCommand) async -> PoolStatus

    private let path: String
    private let descriptor: Int32
    private let source: DispatchSourceRead
    private let handler: Handler

    init(path: String, handler: @escaping Handler) throws {
        self.path = path
        self.handler = handler
        unlink(path)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0, var address = PoolSocket.unixAddress(path) else {
            throw RelayError.malformed
        }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bound == 0, chmod(path, 0o600) == 0, listen(fd, 16) == 0 else {
            close(fd)
            throw RelayError.closed
        }
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        descriptor = fd
        source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: DispatchQueue(label: "relay.control"))
        source.setEventHandler { [weak self] in self?.accept() }
        source.setCancelHandler { close(fd) }
        source.resume()
    }

    private func accept() {
        while true {
            let client = Darwin.accept(descriptor, nil, nil)
            guard client >= 0 else { return }
            DispatchQueue.global().async { self.serve(client) }
        }
    }

    private func serve(_ client: Int32) {
        var uid: uid_t = 0
        var gid: gid_t = 0
        guard getpeereid(client, &uid, &gid) == 0, uid == getuid() else {
            close(client)
            return
        }
        var one: Int32 = 1
        setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        var wait = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
        let flags = fcntl(client, F_GETFL)
        _ = fcntl(client, F_SETFL, flags & ~O_NONBLOCK)

        guard let line = PoolSocket.readLine(client, limit: 1 << 20),
              let command = try? JSONDecoder().decode(PoolCommand.self, from: line)
        else {
            close(client)
            return
        }
        Task {
            let status = await self.handler(command)
            if var payload = try? JSONEncoder().encode(status) {
                payload.append(0x0A)
                _ = PoolSocket.writeAll(client, payload)
            }
            close(client)
        }
    }

    func stop() {
        source.cancel()
        unlink(path)
    }
}
