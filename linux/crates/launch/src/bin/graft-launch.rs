//! What a grafted shortcut runs: `graft-launch <shortcut id>` or
//! `graft-launch <path to graft.json>`. It reads the description it was
//! given, puts it into effect, files records and opens or shows that Claude,
//! then exits. Nothing here asks Graft anything, since the dock starts it
//! with Graft closed as often as open.
//!
//! `graft-launch --stop-sharing` is what removing Graft runs: it puts each
//! closed Claude's own Code settings back and tells the relay it is no longer
//! wanted, so a Claude left with lines pointing at a relay that is gone does
//! not lose the service.

use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(arg) = args.first() else {
        eprintln!("usage: graft-launch <shortcut id | graft.json>");
        return ExitCode::from(2);
    };
    if arg == "--stop-sharing" {
        for path in graft_pool::environment::take_out_lines(None, graft_core::claude::is_running) {
            println!("still open, or changed since Graft read it: {path}");
        }
        return ExitCode::SUCCESS;
    }
    graft_launch::wire();
    match graft_profiles::launch::read_target(arg) {
        Ok(target) => {
            if graft_profiles::launch::run(&target, &graft_launch::Steps) {
                ExitCode::SUCCESS
            } else {
                ExitCode::FAILURE
            }
        }
        Err(graft_profiles::launch::LaunchError::NoDescription(path)) => {
            eprintln!("no profile description at {}", path.display());
            ExitCode::FAILURE
        }
    }
}
