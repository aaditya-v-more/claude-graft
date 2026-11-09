//! One TLS connection from Claude Code, and every request that comes down it.
//!
//! A model request is read whole before anything is sent, because it may have
//! to go out more than once; anything else goes as it arrives. The answer is
//! never held: once an attempt is the one being delivered, its bytes go back
//! as they arrive, which is what keeps a streamed reply streaming.

use crate::conn::{self, Boxed, Reader, RelayError, Result, Writer};
use crate::http::{self, Body, ChunkedFraming, Head};
use crate::relay::Shared;
use crate::state::{Attempt, Served};
use graft_pool::policy::{self, Outcome};
use graft_pool::{now, request, PassReason, PoolPass, HOST};
use serde_json::{json, Value};
use std::sync::Arc;
use std::time::Duration;
use tokio::net::TcpStream;

pub const HEAD_LIMIT: usize = 64 * 1024;
/// Far beyond any request with a long history and a few images in it; past
/// this a body is something other than a conversation.
pub const BODY_LIMIT: usize = 256 * 1024 * 1024;

/// Where requests for the service go. Tests point this at a plain listener on
/// loopback; nothing else can, so the relay never speaks to the real service
/// without TLS.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Upstream {
    Service,
    Loopback(u16),
}

impl Upstream {
    pub async fn connect(&self, connector: &tokio_rustls::TlsConnector) -> Result<(Reader, Writer)> {
        let patience = Duration::from_secs(20);
        match self {
            Upstream::Service => {
                let tcp = tokio::time::timeout(patience, TcpStream::connect((HOST, 443)))
                    .await
                    .map_err(|_| RelayError::TimedOut)??;
                let _ = tcp.set_nodelay(true);
                let name = rustls::pki_types::ServerName::try_from(HOST).map_err(|_| RelayError::Malformed)?;
                let tls = tokio::time::timeout(patience, connector.connect(name, tcp))
                    .await
                    .map_err(|_| RelayError::TimedOut)??;
                Ok(conn::split(Box::new(tls) as Boxed))
            }
            Upstream::Loopback(port) => {
                let tcp = tokio::time::timeout(patience, TcpStream::connect(("127.0.0.1", *port)))
                    .await
                    .map_err(|_| RelayError::TimedOut)??;
                let _ = tcp.set_nodelay(true);
                Ok(conn::split(Box::new(tcp) as Boxed))
            }
        }
    }
}

struct Answer {
    head: Head,
    raw: Vec<u8>,
}

/// Counts an exchange as in flight for as long as it is, however it ends.
struct InFlight(Arc<Shared>);

impl InFlight {
    fn begin(shared: &Arc<Shared>) -> InFlight {
        shared.state().began();
        InFlight(shared.clone())
    }
}

impl Drop for InFlight {
    fn drop(&mut self) {
        self.0.state().ended();
    }
}

pub struct Exchange {
    client_r: Reader,
    client_w: Writer,
    shared: Arc<Shared>,
    connection: Option<(Reader, Writer)>,
    /// The error type of the last refusal read and found not to be a missing
    /// thread, for the diagnostics line that follows it.
    refusal: Option<String>,
}

impl Exchange {
    pub fn new(client: Boxed, shared: Arc<Shared>) -> Exchange {
        let (client_r, client_w) = conn::split(client);
        Exchange { client_r, client_w, shared, connection: None, refusal: None }
    }

    pub async fn run(mut self) {
        while let Ok(true) = self.serve_one().await {}
    }

    async fn fail(&mut self, status: u16, reason: &str, message: &str) {
        let _ = self.client_w.send(&http::failure(status, reason, message)).await;
    }

