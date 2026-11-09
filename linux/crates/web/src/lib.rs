//! Artifacts, Projects and Search Chats: every claude.ai account Graft has been
//! signed in to, one web store each, asked from claude.ai's own pages, and the
//! pages of the window that show what they answer.
//!
//! One sign-in serves all three. `ArtifactCenter` keeps the accounts and the
//! store each lives in; `ProjectCenter` follows it and keeps no sign-in of its
//! own. The widgets in `ui` are what the main window embeds.

pub mod api;
pub mod files;
pub mod local_sessions;
pub mod model;
pub mod projects;
pub mod session;

#[cfg(test)]
mod tests;
