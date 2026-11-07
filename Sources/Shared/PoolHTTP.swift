import Foundation

/// As much HTTP/1.1 as the relay needs to find where one message ends and the
/// next begins, and no more. It never rewrites an answer — those go back to
/// Claude Code byte for byte, chunking and compression included — so the only
/// thing it has to understand about one is its length.
struct HTTPField: Equatable {
    var name: String
    var value: String
}

enum HTTPBody: Equatable {
    case none
    case length(Int)
    case chunked
    /// Only an answer can end this way; it lasts until the server hangs up.
    case untilClose
}

struct HTTPHead: Equatable {
    /// The request line or status line, split on its first two spaces.
    var line: [String]
    var fields: [HTTPField]

    static let terminator = Data("\r\n\r\n".utf8)

    /// How many bytes of `buffer` the head takes, blank line included, once
    /// all of it has arrived.
    static func length(in buffer: Data) -> Int? {
        guard let range = buffer.range(of: terminator) else { return nil }
        return range.upperBound - buffer.startIndex
    }

    /// Latin-1 because it maps every byte to one character and back, so a field
    /// holding UTF-8 goes out exactly as it came in.
    static func parse(_ data: Data) -> HTTPHead? {
        guard let text = String(data: data, encoding: .isoLatin1) else { return nil }
        var lines = text.components(separatedBy: "\r\n")
        while lines.last == "" { lines.removeLast() }
        guard let first = lines.first else { return nil }
        let line = first.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: false).map(String.init)
        guard line.count >= 2, !line[0].isEmpty, !line[1].isEmpty else { return nil }
        var fields: [HTTPField] = []
        for raw in lines.dropFirst() {
            if raw.first == " " || raw.first == "\t" {
                guard !fields.isEmpty else { return nil }
                fields[fields.count - 1].value += " " + raw.trimmingCharacters(in: .whitespaces)
                continue
            }
            guard let colon = raw.firstIndex(of: ":") else { return nil }
            let name = String(raw[..<colon])
            guard !name.isEmpty, !name.contains(" "), !name.contains("\t") else { return nil }
            let value = raw[raw.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            fields.append(HTTPField(name: name, value: value))
        }
        return HTTPHead(line: line, fields: fields)
    }

    var method: String { line[0] }
    var target: String { line[1] }
    var status: Int? { Int(line[1]) }
    var version: String { line[0].hasPrefix("HTTP/") ? line[0] : (line.count > 2 ? line[2] : "") }
    var pairs: [(String, String)] { fields.map { ($0.name, $0.value) } }

    func value(_ name: String) -> String? {
        fields.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    /// Every comma-separated token of a field, across however many lines it
    /// was sent on, lowercased.
    func tokens(_ name: String) -> [String] {
        fields.filter { $0.name.caseInsensitiveCompare(name) == .orderedSame }
            .flatMap { $0.value.split(separator: ",") }
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty }
    }

    /// A request to turn the connection into something other than HTTP, a
    /// WebSocket being the one that matters. Both fields have to be there,
    /// because a server honours `Upgrade` only when `Connection` names it.
    var asksToUpgrade: Bool {
        value("upgrade") != nil && tokens("connection").contains("upgrade")
    }

    var keepsAlive: Bool {
        let connection = tokens("connection")
        if version == "HTTP/1.0" { return connection.contains("keep-alive") }
        return !connection.contains("close")
    }

    /// Nil when the request cannot be framed safely, which is answered with a
    /// refusal rather than a guess: a body read to the wrong length takes the
    /// next request with it.
    var requestBody: HTTPBody? {
        if value("transfer-encoding") != nil {
            return tokens("transfer-encoding").last == "chunked" ? .chunked : nil
        }
        let lengths = Set(fields.filter { $0.name.caseInsensitiveCompare("content-length") == .orderedSame }
            .map { $0.value })
        guard lengths.count <= 1 else { return nil }
        guard let length = lengths.first else { return HTTPBody.none }
        guard let count = Int(length), count >= 0 else { return nil }
        return count == 0 ? HTTPBody.none : .length(count)
    }

    func responseBody(to method: String) -> HTTPBody {
        guard let status else { return .untilClose }
        if method == "HEAD" || (100..<200).contains(status) || status == 204 || status == 304 {
            return .none
        }
        if value("transfer-encoding") != nil {
            return tokens("transfer-encoding").last == "chunked" ? .chunked : .untilClose
        }
        if let length = value("content-length").flatMap({ Int($0) }), length >= 0 {
            return length == 0 ? .none : .length(length)
        }
        return .untilClose
    }
}

