//! Running things on the host rather than inside the sandbox.
//!
//! Graft ships as a Flatpak, and a Flatpak has its own process namespace and
//! its own `/usr`. Claude is installed on the host, its processes are host
//! processes, and `ps` inside the sandbox lists only Graft's own. So anything
//! that has to see or start Claude goes out through `flatpak-spawn --host`,
//! which the manifest allows by talking to `org.freedesktop.Flatpak`. Outside
//! a Flatpak, as in the suite and a development build, the same calls run
//! directly.

use std::ffi::OsStr;
use std::io::Read;
use std::path::Path;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

static DIRECT: AtomicBool = AtomicBool::new(false);

/// Treat this process as outside any Flatpak, so host commands run directly.
///
/// For the suite, which runs inside the SDK's own sandbox: there the host is
/// out of reach, and running directly means `ps` sees only the sandbox, so a
/// stand-in Claude a test starts is the only Claude it can find and a real
/// one on this machine can be neither seen nor touched.
pub fn set_run_directly(on: bool) {
    DIRECT.store(on, Ordering::SeqCst);
}

/// Whether this process is running inside a Flatpak sandbox.
///
/// `GRAFT_RUN_DIRECTLY=1` does what `set_run_directly` does for a whole
/// process, so the suite can run the built launcher the same way.
pub fn in_flatpak() -> bool {
    if DIRECT.load(Ordering::SeqCst) || std::env::var_os("GRAFT_RUN_DIRECTLY").is_some_and(|v| v == "1") {
        return false;
    }
    static ANSWER: OnceLock<bool> = OnceLock::new();
    *ANSWER.get_or_init(|| Path::new("/.flatpak-info").exists())
}

/// The Flatpak application id, when there is one.
pub fn flatpak_id() -> Option<String> {
    std::env::var("FLATPAK_ID").ok().filter(|id| !id.is_empty())
}

/// A command aimed at the host. Environment variables meant for the host
/// process have to be passed as `--env=` arguments, since `flatpak-spawn`
/// does not hand its own environment across.
pub fn command<S: AsRef<OsStr>>(program: S, env: &[(&str, &str)]) -> Command {
    if in_flatpak() {
        let mut cmd = Command::new("flatpak-spawn");
        cmd.arg("--host");
        for (key, value) in env {
            cmd.arg(format!("--env={key}={value}"));
        }
        cmd.arg(program);
        cmd
    } else {
        let mut cmd = Command::new(program);
        for (key, value) in env {
            cmd.env(key, value);
        }
        cmd
    }
}

/// Run a host tool to completion and hand back its exit status, or -1 when it
/// could not be started at all.
pub fn run_tool(program: &str, args: &[&str]) -> i32 {
    let status = command(program, &[])
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status();
    match status {
        Ok(status) => status.code().unwrap_or(-1),
        Err(_) => -1,
    }
}

/// Same, with standard output collected. An empty string when the tool could
/// not run, which every caller reads as "nothing found".
pub fn output(program: &str, args: &[&str]) -> String {
    let child = command(program, &[])
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn();
    let Ok(mut child) = child else { return String::new() };
    let mut collected = Vec::new();
    if let Some(mut out) = child.stdout.take() {
        let _ = out.read_to_end(&mut collected);
    }
    let _ = child.wait();
    String::from_utf8_lossy(&collected).into_owned()
}

/// Start a host program and let it go. It is put in a session of its own, so
/// Claude does not die with the Graft or the launcher that started it, and
/// its output goes nowhere, since nobody is reading it.
///
/// Inside a Flatpak, `flatpak-spawn --host` without `--watch-bus` already
/// leaves the host process running after the caller exits; the spawn process
/// itself is waited on so it does not linger as a zombie.
pub fn spawn_detached(program: &str, args: &[String], env: &[(&str, &str)]) -> bool {
    let mut cmd = if in_flatpak() {
        let mut cmd = command("setsid", env);
        cmd.arg(program);
        cmd
    } else {
        let mut cmd = command(program, env);
        unsafe_new_session(&mut cmd);
        cmd
    };
    cmd.args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    match cmd.spawn() {
        Ok(mut child) => {
            // Reaped on a thread of its own, so neither a short-lived
            // flatpak-spawn nor a long-lived Claude lingers as a zombie of a
            // Graft that keeps running.
            std::thread::spawn(move || {
                let _ = child.wait();
            });
            true
        }
        Err(_) => false,
    }
}

#[cfg(unix)]
fn unsafe_new_session(cmd: &mut Command) {
    use std::os::unix::process::CommandExt;
    // SAFETY: setsid is async-signal-safe, which is all pre_exec requires.
    unsafe {
        cmd.pre_exec(|| {
            libc::setsid();
            Ok(())
        });
    }
}

/// Whether a file exists on the host. The manifest shares the host's
/// filesystem, but not `/usr`, where Claude itself is installed.
pub fn host_path_exists(path: &Path) -> bool {
    if !in_flatpak() {
        return path.exists();
    }
    if path.starts_with("/usr") {
        let text = path.to_string_lossy();
        return run_tool("test", &["-e", &text]) == 0;
    }
    path.exists()
}

/// A host file's contents, for the few under `/usr` the sandbox cannot see.
pub fn host_read(path: &Path) -> Option<Vec<u8>> {
    if !in_flatpak() || !path.starts_with("/usr") {
        return std::fs::read(path).ok();
    }
    let text = path.to_string_lossy();
    let out = command("cat", &[])
        .arg(text.as_ref())
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .ok()?;
    out.status.success().then_some(out.stdout)
}
