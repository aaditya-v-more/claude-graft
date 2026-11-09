//! The requests claude.ai's own artifact pages make, measured against its web
//! client in November 2026. None of them is documented, so each is kept to
//! exactly the shape that page sends.

use crate::model::Artifact;
use serde_json::{json, Map, Value};
use std::time::SystemTime;
use uuid::Uuid;

pub const ORIGIN: &str = "https://claude.ai";
pub const HOST: &str = "claude.ai";
/// The page a signed-in store is asked from. It answers with the account
/// whether or not anything else is open, and it is claude.ai's own, so a
/// request made from it carries the sign-in as the site's pages do.
pub const ACCOUNT_PATH: &str = "/api/account";
pub const SIGN_IN_URL: &str = "https://claude.ai/login";

/// Without the first of these the artifact service answers 404 to a request
/// that is otherwise identical to the page's, as though the artifact did not
/// exist.
pub const HEADERS: [(&str, &str); 2] = [("X-Frame-CP", "go"), ("X-Frame-Platform", "web")];

/// The artifact service takes its own headers and nothing else of claude.ai
/// does, so they are added by path rather than by caller.
pub fn headers_for(path: &str) -> Vec<(String, String)> {
    if path.starts_with("/api/frame/") {
        HEADERS.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    } else {
        Vec::new()
    }
}

/// A page of 48 is what claude.ai asks for; five of them is more than a window
/// could show, and stops a list that never ends from being followed.
pub const PAGE_SIZE: usize = 48;
pub const MAXIMUM_PAGES: usize = 5;

/// The most an export reads, as claude.ai's own download does.
pub const MAXIMUM_EXPORT_BYTES: usize = 200 * 1024 * 1024;

pub fn list_path(rel: &str, org: &str, after: Option<&str>) -> String {
    let limit = PAGE_SIZE.to_string();
    let mut query = vec![("rel", rel), ("empty", "exclude"), ("limit", limit.as_str()), ("org", org), ("sort", "last_viewed")];
    if let Some(token) = after {
        query.push(("page_token", token));
    }
    format!("/api/frame/artifacts?{}", encode_query(&query))
}

pub fn perm_path(artifact: &str, org: &str) -> String {
    format!("/api/frame/perm/{}?{}", component(artifact), encode_query(&[("org", org)]))
}

pub fn invite_path(artifact: &str, org: &str) -> String {
    format!("/api/frame/invite/{}?{}", component(artifact), encode_query(&[("org", org)]))
}

/// Viewer unless a group says otherwise for that person.
pub fn invite_body(email: &str, role: crate::model::ArtifactRole) -> Vec<u8> {
    sorted(&json!({"email": email, "role": role.raw()}))
}

pub fn retitle_path(artifact: &str, org: &str) -> String {
    format!("/api/frame/retitle/{}?{}", component(artifact), encode_query(&[("org", org)]))
}

pub fn retitle_body(title: &str) -> Vec<u8> {
    sorted(&json!({"title": title}))
}

/// Posted with no body: the copy is of the version the owner last saved.
pub fn duplicate_path(artifact: &str, org: &str) -> String {
    format!("/api/frame/duplicate/{}?{}", component(artifact), encode_query(&[("org", org)]))
}

/// `DELETE` removes the artifact; `GET` is what its viewer asks first, and
/// names the version and the short-lived token its files are read with.
pub fn artifact_path(artifact: &str, org: &str) -> String {
    format!("/api/frame/{}?{}", component(artifact), encode_query(&[("org", org)]))
}

/// What the share panel sends to change who can open an artifact. Going back
/// to private keeps whoever was invited: "users" with them, "owner" when there
/// is nobody, which is what the panel itself does.
pub fn read_mode_body(public: bool, invited: &[String]) -> Vec<u8> {
    let read = if public {
        json!({"mode": "public"})
    } else if invited.is_empty() {
        json!({"mode": "owner"})
    } else {
        json!({"mode": "users", "users": invited})
    };
    sorted(&json!({"read": read}))
}

/// A 401 is a sign-in claude.ai no longer takes, wherever it comes from. A 403
/// is that only from the account page, which any signed-in account may read.
/// Anywhere else it is as likely a refusal of that one thing, an account
/// without Claude Code asking for its sessions or an organization that keeps
/// links private, and read as signed out it signed the account out of all
/// three panes.
pub fn means_signed_out(status: u16, path: &str) -> bool {
    status == 401 || (status == 403 && is_account_path(path))
}

/// A 403 from anywhere but the account page, which the account page is then
/// asked about once: signed out there is signed out, and otherwise the 403
/// goes back to the caller as the refusal it was.
pub fn needs_account_check(status: u16, path: &str) -> bool {
    status == 403 && !is_account_path(path)
}

