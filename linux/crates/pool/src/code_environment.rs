//! Graft's half of `environment`: reading a Claude's Code environment,
//! deciding what it should hold, and leaving both finished values in the
//! journal for whichever opens that Claude next.

use crate::environment::{self, Entry, Field};
use graft_core::{diagnostics, files, lock};
use graft_credentials::{Failure, Prompting, Scheme};
use serde_json::{json, Map, Value};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

/// How a profile's values are sealed and opened: the scheme and key Claude
/// itself uses for that profile, judged by its own token cache.
///
/// Electron on Linux seals with `v11` where the desktop keeps it a password
/// and with `v10`, under a key everybody knows, where it has none. A `v10`
/// value therefore opens without the keyring whatever the profile uses now,
/// and a `v11` one only with the keyring's key.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Sealer {
    pub scheme: Scheme,
    pub key: [u8; 16],
}

impl Sealer {
    pub fn new(scheme: Scheme, key: [u8; 16]) -> Sealer {
        Sealer { scheme, key }
    }

    /// The sealer for a profile. One with no login cached yet seals the way
    /// Claude would there: with the keyring's key when the desktop has one,
    /// and the fixed key when it has none.
    pub fn for_profile(profile: &Path, prompting: Prompting) -> Result<Sealer, Failure> {
        if let Some((scheme, key)) = graft_credentials::sealing_key(profile, prompting)? {
            return Ok(Sealer { scheme, key });
        }
        Self::for_graft(prompting)
    }

    /// What Graft seals its own values with, a login signed in from the pane
    /// among them: the keyring's key where there is one.
    pub fn for_graft(prompting: Prompting) -> Result<Sealer, Failure> {
        match graft_credentials::safe_storage_key(prompting)? {
            Some(key) => Ok(Sealer { scheme: Scheme::V11, key }),
            None => Ok(Sealer { scheme: Scheme::V10, key: graft_credentials::peanuts_key() }),
        }
    }

    pub fn seal(&self, plain: &[u8]) -> String {
        graft_credentials::seal(plain, &self.key, self.scheme)
    }

