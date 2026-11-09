//! Every signed-in account's projects in one list, and one search across all
//! of their chats.
//!
//! The accounts are the Artifacts page's: the same claude.ai sign-in, in the
//! same web store, asked from the same page. Nothing here signs in or keeps a
//! sign-in of its own, so an account added or signed out in any one of the
//! three pages is added or gone in all of them at once.

use crate::center::{ArtifactCenter, Change};
use crate::local_sessions::{self, LocalRead, Transcripts};
use crate::projects::{self, ClaudeProject, LocalCodeSession, RemoteCodeSession, SearchFilters, SearchHit, SessionList};
use crate::session::{Failure, WebSession};
use futures_channel::oneshot;
use graft_core::diagnostics;
use gtk::glib;
use serde_json::json;
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::sync::Arc;
use std::time::{Duration, SystemTime};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    Loading,
    Ready,
    SignedOut,
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProjectChange {
    Projects,
    ProjectStates,
    Results,
    SearchStates,
    Filters,
}

/// Each account's whole Code session list is kept for the searches after the
/// one that read it: three pages of two hundred is a lot to ask again for
/// every search in a row. This Mac's transcripts are kept as long, since a
/// cold read of three hundred of them took three seconds, too long to stand
/// in front of every search.
pub const CODE_LIST_LIFETIME: Duration = Duration::from_secs(120);

#[derive(Clone)]
struct CodeList {
    read: SystemTime,
    sessions: Vec<RemoteCodeSession>,
    complete: bool,
}

struct Inner {
    projects: Vec<ClaudeProject>,
    project_states: HashMap<Uuid, State>,
    /// Everything the last search found, before the filters.
    results: Vec<SearchHit>,
    search_states: HashMap<Uuid, State>,
    /// The query the results answer, which is not always what is in the box.
    searched: Option<String>,
    filters: SearchFilters,
    project_lists: HashMap<Uuid, Vec<ClaudeProject>>,
    search_generation: u64,
    code_lists: HashMap<Uuid, CodeList>,
    local_read: Option<LocalRead>,
    /// The read under way, which every account whose list came before it
    /// waits for rather than starting one of its own.
    local_reading: Option<Vec<oneshot::Sender<LocalRead>>>,
    /// Whether the Projects page has asked yet. A sign-in made before it was
    /// ever shown waits for it, like every other account.
    asked_for_projects: bool,
}

type Listener = Rc<dyn Fn(ProjectChange)>;

pub struct ProjectCenter {
    pub accounts: Rc<ArtifactCenter>,
    transcripts: Arc<dyn Transcripts>,
    inner: RefCell<Inner>,
    listeners: RefCell<Vec<(u64, Listener)>>,
    next_listener: Cell<u64>,
    offline: bool,
}

impl ProjectCenter {
    /// `transcripts` is the chat mirror's read-only view of this machine's
    /// Code transcripts, which places each Code session a search finds.
    pub fn new(accounts: Rc<ArtifactCenter>, transcripts: Arc<dyn Transcripts>) -> Rc<ProjectCenter> {
        let offline = accounts.is_offline();
        let center = Rc::new(ProjectCenter {
            accounts: Rc::clone(&accounts),
            transcripts,
            inner: RefCell::new(Inner {
                projects: Vec::new(),
                project_states: HashMap::new(),
                results: Vec::new(),
                search_states: HashMap::new(),
                searched: None,
                filters: SearchFilters::default(),
                project_lists: HashMap::new(),
                search_generation: 0,
                code_lists: HashMap::new(),
                local_read: None,
                local_reading: None,
                asked_for_projects: false,
            }),
            listeners: RefCell::new(Vec::new()),
            next_listener: Cell::new(1),
            offline,
        });
        let weak = Rc::downgrade(&center);
        accounts.subscribe(move |change| {
            let Some(this) = weak.upgrade() else { return };
            match change {
                Change::Accounts => this.accounts_changed(),
                Change::SignedIn(id) => this.signed_in(id),
                _ => {}
            }
        });
        center
    }

