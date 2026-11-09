//! Every account's artifacts in one list, and the groups they are shared
//! with. The accounts and their stores live here and nowhere else: Projects
//! and Search Chats ask through `session` and keep no sign-in of their own.

use crate::api::{self, Identity};
use crate::export::{self, ExportFailure};
use crate::files;
use crate::model::{self, short, Artifact, ArtifactAccess, ArtifactAccount, ArtifactGroup, ExportFormat};
use crate::session::{self, Failure, WebSession};
use crate::signin::SignInWindow;
use graft_core::diagnostics;
use gtk::glib;
use gtk::prelude::*;
use serde_json::json;
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::rc::Rc;
use std::time::SystemTime;
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccountState {
    Unknown,
    Loading,
    Ready,
    SignedOut,
    Failed,
}

impl AccountState {
    pub fn help(self) -> &'static str {
        match self {
            AccountState::Ready => "Signed in",
            AccountState::SignedOut => "Signed out \u{b7} sign in again",
            AccountState::Failed => "claude.ai didn't answer",
            AccountState::Loading => "Asking claude.ai...",
            AccountState::Unknown => "Not asked yet",
        }
    }
}

/// What changed, so a page redraws only the part it shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Change {
    Accounts,
    States,
    Artifacts,
    Groups,
    Status,
    Busy,
    Names,
    /// The last sign-in to finish, whichever pane it was started from, so
    /// every pane can ask for that account at once rather than at its next
    /// refresh.
    SignedIn(Uuid),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SignIn {
    pub account: Uuid,
    pub at: SystemTime,
}

struct State {
    accounts: Vec<ArtifactAccount>,
    states: HashMap<Uuid, AccountState>,
    artifacts: Vec<Artifact>,
    groups: Vec<ArtifactGroup>,
    status: Option<String>,
    busy: HashSet<String>,
    /// Which Claude on this machine each account belongs to, by account UUID.
    claude_names: HashMap<String, String>,
    last_sign_in: Option<SignIn>,
    sessions: HashMap<Uuid, Rc<WebSession>>,
    lists: HashMap<Uuid, Vec<Artifact>>,
    /// Which load of each account is the latest, so one a sign-in has since
    /// superseded writes nothing over the one that followed it.
    generations: HashMap<Uuid, u64>,
    sign_in: Option<Rc<SignInWindow>>,
    may_write_accounts: bool,
    may_write_groups: bool,
}

type Listener = Rc<dyn Fn(Change)>;

pub struct ArtifactCenter {
    state: RefCell<State>,
    listeners: RefCell<Vec<(u64, Listener)>>,
    next_listener: Cell<u64>,
    /// Set for the layout checks, which draw sample artifacts and must never
    /// reach claude.ai.
    offline: bool,
}

impl ArtifactCenter {
    pub fn new() -> Rc<ArtifactCenter> {
        let loaded = files::load(SystemTime::now());
        let center = Rc::new(ArtifactCenter {
            state: RefCell::new(State {
                accounts: loaded.accounts,
                states: HashMap::new(),
                artifacts: Vec::new(),
                groups: loaded.groups,
                status: None,
                busy: HashSet::new(),
                claude_names: HashMap::new(),
                last_sign_in: None,
                sessions: HashMap::new(),
                lists: HashMap::new(),
                generations: HashMap::new(),
                sign_in: None,
                may_write_accounts: loaded.may_write_accounts,
                may_write_groups: loaded.may_write_groups,
            }),
            listeners: RefCell::new(Vec::new()),
            next_listener: Cell::new(1),
            offline: false,
        });
        // Only against a list that was read: one set aside, or never written,
        // may name the accounts these stores belong to.
        if loaded.accounts_were_read {
            let this = Rc::clone(&center);
            glib::MainContext::default().spawn_local(async move { this.remove_stray_stores().await });
        }
        center
    }

