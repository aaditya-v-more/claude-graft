//! The requests claude.ai's Projects page and its search box make, read off its
//! web client in November 2026, and what Graft makes of their answers. None is
//! documented, so each is kept to exactly what that page sends. Unlike the
//! artifact service, none takes the `X-Frame-*` headers; the session adds
//! those to `/api/frame` alone.

use crate::api::{component, date, encode_query, sorted, ORIGIN};
use serde_json::{json, Map, Value};
use std::path::PathBuf;
use std::time::{Duration, SystemTime};
use uuid::Uuid;

/// A project on claude.ai, as its own Projects page lists it.
#[derive(Debug, Clone, PartialEq)]
pub struct ClaudeProject {
    pub id: String,
    /// The signed-in account that listed it.
    pub account: Uuid,
    pub name: String,
    pub summary: String,
    pub is_private: bool,
    pub is_starred: bool,
    /// The "How to use Claude" project every account starts with.
    pub is_example: bool,
    pub creator: Option<String>,
    pub updated: Option<SystemTime>,
}

impl ClaudeProject {
    pub fn open_url(&self) -> String {
        format!("{ORIGIN}/project/{}", self.id)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HitKind {
    Chat,
    Code,
}

/// Where a Code session ran, which is what says whether it can be picked up
/// again on this machine. A chat is always on claude.ai and has none.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Place {
    ThisMachine,
    /// Remote Control from a machine whose transcript is not here, or one
    /// carried here from a home folder this machine does not have.
    OtherMachine,
    Cloud,
    /// A transcript on this machine that claude.ai no longer lists for its
    /// account: deleted there, or never sent.
    GoneFromClaude,
}

/// A range of UTF-16 units, which is how the page's script counts them.
pub type Units = std::ops::Range<usize>;

/// One thing a search found: a chat claude.ai's search matched, or a Code
/// session whose title or folder did.
#[derive(Debug, Clone, PartialEq)]
pub struct SearchHit {
    pub id: String,
    pub account: Uuid,
    pub kind: HitKind,
    pub title: String,
    pub snippet: String,
    pub snippet_matches: Vec<Units>,
    pub title_matches: Vec<Units>,
    pub project: Option<String>,
    pub updated: Option<SystemTime>,
    pub is_archived: bool,
    pub is_starred: bool,
    pub place: Option<Place>,
    /// The folder a Code session worked in, when this machine has its
    /// transcript.
    pub folder: Option<String>,
    pub transcript: Option<PathBuf>,
    /// The order claude.ai ranked it in, within its own account.
    pub rank: usize,
    /// `None` for a session claude.ai no longer has, which is shown in the
    /// file manager instead.
    pub open_url: Option<String>,
}

impl SearchHit {
    /// The first eight characters that name its controls.
    pub fn token(&self) -> String {
        if self.open_url.is_none() {
            return self
                .transcript
                .as_ref()
                .and_then(|t| t.file_stem())
                .map(|s| s.to_string_lossy().chars().take(8).collect())
                .unwrap_or_else(|| "local".into());
        }
        self.id.replace("cse_", "").chars().take(8).collect()
    }
}

/// A Code session as claude.ai's own list has it.
#[derive(Debug, Clone, PartialEq)]
pub struct RemoteCodeSession {
    pub id: String,
    pub title: String,
    pub is_archived: bool,
    pub in_cloud: bool,
    pub updated: Option<SystemTime>,
    pub repository: Option<String>,
}

/// What this machine's transcripts say about a Code session.
#[derive(Debug, Clone, PartialEq)]
pub struct LocalCodeSession {
    pub bridge_ids: Vec<String>,
    pub owner: String,
    /// `ownerOrganizationUuid`: an account in two organizations has a session
    /// list in each, and only one of them is read.
    pub organization: String,
    pub title: String,
    pub folder: String,
    pub transcript: PathBuf,
    pub last_active: SystemTime,
    pub folder_is_here: bool,
    /// A record names it among the sessions its conversation grew out of. The
    /// conversation goes on under a later session, often under another
    /// account's list, so this half not being listed says nothing about it.
    pub carried_on: bool,
}

/// Thirty is what the Projects page asks for. Five pages is more projects than
/// anyone keeps, and stops a list that never ends from being followed.
pub const PROJECT_PAGE_SIZE: usize = 30;
pub const MAXIMUM_PROJECT_PAGES: usize = 5;

pub fn projects_path(org: &str, offset: usize) -> String {
    let limit = PROJECT_PAGE_SIZE.to_string();
    let offset = offset.to_string();
    format!(
        "/api/organizations/{}/projects_v2?{}",
        component(org),
        encode_query(&[
            ("include_harmony_projects", "true"),
            ("limit", &limit),
            ("offset", &offset),
            ("order_by", "latest_chat"),
        ])
    )
}

pub fn search_path(org: &str) -> String {
    format!("/api/organizations/{}/conversation/search/v2", component(org))
}

/// Twenty-five results with a hundred characters around each match is what
/// the search box asks for. Archived chats come back with the rest, marked;
/// the box hides them itself until "Show archived" is pressed.
pub const SEARCH_LIMIT: usize = 25;

pub fn search_body(query: &str) -> Vec<u8> {
    sorted(&json!({"query": query, "n": SEARCH_LIMIT, "target_snippet_size": 100}))
}

/// The search box's Code tab asks for every session, active and archived
/// alike, and matches titles in the page. Two hundred a page is what it asks;
/// ten pages is four times what a heavy account holds.
pub const CODE_PAGE_SIZE: usize = 200;
pub const MAXIMUM_CODE_PAGES: usize = 10;

pub fn code_sessions_path(after: Option<&str>) -> String {
    let limit = CODE_PAGE_SIZE.to_string();
    let mut query = vec![("limit", limit.as_str())];
    if let Some(cursor) = after {
        query.push(("cursor", cursor));
    }
    format!("/v1/code/sessions?{}", encode_query(&query))
}

/// Without the beta and the organization the session service answers 400.
pub fn code_headers(org: &str) -> Vec<(String, String)> {
    vec![
        ("anthropic-beta".into(), "ccr-byoc-2025-07-29".into()),
        ("anthropic-version".into(), "2023-06-01".into()),
        ("x-organization-uuid".into(), org.into()),
    ]
}

/// A session is listed as `cse_...` and opened as `session_...`, the same name
/// after the prefix.
pub fn code_session_url(id: &str) -> String {
    let name = match id.strip_prefix("cse_") {
        Some(rest) => format!("session_{rest}"),
        None => id.to_string(),
    };
    format!("{ORIGIN}/code/{name}")
}

pub fn chat_url(id: &str) -> String {
    format!("{ORIGIN}/chat/{id}")
}

fn object(data: &[u8]) -> Option<Map<String, Value>> {
    serde_json::from_slice(data).ok()
}

fn text(row: &Map<String, Value>, key: &str) -> Option<String> {
    row.get(key).and_then(Value::as_str).map(str::to_string)
}

pub fn projects(data: &[u8], account: Uuid) -> Option<(Vec<ClaudeProject>, bool)> {
    let object = object(data)?;
    let rows = object.get("data")?.as_array()?;
    let projects = rows
        .iter()
        .filter_map(|row| {
            let row = row.as_object()?;
            let id = text(row, "uuid")?;
            // An archived project is off the Projects page, so it is off here.
            if row.get("archived_at").is_some_and(|a| !a.is_null()) {
                return None;
            }
            Some(ClaudeProject {
                id,
                account,
                name: text(row, "name").filter(|n| !n.is_empty()).unwrap_or_else(|| "Untitled".into()),
                summary: text(row, "description").unwrap_or_default(),
                is_private: row.get("is_private").and_then(Value::as_bool).unwrap_or(true),
                is_starred: row.get("is_starred").and_then(Value::as_bool).unwrap_or(false),
                is_example: row.get("is_starter_project").and_then(Value::as_bool).unwrap_or(false),
                creator: row.get("creator").and_then(|c| c.get("full_name")).and_then(Value::as_str).map(str::to_string),
                updated: text(row, "updated_at").and_then(|t| date(&t)),
            })
        })
        .collect();
    let more = object.get("pagination").and_then(|p| p.get("has_more")).and_then(Value::as_bool).unwrap_or(false);
    Some((projects, more))
}

pub fn chats(data: &[u8], account: Uuid) -> Option<Vec<SearchHit>> {
    let object = object(data)?;
    let rows = object.get("data")?.as_array()?;
    Some(
        rows.iter()
            .enumerate()
            .filter_map(|(index, row)| {
                let row = row.as_object()?;
                let conversation = row.get("conversation")?.as_object()?;
                let id = text(conversation, "uuid")?;
                let snippet = row.get("matched_snippet").and_then(Value::as_object);
                let title = text(conversation, "name").filter(|n| !n.is_empty());
                Some(SearchHit {
                    open_url: Some(chat_url(&id)),
                    id,
                    account,
                    kind: HitKind::Chat,
                    title_matches: if title.is_none() { Vec::new() } else { ranges(row.get("title_matches")) },
                    title: title.unwrap_or_else(|| "Untitled".into()),
                    snippet: snippet.and_then(|s| text(s, "text")).unwrap_or_default(),
                    snippet_matches: ranges(snippet.and_then(|s| s.get("matches"))),
                    project: text(conversation, "project_uuid"),
                    updated: text(conversation, "updated_at").and_then(|t| date(&t)),
                    is_archived: conversation.get("is_archived").and_then(Value::as_bool).unwrap_or(false),
                    is_starred: conversation.get("is_starred").and_then(Value::as_bool).unwrap_or(false),
                    place: None,
                    folder: None,
                    transcript: None,
                    rank: row.get("semantic_rank").and_then(Value::as_u64).map(|r| r as usize).unwrap_or(index),
                })
            })
            .collect(),
    )
}

pub fn code_sessions(data: &[u8]) -> Option<(Vec<RemoteCodeSession>, Option<String>)> {
    let object = object(data)?;
    let rows = object.get("data")?.as_array()?;
    let sessions = rows
        .iter()
        .filter_map(|row| {
            let row = row.as_object()?;
            let id = text(row, "id")?;
            let repository = row
                .get("config")
                .and_then(|c| c.get("sources"))
                .and_then(Value::as_array)
                .and_then(|s| s.first())
                .and_then(|s| s.get("url"))
                .and_then(Value::as_str)
                .map(str::to_string);
            Some(RemoteCodeSession {
                id,
                title: text(row, "title").filter(|t| !t.is_empty()).unwrap_or_else(|| "Untitled".into()),
                is_archived: text(row, "status").as_deref() == Some("archived"),
                in_cloud: text(row, "environment_kind").as_deref() == Some("anthropic_cloud"),
                updated: text(row, "last_event_at").and_then(|t| date(&t)).or_else(|| text(row, "updated_at").and_then(|t| date(&t))),
                repository,
            })
        })
        .collect();
    let next = text(&object, "next_cursor").filter(|c| !c.is_empty());
    Some((sessions, next))
}

/// Every word of the query, in any order, somewhere in what is searched, as
/// the Code tab matches; the title's own matches are picked out.
pub fn code_match(query: &str, title: &str, also: &[Option<&str>]) -> Option<Vec<Units>> {
    let words: Vec<String> = query.to_lowercase().split_whitespace().map(str::to_string).collect();
    if words.is_empty() {
        return None;
    }
    let lowered = title.to_lowercase();
    let mut haystack = vec![lowered.clone()];
    haystack.extend(also.iter().flatten().map(|s| s.to_lowercase()));
    let haystack = haystack.join("\n");
    if !words.iter().all(|w| haystack.contains(w.as_str())) {
        return None;
    }
    let units: Vec<u16> = lowered.encode_utf16().collect();
    Some(
        words
            .iter()
            .filter_map(|word| {
                let needle: Vec<u16> = word.encode_utf16().collect();
                if needle.len() > units.len() {
                    return None;
                }
                (0..=units.len() - needle.len())
                    .find(|&start| units[start..start + needle.len()] == needle[..])
                    .map(|start| start..start + needle.len())
            })
            .collect(),
    )
}

/// What a session list was read as, for the claim that a transcript is gone.
pub struct SessionList<'a> {
    pub sessions: &'a [RemoteCodeSession],
    /// Read to its end. One past the last page read is simply not known.
    pub complete: bool,
    /// When its walk started.
    pub listed_at: Option<SystemTime>,
}