    /// False when the connection should close after this request.
    async fn serve_one(&mut self) -> Result<bool> {
        let Some((head, raw)) = self.client_r.read_head(HEAD_LIMIT).await? else { return Ok(false) };
        let framing = head.request_body();
        let (true, false, Some(framing)) = (head.line.len() == 3, head.method() == "CONNECT", framing) else {
            self.fail(400, "Bad Request", "The relay could not read that request.").await;
            return Ok(false);
        };
        let _in_flight = InFlight::begin(&self.shared);

        if head.tokens("expect").iter().any(|t| t == "100-continue") {
            self.client_w.send(b"HTTP/1.1 100 Continue\r\n\r\n").await?;
        }

        let pairs = head.fields.clone();
        let own = request::bearer(&pairs).and_then(|b| self.shared.state().member_for(&b, now()));
        // Only a model request from a Claude the pool knows can change
        // accounts, so only that one is ever taken apart. Remote Control's own
        // session, artifacts, a socket the connection is upgraded into: all of
        // it belongs to the account that sent it, and rebuilding a request
        // drops `Upgrade` as a hop's field, which is all it takes to stop a
        // socket opening.
        let own = match own {
            Some(own) if request::is_inference(head.method(), head.target()) && !head.asks_to_upgrade() => own,
            _ => return self.pass_through(&head, raw, framing).await,
        };
        let body = match self.client_r.read_body(framing, BODY_LIMIT).await {
            Ok(body) => body,
            Err(RelayError::TooLarge) => {
                self.fail(413, "Payload Too Large", "That request is too large for the relay.").await;
                return Ok(false);
            }
            Err(e) => return Err(e),
        };

        let session = head.value("x-claude-code-session-id").map(str::to_string);
        let pinned = request::must_stay_with_sender(&body, head.value("content-encoding"));
        let continuing = request::continues_thread(&body);
        let bound = continuing || request::carries_bound_reasoning(&body);
        let attempts: Vec<Attempt> = self.shared.state().plan(&own, session.as_deref(), pinned, bound, now());
        let model = request::model(&body);
        let lifetime = policy::cache_lifetime(&body);
        let starts = request::starts_conversation(&body);
        let prompted = request::starts_turn(&body);

        let mut passed: Vec<PoolPass> = Vec::new();
        let mut index = 0;
        while index < attempts.len() {
            let attempt = &attempts[index];
            let last = index == attempts.len() - 1;
            let borrowed = attempt.token.is_some();
            let authorization = attempt.token.as_ref().map(|t| format!("Bearer {t}"));
            // A moved thread's refusal is read to tell `thread_not_found` from
            // a plan that lacks something, and a compressed one cannot be.
            let outgoing = http::request(&head, &body, authorization.as_deref(), continuing && borrowed);
            let answer = match self.send(&outgoing).await {
                Ok(answer) => answer,
                Err(error) => {
                    self.connection = None;
                    if last {
                        self.fail(502, "Bad Gateway", &format!("The relay could not reach {HOST}: {error}")).await;
                        return Ok(false);
                    }
                    if let Some(member) = &attempt.member {
                        passed.push(PoolPass { id: member.clone(), reason: PassReason::Unreachable });
                    }
                    index += 1;
                    continue;
                }
            };

            let status = answer.head.status().unwrap_or(502);
            let served = |threaded: bool, passed: &Vec<PoolPass>| Served {
                status,
                passed_over: passed.clone(),
                model: model.clone(),
                cache_lifetime: lifetime,
                threaded,
                starts,
                prompted,
            };
            if let Some(member) = &attempt.member {
                let now = now();
                self.shared.state().observe(member, &answer.head.fields, now);
                let outcome = policy::outcome(status, &answer.head.fields, borrowed, now);
                if outcome == Outcome::RefusedForAccount && status == 404 && continuing && !last {
                    if let Some(missing) = self.thread_missing(&answer, head.method()).await? {
                        // The thread is stored on the account the conversation
                        // left, and sending the request there would undo the
                        // move. Claude Code answers this by sending everything
                        // as a new thread, which this account can take.
                        self.shared.note("relay thread restarted", &[("member", json!(member))]);
                        self.shared.state().served(member, &own, session.as_deref(), served(true, &passed), now);
                        return self.deliver_read(&answer, &missing, head.method(), head.keeps_alive()).await;
                    }
                }
                if outcome != Outcome::Deliver && !last {
                    self.shared.state().apply(&outcome, member, now);
                    // Its answer is a few hundred bytes nobody will read, and
                    // dropping the connection is cheaper than draining it.
                    self.connection = None;
                    let reason = match outcome {
                        Outcome::Exhausted { .. } => {
                            index += 1;
                            PassReason::Full
                        }
                        Outcome::LoginRefused => {
                            index += 1;
                            PassReason::Login
                        }
                        _ => {
                            // What one plan lacks the next may lack too; the
                            // account Claude chose the request for is the one
                            // sure to take it. That is the sender's next
                            // attempt, which the plan always has, and not
                            // merely the last one, which is a lender whenever
                            // the sender ranked ahead.
                            index = attempts[index + 1..]
                                .iter()
                                .position(|a| a.token.is_none())
                                .map(|p| index + 1 + p)
                                .unwrap_or(attempts.len() - 1);
                            PassReason::Refused
                        }
                    };
                    passed.push(PoolPass { id: member.clone(), reason });
                    let mut noted = vec![("member", json!(member)), ("status", json!(status)), ("reason", json!(reason.as_str()))];
                    if continuing {
                        noted.push(("thread", json!("continue")));
                    }
                    if let Some(refusal) = self.refusal.take() {
                        noted.push(("error", json!(refusal)));
                    }
                    self.shared.note("relay passed over", &noted);
                    continue;
                }
                self.shared.state().served(member, &own, session.as_deref(), served(request::carries_thread(&body), &passed), now);
            }
            return self.deliver(&answer, head.method(), head.keeps_alive()).await;
        }
        Ok(false)
    }

