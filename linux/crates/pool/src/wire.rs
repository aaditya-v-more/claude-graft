//! What Graft tells the relay and what the relay says back, over a Unix
//! socket in a folder only this user can open. Tokens travel this way and no
//! other, and live only in the relay's memory: a relay that restarts knows
//! nothing until Graft tells it again, and until then it passes every request
//! through untouched.

use crate::limits::PoolLimits;
use crate::Time;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::os::unix::net::UnixStream;
use std::path::Path;
use std::time::Duration;

/// One account as the relay is told about it: a Claude, or an account signed
/// in through Graft with no Claude of its own.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolMember {
    pub id: String,
    pub name: String,
    /// The account behind it. Two members on one account are one allowance,
    /// so a Claude never borrows from its own account under another name, and
    /// what the service says about one is true of both.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub account: Option<String>,
    /// Digests of every current token the profile holds. Claude hands Claude
    /// Code one of these, which is how a request is traced to its profile.
    pub identify: Vec<String>,
    /// The token lent to other members' requests. `None` when the login could
    /// not be read, which leaves a Claude able to borrow but not to lend.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
    /// What Graft last read from the usage endpoint, so an account the relay
    /// has not sent anything to yet is not a blank.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limits: Option<PoolLimits>,
    /// The members whose accounts may answer this one's Claude Code. Each
    /// Claude has its own list; nobody borrows from an account it was not
    /// given.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub borrows_from: Option<Vec<String>>,
}

