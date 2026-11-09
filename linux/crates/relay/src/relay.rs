//! The relay itself: a proxy on loopback that Claude Code is pointed at
//! through its own environment settings.
//!
//! It is a process of its own, rather than part of the app, so that it goes
//! on answering while Graft is quit, since a Claude told to use a proxy that
//! is not there cannot reach the service at all, and so that an update to
//! Graft replaces it deliberately rather than by accident.

use crate::conn::{self, Boxed, Prefixed};
use crate::exchange::{Exchange, Upstream};
use crate::state::PoolState;
use crate::{certificates, http};
use graft_core::{claude, diagnostics, files, host, paths};
use graft_pool::{environment, now, wire, PoolCommand, PoolConfiguration, PoolStatus, Time, HOST};
use serde_json::{json, Value};
use std::fs::{File, OpenOptions};
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::os::unix::io::AsRawFd;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpSocket, TcpStream, UnixListener, UnixStream};
use tokio::sync::{oneshot, watch};

#[derive(Debug, Clone)]
pub struct Options {
    pub port: u16,
    pub socket: Option<PathBuf>,
    pub upstream: Upstream,
    /// How often the relay looks for its own installation. A test shortens it.
    pub removal_check: Duration,
    /// Lists processes from this process's own `/proc` rather than the
    /// host's, so a test inside a sandbox can stand a process in for an open
    /// Claude.
    pub local_processes: bool,
}

impl Default for Options {
    fn default() -> Self {
        Options {
            port: graft_pool::PORT,
            socket: None,
            upstream: Upstream::Service,
            removal_check: Duration::from_secs(60),
            local_processes: false,
        }
    }
}

/// What every connection and the control socket share.
pub struct Shared {
    state: Mutex<PoolState>,
    pub upstream: Upstream,
    pub connector: tokio_rustls::TlsConnector,
    acceptor: tokio_rustls::TlsAcceptor,
    binary: String,
    started: Time,
    quitting: AtomicBool,
    stop_front: watch::Sender<bool>,
    front_stopped: Mutex<Option<oneshot::Receiver<()>>>,
    stop_control: watch::Sender<bool>,
    control_path: PathBuf,
    lock: Mutex<Option<File>>,
}

impl Shared {
    pub fn state(&self) -> MutexGuard<'_, PoolState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Written only while Graft's folder is there. Once Graft and its folder
    /// have gone, a line here would make the folder again, and nobody is left
    /// to read it.
    pub fn note(&self, event: &str, fields: &[(&str, Value)]) {
        note(event, fields);
    }

    fn status(&self) -> PoolStatus {
        self.state().status(&self.binary, std::process::id(), self.started, now())
    }
}

fn note(event: &str, fields: &[(&str, Value)]) {
    if paths::graft_folder().exists() {
        diagnostics::note(event, fields);
    }
}

fn give_up(stage: &str, error: impl std::fmt::Display) -> ! {
    note("relay could not start", &[("stage", json!(stage)), ("error", json!(error.to_string()))]);
    std::process::exit(1);
}

/// What says Graft is still installed. Outside a Flatpak that is the relay's
/// own file, looked at by the path it started from. Inside one it is not: the
/// sandbox keeps the old files in place for as long as anything runs from
/// them, so a relay there asks the host whether the app is still deployed.
#[derive(Debug, Clone)]
pub enum Installation {
    File(PathBuf),
    Flatpak(String),
}

impl Installation {
    /// A relay run from anywhere but the app's own `/app` is a file like any
    /// other, a development build inside a sandbox included.
    pub fn detect() -> Installation {
        let exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("/proc/self/exe"));
        if host::in_flatpak() && exe.starts_with("/app") {
            if let Some(id) = host::flatpak_id() {
                return Installation::Flatpak(id);
            }
        }
        Installation::File(exe)
    }

    pub fn present(&self) -> bool {
        match self {
            Installation::File(path) => path.exists(),
            Installation::Flatpak(id) => {
                let user = paths::home().join(format!(".local/share/flatpak/app/{id}/current/active"));
                let system = format!("/var/lib/flatpak/app/{id}/current/active");
                let user = paths::text(&user);
                // Only a clear no counts as gone: a host that could not be
                // asked says nothing about the app.
                host::run_tool("test", &["-e", &user, "-o", "-e", &system]) != 1
            }
        }
    }
}