    /// For the layout checks, which must not reach claude.ai.
    pub fn offline(accounts: Vec<ArtifactAccount>, artifacts: Vec<Artifact>, groups: Vec<ArtifactGroup>) -> Rc<ArtifactCenter> {
        let states = accounts.iter().map(|a| (a.id, AccountState::Ready)).collect();
        Rc::new(ArtifactCenter {
            state: RefCell::new(State {
                accounts,
                states,
                artifacts,
                groups,
                status: None,
                busy: HashSet::new(),
                claude_names: HashMap::new(),
                last_sign_in: None,
                sessions: HashMap::new(),
                lists: HashMap::new(),
                generations: HashMap::new(),
                sign_in: None,
                may_write_accounts: false,
                may_write_groups: false,
            }),
            listeners: RefCell::new(Vec::new()),
            next_listener: Cell::new(1),
            offline: true,
        })
    }

    pub fn is_offline(&self) -> bool {
        self.offline
    }

    // MARK: - Watching

    pub fn subscribe(&self, f: impl Fn(Change) + 'static) -> u64 {
        let id = self.next_listener.get();
        self.next_listener.set(id + 1);
        self.listeners.borrow_mut().push((id, Rc::new(f)));
        id
    }

    pub fn unsubscribe(&self, id: u64) {
        self.listeners.borrow_mut().retain(|(i, _)| *i != id);
    }

    fn emit(&self, change: Change) {
        let listeners: Vec<Listener> = self.listeners.borrow().iter().map(|(_, f)| Rc::clone(f)).collect();
        for listener in listeners {
            listener(change);
        }
    }

    // MARK: - Reading

    pub fn accounts(&self) -> Vec<ArtifactAccount> {
        self.state.borrow().accounts.clone()
    }

    pub fn account(&self, id: Uuid) -> Option<ArtifactAccount> {
        self.state.borrow().accounts.iter().find(|a| a.id == id).cloned()
    }

    pub fn state_of(&self, id: Uuid) -> AccountState {
        self.state.borrow().states.get(&id).copied().unwrap_or(AccountState::Unknown)
    }

    pub fn artifacts(&self) -> Vec<Artifact> {
        self.state.borrow().artifacts.clone()
    }

    pub fn groups(&self) -> Vec<ArtifactGroup> {
        self.state.borrow().groups.clone()
    }

    pub fn status(&self) -> Option<String> {
        self.state.borrow().status.clone()
    }

    pub fn is_busy(&self, key: &str) -> bool {
        self.state.borrow().busy.contains(key)
    }

    pub fn last_sign_in(&self) -> Option<SignIn> {
        self.state.borrow().last_sign_in
    }

    pub fn any_loading(&self) -> bool {
        self.state.borrow().states.values().any(|s| *s == AccountState::Loading)
    }

    /// The name a person knows the account by here: the Claude it is signed in
    /// to on this machine when there is one, its address otherwise.
    pub fn name_of(&self, id: Uuid) -> String {
        let state = self.state.borrow();
        let Some(account) = state.accounts.iter().find(|a| a.id == id) else { return String::new() };
        if let Some(name) = account.account_uuid.as_ref().and_then(|u| state.claude_names.get(u)) {
            return name.clone();
        }
        account.email.clone().unwrap_or_else(|| "Signing in...".into())
    }

    /// What another pane learned about a sign-in, so every pane's chip says
    /// the same thing about it.
    pub fn note_sign_in(&self, id: Uuid, accepted: bool) {
        {
            let mut state = self.state.borrow_mut();
            if !state.accounts.iter().any(|a| a.id == id) || state.states.get(&id) == Some(&AccountState::Loading) {
                return;
            }
            let next = if accepted { AccountState::Ready } else { AccountState::SignedOut };
            if state.states.get(&id) == Some(&next) {
                return;
            }
            state.states.insert(id, next);
        }
        self.emit(Change::States);
    }

    pub fn clear_status(&self) {
        self.set_status(None);
    }

    fn set_status(&self, status: Option<String>) {
        self.state.borrow_mut().status = status;
        self.emit(Change::Status);
    }

    fn set_busy(&self, key: &str, on: bool) {
        {
            let mut state = self.state.borrow_mut();
            if on {
                state.busy.insert(key.to_string());
            } else {
                state.busy.remove(key);
            }
        }
        self.emit(Change::Busy);
    }

    /// Takes the key, or says another run of the same thing is under way.
    fn begin(&self, key: &str) -> bool {
        if self.is_busy(key) {
            return false;
        }
        self.set_busy(key, true);
        true
    }