/// Follows a chunked body through however many reads it arrives in. An answer
/// is passed on as it comes, so for one of those this only says where it ends;
/// a request is read whole before anything is sent, so its payload is taken
/// out as well.
struct ChunkedFraming {
    enum Failure: Error { case malformed }

    private enum State: Equatable { case size, data(Int), dataEnd, trailer, done }
    private var state = State.size
    private var line = Data()

    var isDone: Bool { state == .done }

    /// Takes the bytes of `input` that belong to this body and returns how
    /// many that was. Anything after the end belongs to the next message.
    mutating func consume(_ input: Data, collecting payload: inout Data?) throws -> Int {
        var index = input.startIndex
        reading: while index < input.endIndex {
            switch state {
            case .done:
                break reading
            case .data(let remaining):
                let count = min(remaining, input.endIndex - index)
                payload?.append(input[index..<(index + count)])
                index += count
                state = remaining == count ? .dataEnd : .data(remaining - count)
            case .size, .dataEnd, .trailer:
                guard try takeLine(input, &index) else { break reading }
                let text = String(decoding: line, as: UTF8.self)
                line = Data()
                switch state {
                case .size:
                    let digits = text.split(separator: ";", maxSplits: 1).first.map(String.init) ?? ""
                    guard let size = Int(digits.trimmingCharacters(in: .whitespaces), radix: 16),
                          size >= 0, size < 1 << 40
                    else { throw Failure.malformed }
                    state = size == 0 ? .trailer : .data(size)
                case .dataEnd:
                    guard text.isEmpty else { throw Failure.malformed }
                    state = .size
                default:
                    if text.isEmpty { state = .done }
                }
            }
        }
        return index - input.startIndex
    }

    /// True once a whole line is in hand, its ending stripped.
    private mutating func takeLine(_ input: Data, _ index: inout Data.Index) throws -> Bool {
        if let feed = input[index...].firstIndex(of: 0x0A) {
            line.append(input[index..<feed])
            index = feed + 1
            if line.last == 0x0D { line.removeLast() }
            return true
        }
        line.append(input[index...])
        index = input.endIndex
        guard line.count <= 16384 else { throw Failure.malformed }
        return false
    }
}

enum HTTPMessage {
    /// Fields that describe one hop rather than the request, and the ones the
    /// relay writes for itself.
    static let hopFields: Set<String> = [
        "connection", "keep-alive", "proxy-connection", "proxy-authorization",
        "proxy-authenticate", "te", "trailer", "transfer-encoding", "upgrade",
        "expect", "content-length",
    ]

    /// The request as it goes upstream: the body whole with its length, every
    /// end-to-end field in the order Claude Code wrote it, and the
    /// `Authorization` line replaced only when another account is answering.
    /// `plainAnswer` asks for an answer that is not compressed, for a request
    /// whose refusal the relay must read before deciding where it goes: the
    /// service compresses even its errors for a client that accepts it.
    static func request(_ head: HTTPHead, body: Data, authorization: String?,
                        plainAnswer: Bool = false) -> Data {
        let named = Set(head.tokens("connection"))
        var text = "\(head.method) \(head.target) HTTP/1.1\r\n"
        var sawHost = false
        for field in head.fields {
            let lower = field.name.lowercased()
            if hopFields.contains(lower) || named.contains(lower) { continue }
            if lower == "authorization", authorization != nil { continue }
            if lower == "accept-encoding", plainAnswer { continue }
            if lower == "host" { sawHost = true }
            text += "\(field.name): \(field.value)\r\n"
        }
        if !sawHost { text += "Host: \(UsagePool.host)\r\n" }
        if let authorization { text += "Authorization: \(authorization)\r\n" }
        if plainAnswer { text += "Accept-Encoding: identity\r\n" }
        if !body.isEmpty || ["POST", "PUT", "PATCH"].contains(head.method) {
            text += "Content-Length: \(body.count)\r\n"
        }
        text += "\r\n"
        var data = text.data(using: .isoLatin1) ?? Data(text.utf8)
        data.append(body)
        return data
    }

    /// An answer the relay makes up itself, shaped like the service's own
    /// errors so Claude Code shows the sentence rather than a parse failure.
    static func failure(_ status: Int, _ reason: String, _ message: String) -> Data {
        let error: [String: Any] = ["type": "error",
                                    "error": ["type": "api_error", "message": message]]
        let body = (try? JSONSerialization.data(withJSONObject: error)) ?? Data()
        var head = "HTTP/1.1 \(status) \(reason)\r\n"
        head += "Content-Type: application/json\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        return Data(head.utf8) + body
    }
}
