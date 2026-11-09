//! What Graft knows about artifacts, the accounts that list them and the
//! groups they are shared with. Nothing here reaches claude.ai or needs a
//! display, which is why the suite can hold every rule in it.

use crate::api::{Identity, Permissions};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::Value;
use std::collections::BTreeMap;
use std::time::SystemTime;
use uuid::Uuid;

/// An account Graft has been signed in to on claude.ai, for its artifacts.
///
/// The sign-in itself is not here and never passes through Graft: it lives in
/// the web store named by `id`, and only pages from claude.ai, loaded into
/// that store, ever send it. What is kept on disk is what the account said
/// about itself once signed in, so the list can name it before it is asked.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ArtifactAccount {
    #[serde(with = "upper_uuid")]
    pub id: Uuid,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    #[serde(rename = "accountUuid", default, skip_serializing_if = "Option::is_none")]
    pub account_uuid: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub org: Option<String>,
}

impl ArtifactAccount {
    pub fn new(id: Uuid) -> ArtifactAccount {
        ArtifactAccount { id, email: None, account_uuid: None, org: None }
    }
}

/// The store just signed into is the one that works. A second sign-in to an
/// account already listed is made because the first one stopped working, and a
/// sign-in cannot be moved between stores without reading it out of one, which
/// is never done. So the record moves to the new store, keeping its place in
/// the list, and the stores it was under before come back in the second half to
/// be removed. A record that was under this store as some other account goes
/// too: the sign-in it had is the one this replaced.
pub fn signed_in(
    accounts: &[ArtifactAccount],
    store: Uuid,
    identity: &Identity,
) -> (Vec<ArtifactAccount>, Vec<Uuid>) {
    let record = ArtifactAccount {
        id: store,
        email: Some(identity.email.clone()),
        account_uuid: Some(identity.account_uuid.clone()),
        org: Some(identity.org.clone()),
    };
    let retired: Vec<Uuid> = accounts
        .iter()
        .filter(|a| a.account_uuid.as_deref() == Some(identity.account_uuid.as_str()) && a.id != store)
        .map(|a| a.id)
        .collect();
    let replaced = |id: &Uuid| retired.contains(id) || *id == store;
    let mut list: Vec<ArtifactAccount> = accounts.iter().filter(|a| !replaced(&a.id)).cloned().collect();
    let place = accounts
        .iter()
        .position(|a| retired.contains(&a.id))
        .or_else(|| accounts.iter().position(|a| a.id == store));
    match place {
        Some(place) => {
            let before = accounts[..place].iter().filter(|a| !replaced(&a.id)).count();
            list.insert(before, record);
        }
        None => list.push(record),
    }
    (list, retired)
}

/// What an invited person may do with an artifact, by the names claude.ai's
/// invitations use.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum ArtifactRole {
    Reader,
    Commenter,
    Editor,
}

impl ArtifactRole {
    pub const ALL: [ArtifactRole; 3] = [ArtifactRole::Reader, ArtifactRole::Commenter, ArtifactRole::Editor];

    pub fn raw(self) -> &'static str {
        match self {
            ArtifactRole::Reader => "reader",
            ArtifactRole::Commenter => "commenter",
            ArtifactRole::Editor => "editor",
        }
    }

    pub fn from_raw(text: &str) -> Option<ArtifactRole> {
        ArtifactRole::ALL.into_iter().find(|r| r.raw() == text)
    }

    pub fn label(self) -> &'static str {
        match self {
            ArtifactRole::Reader => "Can View",
            ArtifactRole::Commenter => "Can Comment",
            ArtifactRole::Editor => "Can Edit",
        }
    }

    /// The most an artifact will take of what was asked, never more: one that
    /// has no editors makes an editor a commenter if it can, a viewer
    /// otherwise.
    pub fn offered(self, permissions: &Permissions) -> ArtifactRole {
        match self {
            ArtifactRole::Editor if permissions.editors => ArtifactRole::Editor,
            ArtifactRole::Editor | ArtifactRole::Commenter => {
                if permissions.commenters {
                    ArtifactRole::Commenter
                } else {
                    ArtifactRole::Reader
                }
            }
            ArtifactRole::Reader => ArtifactRole::Reader,
        }
    }
}