    /// For the layout checks, which draw sample rows and must never reach
    /// claude.ai.
    pub fn offline(
        accounts: Rc<ArtifactCenter>,
        projects: Vec<ClaudeProject>,
        results: Vec<SearchHit>,
        searched: Option<String>,
    ) -> Rc<ProjectCenter> {
        let center = ProjectCenter::new(accounts, Arc::new(local_sessions::NoTranscripts));
        {
            let mut inner = center.inner.borrow_mut();
            inner.projects = projects;
            inner.results = results;
            inner.searched = searched;
            for account in center.accounts.accounts() {
                inner.project_states.insert(account.id, State::Ready);
                inner.search_states.insert(account.id, State::Ready);
            }
        }
        center
    }

    pub fn subscribe(&self, f: impl Fn(ProjectChange) + 'static) -> u64 {
        let id = self.next_listener.get();
        self.next_listener.set(id + 1);
        self.listeners.borrow_mut().push((id, Rc::new(f)));
        id
    }

    pub fn unsubscribe(&self, id: u64) {
        self.listeners.borrow_mut().retain(|(i, _)| *i != id);
    }

    fn emit(&self, change: ProjectChange) {
        let listeners: Vec<Listener> = self.listeners.borrow().iter().map(|(_, f)| Rc::clone(f)).collect();
        for listener in listeners {
            listener(change);
        }
    }

    // MARK: - Reading

    pub fn projects(&self) -> Vec<ClaudeProject> {
        self.inner.borrow().projects.clone()
    }

    pub fn results(&self) -> Vec<SearchHit> {
        self.inner.borrow().results.clone()
    }

    pub fn searched(&self) -> Option<String> {
        self.inner.borrow().searched.clone()
    }

    pub fn filters(&self) -> SearchFilters {
        self.inner.borrow().filters.clone()
    }

    /// Narrows what came back. Nothing is asked of claude.ai.
    pub fn set_filters(&self, filters: SearchFilters) {
        if self.inner.borrow().filters == filters {
            return;
        }
        self.inner.borrow_mut().filters = filters;
        self.emit(ProjectChange::Filters);
    }

    pub fn loading_projects(&self) -> bool {
        self.inner.borrow().project_states.values().any(|s| *s == State::Loading)
    }

    pub fn searching(&self) -> bool {
        self.inner.borrow().search_states.values().any(|s| *s == State::Loading)
    }

    /// What the filters let through, in the order found.
    pub fn shown(&self) -> Vec<SearchHit> {
        let inner = self.inner.borrow();
        let now = SystemTime::now();
        inner.results.iter().filter(|h| inner.filters.admits(h, now)).cloned().collect()
    }

    /// The accounts claude.ai would not answer for, by name, for the one line
    /// under a list.
    pub fn trouble(&self, projects: bool) -> Vec<String> {
        let states = {
            let inner = self.inner.borrow();
            if projects { inner.project_states.clone() } else { inner.search_states.clone() }
        };
        self.accounts
            .accounts()
            .iter()
            .filter_map(|account| match states.get(&account.id) {
                Some(State::SignedOut) => Some(format!("Sign in to {} again", self.accounts.name_of(account.id))),
                Some(State::Failed) => Some(format!("{} didn't answer", self.accounts.name_of(account.id))),
                _ => None,
            })
            .collect()
    }

    pub fn project_name(&self, id: Option<&str>, account: Uuid) -> Option<String> {
        let id = id?;
        self.inner.borrow().projects.iter().find(|p| p.id == id && p.account == account).map(|p| p.name.clone())
    }

    fn listed(&self, id: Uuid) -> bool {
        self.accounts.account(id).is_some()
    }

    // MARK: - Accounts

    /// An account signed out anywhere leaves every list here with it.
    fn accounts_changed(&self) {
        let ids: HashSet<Uuid> = self.accounts.accounts().iter().map(|a| a.id).collect();
        {
            let mut inner = self.inner.borrow_mut();
            inner.project_lists.retain(|k, _| ids.contains(k));
            inner.project_states.retain(|k, _| ids.contains(k));
            inner.search_states.retain(|k, _| ids.contains(k));
            inner.code_lists.retain(|k, _| ids.contains(k));
            inner.projects.retain(|p| ids.contains(&p.account));
            inner.results.retain(|h| ids.contains(&h.account));
            if inner.filters.account.is_some_and(|a| !ids.contains(&a)) {
                inner.filters.account = None;
            }
        }
        self.emit(ProjectChange::Projects);
        self.emit(ProjectChange::ProjectStates);
        self.emit(ProjectChange::Results);
        self.emit(ProjectChange::SearchStates);
        self.emit(ProjectChange::Filters);
    }