    fn save_accounts(&self) {
        let state = self.state.borrow();
        if !state.may_write_accounts {
            return;
        }
        files::save(&state.accounts, &files::accounts_file());
    }

    // MARK: - Stores

    /// Stores no listed account is under: a removal that would not go, a
    /// sign-in window closed before it finished, a Graft that quit before
    /// either. Asked once, before this launch has made a page on any of them.
    async fn remove_stray_stores(self: Rc<Self>) {
        let listed: Vec<Uuid> = self.state.borrow().accounts.iter().map(|a| a.id).collect();
        let stray = {
            let state = self.state.borrow();
            // A sign-in started since launch is writing into its store.
            files::stray_stores(&listed, |id| {
                state.sessions.contains_key(&id) || state.sign_in.as_ref().is_some_and(|w| w.account == id)
            })
        };
        for id in stray {
            diagnostics::note("artifacts.store stray", &[("store", json!(short(id)))]);
            session::forget(id).await;
        }
    }

    /// Shared with the Projects and Search panes, which ask from the same
    /// signed-in page rather than loading a second one per account.
    ///
    /// An account no longer listed gets one that answers signed out and loads
    /// nothing: a load or search still running after a sign-out would
    /// otherwise build a page on the store being removed, and keep it.
    pub fn session(&self, id: Uuid) -> Rc<WebSession> {
        if let Some(session) = self.state.borrow().sessions.get(&id) {
            return Rc::clone(session);
        }
        let session = WebSession::new(id);
        if self.account(id).is_none() {
            session.close();
            return session;
        }
        self.state.borrow_mut().sessions.insert(id, Rc::clone(&session));
        session
    }

    /// Lets go of everything made on an account's store, then removes the
    /// store. A store whose pages are still alive keeps its files busy, and a
    /// removal made then used to leave a kept store holding a live claude.ai
    /// sign-in that nothing listed and nothing would ever remove.
    fn retire(&self, id: Uuid) {
        let session = {
            let mut state = self.state.borrow_mut();
            state.lists.remove(&id);
            state.states.remove(&id);
            state.generations.remove(&id);
            state.sessions.remove(&id)
        };
        if let Some(session) = &session {
            session.close();
        }
        glib::MainContext::default().spawn_local(async move {
            if let Some(session) = session {
                session.released().await;
            }
            session::forget(id).await;
        });
    }

    // MARK: - Signing in

    pub fn sign_in_new_account(self: &Rc<Self>, parent: Option<&gtk::Window>) {
        self.sign_in(ArtifactAccount::new(Uuid::new_v4()), parent);
    }

    pub fn sign_in_again(self: &Rc<Self>, id: Uuid, parent: Option<&gtk::Window>) {
        if let Some(account) = self.account(id) {
            self.sign_in(account, parent);
        }
    }

    fn sign_in(self: &Rc<Self>, account: ArtifactAccount, parent: Option<&gtk::Window>) {
        if self.offline {
            return;
        }
        let earlier = self.state.borrow_mut().sign_in.take();
        if let Some(window) = earlier {
            window.close();
        }
        let weak = Rc::downgrade(self);
        let id = account.id;
        let window = SignInWindow::new(id, move |identity| {
            if let Some(this) = weak.upgrade() {
                this.signed_in(id, identity);
            }
        });
        self.state.borrow_mut().sign_in = Some(Rc::clone(&window));
        window.show(parent);
    }

    fn signed_in(self: &Rc<Self>, id: Uuid, identity: Identity) {
        let (retired, was_another) = {
            let mut state = self.state.borrow_mut();
            state.sign_in = None;
            let was_another = state
                .accounts
                .iter()
                .find(|a| a.id == id)
                .is_some_and(|a| a.account_uuid.as_deref() != Some(identity.account_uuid.as_str()));
            let (accounts, retired) = model::signed_in(&state.accounts, id, &identity);
            state.accounts = accounts;
            (retired, was_another)
        };
        self.save_accounts();
        for earlier in &retired {
            self.retire(*earlier);
        }
        if was_another {
            self.state.borrow_mut().lists.remove(&id);
        }
        if !retired.is_empty() {
            self.state.borrow_mut().status = Some(format!("Signed in to {} again", identity.email));
            self.emit(Change::Status);
        }
        let session = self.state.borrow().sessions.get(&id).cloned();
        if let Some(session) = session {
            session.reset();
        }
        self.emit(Change::Accounts);
        let this = Rc::clone(self);
        glib::MainContext::default().spawn_local(async move { this.load(id, true).await });
        self.state.borrow_mut().last_sign_in = Some(SignIn { account: id, at: SystemTime::now() });
        self.emit(Change::SignedIn(id));
    }