/// People to share with in one go. claude.ai has no such thing for a personal
/// account, so the group is Graft's, and sharing with it invites each address.
///
/// Groups written before roles existed hold `emails` alone, and everyone in
/// them was invited as a viewer, which is what an address missing from `roles`
/// still means. So an old file reads as it always did, and a Graft from before
/// roles reads a new one as viewers only: less than was asked, never more.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArtifactGroup {
    pub id: Uuid,
    pub name: String,
    pub emails: Vec<String>,
    pub roles: BTreeMap<String, ArtifactRole>,
}

impl ArtifactGroup {
    pub fn new(id: Uuid, name: &str, emails: Vec<String>) -> ArtifactGroup {
        ArtifactGroup { id, name: name.to_string(), emails, roles: BTreeMap::new() }
    }

    pub fn role_of(&self, email: &str) -> ArtifactRole {
        self.roles.get(email).copied().unwrap_or(ArtifactRole::Reader)
    }

    pub fn set_role(&mut self, role: ArtifactRole, email: &str) {
        if role == ArtifactRole::Reader {
            self.roles.remove(email);
        } else {
            self.roles.insert(email.to_string(), role);
        }
    }

    /// Everyone given one role, the way a group of editors or of commenters is
    /// made.
    pub fn set_role_for_everyone(&mut self, role: ArtifactRole) {
        self.roles = if role == ArtifactRole::Reader {
            BTreeMap::new()
        } else {
            self.emails.iter().map(|e| (e.clone(), role)).collect()
        };
    }

    /// The one role everyone has, or `None` for a group that mixes them.
    pub fn common_role(&self) -> Option<ArtifactRole> {
        if self.emails.is_empty() {
            return Some(ArtifactRole::Reader);
        }
        let first = self.role_of(&self.emails[0]);
        self.emails.iter().all(|e| self.role_of(e) == first).then_some(first)
    }

    /// Pasted addresses join with the role everyone already has, so adding to
    /// a group of editors adds editors.
    pub fn add_addresses(&mut self, text: &str) {
        let role = self.common_role().unwrap_or(ArtifactRole::Reader);
        for email in parse_addresses(text) {
            if !self.emails.contains(&email) {
                self.emails.push(email.clone());
                self.set_role(role, &email);
            }
        }
    }

    pub fn remove(&mut self, email: &str) {
        self.emails.retain(|e| e != email);
        self.roles.remove(email);
    }

    pub fn role_summary(&self) -> &'static str {
        self.common_role().map(ArtifactRole::label).unwrap_or("Mixed")
    }
}

/// Written with its keys in order and `roles` left out when nobody in it is
/// more than a viewer, so a group of viewers is the same bytes a Graft from
/// before roles wrote.
impl Serialize for ArtifactGroup {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeStruct;
        let kept: BTreeMap<&String, &'static str> = self
            .roles
            .iter()
            .filter(|(email, role)| self.emails.contains(email) && **role != ArtifactRole::Reader)
            .map(|(email, role)| (email, role.raw()))
            .collect();
        let mut out = s.serialize_struct("ArtifactGroup", if kept.is_empty() { 3 } else { 4 })?;
        out.serialize_field("emails", &self.emails)?;
        out.serialize_field("id", &self.id.to_string().to_uppercase())?;
        out.serialize_field("name", &self.name)?;
        if !kept.is_empty() {
            out.serialize_field("roles", &kept)?;
        }
        out.end()
    }
}