/// The processes `/proc` lists, joined the way `ps` prints them.
fn local_processes() -> Vec<claude::Process> {
    let mut out = Vec::new();
    for entry in std::fs::read_dir("/proc").into_iter().flatten().flatten() {
        let Some(pid) = entry.file_name().to_str().and_then(|n| n.parse::<i32>().ok()) else { continue };
        let Ok(raw) = std::fs::read(entry.path().join("cmdline")) else { continue };
        let words: Vec<String> =
            raw.split(|b| *b == 0).filter(|w| !w.is_empty()).map(|w| String::from_utf8_lossy(w).into_owned()).collect();
        if !words.is_empty() {
            out.push(claude::Process { pid, command: words.join(" ") });
        }
    }
    out
}

fn is_running(local: bool, profile: &Path) -> bool {
    if local {
        claude::is_running_in(&local_processes(), profile)
    } else {
        claude::is_running(profile)
    }
}

fn digest_of_self() -> String {
    std::env::current_exe().ok().and_then(|p| std::fs::read(p).ok()).map(|d| files::digest(&d)).unwrap_or_default()
}

fn server_config() -> rustls::ServerConfig {
    let (chain, key) = certificates::identity().unwrap_or_else(|e| give_up("identity", e));
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut config = rustls::ServerConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .unwrap_or_else(|e| give_up("identity", e))
        .with_no_client_auth()
        .with_single_cert(chain, key)
        .unwrap_or_else(|e| give_up("identity", e));
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    config
}

fn client_config() -> rustls::ClientConfig {
    let roots = rustls::RootCertStore { roots: webpki_roots::TLS_SERVER_ROOTS.to_vec() };
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut config = rustls::ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .unwrap_or_else(|e| give_up("upstream", e))
        .with_root_certificates(roots)
        .with_no_client_auth();
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    config
}

/// Runs the relay until it leaves, which is always by `exit`.
pub fn run(options: Options) -> ! {
    let directory = graft_pool::directory();
    let _ = std::fs::create_dir_all(&directory);
    let _ = std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700));
    // One relay at a time, settled before anything is bound: the one stepping
    // aside lets go of this as it stops listening, and a second copy started
    // by mistake leaves without touching the socket the first is answering
    // on. Settled before the certificate as well: a second copy that renewed
    // it on its way to finding the lock taken would leave the running relay
    // presenting a certificate from an authority every session started
    // afterwards no longer trusts.
    let lock = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .mode(0o600)
        .open(graft_pool::relay_lock_file());
    // SAFETY: flock on a descriptor this process owns.
    let held = lock.as_ref().is_ok_and(|f| unsafe { libc::flock(f.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } == 0);
    let lock = match (lock, held) {
        (Ok(file), true) => file,
        _ => {
            note("relay already running", &[]);
            std::process::exit(0);
        }
    };
    if let Err(e) = certificates::ensure() {
        give_up("certificate", e);
    }
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .unwrap_or_else(|e| give_up("runtime", e));
    runtime.block_on(serve(options, lock));
    std::process::exit(0);
}

async fn serve(options: Options, lock: File) {
    let (stop_front, mut front_signal) = watch::channel(false);
    let (stop_control, control_signal) = watch::channel(false);
    let (front_done, front_stopped) = oneshot::channel();
    let control_path = options.socket.clone().unwrap_or_else(graft_pool::socket_file);
    let shared = Arc::new(Shared {
        state: Mutex::new(PoolState::new()),
        upstream: options.upstream,
        connector: tokio_rustls::TlsConnector::from(Arc::new(client_config())),
        acceptor: tokio_rustls::TlsAcceptor::from(Arc::new(server_config())),
        binary: digest_of_self(),
        started: now(),
        quitting: AtomicBool::new(false),
        stop_front,
        front_stopped: Mutex::new(Some(front_stopped)),
        stop_control,
        control_path: control_path.clone(),
        lock: Mutex::new(Some(lock)),
    });

    let socket = TcpSocket::new_v4().unwrap_or_else(|e| give_up("front listener", e));
    // Connections closed by the relay sit in TIME_WAIT on this port for a
    // while, and the relay that replaces this one must still be able to bind
    // it. The lock, not the port, is what keeps there to one.
    let _ = socket.set_reuseaddr(true);
    socket
        .bind(std::net::SocketAddr::from(([127, 0, 0, 1], options.port)))
        .unwrap_or_else(|e| give_up("front listener", e));
    let front = socket.listen(1024).unwrap_or_else(|e| give_up("front listener", e));

    let _ = std::fs::remove_file(&control_path);
    let control = UnixListener::bind(&control_path).unwrap_or_else(|e| give_up("control socket", e));
    if let Err(e) = std::fs::set_permissions(&control_path, std::fs::Permissions::from_mode(0o600)) {
        give_up("control socket", e);
    }
    tokio::spawn(control_loop(control, control_signal, shared.clone()));
    note(
        "relay started",
        &[("port", json!(options.port)), ("binary", json!(shared.binary.chars().take(12).collect::<String>()))],
    );
    tokio::spawn(watch_for_removal(shared.clone(), options.removal_check, options.local_processes));

    loop {
        tokio::select! {
            accepted = front.accept() => {
                if let Ok((stream, _)) = accepted {
                    let _ = stream.set_nodelay(true);
                    tokio::spawn(tunnel(stream, shared.clone()));
                }
            }
            _ = front_signal.changed() => break,
        }
    }
    drop(front);
    let _ = front_done.send(());
    std::future::pending::<()>().await;
}