    pub fn sign_out(&self, id: Uuid) {
        let window = {
            let mut state = self.state.borrow_mut();
            if state.sign_in.as_ref().is_some_and(|w| w.account == id) {
                state.sign_in.take()
            } else {
                None
            }
        };
        if let Some(window) = window {
            window.close();
        }
        self.state.borrow_mut().accounts.retain(|a| a.id != id);
        self.save_accounts();
        self.retire(id);
        self.remerge();
        self.emit(Change::Accounts);
        self.emit(Change::States);
    }

    // MARK: - Listing

    /// From the page being shown and the refresh button, never a timer: each
    /// pass is several requests per account to a service that did not invite
    /// them.
    pub fn refresh(self: &Rc<Self>) {
        if self.offline {
            return;
        }
        for account in self.accounts() {
            let this = Rc::clone(self);
            glib::MainContext::default().spawn_local(async move { this.load(account.id, false).await });
        }
        self.refresh_names();
    }

    pub fn refresh_names(self: &Rc<Self>) {
        let weak = Rc::downgrade(self);
        glib::MainContext::default().spawn_local(async move {
            let Ok(names) = gtk::gio::spawn_blocking(claude_names_on_this_machine).await else { return };
            if let Some(this) = weak.upgrade() {
                this.state.borrow_mut().claude_names = names;
                this.emit(Change::Names);
            }
        });
    }

    /// The organization an account's chats, projects and artifacts live
    /// under, asked of claude.ai the first time and kept after that.
    pub async fn organization_of(self: &Rc<Self>, id: Uuid) -> Result<String, Failure> {
        let mut account = self.account(id).unwrap_or_else(|| ArtifactAccount::new(id));
        if account.org.is_none() || account.email.is_none() {
            let identity = self.session(id).identity().await?;
            account.email = Some(identity.email);
            account.account_uuid = Some(identity.account_uuid);
            account.org = Some(identity.org);
            let changed = {
                let mut state = self.state.borrow_mut();
                match state.accounts.iter_mut().find(|a| a.id == id) {
                    Some(slot) => {
                        *slot = account.clone();
                        true
                    }
                    None => false,
                }
            };
            if changed {
                self.save_accounts();
                self.emit(Change::Accounts);
            }
        }
        account.org.ok_or(Failure::Unreadable)
    }

    fn remerge(&self) {
        {
            let mut state = self.state.borrow_mut();
            let lists: Vec<Vec<Artifact>> =
                state.accounts.iter().filter_map(|a| state.lists.get(&a.id).cloned()).collect();
            state.artifacts = api::merge(&lists);
        }
        self.emit(Change::Artifacts);
    }

    /// `again` is for a sign-in just made: a load already under way was asked
    /// of the page before it, and is left to finish without writing.
    async fn load(self: &Rc<Self>, id: Uuid, again: bool) {
        let generation = {
            let mut state = self.state.borrow_mut();
            if !state.accounts.iter().any(|a| a.id == id)
                || (!again && state.states.get(&id) == Some(&AccountState::Loading))
            {
                return;
            }
            let generation = state.generations.get(&id).copied().unwrap_or(0) + 1;
            state.generations.insert(id, generation);
            state.states.insert(id, AccountState::Loading);
            generation
        };
        self.emit(Change::States);
        let outcome = self.list_all(id).await;
        // A newer load owns what is shown now, and an account signed out while
        // this one ran owns nothing: written back, its list came back with it
        // at the next sign-out.
        {
            let mut state = self.state.borrow_mut();
            if state.generations.get(&id) != Some(&generation) || !state.accounts.iter().any(|a| a.id == id) {
                return;
            }
            match outcome {
                Ok(found) => {
                    state.lists.insert(id, found);
                    state.states.insert(id, AccountState::Ready);
                }
                Err(Failure::SignedOut) => {
                    state.lists.remove(&id);
                    state.states.insert(id, AccountState::SignedOut);
                }
                Err(error) => {
                    state.states.insert(id, AccountState::Failed);
                    diagnostics::note("artifacts.list failed", &[("reason", json!(format!("{error:?}")))]);
                }
            }
        }
        self.emit(Change::States);
        self.remerge();
    }