    /// A request that leaves under its sender's login whatever happens goes as
    /// it arrived. One with a stated length is read first, so a connection
    /// kept from the last request can be tried and, if the service had dropped
    /// it, tried again. A chunked body may still be coming when its answer
    /// starts, a stream that ends only when its sender does, waiting on what
    /// comes back, and an upgraded connection carries no more HTTP at all, so
    /// neither is held: each gets a connection of its own and its body is
    /// forwarded alongside the answer.
    async fn pass_through(&mut self, head: &Head, raw: Vec<u8>, framing: Body) -> Result<bool> {
        let upgrade = head.asks_to_upgrade();
        if !upgrade && framing != Body::Chunked {
            let body = match self.client_r.read_body(framing, BODY_LIMIT).await {
                Ok(body) => body,
                Err(RelayError::TooLarge) => {
                    self.fail(413, "Payload Too Large", "That request is too large for the relay.").await;
                    return Ok(false);
                }
                Err(e) => return Err(e),
            };
            let mut outgoing = raw;
            outgoing.extend_from_slice(&body);
            let answer = match self.send(&outgoing).await {
                Ok(answer) => answer,
                Err(error) => {
                    self.connection = None;
                    self.fail(502, "Bad Gateway", &format!("The relay could not reach {HOST}: {error}")).await;
                    return Ok(false);
                }
            };
            return self.deliver(&answer, head.method(), head.keeps_alive()).await;
        }

        self.connection = None;
        let (mut up_r, mut up_w) = match self.shared.upstream.connect(&self.shared.connector).await {
            Ok(fresh) => fresh,
            Err(error) => {
                self.fail(502, "Bad Gateway", &format!("The relay could not reach {HOST}: {error}")).await;
                return Ok(false);
            }
        };
        up_w.send(&raw).await?;
        let method = head.method().to_string();
        let keep_alive = head.keeps_alive();
        let client_r = &mut self.client_r;
        let client_w = &mut self.client_w;
        let upload = forward(framing, client_r, &mut up_w);
        let answering = async {
            let first = final_head(&mut up_r).await?.ok_or(RelayError::Closed)?;
            if upgrade && first.head.status() == Some(101) {
                return Ok::<_, RelayError>((first, None));
            }
            let ended = relay_body(&mut up_r, client_w, &first, &method).await?;
            Ok((first, Some(ended)))
        };
        let (uploaded, answered) = tokio::join!(upload, answering);
        let (first, ended) = answered?;
        uploaded?;
        match ended {
            None => {
                self.client_w.send(&first.raw).await?;
                conn::splice(&mut self.client_r, &mut self.client_w, &mut up_r, &mut up_w).await;
                Ok(false)
            }
            Some(BodyEnd::Closed) => Ok(false),
            Some(BodyEnd::Reusable(reusable)) => {
                if reusable {
                    self.connection = Some((up_r, up_w));
                }
                Ok(keep_alive && first.head.keeps_alive())
            }
        }
    }

