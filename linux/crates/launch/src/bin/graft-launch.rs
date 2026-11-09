//! What a grafted shortcut runs: `graft-launch <shortcut id>` or
//! `graft-launch <path to graft.json>`. It reads the description it was
//! given, puts it into effect, files records and opens or shows that Claude,
//! then exits. Nothing here asks Graft anything, since the dock starts it
//! with Graft closed as often as open.

use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(arg) = args.first() else {
        eprintln!("usage: graft-launch <shortcut id | graft.json>");
        return ExitCode::from(2);
    };
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