    async fn list_all(self: &Rc<Self>, id: Uuid) -> Result<Vec<Artifact>, Failure> {
        let org = self.organization_of(id).await?;
        let session = self.session(id);
        let mut found = Vec::new();
        for rel in ["mine", "shared"] {
            let mut token: Option<String> = None;
            for _ in 0..api::MAXIMUM_PAGES {
                let answer = session.send("GET", &api::list_path(rel, &org, token.as_deref()), None, &[]).await?;
                if answer.status != 200 {
                    return Err(Failure::Unreadable);
                }
                let page = api::page(&answer.data, id, &org).ok_or(Failure::Unreadable)?;
                found.extend(page.artifacts);
                token = page.next;
                if token.is_none() {
                    break;
                }
            }
        }
        Ok(found)
    }

    fn replace(&self, id: &str, change: impl Fn(&mut Artifact)) {
        {
            let mut state = self.state.borrow_mut();
            for artifact in state.artifacts.iter_mut().filter(|a| a.id == id) {
                change(artifact);
            }
            for list in state.lists.values_mut() {
                for artifact in list.iter_mut().filter(|a| a.id == id) {
                    change(artifact);
                }
            }
        }
        self.emit(Change::Artifacts);
    }

    fn message(&self, error: Failure, account: Uuid) -> String {
        if error == Failure::SignedOut {
            self.state.borrow_mut().states.insert(account, AccountState::SignedOut);
            self.emit(Change::States);
            return format!("Sign in to {} again", self.name_of(account));
        }
        "claude.ai didn't answer".into()
    }

    fn refused(answer: &session::Answer) -> String {
        match api::reason(&answer.data).as_deref() {
            Some("title_invalid") => "claude.ai won't take that title".into(),
            Some("org_mismatch") => "claude.ai won't copy it into this account".into(),
            Some(reason) if reason.contains("limit") || reason.contains("cap") => {
                "claude.ai won't make more copies today".into()
            }
            _ => format!("claude.ai refused the change ({})", answer.status),
        }
    }

    // MARK: - Sharing

    pub fn set_public(self: &Rc<Self>, artifact: &Artifact, public: bool) {
        let key = format!("access-{}", artifact.id);
        if !artifact.access().changes(public) || !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        glib::MainContext::default().spawn_local(async move {
            let status = this.set_public_now(&artifact, public).await;
            if let Some(status) = status {
                this.set_status(Some(status));
            }
            this.set_busy(&key, false);
        });
    }

    async fn set_public_now(self: &Rc<Self>, artifact: &Artifact, public: bool) -> Option<String> {
        let session = self.session(artifact.account);
        let path = api::perm_path(&artifact.id, &artifact.org);
        let permissions = match session.send("GET", &path, None, &[]).await {
            Ok(answer) if answer.status == 200 => match api::permissions(&answer.data) {
                Some(p) => p,
                None => return Some(self.message(Failure::Unreadable, artifact.account)),
            },
            Ok(_) => return Some(self.message(Failure::Unreadable, artifact.account)),
            Err(e) => return Some(self.message(e, artifact.account)),
        };
        // Changed elsewhere since the list was read: what it is now is shown,
        // and nothing is sent to make it what it already is.
        if let Some(mode) = &permissions.mode {
            if !ArtifactAccess::from_audience(mode).changes(public) {
                let mode = mode.clone();
                self.replace(&artifact.id, |a| a.audience = mode.clone());
                return None;
            }
        }
        let headers: Vec<(String, String)> =
            permissions.version.iter().map(|v| ("If-Match".to_string(), v.clone())).collect();
        let body = api::read_mode_body(public, &permissions.invited);
        match session.send("PATCH", &path, Some(&body), &headers).await {
            Ok(answer) if answer.ok() => {
                let audience = if public {
                    "public"
                } else if permissions.invited.is_empty() {
                    "owner"
                } else {
                    "users"
                };
                self.replace(&artifact.id, |a| a.audience = audience.into());
                Some(if public {
                    format!("Anyone with the link can view \"{}\"", artifact.title)
                } else {
                    format!("\"{}\" is private again", artifact.title)
                })
            }
            Ok(answer) => Some(format!("claude.ai refused the change ({})", answer.status)),
            Err(e) => Some(self.message(e, artifact.account)),
        }
    }