/// One account's Code sessions that match, each placed by what this machine's
/// transcripts say about it, and the transcripts claude.ai no longer has.
///
/// A session is only called gone when the account's whole list was read: one
/// past the last page read is simply not known. So is one owned under another
/// organization than the list was read for, which that list never holds, one
/// whose transcript moved on after the list was read, which may have begun
/// since, and an earlier half of a conversation still going.
pub fn code_hits(
    query: &str,
    account: Uuid,
    account_uuid: Option<&str>,
    organization: Option<&str>,
    list: &SessionList,
    local: &[LocalCodeSession],
) -> Vec<SearchHit> {
    let mut by_bridge: std::collections::HashMap<&str, &LocalCodeSession> = std::collections::HashMap::new();
    for session in local {
        for id in &session.bridge_ids {
            by_bridge.insert(id.as_str(), session);
        }
    }
    let mut hits = Vec::new();
    for (index, session) in list.sessions.iter().enumerate() {
        let here = by_bridge.get(session.id.as_str()).copied();
        let Some(matches) = code_match(
            query,
            &session.title,
            &[session.repository.as_deref(), here.map(|h| h.folder.as_str())],
        ) else {
            continue;
        };
        let place = if session.in_cloud {
            Place::Cloud
        } else {
            match here {
                Some(h) if h.folder_is_here => Place::ThisMachine,
                _ => Place::OtherMachine,
            }
        };
        hits.push(SearchHit {
            id: session.id.clone(),
            account,
            kind: HitKind::Code,
            title: session.title.clone(),
            snippet: here.map(|h| h.folder.clone()).or_else(|| session.repository.clone()).unwrap_or_default(),
            snippet_matches: Vec::new(),
            title_matches: matches,
            project: None,
            updated: session.updated,
            is_archived: session.is_archived,
            is_starred: false,
            place: Some(place),
            folder: here.map(|h| h.folder.clone()),
            transcript: here.map(|h| h.transcript.clone()),
            rank: index,
            open_url: Some(code_session_url(&session.id)),
        });
    }
    let (true, Some(account_uuid), Some(organization), Some(listed_at)) =
        (list.complete, account_uuid, organization, list.listed_at)
    else {
        return hits;
    };
    let listed: std::collections::HashSet<&str> = list.sessions.iter().map(|s| s.id.as_str()).collect();
    for session in local {
        if session.owner != account_uuid
            || session.organization != organization
            || session.last_active >= listed_at
            || session.carried_on
            || session.bridge_ids.iter().any(|id| listed.contains(id.as_str()))
        {
            continue;
        }
        let Some(matches) = code_match(query, &session.title, &[Some(session.folder.as_str())]) else { continue };
        hits.push(SearchHit {
            id: format!("local:{}", session.transcript.to_string_lossy()),
            account,
            kind: HitKind::Code,
            title: session.title.clone(),
            snippet: session.folder.clone(),
            snippet_matches: Vec::new(),
            title_matches: matches,
            project: None,
            updated: Some(session.last_active),
            is_archived: false,
            is_starred: false,
            place: Some(Place::GoneFromClaude),
            folder: Some(session.folder.clone()),
            transcript: Some(session.transcript.clone()),
            rank: hits.len(),
            open_url: None,
        });
    }
    hits
}

