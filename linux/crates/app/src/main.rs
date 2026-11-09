//! Claude Graft for Linux: the window, the tray item and the tray's window,
//! on top of the crates that do the work.

mod app;
mod chat_conflict;
mod dropdown;
mod front;
mod icons;
mod links;
mod main_detail;
mod policy;
mod preferences;
mod reach;
mod settings;
mod shared;
mod shared_usage;
mod sharing;
mod shortcut_detail;
mod title;
mod transcripts;
mod tray;
mod ui;
mod updates;
mod usage_view;
mod window;

#[cfg(test)]
mod tests;

use std::process::ExitCode;

const HELP: &str = "usage: graft [--help | --version]

Opens Claude Graft: its window, and its item in the tray. A second start
shows the window of the one already running.";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        None => {}
        Some("--help" | "-h") => {
            println!("{HELP}");
            return ExitCode::SUCCESS;
        }
        Some("--version" | "-V") => {
            println!("Claude Graft {}", graft_profiles::installer::graft_version());
            return ExitCode::SUCCESS;
        }
        Some(other) => {
            eprintln!("graft: unknown argument {other}\n\n{HELP}");
            return ExitCode::from(2);
        }
    }
    let code = app::run();
    if code == gtk::glib::ExitCode::SUCCESS {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}