/// The relay's door for Graft: a Unix socket in a folder only this user can
/// open, checked again against the caller's user id, taking one line of JSON
/// and answering with one. Tokens arrive this way and no other.
async fn control_loop(listener: UnixListener, mut stop: watch::Receiver<bool>, shared: Arc<Shared>) {
    loop {
        tokio::select! {
            accepted = listener.accept() => {
                if let Ok((stream, _)) = accepted {
                    tokio::spawn(control_one(stream, shared.clone()));
                }
            }
            _ = stop.changed() => break,
        }
    }
    drop(listener);
    let _ = std::fs::remove_file(&shared.control_path);
}

async fn control_one(mut stream: UnixStream, shared: Arc<Shared>) {
    // SAFETY: getuid cannot fail.
    let me = unsafe { libc::getuid() };
    match stream.peer_cred() {
        Ok(cred) if cred.uid() == me => {}
        _ => return,
    }
    let patience = Duration::from_secs(5);
    let mut line = Vec::new();
    let read = tokio::time::timeout(patience, async {
        let mut chunk = [0u8; 65536];
        while line.len() < 1 << 20 {
            let got = stream.read(&mut chunk).await.ok()?;
            if got == 0 {
                break;
            }
            line.extend_from_slice(&chunk[..got]);
            if let Some(end) = line.iter().position(|b| *b == b'\n') {
                line.truncate(end);
                break;
            }
        }
        Some(())
    })
    .await;
    if !matches!(read, Ok(Some(()))) || line.is_empty() {
        return;
    }
    let Ok(command) = serde_json::from_slice::<PoolCommand>(&line) else { return };
    let status = handle(command, &shared).await;
    if let Ok(mut payload) = serde_json::to_vec(&status) {
        payload.push(b'\n');
        let _ = tokio::time::timeout(patience, stream.write_all(&payload)).await;
    }
}

async fn handle(command: PoolCommand, shared: &Arc<Shared>) -> PoolStatus {
    if let Some(configuration) = command.configure {
        shared.state().configure(configuration, now());
    }
    if command.quit == Some(true) {
        step_aside(shared).await;
    }
    shared.status()
}

/// Stops listening at once and lets go of the lock, so the relay replacing
/// this one can start the moment Graft hears back; then leaves when the last
/// request in flight has finished, or after a quarter of an hour, whichever
/// comes first. An idle connection Claude Code keeps open is not in flight,
/// and it reconnects to the new relay on its next request. An upgraded one
/// is, for as long as it lasts, so a socket Remote Control holds open is cut
/// no sooner than the quarter hour, and then reconnects to the new relay the
/// way it would after any dropped network.
async fn step_aside(shared: &Arc<Shared>) {
    if shared.quitting.swap(true, Ordering::SeqCst) {
        return;
    }
    let _ = shared.stop_front.send(true);
    let stopped = shared.front_stopped.lock().unwrap_or_else(|e| e.into_inner()).take();
    if let Some(stopped) = stopped {
        let _ = tokio::time::timeout(Duration::from_secs(2), stopped).await;
    }
    let _ = shared.stop_control.send(true);
    // The control loop takes its socket away as it stops; it is taken here
    // too, so the answer to this very command comes after it has gone.
    let _ = std::fs::remove_file(&shared.control_path);
    drop(shared.lock.lock().unwrap_or_else(|e| e.into_inner()).take());
    note("relay stepping aside", &[]);
    let shared = shared.clone();
    tokio::spawn(async move {
        let deadline = tokio::time::Instant::now() + Duration::from_secs(15 * 60);
        tokio::time::sleep(Duration::from_millis(500)).await;
        while shared.state().in_flight() > 0 && tokio::time::Instant::now() < deadline {
            tokio::time::sleep(Duration::from_secs(1)).await;
        }
        std::process::exit(0);
    });
}