/// Whether a read of this machine's transcripts can stand beside a session
/// list: still fresh, and made no earlier than the list. One made earlier is
/// missing whatever began here in between, which the list holds and would
/// place on another machine.
pub fn local_read_serves(read: SystemTime, list: SystemTime, now: SystemTime, lifetime: Duration) -> bool {
    read >= list && now.duration_since(read).map(|age| age < lifetime).unwrap_or(true)
}

/// Every account's projects in one list: starred ones first, as on claude.ai,
/// then the most recently changed.
pub fn merge(lists: &[Vec<ClaudeProject>]) -> Vec<ClaudeProject> {
    let mut all: Vec<ClaudeProject> = lists.iter().flatten().cloned().collect();
    all.sort_by(|l, r| {
        r.is_starred
            .cmp(&l.is_starred)
            .then_with(|| r.updated.unwrap_or(SystemTime::UNIX_EPOCH).cmp(&l.updated.unwrap_or(SystemTime::UNIX_EPOCH)))
    });
    all
}

/// Each account ranks its own chats and the ranks of two accounts mean nothing
/// against each other, so the lists are dealt in turn: every account's best,
/// then every account's second. Code sessions have no rank but their age, and
/// follow, newest first.
pub fn interleave(lists: &[Vec<SearchHit>]) -> Vec<SearchHit> {
    let chats: Vec<Vec<&SearchHit>> = lists
        .iter()
        .map(|l| {
            let mut c: Vec<&SearchHit> = l.iter().filter(|h| h.kind == HitKind::Chat).collect();
            c.sort_by_key(|h| h.rank);
            c
        })
        .collect();
    let mut merged: Vec<SearchHit> = Vec::new();
    let longest = chats.iter().map(Vec::len).max().unwrap_or(0);
    for position in 0..longest {
        for list in &chats {
            if let Some(hit) = list.get(position) {
                merged.push((*hit).clone());
            }
        }
    }
    let mut code: Vec<SearchHit> = lists.iter().flatten().filter(|h| h.kind == HitKind::Code).cloned().collect();
    code.sort_by(|a, b| b.updated.unwrap_or(SystemTime::UNIX_EPOCH).cmp(&a.updated.unwrap_or(SystemTime::UNIX_EPOCH)));
    merged.extend(code);
    merged
}