impl<'de> Deserialize<'de> for ArtifactGroup {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        #[derive(Deserialize)]
        struct Raw {
            id: String,
            name: String,
            emails: Vec<String>,
            #[serde(default)]
            roles: Option<Value>,
        }
        let raw = Raw::deserialize(d)?;
        let id = Uuid::parse_str(&raw.id).map_err(serde::de::Error::custom)?;
        // One role this Graft does not know, from a newer one, costs that
        // address its role, not the whole group. A `roles` that is not a map
        // of names costs every role and still keeps the group.
        let mut roles = BTreeMap::new();
        if let Some(Value::Object(map)) = raw.roles {
            if map.values().all(Value::is_string) {
                for (email, role) in map {
                    if let Some(role) = role.as_str().and_then(ArtifactRole::from_raw) {
                        roles.insert(email, role);
                    }
                }
            }
        }
        Ok(ArtifactGroup { id, name: raw.name, emails: raw.emails, roles })
    }
}

/// Who can open an artifact, as the share panel on claude.ai says it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ArtifactAccess {
    OnlyYou,
    Invited,
    Organization,
    AnyoneWithLink,
}

impl ArtifactAccess {
    pub fn from_audience(audience: &str) -> ArtifactAccess {
        match audience {
            "public" | "link" => ArtifactAccess::AnyoneWithLink,
            "org" | "space" => ArtifactAccess::Organization,
            "users" => ArtifactAccess::Invited,
            _ => ArtifactAccess::OnlyYou,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            ArtifactAccess::OnlyYou => "Only you",
            ArtifactAccess::Invited => "Only people invited",
            ArtifactAccess::Organization => "Your organization",
            ArtifactAccess::AnyoneWithLink => "Anyone with the link",
        }
    }

