//! `graft-relay`: started detached by Graft or a launcher, and left running
//! while any Claude may be pointed at it.

use graft_core::{diagnostics, paths};
use graft_relay::{Options, Upstream};
use std::path::PathBuf;
use std::time::Duration;

fn main() {
    let mut options = Options::default();
    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            // Named even though they are the defaults, so a Graft pointed
            // somewhere else for a test starts a relay that keeps its socket
            // where that Graft will look for it.
            "--config-home" => {
                if let Some(path) = arguments.next() {
                    paths::set_config_home_override(Some(PathBuf::from(path)));
                }
            }
            "--home" => {
                if let Some(path) = arguments.next() {
                    paths::set_home_override(Some(PathBuf::from(path)));
                }
            }
            "--port" => {
                if let Some(port) = arguments.next().and_then(|v| v.parse().ok()) {
                    options.port = port;
                }
            }
            // Relative on purpose for a test: a socket path longer than 107
            // bytes cannot be bound, and a scratch directory easily runs to
            // that.
            "--socket" => {
                if let Some(path) = arguments.next() {
                    options.socket = Some(PathBuf::from(path));
                }
            }
            // Plain TCP on loopback and nowhere else, so this can only ever
            // point the relay at a stand-in, never at the real service
            // without TLS.
            "--upstream-loopback-port" => {
                if let Some(port) = arguments.next().and_then(|v| v.parse().ok()) {
                    options.upstream = Upstream::Loopback(port);
                }
            }
            "--removal-check" => {
                if let Some(seconds) = arguments.next().and_then(|v| v.parse::<f64>().ok()).filter(|s| *s > 0.0) {
                    options.removal_check = Duration::from_secs_f64(seconds);
                }
            }
            "--local-processes" => options.local_processes = true,
            _ => {}
        }
    }
    diagnostics::set_who("graft-relay");
    graft_relay::run(options);
}