impl PoolMember {
    /// The account it is counted under: its own id when nothing says which.
    pub fn account_key(&self) -> &str {
        self.account.as_deref().unwrap_or(&self.id)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PoolConfiguration {
    pub enabled: bool,
    pub members: Vec<PoolMember>,
    /// Conversations somebody moved by hand, by session, to the member that
    /// should answer them. Kept by Graft and handed over with everything else,
    /// so a relay that restarts still knows.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub assignments: Option<BTreeMap<String, String>>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PoolCommand {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub configure: Option<PoolConfiguration>,
    /// Asks a relay built from a different binary to step aside: it stops
    /// listening at once and leaves when its last exchange finishes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quit: Option<bool>,
}

impl PoolCommand {
    pub fn status() -> PoolCommand {
        PoolCommand::default()
    }

    pub fn configure(configuration: PoolConfiguration) -> PoolCommand {
        PoolCommand { configure: Some(configuration), quit: None }
    }

    pub fn quit() -> PoolCommand {
        PoolCommand { configure: None, quit: Some(true) }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum PassReason {
    /// Out of room in the five-hour window or the week.
    Full,
    /// Its login was refused.
    Login,
    /// It could not serve this request: a model or feature its plan lacks.
    Refused,
    /// It could not be reached at all.
    Unreachable,
}

impl PassReason {
    pub fn as_str(self) -> &'static str {
        match self {
            PassReason::Full => "full",
            PassReason::Login => "login",
            PassReason::Refused => "refused",
            PassReason::Unreachable => "unreachable",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PoolPass {
    pub id: String,
    pub reason: PassReason,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolRouting {
    pub at: Time,
    pub from: String,
    pub served_by: String,
    pub status: u16,
    /// Members tried first that said no, in order.
    pub passed_over: Vec<PoolPass>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session: Option<String>,
}

/// A conversation the relay has carried, by Claude Code's session id.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolSession {
    pub id: String,
    /// The member whose Claude the conversation is in.
    pub from: String,
    /// The account that answered it last. `None` until one has.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub served_by: Option<String>,
    pub last_at: Time,
    pub requests: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    /// When the answering account's cache of it lapses, after which it goes
    /// wherever the ranking says.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cached_until: Option<Time>,
    /// Members passed over on its last request, so a conversation answered by
    /// somebody other than the account chosen for it can say why.
    pub passed_over: Vec<PoolPass>,
    /// When it began, for one the relay saw from its first message. `None`
    /// for one already under way when the relay first saw it: a relay
    /// restarted mid-conversation sees every conversation for the first time,
    /// and only one with no answer in it yet is new to the person too.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub started_at: Option<Time>,
    /// The last time a person typed into it, as against Claude Code carrying
    /// on with a tool's answer or asking for a title beside it. `None` from a
    /// relay that predates it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub prompted_at: Option<Time>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolMemberStatus {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limits: Option<PoolLimits>,
    pub served: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_served: Option<Time>,
    /// The last time a request arrived carrying this member's own token: the
    /// only way to know a Claude is actually connected.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_seen: Option<Time>,
    pub token_refused: bool,
}

/// Every field without a default is required, so a relay from a release
/// whose status has another shape reads as unreadable rather than as one
/// with nothing in it: replacing it is the answer, not trusting it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolStatus {
    /// Digest of the executable that is running, so Graft can tell a relay left
    /// over from an earlier version.
    pub binary: String,
    pub pid: u32,
    pub started: Time,
    pub enabled: bool,
    pub configured: bool,
    pub members: Vec<PoolMemberStatus>,
    pub recent: Vec<PoolRouting>,
    /// Most recent first, and only the last day's.
    pub sessions: Vec<PoolSession>,
    pub open_exchanges: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SocketFailure {
    NotRunning,
    Garbled,
    TooLong,
}

/// Blocking, so never on the thread that draws. The socket is the relay's
/// door, and a relay that does not answer it inside a few seconds is treated
/// as absent rather than waited on.
pub fn send(command: &PoolCommand, path: &Path, timeout: Duration) -> Result<PoolStatus, SocketFailure> {
    let line = exchange(command, path, timeout)?;
    serde_json::from_slice::<PoolStatus>(&line).map_err(|_| SocketFailure::Garbled)
}

/// The same, to the relay's own socket.
pub fn ask(command: &PoolCommand) -> Result<PoolStatus, SocketFailure> {
    send(command, &crate::socket_file(), Duration::from_secs(4))
}

fn exchange(command: &PoolCommand, path: &Path, timeout: Duration) -> Result<Vec<u8>, SocketFailure> {
    if path.as_os_str().len() >= 108 {
        return Err(SocketFailure::TooLong);
    }
    let mut stream = UnixStream::connect(path).map_err(|e| {
        if e.kind() == std::io::ErrorKind::InvalidInput {
            SocketFailure::TooLong
        } else {
            SocketFailure::NotRunning
        }
    })?;
    let _ = stream.set_read_timeout(Some(timeout));
    let _ = stream.set_write_timeout(Some(timeout));
    let mut payload = serde_json::to_vec(command).map_err(|_| SocketFailure::NotRunning)?;
    payload.push(b'\n');
    stream.write_all(&payload).map_err(|_| SocketFailure::NotRunning)?;
    read_line(&mut stream, 8 << 20).ok_or(SocketFailure::Garbled)
}

/// One line, its ending left off, or whatever arrived before the other side
/// hung up. `None` when nothing arrived at all.
pub fn read_line(stream: &mut impl Read, limit: usize) -> Option<Vec<u8>> {
    let mut collected = Vec::new();
    let mut buffer = [0u8; 65536];
    while collected.len() < limit {
        match stream.read(&mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(count) => {
                let before = collected.len();
                collected.extend_from_slice(&buffer[..count]);
                if let Some(end) = collected[before..].iter().position(|b| *b == b'\n') {
                    collected.truncate(before + end);
                    return Some(collected);
                }
            }
        }
    }
    (!collected.is_empty()).then_some(collected)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::net::UnixListener;

    fn member() -> PoolMember {
        PoolMember {
            id: "main".into(),
            name: "Claude".into(),
            account: None,
            identify: vec![crate::digest("token")],
            token: Some("token".into()),
            limits: Some(PoolLimits::from_percent(Some(34), None, 71, None, 1.0)),
            borrows_from: None,
        }
    }

    #[test]
    fn the_pool_reaches_the_relay_exactly_as_graft_described_it() {
        let command = PoolCommand::configure(PoolConfiguration { enabled: true, members: vec![member()], assignments: None });
        let text = serde_json::to_vec(&command).unwrap();
        assert_eq!(serde_json::from_slice::<PoolCommand>(&text).unwrap(), command);
    }

    #[test]
    fn the_quit_sent_to_a_relay_that_cannot_be_read_is_bare() {
        assert_eq!(serde_json::to_string(&PoolCommand::quit()).unwrap(), r#"{"quit":true}"#);
        assert_eq!(serde_json::to_string(&PoolCommand::status()).unwrap(), "{}");
    }

    #[test]
    fn a_relay_from_before_conversations_were_marked_new_or_typed_into_is_still_read() {
        let older = r#"{"id":"s","from":"c2","lastAt":0,"requests":1,"passedOver":[]}"#;
        let read: PoolSession = serde_json::from_str(older).unwrap();
        assert!(read.started_at.is_none() && read.prompted_at.is_none());
    }

    #[test]
    fn a_status_missing_fields_this_release_requires_does_not_decode() {
        let older = r#"{"binary":"older","pid":1,"started":0,"enabled":true,"configured":true,"members":[],"recent":[]}"#;
        assert!(serde_json::from_str::<PoolStatus>(older).is_err());
    }

    #[test]
    fn a_socket_nobody_listens_on_is_no_relay_at_all() {
        let dir = graft_core::testing::tempfile_lite::TempDir::new();
        let result = send(&PoolCommand::status(), &dir.path().join("nobody.sock"), Duration::from_secs(1));
        assert_eq!(result, Err(SocketFailure::NotRunning));
    }

    #[test]
    fn a_line_comes_back_whole_from_whoever_answers() {
        let dir = graft_core::testing::tempfile_lite::TempDir::new();
        let path = dir.path().join("r.sock");
        let listener = UnixListener::bind(&path).unwrap();
        let answer = std::thread::spawn(move || {
            let (mut client, _) = listener.accept().unwrap();
            let asked = read_line(&mut client, 1 << 20).unwrap();
            client.write_all(b"{\"seen\":").unwrap();
            client.write_all(&asked).unwrap();
            client.write_all(b"}\n").unwrap();
        });
        let line = exchange(&PoolCommand::quit(), &path, Duration::from_secs(2)).unwrap();
        answer.join().unwrap();
        assert_eq!(line, br#"{"seen":{"quit":true}}"#);
    }
}