    /// A connection kept from the last request may have been closed by the far
    /// end while it sat idle, which shows up as a dead connection before a
    /// byte of answer. That one is tried again on a fresh connection; anything
    /// that failed after an answer had started is not.
    async fn send(&mut self, outgoing: &[u8]) -> Result<Answer> {
        if let Some((mut r, mut w)) = self.connection.take() {
            let before = r.received;
            let tried = async {
                w.send(outgoing).await?;
                final_head(&mut r).await
            }
            .await;
            match tried {
                Ok(Some(answer)) => {
                    self.connection = Some((r, w));
                    return Ok(answer);
                }
                Ok(None) => {}
                Err(_) if r.received == before => {}
                Err(e) => return Err(e),
            }
            if r.received != before {
                return Err(RelayError::Truncated);
            }
        }
        let (mut r, mut w) = self.shared.upstream.connect(&self.shared.connector).await?;
        w.send(outgoing).await?;
        let answer = final_head(&mut r).await?.ok_or(RelayError::Closed)?;
        self.connection = Some((r, w));
        Ok(answer)
    }

    /// The body of a refusal, when it is the service saying it holds no such
    /// thread. Read rather than dropped only here: an error is a few hundred
    /// bytes, and which error it is decides whether it goes back or on.
    async fn thread_missing(&mut self, answer: &Answer, method: &str) -> Result<Option<Vec<u8>>> {
        let Some((up, _)) = self.connection.as_mut() else { return Ok(None) };
        let framing = answer.head.response_body(method);
        if framing == Body::UntilClose {
            return Ok(None);
        }
        if let Some(encoding) = answer.head.value("content-encoding") {
            if !encoding.is_empty() && !encoding.eq_ignore_ascii_case("identity") {
                return Ok(None);
            }
        }
        let body = up.read_body(framing, 64 * 1024).await?;
        if body.windows(request::THREAD_MISSING.len()).any(|w| w == request::THREAD_MISSING) {
            return Ok(Some(body));
        }
        // A guess at why a moved thread was refused is settled next time by
        // the service's own error type.
        let error: Option<Value> = serde_json::from_slice(&body).ok();
        self.refusal = Some(
            error
                .as_ref()
                .and_then(|e| e["error"]["type"].as_str())
                .unwrap_or("unreadable")
                .to_string(),
        );
        Ok(None)
    }

    /// An answer whose body was read to decide on it. A chunked one goes back
    /// as a single chunk, which is the same body however it was cut.
    async fn deliver_read(&mut self, answer: &Answer, body: &[u8], method: &str, keep_alive: bool) -> Result<bool> {
        self.client_w.send(&answer.raw).await?;
        if answer.head.response_body(method) == Body::Chunked {
            if !body.is_empty() {
                let mut chunk = format!("{:x}\r\n", body.len()).into_bytes();
                chunk.extend_from_slice(body);
                chunk.extend_from_slice(b"\r\n");
                self.client_w.send(&chunk).await?;
            }
            self.client_w.send(b"0\r\n\r\n").await?;
        } else {
            self.client_w.send(body).await?;
        }
        let spare = self.connection.as_ref().is_some_and(|(r, _)| !r.buffer.is_empty());
        if !answer.head.keeps_alive() || spare {
            self.connection = None;
        }
        Ok(keep_alive && answer.head.keeps_alive())
    }