/// The relay outlives Graft, so it is the one left to notice Graft has gone:
/// uninstalled, or replaced by a build from before sharing, which carries no
/// relay. Each Claude that borrowed still holds the lines, and once this relay
/// goes, at the next logout since nothing will start it again, every Claude
/// Code request there fails. So once its installation has been missing at two
/// looks a minute apart, which an update swapping the files never is, it stops
/// lending, puts back each closed Claude's environment as it was, and leaves
/// once none still points at it.
async fn watch_for_removal(shared: Arc<Shared>, every: Duration, local: bool) {
    let installation = Installation::detect();
    let mut missing = 0u32;
    // Read at once as well, so a removal that lands before the first look
    // still finds the journal it took with it.
    let mut remembered = environment::journal_on_disk();
    loop {
        tokio::time::sleep(every).await;
        let look = installation.clone();
        let present = tokio::task::spawn_blocking(move || look.present()).await.unwrap_or(true);
        if present {
            missing = 0;
            // Kept for the folder going with the app, which takes the journal
            // with it while the Claudes it describes are still open.
            if let Some(journal) = environment::journal_on_disk() {
                remembered = Some(journal);
            }
            continue;
        }
        missing += 1;
        if missing < 2 {
            continue;
        }
        if missing == 2 {
            shared.state().configure(PoolConfiguration { enabled: false, members: vec![], assignments: None }, now());
        }
        let journal = remembered.clone();
        let left = tokio::task::spawn_blocking(move || {
            environment::take_out_lines(journal.as_ref(), |profile| is_running(local, profile))
        })
        .await
        .unwrap_or_else(|_| vec![String::new()]);
        if !left.is_empty() {
            continue;
        }
        if shared.state().in_flight() == 0 {
            let _ = shared.stop_control.send(true);
            let _ = std::fs::remove_file(&shared.control_path);
            std::process::exit(0);
        }
    }
}

/// A host and port from a `CONNECT` target, an IPv6 address in brackets.
pub fn authority(target: &str) -> Option<(String, u16)> {
    if let Some(rest) = target.strip_prefix('[') {
        let close = rest.find(']')?;
        let port = rest[close + 1..].strip_prefix(':')?.parse().ok()?;
        return Some((rest[..close].to_string(), port));
    }
    let colon = target.rfind(':')?;
    Some((target[..colon].to_string(), target[colon + 1..].parse().ok()?))
}

/// The host and port of an `http://` request in absolute form.
pub fn absolute(target: &str) -> Option<(String, u16)> {
    let scheme_end = target.find("://")?;
    if !target[..scheme_end].eq_ignore_ascii_case("http") {
        return None;
    }
    let rest = &target[scheme_end + 3..];
    let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let place = &rest[..end];
    let place = place.rsplit('@').next().unwrap_or(place);
    if place.is_empty() {
        return None;
    }
    if place.starts_with('[') || place.contains(':') {
        if let Some(found) = authority(place) {
            return Some(found);
        }
        if let Some(inner) = place.strip_prefix('[').and_then(|p| p.strip_suffix(']')) {
            return Some((inner.to_string(), 80));
        }
        return None;
    }
    Some((place.to_string(), 80))
}

async fn reach(host: &str, port: u16) -> Option<TcpStream> {
    let stream = tokio::time::timeout(Duration::from_secs(20), TcpStream::connect((host, port))).await.ok()?.ok()?;
    let _ = stream.set_nodelay(true);
    Some(stream)
}