/// The parts of `text` a search matched, in bold Pango markup, with any range
/// that does not fit the text, or does not fall between two characters,
/// dropped rather than trusted.
pub fn highlighted(text: &str, ranges: &[Units]) -> String {
    let units: Vec<u16> = text.encode_utf16().collect();
    // The unit offset each character starts at, so a range is cut only where
    // a character begins.
    let mut starts: Vec<(usize, usize)> = Vec::new();
    let mut unit = 0;
    for (byte, ch) in text.char_indices() {
        starts.push((unit, byte));
        unit += ch.len_utf16();
    }
    starts.push((units.len(), text.len()));
    let byte_at = |u: usize| starts.iter().find(|(s, _)| *s == u).map(|(_, b)| *b);
    let mut marks: Vec<(usize, usize)> = ranges
        .iter()
        .filter(|r| r.start < r.end && r.end <= units.len())
        .filter_map(|r| Some((byte_at(r.start)?, byte_at(r.end)?)))
        .collect();
    marks.sort();
    let mut out = String::new();
    let mut cursor = 0;
    for (from, to) in marks {
        if from < cursor {
            continue;
        }
        out.push_str(&escape(&text[cursor..from]));
        out.push_str("<b>");
        out.push_str(&escape(&text[from..to]));
        out.push_str("</b>");
        cursor = to;
    }
    out.push_str(&escape(&text[cursor..]));
    out
}