    async fn deliver(&mut self, answer: &Answer, method: &str, keep_alive: bool) -> Result<bool> {
        let Some((mut r, w)) = self.connection.take() else { return Ok(false) };
        match relay_body(&mut r, &mut self.client_w, answer, method).await? {
            BodyEnd::Closed => Ok(false),
            BodyEnd::Reusable(reusable) => {
                if reusable {
                    self.connection = Some((r, w));
                }
                // A `Connection: close` from the service went back to Claude
                // Code with the rest of the head, so it is closing its end too.
                Ok(keep_alive && answer.head.keeps_alive())
            }
        }
    }
}

enum BodyEnd {
    /// The answer ran until the service hung up.
    Closed,
    /// Whether the connection can carry the next request.
    Reusable(bool),
}

/// The head and then the body of an answer, as they arrive.
async fn relay_body(up: &mut Reader, client: &mut Writer, answer: &Answer, method: &str) -> Result<BodyEnd> {
    client.send(&answer.raw).await?;
    match answer.head.response_body(method) {
        Body::None => {}
        Body::Length(total) => {
            let mut remaining = total;
            while remaining > 0 {
                if up.buffer.is_empty() && !up.fill().await? {
                    return Err(RelayError::Truncated);
                }
                let piece = up.take(remaining.min(up.buffer.len()));
                remaining -= piece.len();
                client.send(&piece).await?;
            }
        }
        Body::Chunked => {
            let mut framing = ChunkedFraming::default();
            loop {
                if !up.buffer.is_empty() {
                    let used = framing.consume(&up.buffer, None).map_err(|_| RelayError::Malformed)?;
                    let piece = up.take(used);
                    client.send(&piece).await?;
                }
                if framing.is_done() {
                    break;
                }
                if !up.fill().await? {
                    return Err(RelayError::Truncated);
                }
            }
        }
        Body::UntilClose => {
            let pending = up.take_all();
            client.send(&pending).await?;
            while up.fill().await? {
                let pending = up.take_all();
                client.send(&pending).await?;
            }
            return Ok(BodyEnd::Closed);
        }
    }
    Ok(BodyEnd::Reusable(answer.head.keeps_alive() && up.buffer.is_empty()))
}

/// Skips interim answers. The relay has already said `100 Continue` to Claude
/// Code itself.
async fn final_head(wire: &mut Reader) -> Result<Option<Answer>> {
    loop {
        let Some((head, raw)) = wire.read_head(HEAD_LIMIT).await? else { return Ok(None) };
        if let Some(status) = head.status() {
            if (100..200).contains(&status) && status != 101 {
                continue;
            }
        }
        return Ok(Some(Answer { head, raw }));
    }
}

/// The rest of a request's body, as it comes, to the connection its head went
/// down.
async fn forward(framing: Body, client: &mut Reader, up: &mut Writer) -> Result<()> {
    match framing {
        Body::None | Body::UntilClose => Ok(()),
        Body::Length(total) => {
            let mut remaining = total;
            while remaining > 0 {
                if client.buffer.is_empty() && !client.fill().await? {
                    return Err(RelayError::Truncated);
                }
                let piece = client.take(remaining.min(client.buffer.len()));
                remaining -= piece.len();
                up.send(&piece).await?;
            }
            Ok(())
        }
        Body::Chunked => {
            let mut framing = ChunkedFraming::default();
            loop {
                if !client.buffer.is_empty() {
                    let used = framing.consume(&client.buffer, None).map_err(|_| RelayError::Malformed)?;
                    let piece = client.take(used);
                    up.send(&piece).await?;
                }
                if framing.is_done() {
                    return Ok(());
                }
                if !client.fill().await? {
                    return Err(RelayError::Truncated);
                }
            }
        }
    }
}