    /// One invitation per address, each as claude.ai sends it from its own
    /// panel, which also emails the person. An address that fails does not
    /// stop the rest.
    ///
    /// Each person gets the role the group gives them where this artifact
    /// takes it. Where it does not, they get the most it does take, never
    /// more, and the result says how many were given less.
    pub fn share(self: &Rc<Self>, artifact: &Artifact, group: &ArtifactGroup) {
        let key = format!("share-{}", artifact.id);
        if group.emails.is_empty() || !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        let group = group.clone();
        glib::MainContext::default().spawn_local(async move {
            let status = this.share_now(&artifact, &group).await;
            this.set_status(Some(status));
            this.set_busy(&key, false);
        });
    }

    async fn share_now(self: &Rc<Self>, artifact: &Artifact, group: &ArtifactGroup) -> String {
        let session = self.session(artifact.account);
        let permissions = match session.send("GET", &api::perm_path(&artifact.id, &artifact.org), None, &[]).await {
            Ok(answer) if answer.status == 200 => match api::permissions(&answer.data) {
                Some(p) => p,
                None => return self.message(Failure::Unreadable, artifact.account),
            },
            Ok(_) => return self.message(Failure::Unreadable, artifact.account),
            Err(e) => return self.message(e, artifact.account),
        };
        let mut invited = 0;
        let mut lowered = 0;
        let mut refusal: Option<String> = None;
        for email in &group.emails {
            let wanted = group.role_of(email);
            let role = wanted.offered(&permissions);
            let body = api::invite_body(email, role);
            match session.send("POST", &api::invite_path(&artifact.id, &artifact.org), Some(&body), &[]).await {
                Ok(answer) if answer.ok() => {
                    invited += 1;
                    if role != wanted {
                        lowered += 1;
                    }
                }
                Ok(answer) => {
                    refusal.get_or_insert(answer.status.to_string());
                }
                // Every address after this one would be turned away the same
                // way.
                Err(Failure::SignedOut) => return self.message(Failure::SignedOut, artifact.account),
                Err(_) => {
                    refusal.get_or_insert("unanswered".into());
                }
            }
        }
        if invited > 0 && artifact.access() == ArtifactAccess::OnlyYou {
            self.replace(&artifact.id, |a| a.audience = "users".into());
        }
        if let Some(refusal) = refusal {
            diagnostics::note("artifacts.invite refused", &[("status", json!(refusal))]);
        }
        let total = group.emails.len();
        if lowered == 0 {
            format!("Shared \"{}\" with {invited} of {total} in {}", artifact.title, group.name)
        } else {
            format!("Shared \"{}\" with {invited} of {total} in {}, {lowered} with less than asked", artifact.title, group.name)
        }
    }

    // MARK: - Changing an artifact

    pub fn rename(self: &Rc<Self>, artifact: &Artifact, title: &str) {
        let title = title.trim().to_string();
        let key = format!("rename-{}", artifact.id);
        if !artifact.is_own() || title.is_empty() || title == artifact.title || !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        glib::MainContext::default().spawn_local(async move {
            let body = api::retitle_body(&title);
            let path = api::retitle_path(&artifact.id, &artifact.org);
            let status = match this.session(artifact.account).send("POST", &path, Some(&body), &[]).await {
                Ok(answer) if answer.ok() => {
                    let renamed = title.clone();
                    this.replace(&artifact.id, |a| a.title = renamed.clone());
                    format!("Renamed \"{title}\"")
                }
                Ok(answer) => Self::refused(&answer),
                Err(e) => this.message(e, artifact.account),
            };
            this.set_status(Some(status));
            this.set_busy(&key, false);
        });
    }