/// A `CONNECT` for the service is opened; every other is passed through
/// untouched, and so is an `http://` request in absolute form, for a client
/// that sends everything to the one proxy it was given.
async fn tunnel(mut stream: TcpStream, shared: Arc<Shared>) {
    let mut buffer = Vec::new();
    let mut chunk = [0u8; 16384];
    let length = loop {
        if let Some(length) = http::head_length(&buffer) {
            break length;
        }
        if buffer.len() > 16384 {
            return;
        }
        match stream.read(&mut chunk).await {
            Ok(0) | Err(_) => return,
            Ok(got) => buffer.extend_from_slice(&chunk[..got]),
        }
    };
    let rest = buffer.split_off(length);
    let raw = buffer;
    let Some(head) = http::Head::parse(&raw) else { return };
    let refuse = |status: u16, reason: &'static str, message: String| http::failure(status, reason, &message);

    if head.method() == "CONNECT" {
        let Some((host, port)) = authority(head.target()) else {
            let _ = stream.write_all(&refuse(400, "Bad Request", "The relay could not read that address.".into())).await;
            return;
        };
        if host.eq_ignore_ascii_case(HOST) && port == 443 {
            if stream.write_all(b"HTTP/1.1 200 Connection Established\r\n\r\n").await.is_err() {
                return;
            }
            let accepting = shared.acceptor.accept(Prefixed::new(rest, stream));
            let Ok(Ok(tls)) = tokio::time::timeout(Duration::from_secs(20), accepting).await else { return };
            Exchange::new(Box::new(tls) as Boxed, shared).run().await;
            return;
        }
        let Some(far) = reach(&host, port).await else {
            let _ = stream.write_all(&refuse(502, "Bad Gateway", format!("The relay could not reach {host}."))).await;
            return;
        };
        if stream.write_all(b"HTTP/1.1 200 Connection Established\r\n\r\n").await.is_err() {
            return;
        }
        let (mut a_r, mut a_w) = conn::split_with(Box::new(stream), rest);
        let (mut b_r, mut b_w) = conn::split(Box::new(far));
        conn::splice(&mut a_r, &mut a_w, &mut b_r, &mut b_w).await;
    } else if let Some((host, port)) = absolute(head.target()) {
        let Some(mut far) = reach(&host, port).await else {
            let _ = stream.write_all(&refuse(502, "Bad Gateway", format!("The relay could not reach {host}."))).await;
            return;
        };
        if far.write_all(&raw).await.is_err() {
            return;
        }
        let (mut a_r, mut a_w) = conn::split_with(Box::new(stream), rest);
        let (mut b_r, mut b_w) = conn::split(Box::new(far));
        conn::splice(&mut a_r, &mut a_w, &mut b_r, &mut b_w).await;
    } else {
        let _ = stream.write_all(&refuse(400, "Bad Request", "The relay only forwards CONNECT.".into())).await;
    }
}

/// Whether a relay answers on the socket, for scripts and tests.
pub fn answers(path: &Path) -> bool {
    wire::send(&PoolCommand::status(), path, Duration::from_secs(2)).is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_connect_target_is_read_as_a_host_and_a_port() {
        assert_eq!(authority("api.anthropic.com:443"), Some(("api.anthropic.com".into(), 443)));
        assert_eq!(authority("[::1]:8080"), Some(("::1".into(), 8080)));
        assert_eq!(authority("nowhere"), None, "and one without a port is refused");
    }

    #[test]
    fn an_absolute_request_names_where_it_goes() {
        assert_eq!(absolute("http://example.com/x"), Some(("example.com".into(), 80)));
        assert_eq!(absolute("http://example.com:8080/x?y"), Some(("example.com".into(), 8080)));
        assert_eq!(absolute("/v1/messages"), None, "while a path alone is not one");
        assert_eq!(absolute("https://example.com/"), None, "and a secure address is reached by CONNECT");
    }

    #[test]
    fn a_relay_never_writes_a_login_into_the_diagnostics_log() {
        let sources = [include_str!("relay.rs"), include_str!("exchange.rs"), include_str!("state.rs"), include_str!("conn.rs")];
        let notes: Vec<&str> = sources.iter().flat_map(|s| s.lines()).filter(|l| l.contains("note(")).collect();
        assert!(!notes.is_empty());
        for line in notes {
            let lower = line.to_lowercase();
            assert!(!lower.contains("token") && !lower.contains("bearer"), "{line}");
        }
    }
}