    /// An account signed in from any page is asked for here as well, once,
    /// because a person just asked for it; a search on screen is run again so
    /// it covers the new account too.
    fn signed_in(self: &Rc<Self>, id: Uuid) {
        if self.offline {
            return;
        }
        let (asked, searched) = {
            let mut inner = self.inner.borrow_mut();
            inner.code_lists.remove(&id);
            inner.project_states.remove(&id);
            (inner.asked_for_projects, inner.searched.clone())
        };
        if asked {
            let this = Rc::clone(self);
            glib::MainContext::default().spawn_local(async move { this.load_projects(id).await });
        }
        if let Some(searched) = searched {
            self.search(&searched);
        }
    }

    // MARK: - Projects

    /// From the page being shown and its refresh button, never a timer.
    pub fn refresh_projects(self: &Rc<Self>) {
        if self.offline {
            return;
        }
        self.inner.borrow_mut().asked_for_projects = true;
        self.accounts.refresh_names();
        for account in self.accounts.accounts() {
            let this = Rc::clone(self);
            glib::MainContext::default().spawn_local(async move { this.load_projects(account.id).await });
        }
    }

    /// What a search needs to name the project a chat is in. Only accounts
    /// never asked: one with no projects is answered already, and asking again
    /// whenever the list was empty walked every account's projects on every
    /// search.
    fn ask_for_projects_not_yet_asked(self: &Rc<Self>) {
        let unasked: Vec<Uuid> = {
            let inner = self.inner.borrow();
            self.accounts.accounts().iter().map(|a| a.id).filter(|id| !inner.project_states.contains_key(id)).collect()
        };
        if unasked.is_empty() {
            return;
        }
        self.inner.borrow_mut().asked_for_projects = true;
        for id in unasked {
            let this = Rc::clone(self);
            glib::MainContext::default().spawn_local(async move { this.load_projects(id).await });
        }
    }

    async fn load_projects(self: &Rc<Self>, id: Uuid) {
        {
            let mut inner = self.inner.borrow_mut();
            if !self.listed(id) || inner.project_states.get(&id) == Some(&State::Loading) {
                return;
            }
            inner.project_states.insert(id, State::Loading);
        }
        self.emit(ProjectChange::ProjectStates);
        let outcome = self.list_projects(id).await;
        // Signed out while this ran: what it found goes with the account.
        if !self.listed(id) {
            return;
        }
        match outcome {
            Ok(found) => {
                {
                    let mut inner = self.inner.borrow_mut();
                    inner.project_lists.insert(id, found);
                    inner.project_states.insert(id, State::Ready);
                }
                self.accounts.note_sign_in(id, true);
            }
            Err(Failure::SignedOut) => {
                {
                    let mut inner = self.inner.borrow_mut();
                    inner.project_lists.remove(&id);
                    inner.project_states.insert(id, State::SignedOut);
                }
                self.accounts.note_sign_in(id, false);
            }
            Err(error) => {
                self.inner.borrow_mut().project_states.insert(id, State::Failed);
                diagnostics::note("projects.list failed", &[("reason", json!(format!("{error:?}")))]);
            }
        }
        {
            let order: Vec<Uuid> = self.accounts.accounts().iter().map(|a| a.id).collect();
            let mut inner = self.inner.borrow_mut();
            inner.project_lists.retain(|k, _| order.contains(k));
            let lists: Vec<Vec<ClaudeProject>> = order.iter().filter_map(|id| inner.project_lists.get(id).cloned()).collect();
            inner.projects = projects::merge(&lists);
        }
        self.emit(ProjectChange::ProjectStates);
        self.emit(ProjectChange::Projects);
    }