fn is_account_path(path: &str) -> bool {
    path == ACCOUNT_PATH || path.starts_with(&format!("{ACCOUNT_PATH}?"))
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Identity {
    pub account_uuid: String,
    pub email: String,
    pub org: String,
}

/// The organization artifacts live under is the one that can chat; an account
/// in a team as well as on its own plan belongs to both, and the first
/// membership listed is not reliably either.
pub fn identity(data: &[u8]) -> Option<Identity> {
    let object: Map<String, Value> = serde_json::from_slice(data).ok()?;
    let uuid = object.get("uuid")?.as_str()?;
    let email = object.get("email_address")?.as_str()?;
    let memberships = object.get("memberships")?.as_array()?;
    let organizations: Vec<&Map<String, Value>> =
        memberships.iter().filter_map(|m| m.get("organization")?.as_object()).collect();
    let chats = |o: &&&Map<String, Value>| {
        o.get("capabilities")
            .and_then(Value::as_array)
            .is_some_and(|c| c.iter().any(|v| v.as_str() == Some("chat")))
    };
    let chosen = organizations.iter().find(chats).or(organizations.first())?;
    let org = chosen.get("uuid")?.as_str()?;
    Some(Identity { account_uuid: uuid.into(), email: email.into(), org: org.into() })
}

/// The keys of an answer that would not read as an account, never its values,
/// for the one failure nothing on screen would show.
pub fn answer_keys(data: &[u8]) -> Vec<String> {
    let mut keys: Vec<String> = serde_json::from_slice::<Map<String, Value>>(data)
        .map(|m| m.keys().cloned().collect())
        .unwrap_or_default();
    keys.sort();
    keys
}

pub struct Page {
    pub artifacts: Vec<Artifact>,
    pub next: Option<String>,
}

pub fn page(data: &[u8], account: Uuid, org: &str) -> Option<Page> {
    let object: Map<String, Value> = serde_json::from_slice(data).ok()?;
    let rows = object.get("artifacts")?.as_array()?;
    let artifacts = rows
        .iter()
        .filter_map(|row| {
            let row = row.as_object()?;
            let id = row.get("id")?.as_str()?.to_string();
            let text = |key: &str| row.get(key).and_then(Value::as_str).map(str::to_string);
            let flag = |key: &str, default: bool| row.get(key).and_then(Value::as_bool).unwrap_or(default);
            Some(Artifact {
                id,
                account,
                org: org.to_string(),
                title: text("title").filter(|t| !t.is_empty()).unwrap_or_else(|| "Untitled".into()),
                kind: text("kind").unwrap_or_else(|| "page".into()),
                open_url: text("open_url").filter(|u| u.starts_with("https://") || u.starts_with("http://")),
                audience: text("audience").unwrap_or_else(|| "owner".into()),
                rel: text("rel").unwrap_or_else(|| "mine".into()),
                sort_time: text("sort_time").and_then(|t| date(&t)).or_else(|| text("updated_at").and_then(|t| date(&t))),
                owner_account: text("owner_account"),
                renamable: flag("renamable", false),
                duplicable: flag("duplicable", false),
                duplicate_available: flag("duplicate_available", true),
            })
        })
        .collect();
    let next = object.get("next_page_token").and_then(Value::as_str).filter(|t| !t.is_empty()).map(str::to_string);
    Some(Page { artifacts, next })
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Permissions {
    pub version: Option<String>,
    pub invited: Vec<String>,
    /// Whether this artifact takes commenters and editors at all. Its own
    /// panel offers only the roles these allow.
    pub commenters: bool,
    pub editors: bool,
    /// `read.mode` as claude.ai has it now, which a list read earlier may not.
    pub mode: Option<String>,
}

pub fn permissions(data: &[u8]) -> Option<Permissions> {
    let object: Map<String, Value> = serde_json::from_slice(data).ok()?;
    let read = object.get("read").and_then(Value::as_object);
    // Older answers name editing `writersEnabled`; a flag that is not there
    // offers nothing, so a change in the answer cannot hand out more than
    // viewing.
    let editors = object
        .get("editorsEnabled")
        .and_then(Value::as_bool)
        .or_else(|| object.get("writersEnabled").and_then(Value::as_bool))
        .unwrap_or(false);
    let version = match object.get("version") {
        Some(Value::String(s)) => Some(s.clone()),
        Some(Value::Number(n)) if n.is_i64() || n.is_u64() => Some(n.to_string()),
        _ => None,
    };
    Some(Permissions {
        version,
        invited: read
            .and_then(|r| r.get("users"))
            .and_then(Value::as_array)
            .map(|u| u.iter().filter_map(|v| v.as_str().map(str::to_string)).collect())
            .unwrap_or_default(),
        commenters: object.get("commentersEnabled").and_then(Value::as_bool).unwrap_or(false),
        editors,
        mode: read.and_then(|r| r.get("mode")).and_then(Value::as_str).map(str::to_string),
    })
}

/// Why claude.ai refused, when it says: `title_invalid`, `org_mismatch`, a
/// daily cap on copies.
pub fn reason(data: &[u8]) -> Option<String> {
    let object: Map<String, Value> = serde_json::from_slice(data).ok()?;
    if let Some(reason) = object.get("reason").and_then(Value::as_str) {
        return Some(reason.into());
    }
    object.get("error")?.get("reason")?.as_str().map(str::to_string)
}

/// What reading an artifact's files takes: the version they belong to and the
/// token its viewer reads them with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Source {
    pub version: String,
    pub token: String,
    pub title: Option<String>,
    /// Relative paths, `index.html` first. Anything that could climb out of
    /// the folder it is written into is dropped here.
    pub files: Vec<String>,
}

pub fn source(data: &[u8]) -> Option<Source> {
    let object: Map<String, Value> = serde_json::from_slice(data).ok()?;
    let token = object.get("assetToken")?.as_str().filter(|t| !t.is_empty())?.to_string();
    let version = match object.get("ver") {
        Some(Value::String(s)) => s.clone(),
        Some(Value::Number(n)) if n.is_i64() || n.is_u64() => n.to_string(),
        _ => return None,
    };
    if version.is_empty() {
        return None;
    }
    let mut files = vec!["index.html".to_string()];
    for entry in object.get("files").and_then(Value::as_array).into_iter().flatten() {
        if let Some(path) = entry.get("path").and_then(Value::as_str) {
            if is_safe_file_path(path) && !files.iter().any(|f| f == path) {
                files.push(path.to_string());
            }
        }
    }
    Some(Source { version, token, title: object.get("title").and_then(Value::as_str).map(str::to_string), files })
}

pub fn is_safe_file_path(path: &str) -> bool {
    !path.is_empty()
        && !path.starts_with('/')
        && !path.contains('\\')
        && path.split('/').all(|p| !p.is_empty() && p != "." && p != "..")
}

/// Where an artifact's files are served, exactly as its viewer reads them. The
/// token goes in the address because that is the only way the file host takes
/// it; it is the artifact's own, lasts minutes, and is asked for afresh every
/// export.
pub fn file_url(artifact: &str, source: &Source, path: &str) -> Option<String> {
    let host: String = artifact.to_lowercase().chars().filter(|c| c.is_ascii_hexdigit() || *c == '-').collect();
    if host.is_empty() || !is_safe_file_path(path) {
        return None;
    }
    let encoded: Vec<String> = path.split('/').map(component).collect();
    Some(format!(
        "https://{host}.frame.claudeusercontent.com/_f/{}/_src/{}?{}",
        component(&source.version),
        encoded.join("/"),
        encode_query(&[("__frame_t", source.token.as_str())])
    ))
}

/// One artifact may come back from two accounts: one that owns it, and one it
/// was shared with. It is listed once, under the owner.
pub fn merge(lists: &[Vec<Artifact>]) -> Vec<Artifact> {
    let mut seen: std::collections::HashMap<String, usize> = std::collections::HashMap::new();
    let mut merged: Vec<Artifact> = Vec::new();
    for artifact in lists.iter().flatten() {
        if let Some(&index) = seen.get(&artifact.id) {
            if artifact.is_own() && !merged[index].is_own() {
                merged[index] = artifact.clone();
            }
            continue;
        }
        seen.insert(artifact.id.clone(), merged.len());
        merged.push(artifact.clone());
    }
    merged.sort_by(|a, b| b.sort_time.unwrap_or(SystemTime::UNIX_EPOCH).cmp(&a.sort_time.unwrap_or(SystemTime::UNIX_EPOCH)));
    merged
}

pub fn date(text: &str) -> Option<SystemTime> {
    let parsed = time::OffsetDateTime::parse(text, &time::format_description::well_known::Rfc3339).ok()?;
    Some(SystemTime::from(parsed))
}

/// JSON with its keys in order, as the page's own requests are written.
pub fn sorted(value: &Value) -> Vec<u8> {
    graft_core::files::sorted_json(value)
}

/// A path component, encoded the way a browser's address keeps it: a slash
/// inside an id is not a folder.
pub fn component(text: &str) -> String {
    percent(text, b"!$&'()*+,-.:=@_~")
}

/// A query value with `&`, `=`, `+` and `?` encoded, so a page token holding a
/// plus sign is not read back as a space.
pub fn encode_query(pairs: &[(&str, &str)]) -> String {
    pairs
        .iter()
        .map(|(k, v)| format!("{k}={}", percent(v, b"!$'()*,-./:;@_~")))
        .collect::<Vec<_>>()
        .join("&")
}

fn percent(text: &str, allowed: &[u8]) -> String {
    let mut out = String::with_capacity(text.len());
    for byte in text.bytes() {
        if byte.is_ascii_alphanumeric() || allowed.contains(&byte) {
            out.push(byte as char);
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}
