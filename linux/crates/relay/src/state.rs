//! Everything the relay knows, which is only what Graft last told it and what
//! the service has said since. Nothing here is written to disk, tokens least
//! of all, and a restarted relay passes every request through untouched until
//! Graft has told it again.

use graft_pool::policy::{self, Account, Outcome};
use graft_pool::{
    digest, PoolConfiguration, PoolLimits, PoolMember, PoolMemberStatus, PoolPass, PoolRouting, PoolSession, PoolStatus, Time,
};
use std::collections::{HashMap, VecDeque};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Attempt {
    /// `None` for a request from nobody in the pool, which goes out as sent.
    pub member: Option<String>,
    /// `None` means the token the request arrived with.
    pub token: Option<String>,
}

#[derive(Debug, Clone, Default)]
struct Runtime {
    served: u64,
    last_served: Option<Time>,
    last_seen: Option<Time>,
    /// Digest of the lent token the service refused. Lending stops until
    /// Graft hands over a different one, which is what a renewed login is.
    refused_token: Option<String>,
}

#[derive(Debug, Clone)]
struct Conversation {
    from: String,
    served_by: Option<String>,
    last_at: Time,
    requests: u64,
    model: Option<String>,
    cache_lifetime: f64,
    passed_over: Vec<PoolPass>,
    /// Set by the first answer to a request carrying a thread. From then on
    /// only such answers say where the conversation is: a title asked for
    /// beside it, answered by another account, holds none of it, and letting
    /// it move `served_by` sent the next turn to an account that answered
    /// `thread_not_found` and named the wrong one in the dropdown.
    threaded: bool,
    started_at: Option<Time>,
    prompted_at: Option<Time>,
}

/// Long enough for the dropdown to go on naming a conversation somebody
/// stepped away from for the afternoon, short enough that the list stays the
/// day's.
pub const CONVERSATION_MEMORY: f64 = 24.0 * 60.0 * 60.0;

/// What one answer said, for `PoolState::served`.
#[derive(Debug, Clone, Default)]
pub struct Served {
    pub status: u16,
    pub passed_over: Vec<PoolPass>,
    pub model: Option<String>,
    pub cache_lifetime: f64,
    pub threaded: bool,
    pub starts: bool,
    pub prompted: bool,
}

#[derive(Debug, Default)]
pub struct PoolState {
    configuration: Option<PoolConfiguration>,
    member_by_digest: HashMap<String, String>,
    lending_digest: HashMap<String, String>,
    runtime: HashMap<String, Runtime>,
    /// By account rather than by member: two members on one account are one
    /// allowance, and what the service says about either is true of both.
    limits: HashMap<String, PoolLimits>,
    conversations: HashMap<String, Conversation>,
    recent: VecDeque<PoolRouting>,
    active: u64,
}

impl PoolState {
    pub fn new() -> PoolState {
        PoolState::default()
    }

    fn account_key(&self, id: &str) -> String {
        self.configuration
            .as_ref()
            .and_then(|c| c.members.iter().find(|m| m.id == id))
            .and_then(|m| m.account.clone())
            .unwrap_or_else(|| id.to_string())
    }

    pub fn configure(&mut self, new: PoolConfiguration, now: Time) {
        self.member_by_digest.clear();
        self.lending_digest.clear();
        let mut kept = HashMap::new();
        for member in &new.members {
            for d in &member.identify {
                self.member_by_digest.entry(d.clone()).or_insert_with(|| member.id.clone());
            }
            let mut state = self.runtime.get(&member.id).cloned().unwrap_or_default();
            if let Some(token) = &member.token {
                let d = digest(token);
                self.lending_digest.insert(member.id.clone(), d.clone());
                self.member_by_digest.entry(d.clone()).or_insert_with(|| member.id.clone());
                if state.refused_token.as_deref() != Some(&d) {
                    state.refused_token = None;
                }
            }
            // The service's own headers are fresher than any poll while the
            // account is in use, and a poll is fresher once it has gone quiet.
            let key = member.account_key().to_string();
            if let Some(pushed) = &member.limits {
                let known = self.limits.get(&key).map(|l| l.observed).unwrap_or(f64::NEG_INFINITY);
                if pushed.observed > known {
                    let refused = self.limits.get(&key).and_then(|l| l.refused_until);
                    let mut fresh = pushed.clone();
                    if let Some(refused) = refused.filter(|r| *r > now) {
                        fresh.refused_until = Some(refused);
                    }
                    self.limits.insert(key, fresh);
                }
            }
            kept.insert(member.id.clone(), state);
        }
        self.runtime = kept;
        // An account nobody names any more is forgotten with its members, so
        // what is held stays the size of what Graft describes.
        let named: std::collections::HashSet<String> = new.members.iter().map(|m| m.account_key().to_string()).collect();
        self.limits.retain(|k, _| named.contains(k));
        self.configuration = Some(new);
    }