    /// A symbolic icon from the desktop's own theme for each.
    pub fn icon(self) -> &'static str {
        match self {
            ArtifactAccess::OnlyYou => "changes-prevent-symbolic",
            ArtifactAccess::Invited => "system-users-symbolic",
            ArtifactAccess::Organization => "network-workgroup-symbolic",
            ArtifactAccess::AnyoneWithLink => "web-browser-symbolic",
        }
    }

    /// The entry the access menu checks. Graft offers only invited and public,
    /// so an artifact only its owner opens checks the invited entry, as
    /// claude.ai's own panel does, and one shared with the organization checks
    /// an entry of its own: read as invited, choosing the checked entry took
    /// the organization's access away without a word.
    pub fn menu_choice(self) -> ArtifactAccess {
        if self == ArtifactAccess::OnlyYou {
            ArtifactAccess::Invited
        } else {
            self
        }
    }

    /// Whether making an artifact public, or no longer public, changes who can
    /// open it. Asked for what it already is, nothing is sent.
    pub fn changes(self, to_public: bool) -> bool {
        match self {
            ArtifactAccess::AnyoneWithLink => !to_public,
            ArtifactAccess::OnlyYou | ArtifactAccess::Invited => to_public,
            ArtifactAccess::Organization => true,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Artifact {
    /// The artifact's UUID, which every request is keyed by. The page address
    /// uses a shorter id of its own, which is why `open_url` is kept as given.
    pub id: String,
    /// The signed-in account that listed it.
    pub account: Uuid,
    pub org: String,
    pub title: String,
    pub kind: String,
    pub open_url: Option<String>,
    pub audience: String,
    /// `mine`, or `shared` for one someone else owns.
    pub rel: String,
    pub sort_time: Option<SystemTime>,
    pub owner_account: Option<String>,
    /// What the list says can be done with it, so nothing is offered that
    /// claude.ai would refuse.
    pub renamable: bool,
    pub duplicable: bool,
    /// A copy claude.ai would make, but not today: it caps copies a day.
    pub duplicate_available: bool,
}

impl Artifact {
    pub fn is_own(&self) -> bool {
        self.rel == "mine"
    }

    pub fn access(&self) -> ArtifactAccess {
        ArtifactAccess::from_audience(&self.audience)
    }

    /// The first eight characters of its id, which name its controls.
    pub fn token(&self) -> String {
        self.id.chars().take(8).collect()
    }
}

/// Addresses as people paste them: one per line, or a list out of a mail
/// header with commas, semicolons and names in angle brackets. Each one once,
/// in the order given, and anything that is not an address dropped.
pub fn parse_addresses(text: &str) -> Vec<String> {
    let mut seen = std::collections::HashSet::new();
    let mut out = Vec::new();
    for piece in text.split(|c: char| c == ',' || c == ';' || c.is_whitespace()) {
        let trimmed = piece.trim_matches(|c: char| "<>\"'()".contains(c)).to_lowercase();
        if is_address(&trimmed) && seen.insert(trimmed.clone()) {
            out.push(trimmed);
        }
    }
    out
}

pub fn is_address(text: &str) -> bool {
    let parts: Vec<&str> = text.split('@').collect();
    if parts.len() != 2 || parts[0].is_empty() {
        return false;
    }
    let domain = parts[1];
    domain.contains('.') && !domain.starts_with('.') && !domain.ends_with('.')
}

/// The forms claude.ai's own export menu offers that need nothing but this
/// machine to make. Google Docs and Notion are left out: each is a connection
/// to another service, not a file.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ExportFormat {
    Markdown,
    Pdf,
    Word,
    Html,
}

impl ExportFormat {
    pub const ALL: [ExportFormat; 4] =
        [ExportFormat::Markdown, ExportFormat::Pdf, ExportFormat::Word, ExportFormat::Html];

    pub fn raw(self) -> &'static str {
        match self {
            ExportFormat::Markdown => "markdown",
            ExportFormat::Pdf => "pdf",
            ExportFormat::Word => "word",
            ExportFormat::Html => "html",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            ExportFormat::Markdown => "Markdown",
            ExportFormat::Pdf => "PDF",
            ExportFormat::Word => "Word Document",
            ExportFormat::Html => "Page Source",
        }
    }

    /// A page made of several files goes out as all of them, zipped, since its
    /// own page alone would open with pieces missing.
    pub fn extension(self, single_file: bool) -> &'static str {
        match self {
            ExportFormat::Markdown => "md",
            ExportFormat::Pdf => "pdf",
            ExportFormat::Word => "docx",
            ExportFormat::Html => {
                if single_file {
                    "html"
                } else {
                    "zip"
                }
            }
        }
    }

    pub fn mime_type(self, single_file: bool) -> &'static str {
        match (self, single_file) {
            (ExportFormat::Markdown, _) => "text/markdown",
            (ExportFormat::Pdf, _) => "application/pdf",
            (ExportFormat::Word, _) => {
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            }
            (ExportFormat::Html, true) => "text/html",
            (ExportFormat::Html, false) => "application/zip",
        }
    }

    /// The artifact's title as a file name: what a save dialog would refuse,
    /// or a file manager would read as a folder, made a dash.
    pub fn file_name(title: &str, format: ExportFormat, single_file: bool) -> String {
        let replaced = title.replace(['/', ':'], "-");
        let mut name: String = replaced
            .trim_matches(|c: char| c.is_whitespace() || c == '.')
            .to_string();
        if name.chars().count() > 120 {
            name = name.chars().take(120).collect::<String>().trim().to_string();
        }
        if name.is_empty() {
            name = "Artifact".into();
        }
        format!("{name}.{}", format.extension(single_file))
    }
}

/// UUIDs in the same upper-case form a Mac Graft writes, so these files read
/// the same on either.
pub(crate) mod upper_uuid {
    use serde::{Deserialize, Deserializer, Serializer};
    use uuid::Uuid;

    pub fn serialize<S: Serializer>(id: &Uuid, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&id.to_string().to_uppercase())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Uuid, D::Error> {
        let text = String::deserialize(d)?;
        Uuid::parse_str(&text).map_err(serde::de::Error::custom)
    }
}

/// The form a store's id takes in its folder name and in a control's name.
pub fn upper(id: Uuid) -> String {
    id.to_string().to_uppercase()
}

pub fn short(id: Uuid) -> String {
    upper(id).chars().take(8).collect()
}