    async fn list_projects(self: &Rc<Self>, id: Uuid) -> Result<Vec<ClaudeProject>, Failure> {
        let org = self.accounts.organization_of(id).await?;
        let session = self.accounts.session(id);
        let mut found = Vec::new();
        for page in 0..projects::MAXIMUM_PROJECT_PAGES {
            let path = projects::projects_path(&org, page * projects::PROJECT_PAGE_SIZE);
            let answer = session.send("GET", &path, None, &[]).await?;
            if answer.status != 200 {
                return Err(Failure::Unreadable);
            }
            let (list, more) = projects::projects(&answer.data, id).ok_or(Failure::Unreadable)?;
            found.extend(list);
            if !more {
                break;
            }
        }
        Ok(found)
    }

    // MARK: - Search

    /// When a person presses Return or the search button, never as they type:
    /// each search asks every account, and claude.ai's own box asks once per
    /// pause, not once per key. The filters narrow what came back and ask
    /// nothing.
    pub fn search(self: &Rc<Self>, text: &str) {
        let query = text.trim().to_string();
        if self.offline {
            return;
        }
        let generation = {
            let mut inner = self.inner.borrow_mut();
            inner.search_generation += 1;
            inner.search_generation
        };
        if query.is_empty() {
            self.clear_search();
            return;
        }
        let ids: Vec<Uuid> = self.accounts.accounts().iter().map(|a| a.id).collect();
        {
            let mut inner = self.inner.borrow_mut();
            inner.searched = Some(query.clone());
            inner.results.clear();
            for id in &ids {
                inner.search_states.insert(*id, State::Loading);
            }
        }
        self.emit(ProjectChange::Results);
        self.emit(ProjectChange::SearchStates);
        self.ask_for_projects_not_yet_asked();
        let lists: Rc<RefCell<HashMap<Uuid, Vec<SearchHit>>>> = Rc::new(RefCell::new(HashMap::new()));
        for id in ids {
            let this = Rc::clone(self);
            let query = query.clone();
            let lists = Rc::clone(&lists);
            glib::MainContext::default().spawn_local(async move {
                let outcome = this.search_one(id, &query).await;
                // A newer search has started, and this one's answers would
                // only mix into its list; or the account was signed out while
                // it ran, and its hits would come back with it.
                if this.inner.borrow().search_generation != generation || !this.listed(id) {
                    return;
                }
                match outcome {
                    Ok(found) => {
                        lists.borrow_mut().insert(id, found);
                        this.inner.borrow_mut().search_states.insert(id, State::Ready);
                        this.accounts.note_sign_in(id, true);
                    }
                    Err(Failure::SignedOut) => {
                        this.inner.borrow_mut().search_states.insert(id, State::SignedOut);
                        this.accounts.note_sign_in(id, false);
                    }
                    Err(error) => {
                        this.inner.borrow_mut().search_states.insert(id, State::Failed);
                        diagnostics::note("search failed", &[("reason", json!(format!("{error:?}")))]);
                    }
                }
                let ordered: Vec<Vec<SearchHit>> = {
                    let lists = lists.borrow();
                    this.accounts.accounts().iter().filter_map(|a| lists.get(&a.id).cloned()).collect()
                };
                this.inner.borrow_mut().results = projects::interleave(&ordered);
                this.emit(ProjectChange::SearchStates);
                this.emit(ProjectChange::Results);
            });
        }
    }

    async fn search_one(self: &Rc<Self>, id: Uuid, query: &str) -> Result<Vec<SearchHit>, Failure> {
        let org = self.accounts.organization_of(id).await?;
        let session = self.accounts.session(id);
        let body = projects::search_body(query);
        let answer = session.send("POST", &projects::search_path(&org), Some(&body), &[]).await?;
        if answer.status != 200 {
            return Err(Failure::Unreadable);
        }
        let mut hits = projects::chats(&answer.data, id).ok_or(Failure::Unreadable)?;
        let code = self.code_sessions(id, &org, &session).await;
        let local = self.local_sessions(code.read).await;
        let account_uuid = self.accounts.account(id).and_then(|a| a.account_uuid);
        let list = SessionList { sessions: &code.sessions, complete: code.complete, listed_at: Some(code.read) };
        hits.extend(projects::code_hits(query, id, account_uuid.as_deref(), Some(&org), &list, &local));
        Ok(hits)
    }