    pub fn unseal(&self, sealed: &str) -> Option<Vec<u8>> {
        use base64::Engine;
        let blob = base64::engine::general_purpose::STANDARD.decode(sealed).ok()?;
        match graft_credentials::scheme_of(&blob)? {
            Scheme::V10 => graft_credentials::decrypt(&blob, &graft_credentials::peanuts_key()),
            Scheme::V11 if self.scheme == Scheme::V11 => graft_credentials::decrypt(&blob, &self.key),
            Scheme::V11 => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum State {
    Off,
    On,
    /// Wanted, and that Claude is open: it takes the lines when it next
    /// starts, since a running one would never read them.
    Waiting,
    /// No longer wanted, and that Claude is open: they come out when it
    /// closes. The relay stays up until then.
    Leaving,
    /// The person already sends this Claude's Claude Code somewhere of their
    /// own, or trusts a certificate of their own. Both are theirs; replacing
    /// either would quietly break what they set up. `holding` is whether a
    /// line of Graft's is still there beside it, in a Claude that is open,
    /// which keeps the relay up until it closes.
    Conflict { name: String, holding: bool },
    Unreadable,
}

impl State {
    pub fn holds_lines(&self) -> bool {
        match self {
            State::On | State::Leaving => true,
            State::Conflict { holding, .. } => *holding,
            State::Off | State::Waiting | State::Unreadable => false,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Reading {
    pub entry: Entry,
    /// The field as read, byte for byte.
    pub current: Option<String>,
    /// Some name holds exactly a value of Graft's, so Claude Code there may be
    /// pointed at the relay or trusting its authority.
    pub holding: bool,
    /// One of Graft's names set to a value of the person's own.
    pub conflict: Option<String>,
}

/// What the profile's environment holds now, and the two values to swap
/// between. The value that matches what is on disk is the one on disk, byte
/// for byte, so a launcher acting on this later recognises it. `None` is an
/// environment that cannot be read.
///
/// A name holding exactly one of Graft's values is Graft's whatever sits
/// beside it. A line used to stop counting as Graft's once a value of the
/// person's own appeared under the other name, and then nothing took it out:
/// a proxy left without the certificate that makes it work, and a profile read
/// as holding nothing while it still pointed at the relay.
pub fn read(profile: &Path, wanted: bool, sealer: &Sealer) -> Option<Reading> {
    let outer = environment::outer(profile)?;
    let current = match outer.get(environment::FIELD) {
        None | Some(Value::Null) => None,
        Some(Value::String(text)) => Some(text.clone()),
        Some(_) => return None,
    };
    let mut variables: BTreeMap<String, String> = BTreeMap::new();
    if let Some(current) = &current {
        let plain = sealer.unseal(current)?;
        let Ok(Value::Object(object)) = serde_json::from_slice::<Value>(&plain) else { return None };
        for (name, value) in object {
            let Value::String(value) = value else { return None };
            variables.insert(name, value);
        }
    }
    let mut conflict = conflict_in(&variables);
    // Scoped values are laid over these when a session starts, so one naming
    // either variable would win over the lines without a word.
    if let Some(Value::String(scoped)) = outer.get(environment::SCOPED_FIELD) {
        let plain = sealer.unseal(scoped)?;
        let object: Value = serde_json::from_slice(&plain).ok()?;
        conflict = conflict.or_else(|| conflict_nested(&object));
    }

    let lines = environment::lines();
    let base: BTreeMap<String, String> =
        variables.iter().filter(|(n, v)| !is_line(n, v)).map(|(n, v)| (n.clone(), v.clone())).collect();
    let holding = base.len() < variables.len();
    let mut merged = base.clone();
    merged.extend(lines);
    let exact = variables == merged;
    let plain = if holding {
        (!base.is_empty()).then(|| seal(&base, sealer))
    } else {
        current.clone()
    };
    let pooled = if conflict.is_some() {
        // The lines never go in beside a value of the person's own, so the
        // only swap left is the one taking Graft's out.
        match (&current, holding) {
            (Some(current), true) => current.clone(),
            _ => seal(&base, sealer),
        }
    } else if exact {
        current.clone().unwrap_or_else(|| seal(&merged, sealer))
    } else {
        seal(&merged, sealer)
    };
    Some(Reading {
        entry: Entry { wanted: wanted && conflict.is_none(), plain, pooled },
        current,
        holding,
        conflict,
    })
}

/// One of Graft's names, whatever its case, holding exactly Graft's value.
pub fn is_line(name: &str, value: &str) -> bool {
    environment::lines().iter().any(|(ours, wanted)| name.eq_ignore_ascii_case(ours) && value == wanted)
}

/// Either name set to anything but the relay's value. Names are matched
/// without regard to case, since a lower-case proxy variable is read too.
pub fn conflict_in(variables: &BTreeMap<String, String>) -> Option<String> {
    let lines = environment::lines();
    for (name, value) in variables {
        for (ours, wanted) in &lines {
            if name.eq_ignore_ascii_case(ours) && value != wanted {
                return Some(ours.clone());
            }
        }
    }
    None
}

fn conflict_nested(object: &Value) -> Option<String> {
    let Value::Object(map) = object else { return None };
    if map.values().all(Value::is_string) {
        let strings: BTreeMap<String, String> =
            map.iter().map(|(k, v)| (k.clone(), v.as_str().unwrap_or_default().to_string())).collect();
        return conflict_in(&strings);
    }
    map.values().find_map(conflict_nested)
}

/// Compact and in a fixed order, the way `JSON.stringify` writes an object
/// built in that order, so the same variables always seal to the same value.
pub fn seal(variables: &BTreeMap<String, String>, sealer: &Sealer) -> String {
    let json = serde_json::to_vec(variables).unwrap_or_else(|_| b"{}".to_vec());
    sealer.seal(&json)
}

fn note(event: &str, profile: &Path, ok: bool) {
    let name = profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    diagnostics::note(event, &[("profile", json!(name)), ("ok", json!(ok))]);
}

/// One pass over every profile that wants the lines or may still hold them.
/// Writes the journal, then settles each closed profile under the launch
/// lock: the same lock and the same swap a launcher uses, so the two can never
/// both be writing one file.
///
/// `relay_ready` says whether the relay answered in this pass; a profile is
/// never given the lines on the strength of a relay that is not there.
/// `sealer` answers for each profile, and one it cannot answer for reads as
/// unreadable. Answers by `environment::key_for`, since a profile read back
/// out of the journal is a path, and one spelling of a folder need not equal
/// another.
pub fn settle(
    wanted: &[(PathBuf, bool)],
    sealer: impl Fn(&Path) -> Option<Sealer>,
    relay: Option<&str>,
    relay_ready: bool,
    running: impl Fn(&Path) -> bool,
) -> BTreeMap<String, State> {
    let mut journal = environment::read_journal();
    journal.relay = relay.map(str::to_string);
    let mut profiles: BTreeMap<String, (PathBuf, bool)> = BTreeMap::new();
    for (url, wants) in wanted {
        profiles.insert(environment::key_for(url), (url.clone(), *wants));
    }
    for path in journal.profiles.keys() {
        profiles.entry(path.clone()).or_insert_with(|| (PathBuf::from(path), false));
    }

    let mut readings: BTreeMap<String, (Reading, Sealer)> = BTreeMap::new();
    for (path, (url, wants)) in &profiles {
        let Some(sealer) = sealer(url) else { continue };
        let Some(reading) = read(url, *wants, &sealer) else { continue };
        journal.profiles.insert(path.clone(), reading.entry.clone());
        readings.insert(path.clone(), (reading, sealer));
    }
    environment::write_journal(&journal);

    let mut states = BTreeMap::new();
    for (path, (url, wants)) in &profiles {
        let Some((reading, sealer)) = readings.get(path) else {
            states.insert(path.clone(), State::Unreadable);
            continue;
        };
        let mut holds = reading.holding || scoped_copies(url, sealer);
        if !running(url) {
            lock::with_launch_lock(|| {
                if running(url) {
                    return;
                }
                // Part of the lines, left by an edit: neither finished value
                // matches it, so a launcher's swap could never take them out.
                // Graft read this very value and holds the key, so it puts the
                // value without them in its place first.
                let entry = &reading.entry;
                let current = reading.current.as_deref();
                if reading.holding
                    && current != entry.plain.as_deref()
                    && current != Some(entry.pooled.as_str())
                    && environment::field(url) == Field::of(current)
                {
                    let written = environment::write_field(entry.plain.as_deref(), url);
                    note("pool.environment partial lines removed", url, written);
                }
                environment::settle_before_opening(url, || relay_ready);
                remove_scoped_copies(url, sealer);
            });
            holds = holds_lines(url, sealer);
        }
        if let (Some(name), true) = (&reading.conflict, *wants) {
            states.insert(path.clone(), State::Conflict { name: name.clone(), holding: holds });
            continue;
        }
        let state = match (*wants, holds) {
            (true, true) => State::On,
            (true, false) => State::Waiting,
            (false, true) => State::Leaving,
            (false, false) => State::Off,
        };
        states.insert(path.clone(), state);
    }

    // A profile that wants nothing and holds nothing is finished with, and a
    // launcher has nothing left to do for it.
    let finished: Vec<&String> = states.iter().filter(|(_, s)| **s == State::Off).map(|(k, _)| k).collect();
    if !finished.is_empty() {
        let mut cleaned = environment::read_journal();
        for path in finished {
            cleaned.profiles.remove(path);
        }
        environment::write_journal(&cleaned);
    }
    states
}

/// Whether this Claude's Claude Code may still be pointed at the relay, which
/// is what keeps the relay up.
pub fn holds_lines(profile: &Path, sealer: &Sealer) -> bool {
    if scoped_copies(profile, sealer) {
        return true;
    }
    read(profile, false, sealer).map(|r| r.holding).unwrap_or(false)
}

/// Claude keeps what a folder's own environment inherited from this one under
/// `:machine:<scope>` in `scopedEnvVars`, and lays it over `envVars` as a
/// session starts. A copy of the lines made there would outlive their removal
/// from `envVars` and keep pointing Claude Code at a relay that may since have
/// stopped, and the swap a launcher makes cannot reach it, since only
/// `envVars` has finished values waiting in the journal.
pub fn scoped_copies(profile: &Path, sealer: &Sealer) -> bool {
    scoped_object(profile, sealer).map(|o| without_lines(&o).1).unwrap_or(false)
}

/// Takes every copy of the exact lines out of the scoped values, so that
/// `envVars` alone decides. A value of the person's own under either name is
/// theirs and stays where it is. Graft's to do, with the key, and only with
/// the profile closed under the launch lock.
pub fn remove_scoped_copies(profile: &Path, sealer: &Sealer) -> bool {
    let Some(object) = scoped_object(profile, sealer) else { return false };
    let (kept, found) = without_lines(&object);
    if !found {
        return false;
    }
    let sealed = sealer.seal(&files::sorted_json(&kept));
    let written = environment::write(Some(&sealed), environment::SCOPED_FIELD, profile);
    note("pool.environment scoped copies removed", profile, written);
    written
}

fn scoped_object(profile: &Path, sealer: &Sealer) -> Option<Value> {
    let outer = environment::outer(profile)?;
    let Value::String(scoped) = outer.get(environment::SCOPED_FIELD)? else { return None };
    let plain = sealer.unseal(scoped)?;
    serde_json::from_slice(&plain).ok()
}

fn without_lines(object: &Value) -> (Value, bool) {
    let Value::Object(map) = object else { return (object.clone(), false) };
    let mut kept = Map::new();
    let mut found = false;
    for (name, value) in map {
        if let Value::String(text) = value {
            if is_line(name, text) {
                found = true;
                continue;
            }
        }
        let (inner, inner_found) = without_lines(value);
        found |= inner_found;
        kept.insert(name.clone(), inner);
    }
    (Value::Object(kept), found)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::environment::{key_for, read_journal, write_journal, Journal, CERTIFICATE_NAME, PROXY_NAME};
    use graft_core::testing::Sandbox;

    fn sealer() -> Sealer {
        let key: Vec<u8> = (0..16u8).map(|i| i * 7 + 1).collect();
        Sealer::new(Scheme::V11, key.try_into().unwrap())
    }

    fn vars(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    }

    fn sealed(v: &BTreeMap<String, String>) -> String {
        seal(v, &sealer())
    }

    fn variables(value: Option<&str>) -> Option<BTreeMap<String, String>> {
        let plain = sealer().unseal(value?)?;
        serde_json::from_slice(&plain).ok()
    }

    fn profile(sandbox: &Sandbox, name: &str, object: Option<Value>) -> PathBuf {
        let url = sandbox.config().join("PoolProfiles").join(name);
        let _ = std::fs::remove_dir_all(&url);
        std::fs::create_dir_all(&url).unwrap();
        if let Some(object) = object {
            std::fs::write(environment::file_in(&url), serde_json::to_vec_pretty(&object).unwrap()).unwrap();
        }
        url
    }

    fn field(profile: &Path) -> Option<String> {
        environment::field(profile).value().map(str::to_string)
    }

    fn with(base: &BTreeMap<String, String>, more: BTreeMap<String, String>) -> BTreeMap<String, String> {
        let mut out = base.clone();
        out.extend(more);
        out
    }

    fn existing() -> BTreeMap<String, String> {
        vars(&[("FOO", "bar"), ("PATH_EXTRA", "/opt/x")])
    }

    fn pass(wanted: &[(&Path, bool)], relay: Option<&str>, ready: bool, running: impl Fn(&Path) -> bool) -> BTreeMap<String, State> {
        let wanted: Vec<(PathBuf, bool)> = wanted.iter().map(|(p, w)| (p.to_path_buf(), *w)).collect();
        settle(&wanted, |_| Some(sealer()), relay, ready, running)
    }

    #[test]
    fn a_value_sealed_with_claudes_key_opens_with_it_and_seals_the_same_every_time() {
        let plain = br#"{"A":"1"}"#;
        let s = sealer();
        let one = s.seal(plain);
        assert_eq!(s.unseal(&one).as_deref(), Some(&plain[..]));
        assert_eq!(one, s.seal(plain), "so a launcher can recognise it");
        use base64::Engine;
        let blob = base64::engine::general_purpose::STANDARD.decode(&one).unwrap();
        assert_eq!(&blob[..3], b"v11", "in Claude's own format");
    }

    #[test]
    fn variables_are_written_compact_and_in_order_slashes_left_alone() {
        let value = seal(&vars(&[("B", "/x/y"), ("A", "1")]), &sealer());
        assert_eq!(sealer().unseal(&value).unwrap(), br#"{"A":"1","B":"/x/y"}"#);
    }

    #[test]
    fn a_value_made_with_the_fixed_key_opens_whatever_the_profile_uses() {
        let fixed = Sealer::new(Scheme::V10, graft_credentials::peanuts_key());
        let value = fixed.seal(b"{}");
        assert_eq!(sealer().unseal(&value).as_deref(), Some(&b"{}"[..]));
        assert_eq!(fixed.unseal(&sealer().seal(b"{}")), None, "while a keyring's value needs the keyring");
    }

    #[test]
    fn a_claude_with_no_code_environment_yet_gets_one_holding_just_the_two_lines() {
        let sandbox = Sandbox::new();
        let empty = profile(&sandbox, "empty", None);
        let reading = read(&empty, true, &sealer()).unwrap();
        assert!(!reading.holding && reading.entry.plain.is_none());
        assert_eq!(variables(Some(&reading.entry.pooled)), Some(environment::lines()));
    }

    #[test]
    fn what_a_claude_already_holds_is_recognised_byte_for_byte() {
        let sandbox = Sandbox::new();
        let kept = profile(&sandbox, "kept", Some(json!({"envVars": sealed(&existing()), "tmpDirOverride": null})));
        let reading = read(&kept, true, &sealer()).unwrap();
        assert!(!reading.holding && reading.entry.plain.as_deref() == Some(sealed(&existing()).as_str()));
        assert_eq!(
            variables(Some(&reading.entry.pooled)),
            Some(with(&existing(), environment::lines())),
            "and the lines go in beside its own variables, which stay as they were"
        );
    }

    #[test]
    fn a_claude_holding_the_lines_can_be_given_back_exactly_what_it_had_without_them() {
        let sandbox = Sandbox::new();
        let holding = profile(&sandbox, "holding", Some(json!({"envVars": sealed(&with(&existing(), environment::lines()))})));
        let reading = read(&holding, false, &sealer()).unwrap();
        assert!(reading.holding && variables(reading.entry.plain.as_deref()) == Some(existing()));
        let bare = profile(&sandbox, "bare", Some(json!({"envVars": sealed(&environment::lines())})));
        let reading = read(&bare, false, &sealer()).unwrap();
        assert!(reading.holding && reading.entry.plain.is_none(), "and one that had nothing before gets nothing back");
    }

    #[test]
    fn a_proxy_of_the_persons_own_is_theirs_whatever_its_case_and_is_left_alone() {
        let sandbox = Sandbox::new();
        let corporate = profile(&sandbox, "corporate", Some(json!({"envVars": sealed(&vars(&[("https_proxy", "http://proxy.example:8080")]))})));
        let reading = read(&corporate, true, &sealer()).unwrap();
        assert!(reading.conflict.as_deref() == Some(PROXY_NAME) && !reading.entry.wanted && !reading.holding);

        let scoped_value = sealer().seal(br#"{"acct":{"scope":{"NODE_EXTRA_CA_CERTS":"/mine.pem"}}}"#);
        let scoped = profile(&sandbox, "scoped", Some(json!({"envVars": sealed(&vars(&[])), "scopedEnvVars": scoped_value})));
        assert_eq!(
            read(&scoped, true, &sealer()).unwrap().conflict.as_deref(),
            Some(CERTIFICATE_NAME),
            "and so is a certificate set for one folder, which would win over the lines without a word"
        );
    }

    #[test]
    fn an_environment_that_will_not_open_is_not_read_as_an_empty_one() {
        let sandbox = Sandbox::new();
        let garbled = profile(&sandbox, "garbled", Some(json!({"envVars": "not sealed at all"})));
        assert!(read(&garbled, true, &sealer()).is_none());
        let kept = profile(&sandbox, "kept", Some(json!({"envVars": sealed(&existing())})));
        assert!(read(&kept, true, &Sealer::new(Scheme::V11, [9; 16])).is_none(), "nor one sealed with a key Graft lacks");
        let torn = profile(&sandbox, "torn", Some(json!({"envVars": sealed(&existing())})));
        std::fs::write(torn.join(format!("{}.journal", environment::FILE_NAME)), b"").unwrap();
        assert!(read(&torn, true, &sealer()).is_none(), "nor one Claude was part way through writing");
    }

    #[test]
    fn a_whole_pass_gives_a_closed_claude_the_lines_and_leaves_an_open_one_until_it_starts() {
        let sandbox = Sandbox::new();
        write_journal(&Journal::default());
        let closed = profile(&sandbox, "closed", Some(json!({"envVars": sealed(&existing())})));
        let opened = profile(&sandbox, "open", Some(json!({"envVars": sealed(&existing())})));
        let open_key = key_for(&opened);
        let states = pass(&[(&closed, true), (&opened, true)], Some("/relay"), true, |p| key_for(p) == open_key);
        assert!(states[&key_for(&closed)] == State::On && variables(field(&closed).as_deref()) == Some(with(&existing(), environment::lines())));
        assert!(
            states[&key_for(&opened)] == State::Waiting && variables(field(&opened).as_deref()) == Some(existing()),
            "an open one is left alone until it next starts, and says so"
        );
        let journal = read_journal();
        assert!(
            journal.relay.as_deref() == Some("/relay") && journal.profiles[&key_for(&opened)].wanted,
            "which is the launcher's to do, from what Graft left it"
        );

        let states = pass(&[], Some("/relay"), true, |_| false);
        assert!(
            states[&key_for(&closed)] == State::Off
                && states[&key_for(&opened)] == State::Off
                && variables(field(&closed).as_deref()) == Some(existing())
                && variables(field(&opened).as_deref()) == Some(existing()),
            "a Claude that stops borrowing has its own environment back, exactly"
        );
        assert!(read_journal().profiles.is_empty(), "and nothing is left for a launcher to do");

        let states = pass(&[(&closed, true)], None, false, |_| false);
        assert!(
            states[&key_for(&closed)] == State::Waiting && variables(field(&closed).as_deref()) == Some(existing()),
            "with no relay to send it to, a Claude is not pointed at one"
        );
    }

    #[test]
    fn a_line_of_grafts_beside_a_certificate_of_the_persons_own_is_still_grafts_to_take_out() {
        let sandbox = Sandbox::new();
        let own_certificate = vars(&[(CERTIFICATE_NAME, "/mine.pem")]);
        let beside_vars = with(&with(&existing(), own_certificate.clone()), vars(&[(PROXY_NAME, &environment::proxy_value())]));
        let beside = profile(&sandbox, "beside", Some(json!({"envVars": sealed(&beside_vars)})));
        let reading = read(&beside, true, &sealer()).unwrap();
        assert!(
            reading.holding
                && reading.conflict.as_deref() == Some(CERTIFICATE_NAME)
                && variables(reading.entry.plain.as_deref()) == Some(with(&existing(), own_certificate.clone()))
        );
        let key = key_for(&beside);
        let conflict = |holding| State::Conflict { name: CERTIFICATE_NAME.to_string(), holding };
        let states = pass(&[(&beside, true)], Some("/relay"), true, |p| key_for(p) == key);
        assert!(
            states[&key] == conflict(true) && states[&key].holds_lines() && read_journal().profiles.contains_key(&key),
            "and while that Claude is open it says why it is not shared and keeps the relay up"
        );
        let states = pass(&[], Some("/relay"), true, |p| key_for(p) == key);
        assert_eq!(states[&key], State::Leaving, "even once nothing is lent to it any more");
        let states = pass(&[(&beside, true)], Some("/relay"), true, |_| false);
        assert!(
            states[&key] == conflict(false) && variables(field(&beside).as_deref()) == Some(with(&existing(), own_certificate)),
            "once it closes the proxy comes out and the person's certificate stays"
        );
    }

    #[test]
    fn a_proxy_of_grafts_left_alone_after_the_certificate_was_deleted_still_points_at_the_relay() {
        let sandbox = Sandbox::new();
        let lone_vars = with(&existing(), vars(&[(PROXY_NAME, &environment::proxy_value())]));
        let lone = profile(&sandbox, "lone", Some(json!({"envVars": sealed(&lone_vars)})));
        let key = key_for(&lone);
        let states = pass(&[(&lone, false)], Some("/relay"), true, |p| key_for(p) == key);
        assert_eq!(states[&key], State::Leaving);
        let states = pass(&[(&lone, false)], Some("/relay"), true, |_| false);
        assert!(
            states[&key] == State::Off && variables(field(&lone).as_deref()) == Some(existing()),
            "and comes out when that Claude closes, though no finished value matched it"
        );
        let lone = profile(&sandbox, "lone", Some(json!({"envVars": sealed(&lone_vars)})));
        let states = pass(&[(&lone, true)], Some("/relay"), true, |_| false);
        assert!(
            states[&key] == State::On && variables(field(&lone).as_deref()) == Some(with(&existing(), environment::lines())),
            "while a Claude still borrowing has the pair made whole"
        );
    }

    #[test]
    fn lines_copied_into_a_folders_own_environment_still_point_claude_code_at_the_relay() {
        let sandbox = Sandbox::new();
        let mut machine: Map<String, Value> =
            environment::lines().into_iter().map(|(k, v)| (k, Value::String(v))).collect();
        machine.insert("FOO".into(), json!("bar"));
        let scopes = json!({"acct": {":machine:builtin_local": Value::Object(machine), "builtin_local": {"OWN": "1"}}});
        let scoped_value = sealer().seal(&files::sorted_json(&scopes));
        let copied = profile(&sandbox, "copied", Some(json!({"envVars": sealed(&existing()), "scopedEnvVars": scoped_value})));
        assert!(holds_lines(&copied, &sealer()));
        let key = key_for(&copied);
        let states = pass(&[(&copied, false)], Some("/relay"), true, |p| key_for(p) == key);
        assert!(
            states[&key] == State::Leaving && read_journal().profiles.contains_key(&key),
            "so a Claude holding them that way keeps the relay, until it closes"
        );
        let states = pass(&[(&copied, false)], Some("/relay"), true, |_| false);
        let outer = environment::outer(&copied).unwrap();
        let after: Value = serde_json::from_slice(&sealer().unseal(outer["scopedEnvVars"].as_str().unwrap()).unwrap()).unwrap();
        assert!(
            states[&key] == State::Off
                && after["acct"][":machine:builtin_local"] == json!({"FOO": "bar"})
                && after["acct"]["builtin_local"] == json!({"OWN": "1"})
                && variables(field(&copied).as_deref()) == Some(existing()),
            "and once it closes the copies come out, leaving everything else in every folder as it was"
        );
    }

    #[test]
    fn claudes_code_environment_is_named_in_one_place_and_only_that_place_writes_it() {
        let crates = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap();
        let mut named = Vec::new();
        let mut stack = vec![crates.to_path_buf()];
        while let Some(dir) = stack.pop() {
            for entry in std::fs::read_dir(&dir).unwrap().flatten() {
                let path = entry.path();
                if path.is_dir() {
                    if path.file_name().is_some_and(|n| n != "target") {
                        stack.push(path);
                    }
                } else if path.extension().is_some_and(|e| e == "rs") {
                    let text = std::fs::read_to_string(&path).unwrap_or_default();
                    if text.contains(concat!("ccd-environment", "-config")) {
                        named.push(path.strip_prefix(crates).unwrap().to_path_buf());
                    }
                }
            }
        }
        assert_eq!(named, vec![PathBuf::from("pool/src/environment.rs")]);
    }
}