pub fn escape(text: &str) -> String {
    gtk::glib::markup_escape_text(text).to_string()
}

fn ranges(value: Option<&Value>) -> Vec<Units> {
    value
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|m| {
                    let start = m.get("start")?.as_u64()? as usize;
                    let end = m.get("end")?.as_u64()? as usize;
                    (start < end).then_some(start..end)
                })
                .collect()
        })
        .unwrap_or_default()
}

/// What a person narrows a search to. Every filter is applied to what came
/// back, so changing one asks claude.ai nothing.
#[derive(Debug, Clone, PartialEq)]
pub struct SearchFilters {
    pub kind: KindFilter,
    /// Active only, as claude.ai's own box starts.
    pub status: StatusFilter,
    pub place: Option<Place>,
    pub age: AgeFilter,
    pub account: Option<Uuid>,
    pub project: ProjectChoice,
    pub starred_only: bool,
}

impl Default for SearchFilters {
    fn default() -> Self {
        SearchFilters {
            kind: KindFilter::All,
            status: StatusFilter::Active,
            place: None,
            age: AgeFilter::Any,
            account: None,
            project: ProjectChoice::Any,
            starred_only: false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KindFilter {
    All,
    Chats,
    Code,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StatusFilter {
    Active,
    Archived,
    Any,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AgeFilter {
    Any,
    Day,
    Week,
    Month,
    Year,
}

impl AgeFilter {
    pub fn span(self) -> Option<Duration> {
        let day = 86_400;
        match self {
            AgeFilter::Any => None,
            AgeFilter::Day => Some(Duration::from_secs(day)),
            AgeFilter::Week => Some(Duration::from_secs(7 * day)),
            AgeFilter::Month => Some(Duration::from_secs(31 * day)),
            AgeFilter::Year => Some(Duration::from_secs(366 * day)),
        }
    }
}

/// A project of a chat, or chats in no project at all.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum ProjectChoice {
    Any,
    None,
    Project(String),
}

impl SearchFilters {
    pub fn is_narrowed(&self) -> bool {
        *self != SearchFilters::default()
    }

    /// A place, a project or a star is something only one kind has, so asking
    /// for one leaves the other kind out rather than letting it through.
    pub fn admits(&self, hit: &SearchHit, now: SystemTime) -> bool {
        match self.kind {
            KindFilter::All => {}
            KindFilter::Chats if hit.kind != HitKind::Chat => return false,
            KindFilter::Code if hit.kind != HitKind::Code => return false,
            _ => {}
        }
        match self.status {
            StatusFilter::Any => {}
            StatusFilter::Active if hit.is_archived => return false,
            StatusFilter::Archived if !hit.is_archived => return false,
            _ => {}
        }
        if let Some(wanted) = self.place {
            if hit.place != Some(wanted) {
                return false;
            }
        }
        if let Some(span) = self.age.span() {
            let Some(updated) = hit.updated else { return false };
            if now.duration_since(updated).map(|age| age > span).unwrap_or(false) {
                return false;
            }
        }
        if let Some(account) = self.account {
            if hit.account != account {
                return false;
            }
        }
        match &self.project {
            ProjectChoice::Any => {}
            ProjectChoice::None => {
                if hit.kind != HitKind::Chat || hit.project.is_some() {
                    return false;
                }
            }
            ProjectChoice::Project(id) => {
                if hit.project.as_deref() != Some(id.as_str()) {
                    return false;
                }
            }
        }
        !(self.starred_only && !hit.is_starred)
    }
}

/// What each filter and each place is called, in one place so the rows and
/// the menus say the same thing.
pub mod labels {
    use super::*;

    pub fn kind(kind: KindFilter) -> &'static str {
        match kind {
            KindFilter::All => "Chats and Code",
            KindFilter::Chats => "Chats",
            KindFilter::Code => "Code Sessions",
        }
    }

    pub fn status(status: StatusFilter) -> &'static str {
        match status {
            StatusFilter::Active => "Active",
            StatusFilter::Archived => "Archived",
            StatusFilter::Any => "Active and Archived",
        }
    }

    pub fn place_filter(place: Option<Place>) -> &'static str {
        match place {
            None => "Anywhere",
            Some(Place::ThisMachine) => "On This Computer",
            Some(Place::OtherMachine) => "On Another Computer",
            Some(Place::Cloud) => "In the Cloud",
            Some(Place::GoneFromClaude) => "Gone from claude.ai",
        }
    }

    pub fn age(age: AgeFilter) -> &'static str {
        match age {
            AgeFilter::Any => "Any Time",
            AgeFilter::Day => "Past Day",
            AgeFilter::Week => "Past Week",
            AgeFilter::Month => "Past Month",
            AgeFilter::Year => "Past Year",
        }
    }