    /// Which member a token belongs to. Every request carrying one counts as
    /// that Claude being connected, whatever the request was for.
    pub fn member_for(&mut self, bearer: &str, now: Time) -> Option<String> {
        let id = self.member_by_digest.get(&digest(bearer))?.clone();
        self.runtime.entry(id.clone()).or_default().last_seen = Some(now);
        Some(id)
    }

    /// The sender first, then the accounts it was given, each account once. A
    /// lender on the sender's own account is no lender at all: borrowing from
    /// it spends the very allowance the request was going to spend anyway. An
    /// account given under two names is counted under the first that can
    /// lend: a Claude's own login lapses while it stays closed, and the same
    /// account signed in through Graft lasts a year.
    pub fn candidates<'a>(
        sender: &'a PoolMember,
        members: &'a [PoolMember],
        can_lend: impl Fn(&PoolMember) -> bool,
    ) -> Vec<&'a PoolMember> {
        let mut out = vec![sender];
        let mut place: HashMap<&str, usize> = HashMap::from([(sender.account_key(), 0)]);
        for id in sender.borrows_from.iter().flatten() {
            if *id == sender.id {
                continue;
            }
            let Some(lender) = members.iter().find(|m| m.id == *id) else { continue };
            let account = lender.account_key();
            if let Some(&index) = place.get(account) {
                if index > 0 && !can_lend(out[index]) && can_lend(lender) {
                    out[index] = lender;
                }
            } else {
                place.insert(account, out.len());
                out.push(lender);
            }
        }
        out
    }

    /// `bound` is a request only the account that last answered the
    /// conversation can take as it stands: one carrying reasoning that account
    /// wrote (`request::carries_bound_reasoning`), or continuing a thread
    /// stored there (`request::continues_thread`).
    pub fn plan(&self, own: &str, session: Option<&str>, pinned: bool, bound: bool, now: Time) -> Vec<Attempt> {
        let as_sent = vec![Attempt { member: Some(own.to_string()), token: None }];
        let Some(configuration) = &self.configuration else { return as_sent };
        if !configuration.enabled || pinned {
            return as_sent;
        }
        let Some(sender) = configuration.members.iter().find(|m| m.id == own) else { return as_sent };
        let can_lend = |member: &PoolMember| {
            member.token.is_some()
                && self.runtime.get(&member.id).and_then(|r| r.refused_token.as_ref()) != self.lending_digest.get(&member.id)
        };
        let candidates = Self::candidates(sender, &configuration.members, can_lend);
        if candidates.len() <= 1 {
            return as_sent;
        }
        let accounts: Vec<Account> = candidates
            .iter()
            .map(|m| Account {
                id: m.id.clone(),
                limits: self.limits.get(m.account_key()).cloned(),
                usable: m.id == own || can_lend(m),
            })
            .collect();
        // One account is one candidate whichever name it came under, so a name
        // the relay did not count stands for the one it did: a choice made in
        // the dropdown, and equally the name that last answered, once a
        // Claude's own login lapsed and the same account signed in here took
        // its place.
        let counted = |id: &str| -> Option<String> {
            let account = configuration
                .members
                .iter()
                .find(|m| m.id == id)
                .map(|m| m.account_key().to_string())
                .unwrap_or_else(|| id.to_string());
            candidates.iter().find(|m| m.account_key() == account).map(|m| m.id.clone())
        };
        let mut prefer: Vec<String> = Vec::new();
        let mut holder: Option<String> = None;
        if let Some(session) = session {
            let held = self.conversations.get(session);
            if let Some(chosen) = configuration.assignments.as_ref().and_then(|a| a.get(session)) {
                if let Some(id) = counted(chosen) {
                    prefer.push(id);
                }
            }
            if let Some(held) = held {
                if let Some(last) = &held.served_by {
                    if bound || now - held.last_at < held.cache_lifetime {
                        // Reasoning outlives the cache: it is lost on any other
                        // account however long the conversation sat. So it
                        // stays until its account has no room, and then stays
                        // wherever it went, which loses the reasoning once
                        // rather than on every move. A person moving it from the
                        // dropdown is that one move, made on purpose.
                        holder = counted(last);
                        if let Some(holder) = &holder {
                            prefer.push(holder.clone());
                        }
                    }
                }
            }
        }
        // Nothing says where a conversation the relay has not seen wrote its
        // reasoning, one from before the relay last started, one with no
        // session, or one last answered by an account no longer lent, and the
        // sender's own account is where it began.
        if bound && holder.is_none() {
            prefer.push(own.to_string());
        }
        let mut ids = policy::order(own, &accounts, &prefer, now);
        // The last word is always the sender's own account, asked again if it
        // was asked already: whatever the lent accounts answered last, a
        // refused login, a plan without the model, a limit with their own reset
        // times, is about a login Claude Code never sent.
        if ids.last().map(String::as_str) != Some(own) {
            ids.push(own.to_string());
        }
        ids.into_iter()
            .map(|id| {
                if id == own {
                    Attempt { member: Some(id), token: None }
                } else {
                    let token = candidates.iter().find(|m| m.id == id).and_then(|m| m.token.clone());
                    Attempt { member: Some(id), token }
                }
            })
            .collect()
    }

    pub fn observe(&mut self, member: &str, headers: &[(String, String)], now: Time) {
        if let Some(parsed) = PoolLimits::parse(headers, now) {
            let key = self.account_key(member);
            self.limits.insert(key, parsed);
        }
    }

    pub fn apply(&mut self, outcome: &Outcome, member: &str, now: Time) {
        match outcome {
            Outcome::Deliver | Outcome::RefusedForAccount => {}
            Outcome::Exhausted { until } => {
                let key = self.account_key(member);
                let known = self.limits.entry(key).or_insert_with(|| PoolLimits::refused(*until, now));
                known.refused_until = Some(*until);
            }
            Outcome::LoginRefused => {
                let refused = self.lending_digest.get(member).cloned();
                self.runtime.entry(member.to_string()).or_default().refused_token = refused;
            }
        }
    }

    pub fn served(&mut self, member: &str, own: &str, session: Option<&str>, answer: Served, now: Time) {
        let runtime = self.runtime.entry(member.to_string()).or_default();
        runtime.served += 1;
        runtime.last_served = Some(now);
        if let Some(session) = session {
            let mut held = self.conversations.remove(session).unwrap_or(Conversation {
                from: own.to_string(),
                served_by: None,
                last_at: now,
                requests: 0,
                model: None,
                cache_lifetime: policy::SHORT_CACHE,
                passed_over: vec![],
                threaded: false,
                started_at: answer.starts.then_some(now),
                prompted_at: None,
            });
            held.from = own.to_string();
            held.requests += 1;
            if answer.prompted {
                held.prompted_at = Some(now);
            }
            if answer.threaded || !held.threaded {
                held.last_at = now;
                held.model = answer.model.clone().or(held.model);
                held.passed_over = answer.passed_over.clone();
                // A failed answer wrote no cache anywhere, so it moves nothing.
                if (200..300).contains(&answer.status) {
                    held.served_by = Some(member.to_string());
                    held.cache_lifetime = answer.cache_lifetime;
                    held.threaded = held.threaded || answer.threaded;
                }
            }
            self.conversations.insert(session.to_string(), held);
            if self.conversations.len() > 256 {
                self.conversations.retain(|_, c| now - c.last_at < CONVERSATION_MEMORY);
            }
        }
        self.recent.push_back(PoolRouting {
            at: now,
            from: own.to_string(),
            served_by: member.to_string(),
            status: answer.status,
            passed_over: answer.passed_over,
            model: answer.model,
            session: session.map(str::to_string),
        });
        while self.recent.len() > 40 {
            self.recent.pop_front();
        }
    }

    pub fn began(&mut self) {
        self.active += 1;
    }

    pub fn ended(&mut self) {
        self.active = self.active.saturating_sub(1);
    }

    pub fn in_flight(&self) -> u64 {
        self.active
    }

    pub fn status(&self, binary: &str, pid: u32, started: Time, now: Time) -> PoolStatus {
        let members = self
            .configuration
            .as_ref()
            .map(|c| c.members.as_slice())
            .unwrap_or(&[])
            .iter()
            .map(|m| {
                let state = self.runtime.get(&m.id);
                PoolMemberStatus {
                    id: m.id.clone(),
                    limits: self.limits.get(m.account_key()).cloned(),
                    served: state.map(|s| s.served).unwrap_or(0),
                    last_served: state.and_then(|s| s.last_served),
                    last_seen: state.and_then(|s| s.last_seen),
                    token_refused: state.is_some_and(|s| s.refused_token.is_some()),
                }
            })
            .collect();
        let mut sessions: Vec<(&String, &Conversation)> =
            self.conversations.iter().filter(|(_, c)| now - c.last_at < CONVERSATION_MEMORY).collect();
        sessions.sort_by(|a, b| b.1.last_at.partial_cmp(&a.1.last_at).unwrap_or(std::cmp::Ordering::Equal));
        let sessions = sessions
            .into_iter()
            .take(64)
            .map(|(id, c)| PoolSession {
                id: id.clone(),
                from: c.from.clone(),
                served_by: c.served_by.clone(),
                last_at: c.last_at,
                requests: c.requests,
                model: c.model.clone(),
                cached_until: c.served_by.as_ref().map(|_| c.last_at + c.cache_lifetime),
                passed_over: c.passed_over.clone(),
                started_at: c.started_at,
                prompted_at: c.prompted_at,
            })
            .collect();
        PoolStatus {
            binary: binary.to_string(),
            pid,
            started,
            enabled: self.configuration.as_ref().is_some_and(|c| c.enabled),
            configured: self.configuration.is_some(),
            members,
            recent: self.recent.iter().cloned().collect(),
            sessions,
            open_exchanges: self.active,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_pool::PassReason;

    const NOW: Time = 1_800_000_000.0;
    const DAY: f64 = 86_400.0;

    fn member(id: &str, token: Option<&str>, borrows: &[&str], account: Option<&str>, reset: Option<f64>, observed: f64) -> PoolMember {
        PoolMember {
            id: id.into(),
            name: id.into(),
            account: account.map(str::to_string),
            identify: token.map(|t| vec![digest(t)]).unwrap_or_default(),
            token: token.map(str::to_string),
            limits: reset.map(|r| PoolLimits {
                five_hour: 0.1,
                five_hour_reset: None,
                week: 0.1,
                week_reset: Some(NOW + r),
                refused_until: None,
                observed: NOW + observed,
            }),
            borrows_from: Some(borrows.iter().map(|s| s.to_string()).collect()),
        }
    }

    fn configured(members: Vec<PoolMember>, assignments: &[(&str, &str)]) -> PoolState {
        let mut state = PoolState::new();
        let assignments = assignments.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect();
        state.configure(PoolConfiguration { enabled: true, members, assignments: Some(assignments) }, NOW);
        state
    }

    fn tokens(plan: &[Attempt]) -> Vec<Option<&str>> {
        plan.iter().map(|a| a.token.as_deref()).collect()
    }

    fn answered(state: &mut PoolState, member: &str, own: &str, session: &str, threaded: bool) {
        let answer = Served { status: 200, cache_lifetime: policy::SHORT_CACHE, threaded, ..Served::default() };
        state.served(member, own, Some(session), answer, NOW);
    }

    #[test]
    fn an_unconfigured_relay_sends_everything_as_it_came() {
        let state = PoolState::new();
        assert_eq!(state.plan("A", Some("s"), false, false, NOW), vec![Attempt { member: Some("A".into()), token: None }]);
    }

    #[test]
    fn the_account_whose_week_resets_soonest_answers_and_the_sender_has_the_last_word() {
        let state = configured(
            vec![member("A", Some("tok-A"), &["B"], None, Some(5.0 * DAY), 0.0), member("B", Some("tok-B"), &[], None, Some(2.0 * DAY), 0.0)],
            &[],
        );
        assert_eq!(tokens(&state.plan("A", Some("s"), false, false, NOW)), [Some("tok-B"), None]);
        assert_eq!(tokens(&state.plan("A", Some("s"), true, false, NOW)), [None], "a pinned request stays with its sender");
    }

    #[test]
    fn a_sender_ranked_first_is_asked_again_last() {
        let state = configured(
            vec![member("A", Some("tok-A"), &["B"], None, Some(DAY), 0.0), member("B", Some("tok-B"), &[], None, Some(3.0 * DAY), 0.0)],
            &[],
        );
        assert_eq!(tokens(&state.plan("A", Some("s"), false, false, NOW)), [None, Some("tok-B"), None]);
    }

    #[test]
    fn an_account_a_claude_is_already_signed_into_is_not_borrowed_from_under_another_name() {
        let members = vec![
            member("B", Some("tok-B"), &["D"], Some("acct-B"), Some(4.0 * DAY), 0.0),
            member("D", Some("tok-D"), &[], Some("acct-B"), Some(3600.0), 0.0),
        ];
        let state = configured(members, &[]);
        assert_eq!(tokens(&state.plan("B", Some("s"), false, false, NOW)), [None]);
    }

    #[test]
    fn an_account_listed_twice_lends_under_its_second_name_when_the_first_has_no_login() {
        let members = vec![
            member("A", Some("tok-A"), &["B", "E"], Some("acct-A"), Some(4.0 * DAY), 0.0),
            member("B", None, &[], Some("acct-B"), Some(DAY), 0.0),
            member("E", Some("tok-E"), &[], Some("acct-B"), Some(DAY), 0.0),
        ];
        let state = configured(members, &[]);
        let plan = state.plan("A", Some("s20"), false, false, NOW);
        assert_eq!(plan[0], Attempt { member: Some("E".into()), token: Some("tok-E".into()) });
    }

    #[test]
    fn a_conversation_moved_to_an_account_listed_twice_goes_to_the_name_the_relay_counted() {
        let members = vec![
            member("A", Some("tok-A"), &["B", "E"], Some("acct-A"), Some(DAY), 0.0),
            member("B", Some("tok-B"), &[], Some("acct-B"), Some(4.0 * DAY), 0.0),
            member("E", Some("tok-E"), &[], Some("acct-B"), Some(4.0 * DAY), 0.0),
        ];
        let state = configured(members, &[("s19", "E")]);
        assert_eq!(tokens(&state.plan("A", Some("s19"), false, false, NOW))[0], Some("tok-B"));
    }

    #[test]
    fn a_conversation_stays_while_its_cache_lives_and_reasoning_stays_past_it() {
        let members = vec![
            member("E", Some("tok-E"), &["D"], None, Some(5.0 * DAY), 0.0),
            member("D", Some("tok-D"), &[], None, Some(3600.0), 0.0),
        ];
        let mut state = configured(members, &[]);
        assert_eq!(tokens(&state.plan("E", Some("s31"), false, true, NOW))[0], None, "reasoning never seen written stays home");
        answered(&mut state, "D", "E", "s30", false);
        let later = NOW + 600.0;
        assert_eq!(tokens(&state.plan("E", Some("s30"), false, true, later))[0], Some("tok-D"), "reasoning D wrote stays with D");
        answered(&mut state, "E", "E", "s32", false);
        assert_eq!(
            tokens(&state.plan("E", Some("s32"), false, false, NOW + 60.0))[0],
            None,
            "a conversation with no reasoning stays on the account holding its cache"
        );
        assert_eq!(
            tokens(&state.plan("E", Some("s32"), false, false, NOW + 600.0))[0],
            Some("tok-D"),
            "and goes where the ranking says once the cache has lapsed"
        );
    }

    #[test]
    fn a_held_conversation_follows_its_account_across_names() {
        let mut state = configured(
            vec![
                member("S", Some("tok-S"), &["X", "Y"], Some("acct-S"), Some(1800.0), 0.0),
                member("X", Some("tok-X"), &[], Some("acct-X"), Some(900.0), 0.0),
                member("Y", Some("tok-Y"), &[], Some("acct-X"), Some(900.0), 0.0),
            ],
            &[],
        );
        answered(&mut state, "X", "S", "s63", false);
        state.configure(
            PoolConfiguration {
                enabled: true,
                members: vec![
                    member("S", Some("tok-S"), &["X", "Y"], Some("acct-S"), Some(1800.0), 10.0),
                    member("X", None, &[], Some("acct-X"), Some(5.0 * DAY), 10.0),
                    member("Y", Some("tok-Y"), &[], Some("acct-X"), Some(5.0 * DAY), 10.0),
                ],
                assignments: None,
            },
            NOW,
        );
        assert_eq!(
            tokens(&state.plan("S", Some("s63"), false, true, NOW))[0],
            Some("tok-Y"),
            "its reasoning stays with the account that wrote it, under the name that account now lends by"
        );
    }

    #[test]
    fn a_refused_login_is_not_lent_again_until_it_changes() {
        let mut state = configured(
            vec![member("A", Some("tok-A"), &["C"], None, Some(5.0 * DAY), 0.0), member("C", Some("tok-C"), &[], None, Some(DAY), 0.0)],
            &[],
        );
        state.apply(&Outcome::LoginRefused, "C", NOW);
        assert_eq!(tokens(&state.plan("A", Some("s"), false, false, NOW)), [None]);
        assert!(state.status("b", 1, NOW, NOW).members[1].token_refused, "the relay says which login was refused");
        state.configure(
            PoolConfiguration {
                enabled: true,
                members: vec![member("A", Some("tok-A"), &["C"], None, None, 0.0), member("C", Some("tok-C2"), &[], None, None, 0.0)],
                assignments: None,
            },
            NOW,
        );
        assert!(tokens(&state.plan("A", Some("s"), false, false, NOW)).contains(&Some("tok-C2")), "a renewed login lends again");
    }

    #[test]
    fn a_full_account_is_not_asked_again_until_its_window_resets() {
        let mut state = configured(
            vec![member("A", Some("tok-A"), &["B"], None, Some(5.0 * DAY), 0.0), member("B", Some("tok-B"), &[], None, Some(2.0 * DAY), 0.0)],
            &[],
        );
        state.apply(&Outcome::Exhausted { until: NOW + 1800.0 }, "B", NOW);
        assert_eq!(tokens(&state.plan("A", Some("s"), false, false, NOW)), [None]);
        assert_eq!(tokens(&state.plan("A", Some("s"), false, false, NOW + 1801.0))[0], Some("tok-B"));
    }

    #[test]
    fn a_title_answered_elsewhere_does_not_say_where_the_thread_is() {
        let mut state = configured(vec![member("H", Some("tok-H"), &["G"], None, None, 0.0), member("G", Some("tok-G"), &[], None, None, 0.0)], &[]);
        answered(&mut state, "H", "H", "s40", true);
        answered(&mut state, "G", "H", "s40", false);
        let status = state.status("b", 1, NOW, NOW);
        assert_eq!(status.sessions[0].served_by.as_deref(), Some("H"));
    }

    #[test]
    fn a_conversation_first_seen_with_its_first_message_is_marked_as_new() {
        let mut state = configured(vec![member("H", Some("tok-H"), &[], None, None, 0.0)], &[]);
        state.served("H", "H", Some("new"), Served { status: 200, starts: true, ..Served::default() }, NOW);
        state.served("H", "H", Some("old"), Served { status: 200, starts: false, ..Served::default() }, NOW);
        let sessions = state.status("b", 1, NOW, NOW).sessions;
        let by: HashMap<&str, &PoolSession> = sessions.iter().map(|s| (s.id.as_str(), s)).collect();
        assert!(by["new"].started_at.is_some() && by["old"].started_at.is_none());
    }

    #[test]
    fn the_record_says_which_account_was_passed_over_and_why() {
        let mut state = configured(vec![member("A", Some("tok-A"), &["B"], None, None, 0.0), member("B", Some("tok-B"), &[], None, None, 0.0)], &[]);
        let passed = vec![PoolPass { id: "B".into(), reason: PassReason::Full }];
        state.served("A", "A", Some("s4"), Served { status: 200, passed_over: passed.clone(), ..Served::default() }, NOW);
        let status = state.status("b", 1, NOW, NOW);
        assert!(status.recent[0].served_by == "A" && status.recent[0].passed_over == passed);
    }
}
