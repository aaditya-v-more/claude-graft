//! As much HTTP/1.1 as the relay needs to find where one message ends and the
//! next begins, and no more. It never rewrites an answer, which goes back to
//! Claude Code byte for byte, chunking and compression included, so the only
//! thing it has to understand about one is its length.

use graft_pool::HOST;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Body {
    None,
    Length(usize),
    Chunked,
    /// Only an answer can end this way; it lasts until the server hangs up.
    UntilClose,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Head {
    /// The request line or status line, split on its first two spaces.
    pub line: Vec<String>,
    pub fields: Vec<(String, String)>,
}

pub const TERMINATOR: &[u8] = b"\r\n\r\n";

/// How many bytes of `buffer` the head takes, blank line included, once all
/// of it has arrived.
pub fn head_length(buffer: &[u8]) -> Option<usize> {
    buffer.windows(4).position(|w| w == TERMINATOR).map(|at| at + 4)
}

/// Latin-1 because it maps every byte to one character and back, so a field
/// holding UTF-8 goes out exactly as it came in.
fn latin1(bytes: &[u8]) -> String {
    bytes.iter().map(|b| *b as char).collect()
}

pub fn to_latin1(text: &str) -> Vec<u8> {
    text.chars().map(|c| if (c as u32) < 256 { c as u8 } else { b'?' }).collect()
}

impl Head {
    pub fn parse(data: &[u8]) -> Option<Head> {
        let text = latin1(data);
        let mut lines: Vec<&str> = text.split("\r\n").collect();
        while lines.last() == Some(&"") {
            lines.pop();
        }
        let first = lines.first()?;
        let line: Vec<String> = first.splitn(3, ' ').map(str::to_string).collect();
        if line.len() < 2 || line[0].is_empty() || line[1].is_empty() {
            return None;
        }
        let mut fields: Vec<(String, String)> = Vec::new();
        for raw in &lines[1..] {
            if raw.starts_with(' ') || raw.starts_with('\t') {
                let last = fields.last_mut()?;
                last.1.push(' ');
                last.1.push_str(raw.trim_matches([' ', '\t']));
                continue;
            }
            let colon = raw.find(':')?;
            let name = &raw[..colon];
            if name.is_empty() || name.contains([' ', '\t']) {
                return None;
            }
            fields.push((name.to_string(), raw[colon + 1..].trim_matches([' ', '\t']).to_string()));
        }
        Some(Head { line, fields })
    }

    pub fn method(&self) -> &str {
        &self.line[0]
    }

    pub fn target(&self) -> &str {
        &self.line[1]
    }

    pub fn status(&self) -> Option<u16> {
        self.line[1].parse().ok()
    }

    pub fn version(&self) -> &str {
        if self.line[0].starts_with("HTTP/") {
            &self.line[0]
        } else {
            self.line.get(2).map(String::as_str).unwrap_or("")
        }
    }

    pub fn value(&self, name: &str) -> Option<&str> {
        self.fields.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    /// Every comma-separated token of a field, across however many lines it
    /// was sent on, lowercased.
    pub fn tokens(&self, name: &str) -> Vec<String> {
        self.fields
            .iter()
            .filter(|(n, _)| n.eq_ignore_ascii_case(name))
            .flat_map(|(_, v)| v.split(','))
            .map(|t| t.trim_matches([' ', '\t']).to_ascii_lowercase())
            .filter(|t| !t.is_empty())
            .collect()
    }

    /// A request to turn the connection into something other than HTTP, a
    /// WebSocket being the one that matters. Both fields have to be there,
    /// because a server honours `Upgrade` only when `Connection` names it.
    pub fn asks_to_upgrade(&self) -> bool {
        self.value("upgrade").is_some() && self.tokens("connection").iter().any(|t| t == "upgrade")
    }

    pub fn keeps_alive(&self) -> bool {
        let connection = self.tokens("connection");
        if self.version() == "HTTP/1.0" {
            return connection.iter().any(|t| t == "keep-alive");
        }
        !connection.iter().any(|t| t == "close")
    }

    /// `None` when the request cannot be framed safely, which is answered with
    /// a refusal rather than a guess: a body read to the wrong length takes the
    /// next request with it.
    pub fn request_body(&self) -> Option<Body> {
        if self.value("transfer-encoding").is_some() {
            return (self.tokens("transfer-encoding").last().map(String::as_str) == Some("chunked")).then_some(Body::Chunked);
        }
        let mut lengths: Vec<&str> =
            self.fields.iter().filter(|(n, _)| n.eq_ignore_ascii_case("content-length")).map(|(_, v)| v.as_str()).collect();
        lengths.sort();
        lengths.dedup();
        if lengths.len() > 1 {
            return None;
        }
        let Some(length) = lengths.first() else { return Some(Body::None) };
        let count: usize = length.parse().ok()?;
        Some(if count == 0 { Body::None } else { Body::Length(count) })
    }

    pub fn response_body(&self, method: &str) -> Body {
        let Some(status) = self.status() else { return Body::UntilClose };
        if method == "HEAD" || (100..200).contains(&status) || status == 204 || status == 304 {
            return Body::None;
        }
        if self.value("transfer-encoding").is_some() {
            return if self.tokens("transfer-encoding").last().map(String::as_str) == Some("chunked") {
                Body::Chunked
            } else {
                Body::UntilClose
            };
        }
        match self.value("content-length").and_then(|v| v.parse::<usize>().ok()) {
            Some(0) => Body::None,
            Some(length) => Body::Length(length),
            None => Body::UntilClose,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Malformed;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ChunkState {
    Size,
    Data(usize),
    DataEnd,
    Trailer,
    Done,
}

/// Follows a chunked body through however many reads it arrives in. An answer
/// is passed on as it comes, so for one of those this only says where it ends;
/// a request is read whole before anything is sent, so its payload is taken
/// out as well.
#[derive(Debug, Clone)]
pub struct ChunkedFraming {
    state: ChunkState,
    line: Vec<u8>,
}

impl Default for ChunkedFraming {
    fn default() -> Self {
        ChunkedFraming { state: ChunkState::Size, line: Vec::new() }
    }
}

impl ChunkedFraming {
    pub fn is_done(&self) -> bool {
        self.state == ChunkState::Done
    }

    /// Takes the bytes of `input` that belong to this body and returns how
    /// many that was. Anything after the end belongs to the next message.
    pub fn consume(&mut self, input: &[u8], mut payload: Option<&mut Vec<u8>>) -> Result<usize, Malformed> {
        let mut index = 0;
        while index < input.len() {
            match self.state {
                ChunkState::Done => break,
                ChunkState::Data(remaining) => {
                    let count = remaining.min(input.len() - index);
                    if let Some(p) = payload.as_deref_mut() {
                        p.extend_from_slice(&input[index..index + count]);
                    }
                    index += count;
                    self.state = if remaining == count { ChunkState::DataEnd } else { ChunkState::Data(remaining - count) };
                }
                ChunkState::Size | ChunkState::DataEnd | ChunkState::Trailer => {
                    if !self.take_line(input, &mut index)? {
                        break;
                    }
                    let text = String::from_utf8_lossy(&self.line).into_owned();
                    self.line.clear();
                    match self.state {
                        ChunkState::Size => {
                            let digits = text.split(';').next().unwrap_or("").trim();
                            let size = usize::from_str_radix(digits, 16).map_err(|_| Malformed)?;
                            if size >= 1 << 40 {
                                return Err(Malformed);
                            }
                            self.state = if size == 0 { ChunkState::Trailer } else { ChunkState::Data(size) };
                        }
                        ChunkState::DataEnd => {
                            if !text.is_empty() {
                                return Err(Malformed);
                            }
                            self.state = ChunkState::Size;
                        }
                        _ => {
                            if text.is_empty() {
                                self.state = ChunkState::Done;
                            }
                        }
                    }
                }
            }
        }
        Ok(index)
    }

    /// True once a whole line is in hand, its ending stripped.
    fn take_line(&mut self, input: &[u8], index: &mut usize) -> Result<bool, Malformed> {
        if let Some(feed) = input[*index..].iter().position(|b| *b == b'\n') {
            self.line.extend_from_slice(&input[*index..*index + feed]);
            *index += feed + 1;
            if self.line.last() == Some(&b'\r') {
                self.line.pop();
            }
            return Ok(true);
        }
        self.line.extend_from_slice(&input[*index..]);
        *index = input.len();
        if self.line.len() > 16384 {
            return Err(Malformed);
        }
        Ok(false)
    }
}

/// Fields that describe one hop rather than the request, and the ones the
/// relay writes for itself.
pub const HOP_FIELDS: [&str; 11] = [
    "connection",
    "keep-alive",
    "proxy-connection",
    "proxy-authorization",
    "proxy-authenticate",
    "te",
    "trailer",
    "transfer-encoding",
    "upgrade",
    "expect",
    "content-length",
];

/// The request as it goes upstream: the body whole with its length, every
/// end-to-end field in the order Claude Code wrote it, and the
/// `Authorization` line replaced only when another account is answering.
/// `plain_answer` asks for an answer that is not compressed, for a request
/// whose refusal the relay must read before deciding where it goes: the
/// service compresses even its errors for a client that accepts it.
pub fn request(head: &Head, body: &[u8], authorization: Option<&str>, plain_answer: bool) -> Vec<u8> {
    let named = head.tokens("connection");
    let mut text = format!("{} {} HTTP/1.1\r\n", head.method(), head.target());
    let mut saw_host = false;
    for (name, value) in &head.fields {
        let lower = name.to_ascii_lowercase();
        if HOP_FIELDS.contains(&lower.as_str()) || named.contains(&lower) {
            continue;
        }
        if lower == "authorization" && authorization.is_some() {
            continue;
        }
        if lower == "accept-encoding" && plain_answer {
            continue;
        }
        if lower == "host" {
            saw_host = true;
        }
        text.push_str(&format!("{name}: {value}\r\n"));
    }
    if !saw_host {
        text.push_str(&format!("Host: {HOST}\r\n"));
    }
    if let Some(authorization) = authorization {
        text.push_str(&format!("Authorization: {authorization}\r\n"));
    }
    if plain_answer {
        text.push_str("Accept-Encoding: identity\r\n");
    }
    if !body.is_empty() || ["POST", "PUT", "PATCH"].contains(&head.method()) {
        text.push_str(&format!("Content-Length: {}\r\n", body.len()));
    }
    text.push_str("\r\n");
    let mut data = to_latin1(&text);
    data.extend_from_slice(body);
    data
}

/// An answer the relay makes up itself, shaped like the service's own errors
/// so Claude Code shows the sentence rather than a parse failure.
pub fn failure(status: u16, reason: &str, message: &str) -> Vec<u8> {
    let body = serde_json::to_vec(&serde_json::json!({"type": "error", "error": {"type": "api_error", "message": message}}))
        .unwrap_or_default();
    let mut out = format!(
        "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        body.len()
    )
    .into_bytes();
    out.extend_from_slice(&body);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sent() -> Head {
        Head::parse(
            b"POST /v1/messages?beta=true HTTP/1.1\r\nHost: api.anthropic.com\r\nContent-Length: 12\r\n\
              Connection: keep-alive, x-hop\r\nX-Hop: 1\r\nAuthorization: Bearer mine\r\nX-Claude-Code-Session-Id: s1\r\n\r\n",
        )
        .unwrap()
    }

    #[test]
    fn a_requests_length_is_read_from_its_head() {
        assert_eq!(sent().request_body(), Some(Body::Length(12)));
    }

    #[test]
    fn two_lengths_that_disagree_are_refused_rather_than_guessed_between() {
        let head = Head::parse(b"POST / HTTP/1.1\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\n").unwrap();
        assert_eq!(head.request_body(), None);
    }

    #[test]
    fn a_chunked_request_is_read_as_chunks() {
        let head = Head::parse(b"POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n").unwrap();
        assert_eq!(head.request_body(), Some(Body::Chunked));
    }

    #[test]
    fn a_request_for_a_socket_is_known_as_one() {
        let ws = Head::parse(b"GET /ws HTTP/1.1\r\nConnection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n\r\n").unwrap();
        assert!(ws.asks_to_upgrade(), "so the relay passes it on rather than rebuilding it");
        let bare = Head::parse(b"GET /ws HTTP/1.1\r\nUpgrade: websocket\r\n\r\n").unwrap();
        assert!(!bare.asks_to_upgrade(), "while an Upgrade the connection does not name asks for nothing");
    }

    #[test]
    fn a_stream_with_neither_a_length_nor_chunks_runs_until_the_connection_closes() {
        let stream = Head::parse(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n").unwrap();
        assert_eq!(stream.response_body("POST"), Body::UntilClose);
        assert_eq!(stream.response_body("HEAD"), Body::None, "an answer to HEAD has no body whatever it says");
        let empty = Head::parse(b"HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n").unwrap();
        assert_eq!(empty.response_body("POST"), Body::None, "and neither has a 204");
    }

    #[test]
    fn a_lent_request_carries_the_lenders_token_and_not_the_senders() {
        let lent = String::from_utf8(request(&sent(), b"hello", Some("Bearer theirs"), false)).unwrap();
        assert!(lent.contains("Authorization: Bearer theirs\r\n") && !lent.contains("mine"));
        assert!(lent.contains("X-Claude-Code-Session-Id: s1\r\n"), "every end-to-end field goes on as it came");
        let lower = lent.to_ascii_lowercase();
        assert!(!lower.contains("x-hop") && !lower.contains("connection:"), "the hop's own fields, and any it names, do not");
        assert!(lent.contains("Content-Length: 5\r\n") && lent.ends_with("\r\n\r\nhello"), "the body goes whole, with a length");
        let kept = String::from_utf8(request(&sent(), b"hello", None, false)).unwrap();
        assert!(kept.contains("Authorization: Bearer mine\r\n"), "a request kept by its sender keeps its own token");
        let plain = String::from_utf8(request(&sent(), b"", None, true)).unwrap();
        assert!(plain.contains("Accept-Encoding: identity\r\n"), "and one whose refusal must be read asks for it plain");
    }

    #[test]
    fn a_field_holding_utf8_goes_out_exactly_as_it_came_in() {
        let raw = "GET / HTTP/1.1\r\nX-Title: caf\u{e9}\r\n\r\n".as_bytes();
        let head = Head::parse(raw).unwrap();
        let out = request(&head, b"", None, false);
        let needle = "X-Title: caf\u{e9}".as_bytes();
        assert!(out.windows(needle.len()).any(|w| w == needle));
    }

    #[test]
    fn a_chunked_body_is_read_whole_however_its_bytes_arrive() {
        let wire = b"5\r\nhello\r\n6;x=1\r\n world\r\n0\r\nTrailer: y\r\n\r\nNEXT";
        let mut framing = ChunkedFraming::default();
        let mut payload = Vec::new();
        let mut used = 0;
        for byte in wire.iter() {
            if framing.is_done() {
                break;
            }
            used += framing.consume(&[*byte], Some(&mut payload)).unwrap();
        }
        assert!(framing.is_done() && payload == b"hello world");
        assert_eq!(used, wire.len() - 4, "and nothing after its end is taken from the next message");
        assert!(ChunkedFraming::default().consume(b"zz\r\n", None).is_err(), "a chunk size that is not hex is refused");
    }
}