    /// What is narrowing the list, for the button's tooltip and for a screen
    /// reader, since nothing else on screen says.
    pub fn describe(filters: &SearchFilters) -> String {
        let plain = SearchFilters::default();
        let mut parts: Vec<&str> = Vec::new();
        if filters.kind != plain.kind {
            parts.push(kind(filters.kind));
        }
        if filters.status != plain.status {
            parts.push(status(filters.status));
        }
        if filters.place != plain.place {
            parts.push(place_filter(filters.place));
        }
        if filters.age != plain.age {
            parts.push(age(filters.age));
        }
        if filters.account.is_some() {
            parts.push("One account");
        }
        match filters.project {
            ProjectChoice::Any => {}
            ProjectChoice::None => parts.push("No Project"),
            ProjectChoice::Project(_) => parts.push("One project"),
        }
        if filters.starred_only {
            parts.push("Starred Only");
        }
        parts.join(" \u{b7} ")
    }

    pub fn place_of(hit: &SearchHit) -> Option<&'static str> {
        match hit.place? {
            Place::ThisMachine => Some("On this computer"),
            Place::OtherMachine => Some("On another computer"),
            Place::Cloud => Some("In the cloud"),
            Place::GoneFromClaude => Some("Gone from claude.ai"),
        }
    }

    pub fn icon_of(hit: &SearchHit) -> &'static str {
        if hit.kind == HitKind::Chat {
            return if hit.is_archived { "folder-documents-symbolic" } else { "chat-bubbles-text-symbolic" };
        }
        match hit.place {
            Some(Place::Cloud) => "weather-overcast-symbolic",
            Some(Place::GoneFromClaude) => "user-trash-symbolic",
            Some(Place::OtherMachine) => "computer-symbolic",
            _ => "utilities-terminal-symbolic",
        }
    }
}