    /// An account with no Code sessions, or whose session list will not
    /// answer, still has its chats searched; only the chats' failure fails it,
    /// and a refusal here does not sign it out.
    async fn code_sessions(self: &Rc<Self>, id: Uuid, org: &str, session: &Rc<WebSession>) -> CodeList {
        if let Some(kept) = self.inner.borrow().code_lists.get(&id) {
            if SystemTime::now().duration_since(kept.read).map(|a| a < CODE_LIST_LIFETIME).unwrap_or(true) {
                return kept.clone();
            }
        }
        // From before the walk: a session that began while it ran may be on a
        // page already read.
        let started = SystemTime::now();
        let mut found = Vec::new();
        let mut cursor: Option<String> = None;
        let mut complete = false;
        for _ in 0..projects::MAXIMUM_CODE_PAGES {
            let path = projects::code_sessions_path(cursor.as_deref());
            let page = match session.send("GET", &path, None, &projects::code_headers(org)).await {
                Ok(answer) if answer.status == 200 => projects::code_sessions(&answer.data),
                Ok(answer) => {
                    diagnostics::note("search.code-sessions refused", &[("status", json!(answer.status.to_string()))]);
                    None
                }
                Err(_) => None,
            };
            let Some((sessions, next)) = page else {
                return CodeList { read: started, sessions: found, complete: false };
            };
            found.extend(sessions);
            cursor = next;
            if cursor.is_none() {
                complete = true;
                break;
            }
        }
        let list = CodeList { read: started, sessions: found, complete };
        self.inner.borrow_mut().code_lists.insert(id, list.clone());
        list
    }

    /// This machine's transcripts, read no earlier than a session list they
    /// are set beside. The two used to be kept two minutes each from reads of
    /// their own, so a session begun here between a list and a newer read of
    /// the transcripts showed as gone from claude.ai, and one begun between a
    /// read and a newer list as on another machine.
    async fn local_sessions(self: &Rc<Self>, list: SystemTime) -> Vec<LocalCodeSession> {
        let serves = |read: &LocalRead| {
            projects::local_read_serves(read.read, list, SystemTime::now(), CODE_LIST_LIFETIME)
        };
        if let Some(kept) = self.inner.borrow().local_read.as_ref().filter(|r| serves(r)) {
            return kept.sessions.clone();
        }
        let waiting = {
            let mut inner = self.inner.borrow_mut();
            match inner.local_reading.as_mut() {
                Some(waiters) => {
                    let (done, wait) = oneshot::channel();
                    waiters.push(done);
                    Some(wait)
                }
                None => None,
            }
        };
        if let Some(wait) = waiting {
            if let Ok(done) = wait.await {
                if serves(&done) {
                    return done.sessions;
                }
            }
        }
        self.inner.borrow_mut().local_reading.get_or_insert_with(Vec::new);
        let transcripts = Arc::clone(&self.transcripts);
        let done = gtk::gio::spawn_blocking(move || {
            let started = SystemTime::now();
            LocalRead { read: started, sessions: local_sessions::read(transcripts.as_ref()) }
        })
        .await
        .unwrap_or_else(|_| LocalRead { read: SystemTime::now(), sessions: Vec::new() });
        let waiters = {
            let mut inner = self.inner.borrow_mut();
            if inner.local_read.as_ref().is_none_or(|r| r.read < done.read) {
                inner.local_read = Some(done.clone());
            }
            inner.local_reading.take().unwrap_or_default()
        };
        for waiter in waiters {
            let _ = waiter.send(done.clone());
        }
        done.sessions
    }

    pub fn clear_search(&self) {
        {
            let mut inner = self.inner.borrow_mut();
            inner.search_generation += 1;
            inner.searched = None;
            inner.results.clear();
            inner.search_states.clear();
        }
        self.emit(ProjectChange::Results);
        self.emit(ProjectChange::SearchStates);
    }
}