    /// The copy belongs to the account that made it, which is why only an
    /// artifact that account can see is offered, and the list is asked again
    /// for it rather than guessed.
    pub fn duplicate(self: &Rc<Self>, artifact: &Artifact) {
        let key = format!("duplicate-{}", artifact.id);
        if !artifact.duplicable || !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        glib::MainContext::default().spawn_local(async move {
            let path = api::duplicate_path(&artifact.id, &artifact.org);
            match this.session(artifact.account).send("POST", &path, None, &[]).await {
                Ok(answer) if answer.ok() => {
                    this.set_status(Some(format!("Made a copy of \"{}\"", artifact.title)));
                    this.load(artifact.account, false).await;
                }
                Ok(answer) => this.set_status(Some(Self::refused(&answer))),
                Err(e) => {
                    let message = this.message(e, artifact.account);
                    this.set_status(Some(message));
                }
            }
            this.set_busy(&key, false);
        });
    }

    /// claude.ai keeps no bin for artifacts, so this is for good; the page
    /// asks first and says so.
    pub fn delete(self: &Rc<Self>, artifact: &Artifact) {
        let key = format!("delete-{}", artifact.id);
        if !artifact.is_own() || !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        glib::MainContext::default().spawn_local(async move {
            let path = api::artifact_path(&artifact.id, &artifact.org);
            let status = match this.session(artifact.account).send("DELETE", &path, None, &[]).await {
                // Already gone is what was wanted.
                Ok(answer) if answer.ok() || (answer.status == 404 && api::reason(&answer.data).as_deref() == Some("not_found")) => {
                    {
                        let mut state = this.state.borrow_mut();
                        for list in state.lists.values_mut() {
                            list.retain(|a| a.id != artifact.id);
                        }
                        state.artifacts.retain(|a| a.id != artifact.id);
                    }
                    this.emit(Change::Artifacts);
                    format!("Deleted \"{}\"", artifact.title)
                }
                Ok(answer) => Self::refused(&answer),
                Err(e) => this.message(e, artifact.account),
            };
            this.set_status(Some(status));
            this.set_busy(&key, false);
        });
    }

    // MARK: - Exporting

    /// Reads the artifact's files as its viewer does, asks where to put the
    /// export, and makes it on this machine. Nothing of the export goes
    /// anywhere but the file chosen.
    pub fn export(self: &Rc<Self>, artifact: &Artifact, format: ExportFormat, parent: Option<gtk::Window>) {
        let key = format!("export-{}", artifact.id);
        if !self.begin(&key) {
            return;
        }
        let this = Rc::clone(self);
        let artifact = artifact.clone();
        glib::MainContext::default().spawn_local(async move {
            let folder = std::env::temp_dir().join(format!("graft-export-{}", Uuid::new_v4()));
            let outcome = this.export_now(&artifact, format, parent.as_ref(), &folder).await;
            let _ = std::fs::remove_dir_all(&folder);
            match outcome {
                Ok(Some(name)) => this.set_status(Some(format!("Exported \"{}\" to {name}", artifact.title))),
                Ok(None) => {}
                Err(Exported::Failed(failure)) => {
                    diagnostics::note(
                        "artifacts.export failed",
                        &[("reason", json!(format!("{failure:?}"))), ("format", json!(format.raw()))],
                    );
                    this.set_status(Some(failure.message()));
                }
                Err(Exported::Session(error)) => {
                    let message = this.message(error, artifact.account);
                    this.set_status(Some(message));
                }
            }
            this.set_busy(&key, false);
        });
    }

    async fn export_now(
        self: &Rc<Self>,
        artifact: &Artifact,
        format: ExportFormat,
        parent: Option<&gtk::Window>,
        folder: &Path,
    ) -> Result<Option<String>, Exported> {
        let files = self.fetch_files(artifact, folder).await?;
        let single = files.len() == 1;
        let Some(destination) = export::ask_where(&artifact.title, format, single, parent).await else {
            return Ok(None);
        };
        export::write(folder, format, single, &destination).await.map_err(Exported::Failed)?;
        Ok(Some(destination.basename().map(|b| b.to_string_lossy().into_owned()).unwrap_or_default()))
    }

