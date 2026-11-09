//! Artifacts, Projects and Search Chats: every claude.ai account Graft has been
//! signed in to, one web store each, asked from claude.ai's own pages, and the
//! pages of the window that show what they answer.
//!
//! One sign-in serves all three. `ArtifactCenter` keeps the accounts and the
//! store each lives in; `ProjectCenter` follows it and keeps no sign-in of its
//! own. The widgets in `ui` are what the main window embeds.

pub mod api;
pub mod center;
pub mod docx;
pub mod export;
pub mod files;
pub mod local_sessions;
pub mod model;
pub mod pane_signin;
pub mod project_center;
pub mod projects;
pub mod session;
pub mod signin;
pub mod ui;

pub use center::{AccountState, ArtifactCenter, Change};
pub use local_sessions::{NoTranscripts, TranscriptFacts, Transcripts};
pub use project_center::{ProjectCenter, ProjectChange};

#[cfg(test)]
mod rule_tests;
#[cfg(test)]
mod tests;