    async fn fetch_files(self: &Rc<Self>, artifact: &Artifact, folder: &Path) -> Result<Vec<String>, Exported> {
        let session = self.session(artifact.account);
        let answer = session.send("GET", &api::artifact_path(&artifact.id, &artifact.org), None, &[]).await?;
        if answer.status != 200 {
            return Err(Exported::Failed(if answer.status == 404 {
                ExportFailure::Gone
            } else {
                ExportFailure::Refused(answer.status)
            }));
        }
        let source = api::source(&answer.data).ok_or(Exported::Session(Failure::Unreadable))?;
        let mut total = 0usize;
        let mut written = Vec::new();
        for path in &source.files {
            let Some(url) = api::file_url(&artifact.id, &source, path) else { continue };
            let file = match session.download(&url, api::MAXIMUM_EXPORT_BYTES.saturating_sub(total)).await {
                Ok(file) => file,
                Err(Failure::TooLarge) => return Err(Exported::Failed(ExportFailure::TooLarge)),
                Err(e) => return Err(Exported::Session(e)),
            };
            if file.status != 200 {
                // Only the page itself is needed; a file it lists that will
                // not come is left out, as a broken link in the page would be.
                if path == "index.html" {
                    return Err(Exported::Failed(ExportFailure::Refused(file.status)));
                }
                continue;
            }
            total += file.data.len();
            if total > api::MAXIMUM_EXPORT_BYTES {
                return Err(Exported::Failed(ExportFailure::TooLarge));
            }
            let target = folder.join(path);
            if let Some(dir) = target.parent() {
                std::fs::create_dir_all(dir).map_err(|_| Exported::Failed(ExportFailure::Unwritable))?;
            }
            std::fs::write(&target, &file.data).map_err(|_| Exported::Failed(ExportFailure::Unwritable))?;
            written.push(path.clone());
        }
        Ok(written)
    }

    /// The link is claude.ai's own, so it opens for whoever claude.ai lets
    /// open it; the status says which.
    pub fn copy_link(&self, artifact: &Artifact) {
        let Some(url) = &artifact.open_url else { return };
        if let Some(display) = gtk::gdk::Display::default() {
            display.clipboard().set_text(url);
        }
        let access = artifact.access();
        self.set_status(Some(if access == ArtifactAccess::OnlyYou || access == ArtifactAccess::Invited {
            "Link copied \u{b7} only people with access can open it".into()
        } else {
            "Link copied".into()
        }));
    }

    // MARK: - Groups

    pub fn add_group(&self, name: &str) -> Uuid {
        let id = Uuid::new_v4();
        self.change_groups(|groups| groups.push(ArtifactGroup::new(id, name, Vec::new())));
        id
    }

    pub fn delete_group(&self, id: Uuid) {
        self.change_groups(|groups| groups.retain(|g| g.id != id));
    }

    /// Changes one group and saves the list when that changed anything.
    pub fn update_group(&self, id: Uuid, change: impl FnOnce(&mut ArtifactGroup)) {
        self.change_groups(|groups| {
            if let Some(group) = groups.iter_mut().find(|g| g.id == id) {
                change(group);
            }
        });
    }

    fn change_groups(&self, change: impl FnOnce(&mut Vec<ArtifactGroup>)) {
        let changed = {
            let mut state = self.state.borrow_mut();
            let before = state.groups.clone();
            change(&mut state.groups);
            let changed = state.groups != before;
            if changed && state.may_write_groups {
                files::save(&state.groups, &files::groups_file());
            }
            changed
        };
        if changed {
            self.emit(Change::Groups);
        }
    }
}

enum Exported {
    Failed(ExportFailure),
    Session(Failure),
}

impl From<Failure> for Exported {
    fn from(error: Failure) -> Exported {
        if error == Failure::TooLarge {
            Exported::Failed(ExportFailure::TooLarge)
        } else {
            Exported::Session(error)
        }
    }
}

/// Each Claude's account, read out of its profile, so an account signed in
/// here is called by the Claude a person knows it as. Run off the main thread.
pub fn claude_names_on_this_machine() -> HashMap<String, String> {
    let mut names = HashMap::new();
    let mut profiles = vec![(graft_core::paths::main_profile(), "Claude".to_string())];
    profiles.extend(graft_core::config::load_shortcuts().into_iter().map(|s| (s.profile_dir(), s.name)));
    for (profile, name) in profiles {
        if let Some(uuid) = graft_core::files::account_of(&profile) {
            names.entry(uuid).or_insert(name);
        }
    }
    names
}
