//! Claude's sidebar records, as the storage helper reads them, and the changes
//! a sync makes to them.
//!
//! On a Mac this lived in the helper's own script and ran inside Claude's
//! runtime. Here it is Graft's, so the suite can drive every rule without a
//! JavaScript engine, and the helper is left with the one thing only Chromium
//! may do: reading and writing the databases.

use crate::snapshot::{replace_shared, Snapshot};
use crate::Failure;
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::collections::{BTreeMap, BTreeSet};

pub const FRAME: &str = "dframe-store";
pub const PINS: &str = "store:pin-state:dframe-starred-code";
pub const LEGACY_PINS: &str = "LSS-persisted.starred-local-code-sessions";
pub const SLICE: &str = "LSS-persisted.dframe-local-slice";
pub const OWNER: &str = "ccd-sync-owner";
pub const ACTIVE: &str = "ccd-sync-active";
pub const QUARANTINE: &str = "ccd-sync-quarantine";
pub const PENDING: &str = "ccd-sync-pending:ccd/dframe-store";

/// Every local-storage record the helper reads. Only the first three and the
/// pending marker are ever written.
pub const LOCAL_KEYS: [&str; 7] = [FRAME, LEGACY_PINS, SLICE, OWNER, ACTIVE, QUARANTINE, PENDING];

const SORTS: [&str; 3] = ["recency", "alpha", "created"];

/// One profile's sidebar records exactly as Chromium holds them: the pin
/// record from IndexedDB, or none, and each local-storage value, or none.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Raw {
    pub pin: Option<String>,
    pub local: BTreeMap<String, Option<String>>,
}

impl Raw {
    pub fn get(&self, key: &str) -> Option<&str> {
        self.local.get(key).and_then(|value| value.as_deref())
    }
}

/// What a sync asks of one profile: its pins and order among the shared
/// chats, and the Code sort mode.
#[derive(Debug, Clone, PartialEq)]
pub struct Change {
    pub shared: Vec<String>,
    pub pins: Vec<String>,
    pub order: Vec<String>,
    pub sort: String,
}

impl Change {
    pub fn from_snapshot(snapshot: &Snapshot, shared: &BTreeSet<String>) -> Change {
        let scoped = snapshot.restricted(shared);
        Change {
            shared: shared.iter().cloned().collect(),
            pins: scoped.pins,
            order: scoped.order,
            sort: scoped.sort,
        }
    }
}

fn unsupported() -> Failure {
    Failure::new("unsupported-storage")
}

fn require(ok: bool) -> Result<(), Failure> {
    if ok {
        Ok(())
    } else {
        Err(unsupported())
    }
}

fn parse(text: &str) -> Result<Value, Failure> {
    serde_json::from_str(text).map_err(|_| Failure::new("unreadable-storage"))
}

fn is_strings(value: Option<&Value>) -> bool {
    matches!(value, Some(Value::Array(items)) if items.iter().all(Value::is_string))
}

fn strings_of(value: Option<&Value>) -> Vec<String> {
    match value {
        Some(Value::Array(items)) => items.iter().filter_map(|v| v.as_str().map(str::to_string)).collect(),
        _ => Vec::new(),
    }
}

fn is_object(value: Option<&Value>) -> bool {
    matches!(value, Some(Value::Object(_)))
}

fn is_local_id(value: &str) -> bool {
    value
        .strip_prefix("local_")
        .is_some_and(|rest| !rest.is_empty() && rest.chars().all(|c| c.is_ascii_alphanumeric() || c == '-'))
}

fn unique(items: impl IntoIterator<Item = String>) -> Vec<String> {
    let mut seen = BTreeSet::new();
    items.into_iter().filter(|item| seen.insert(item.clone())).collect()
}

/// A number as JavaScript would write it: a whole one without a fraction.
fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 9.0e15 {
        Value::from(value as i64)
    } else {
        json!(value)
    }
}

fn now_millis() -> f64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as f64)
        .unwrap_or(0.0)
}

/// Claude persists a pin store only once it differs from its default, so a
/// profile that has never pinned a Code session, or unpinned the last one,
/// has no record at all. That is an empty pin set, not an uninitialized
/// profile; reading it as the latter refused every sync between two such
/// profiles.
fn empty_pins() -> Value {
    json!({"version": 0, "state": {"starredIds": []}})
}

struct Decoded {
    frame: Value,
    pin: Value,
    sort: String,
}

fn decode(raw: &Raw) -> Result<Decoded, Failure> {
    let frame = parse(raw.get(FRAME).ok_or_else(unsupported)?)?;
    let pin = match &raw.pin {
        None => empty_pins(),
        Some(text) => parse(text)?,
    };
    let state = frame.get("state");
    let pin_state = pin.get("state");
    require(
        frame.is_object()
            && frame.get("version").and_then(Value::as_f64) == Some(1.0)
            && is_object(state)
            && is_strings(state.and_then(|s| s.get("pinnedOrder")))
            && is_object(state.and_then(|s| s.get("sortByByMode")))
            && pin.is_object()
            && pin.get("version").and_then(Value::as_f64) == Some(0.0)
            && is_object(pin_state)
            && is_strings(pin_state.and_then(|s| s.get("starredIds"))),
    )?;
    let sort = match state.and_then(|s| s.get("sortByByMode")).and_then(|m| m.get("code")) {
        None | Some(Value::Null) => "recency".to_string(),
        Some(Value::String(sort)) => sort.clone(),
        Some(_) => return Err(unsupported()),
    };
    require(SORTS.contains(&sort.as_str()))?;
    for key in [LEGACY_PINS, SLICE] {
        let Some(text) = raw.get(key) else { continue };
        let value = parse(text)?;
        let inner = value.get("value");
        let fits = if key == LEGACY_PINS {
            is_strings(inner)
        } else {
            is_object(inner) && is_strings(inner.and_then(|v| v.get("pinnedOrder")))
        };
        require(value.is_object() && fits)?;
    }
    Ok(Decoded { frame, pin, sort })
}

/// The choices a profile's records hold, without a fingerprint; the caller
/// adds one over everything the write will later be checked against.
pub fn snapshot_of(raw: &Raw) -> Result<Snapshot, Failure> {
    let Decoded { frame, pin, sort } = decode(raw)?;
    let pins = unique(strings_of(pin["state"].get("starredIds")).into_iter().filter(|id| is_local_id(id)));
    let pinned: BTreeSet<&String> = pins.iter().collect();
    let order = unique(
        strings_of(frame["state"].get("pinnedOrder"))
            .into_iter()
            .filter_map(|id| id.strip_prefix("code:").map(str::to_string))
            .filter(|id| pinned.contains(id))
            .chain(pins.iter().cloned()),
    );
    let order_time = match raw.get(SLICE) {
        Some(text) => parse(text)?.get("timestamp").and_then(Value::as_f64).unwrap_or(0.0),
        None => 0.0,
    };
    let scope = frame["state"].get("lastSidebarScopeKey").and_then(Value::as_str).map(str::to_string);
    let mut sorted = pins.clone();
    sorted.sort();
    Ok(Snapshot { pins: sorted, order, sort, order_time, scope, fingerprint: String::new() })
}

fn set_strings(target: &mut Value, key: &str, items: Vec<String>) {
    if let Value::Object(map) = target {
        map.insert(key.to_string(), Value::Array(items.into_iter().map(Value::String).collect()));
    }
}

/// The records a profile should hold once `change` is applied, refusing any
/// shape this does not know rather than guessing at it.
pub fn patch(raw: &Raw, change: &Change) -> Result<Raw, Failure> {
    patch_at(raw, change, now_millis())
}

pub fn patch_at(raw: &Raw, change: &Change, clock: f64) -> Result<Raw, Failure> {
    let Decoded { mut frame, mut pin, sort } = decode(raw)?;
    let order_set: BTreeSet<&String> = change.order.iter().collect();
    require(
        change.shared.iter().all(|id| is_local_id(id))
            && change.pins.iter().all(|id| change.shared.contains(id))
            && change.order.len() == order_set.len()
            && change.order.len() == change.pins.len()
            && change.order.iter().all(|id| change.pins.contains(id))
            && SORTS.contains(&change.sort.as_str()),
    )?;
    let shared: BTreeSet<String> = change.shared.iter().cloned().collect();
    let shared_keys: BTreeSet<String> = change.shared.iter().map(|id| format!("code:{id}")).collect();
    let order_keys: Vec<String> = change.order.iter().map(|id| format!("code:{id}")).collect();
    let now = clock.max(pin.get("updatedAt").and_then(Value::as_f64).unwrap_or(0.0) + 1.0);

    let starred = replace_shared(&strings_of(pin["state"].get("starredIds")), &shared, &change.order);
    set_strings(&mut pin["state"], "starredIds", starred);
    pin["updatedAt"] = number(now);
    let pinned_order = replace_shared(&strings_of(frame["state"].get("pinnedOrder")), &shared_keys, &order_keys);
    set_strings(&mut frame["state"], "pinnedOrder", pinned_order);
    frame["state"]["sortByByMode"]["code"] = Value::String(change.sort.clone());

    let mut local = raw.local.clone();
    local.insert(FRAME.into(), Some(frame.to_string()));
    if sort != change.sort {
        // Claude reconciles this store with its account settings on startup.
        // Use its own account-scoped pending-edit marker, otherwise the
        // server immediately restores the old sort choice. Pins are
        // local-only and must not create an account preference upload by
        // themselves.
        let scope = frame["state"].get("lastSidebarScopeKey").and_then(Value::as_str);
        require(raw.get(QUARANTINE) != Some("1"))?;
        if let Some(owner) = raw.get(OWNER) {
            let scope = scope.ok_or_else(unsupported)?;
            let parts: Vec<&str> = scope.split('/').collect();
            require(parts.len() == 2 && owner == parts[0])?;
            let pending = raw.get(PENDING);
            let migrate = format!("{scope}|migrate");
            require(matches!(pending, None | Some("1")) || pending == Some(scope) || pending == Some(migrate.as_str()))?;
            local.insert(PENDING.into(), Some(scope.to_string()));
        } else {
            require(raw.get(ACTIVE) != Some("1") && raw.get(PENDING).is_none())?;
        }
    }

    let mut legacy = match raw.get(LEGACY_PINS) {
        None => json!({"value": [], "tabId": "graft", "timestamp": 0}),
        Some(text) => parse(text)?,
    };
    let legacy_pins = replace_shared(&strings_of(legacy.get("value")), &shared, &change.order);
    set_strings(&mut legacy, "value", legacy_pins);
    let stamp = legacy.get("timestamp").and_then(Value::as_f64).unwrap_or(0.0);
    legacy["timestamp"] = number(now.max(stamp + 1.0));
    local.insert(LEGACY_PINS.into(), Some(legacy.to_string()));

    let mut slice = match raw.get(SLICE) {
        None => json!({"value": {"pinnedOrder": [], "homeProjectsPinnedOrder": []}, "tabId": "graft", "timestamp": 0}),
        Some(text) => parse(text)?,
    };
    let slice_order = replace_shared(&strings_of(slice["value"].get("pinnedOrder")), &shared_keys, &order_keys);
    set_strings(&mut slice["value"], "pinnedOrder", slice_order);
    let stamp = slice.get("timestamp").and_then(Value::as_f64).unwrap_or(0.0);
    slice["timestamp"] = number(now.max(stamp + 1.0));
    local.insert(SLICE.into(), Some(slice.to_string()));

    Ok(Raw { pin: Some(pin.to_string()), local })
}

/// The two fallback preferences in `claude_desktop_config.json`, changed to
/// agree with the sidebar and nothing else in the file touched: they must
/// agree too, or an empty pin set is filled again from them at startup.
pub fn patch_preferences(data: &Value, change: &Change) -> Result<Value, Failure> {
    let mut result = data.clone();
    let root = result.as_object_mut().ok_or_else(unsupported)?;
    require(matches!(root.get("preferences"), None | Some(Value::Object(_))))?;
    let prefs = root
        .entry("preferences")
        .or_insert_with(|| Value::Object(Map::new()))
        .as_object_mut()
        .ok_or_else(unsupported)?;
    require(matches!(prefs.get("epitaxyPrefs"), None | Some(Value::Object(_))))?;
    let epi = prefs
        .entry("epitaxyPrefs")
        .or_insert_with(|| Value::Object(Map::new()))
        .as_object_mut()
        .ok_or_else(unsupported)?;
    let pins = match epi.get("starred-local-code-sessions") {
        None | Some(Value::Null) => Value::Array(Vec::new()),
        Some(value) => value.clone(),
    };
    let mut slice = match epi.get("dframe-local-slice") {
        None | Some(Value::Null) => json!({"pinnedOrder": [], "homeProjectsPinnedOrder": []}),
        Some(value) => value.clone(),
    };
    require(is_strings(Some(&pins)) && slice.is_object() && is_strings(slice.get("pinnedOrder")))?;
    let shared: BTreeSet<String> = change.shared.iter().cloned().collect();
    let shared_keys: BTreeSet<String> = change.shared.iter().map(|id| format!("code:{id}")).collect();
    let order_keys: Vec<String> = change.order.iter().map(|id| format!("code:{id}")).collect();
    let starred = replace_shared(&strings_of(Some(&pins)), &shared, &change.order);
    epi.insert(
        "starred-local-code-sessions".into(),
        Value::Array(starred.into_iter().map(Value::String).collect()),
    );
    let pinned_order = replace_shared(&strings_of(slice.get("pinnedOrder")), &shared_keys, &order_keys);
    set_strings(&mut slice, "pinnedOrder", pinned_order);
    epi.insert("dframe-local-slice".into(), slice);
    Ok(result)
}

/// `claude_desktop_config.json` is a file people open and edit by hand.
/// Rewritten as one compact line it was correct and unreadable, so the layout
/// it had is kept: its indent, or none if it had none, and its final newline.
pub fn preferences_text(original: Option<&str>, data: &Value) -> String {
    let Some(original) = original else {
        return pretty(data, "  ");
    };
    let indent = first_indent(original);
    let text = if indent.is_empty() {
        data.to_string()
    } else {
        pretty(data, indent)
    };
    if original.ends_with('\n') {
        text + "\n"
    } else {
        text
    }
}

/// The whitespace at the start of the first indented line, as the browser's
/// own `JSON.stringify` would be asked to repeat it: at most ten characters.
fn first_indent(text: &str) -> &str {
    for line in text.split('\n').skip(1) {
        let width = line.len() - line.trim_start_matches([' ', '\t']).len();
        if width > 0 && line[width..].chars().next().is_some_and(|c| !c.is_whitespace()) {
            let mut end = width.min(10);
            while !line.is_char_boundary(end) {
                end -= 1;
            }
            return &line[..end];
        }
    }
    ""
}

fn pretty(data: &Value, indent: &str) -> String {
    use serde::Serialize as _;
    let mut out = Vec::new();
    let formatter = serde_json::ser::PrettyFormatter::with_indent(indent.as_bytes());
    let mut serializer = serde_json::Serializer::with_formatter(&mut out, formatter);
    if data.serialize(&mut serializer).is_err() {
        return data.to_string();
    }
    String::from_utf8(out).unwrap_or_else(|_| data.to_string())
}

/// What a write is checked against: the records, the settings file's text and
/// the origin they were read at. Any one of them changing between the read
/// and the write means somebody else wrote in between.
pub fn fingerprint(raw: &Raw, prefs_text: Option<&str>, origin: &str) -> String {
    let value = json!({"raw": raw, "prefsText": prefs_text, "origin": origin});
    graft_core::files::digest(&graft_core::files::sorted_json(&value))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame() -> Value {
        json!({"version": 1, "state": {
            "pinnedOrder": ["chat:remote", "code:local_a", "cowork:space", "code:local_b"],
            "sortByByMode": {"code": "recency", "cowork": "created"},
            "groupByByMode": {"code": "project"}, "sidebarWidth": 290,
            "customGroupsByScope": {"account/org": {"groups": [{"id": "g", "name": "Keep this"}]}},
            "lastSidebarScopeKey": "account/org"}})
    }

    fn raw() -> Raw {
        let frame = frame();
        let mut local = BTreeMap::new();
        local.insert(FRAME.to_string(), Some(frame.to_string()));
        local.insert(OWNER.to_string(), Some("account".to_string()));
        local.insert(ACTIVE.to_string(), Some("1".to_string()));
        local.insert(QUARANTINE.to_string(), None);
        local.insert(PENDING.to_string(), None);
        local.insert(LEGACY_PINS.to_string(), Some(json!({"value": ["local_a", "local_b", "local_private"],
            "tabId": "existing", "timestamp": 5}).to_string()));
        local.insert(SLICE.to_string(), Some(json!({"value": {"pinnedOrder": frame["state"]["pinnedOrder"],
            "homeProjectsPinnedOrder": ["project"]}, "tabId": "existing", "timestamp": 10}).to_string()));
        Raw {
            pin: Some(json!({"version": 0, "state": {"starredIds": ["local_a", "local_b", "remote"],
                "unrelated": true}, "updatedAt": 1}).to_string()),
            local,
        }
    }

    fn change() -> Change {
        Change {
            shared: vec!["local_a".into(), "local_b".into(), "local_c".into()],
            pins: vec!["local_b".into(), "local_c".into()],
            order: vec!["local_c".into(), "local_b".into()],
            sort: "alpha".into(),
        }
    }

    fn json_of(raw: &Raw, key: &str) -> Value {
        serde_json::from_str(raw.get(key).unwrap()).unwrap()
    }

    fn pin_of(raw: &Raw) -> Value {
        serde_json::from_str(raw.pin.as_deref().unwrap()).unwrap()
    }

    fn with(raw: &Raw, key: &str, value: Option<&str>) -> Raw {
        let mut copy = raw.clone();
        copy.local.insert(key.to_string(), value.map(str::to_string));
        copy
    }

    #[test]
    fn an_unpin_and_a_new_pin_reach_the_authoritative_pin_set() {
        let changed = patch(&raw(), &change()).unwrap();
        assert_eq!(pin_of(&changed)["state"]["starredIds"], json!(["local_c", "local_b", "remote"]));
    }

    #[test]
    fn only_shared_code_positions_change_in_the_mixed_pinned_section() {
        let changed = patch(&raw(), &change()).unwrap();
        assert_eq!(json_of(&changed, FRAME)["state"]["pinnedOrder"],
            json!(["chat:remote", "code:local_c", "cowork:space", "code:local_b"]));
    }

    #[test]
    fn groups_layout_and_cowork_sort_choices_survive_a_code_sort_change() {
        let changed = patch(&raw(), &change()).unwrap();
        let result = json_of(&changed, FRAME);
        let mut expected = frame();
        expected["state"]["pinnedOrder"] = result["state"]["pinnedOrder"].clone();
        expected["state"]["sortByByMode"]["code"] = json!("alpha");
        assert_eq!(result, expected);
        assert_eq!(changed.get(FRAME).unwrap(), expected.to_string(), "and their order in the record is kept");
    }

    #[test]
    fn a_changed_sort_is_queued_as_this_accounts_own_pending_preference_edit() {
        let changed = patch(&raw(), &change()).unwrap();
        assert_eq!(changed.get(PENDING), Some("account/org"));
        assert_eq!(changed.get(OWNER), Some("account"));
    }

    #[test]
    fn pin_and_manual_order_changes_alone_do_not_queue_an_account_settings_upload() {
        let mut same_sort = change();
        same_sort.sort = "recency".into();
        assert_eq!(patch(&raw(), &same_sort).unwrap().get(PENDING), None);
    }

    #[test]
    fn sort_changes_cannot_cross_a_pending_account_or_organization_boundary() {
        for (key, value) in [(OWNER, "another-account"), (QUARANTINE, "1"), (PENDING, "account/another-org")] {
            assert!(patch(&with(&raw(), key, Some(value)), &change()).is_err(), "{key} = {value} is refused");
        }
    }

    #[test]
    fn a_sort_change_preserves_the_identity_of_an_existing_local_migration() {
        let result = patch(&with(&raw(), PENDING, Some("account/org|migrate")), &change()).unwrap();
        assert_eq!(result.get(PENDING), Some("account/org"));
    }

    #[test]
    fn local_only_profiles_do_not_gain_a_server_preference_queue() {
        let local = with(&with(&raw(), OWNER, None), ACTIVE, None);
        assert_eq!(patch(&local, &change()).unwrap().get(PENDING), None);
        assert!(patch(&with(&local, ACTIVE, Some("1")), &change()).is_err());
    }

    #[test]
    fn the_fallback_pin_list_cannot_bring_an_unpinned_chat_back() {
        let changed = patch(&raw(), &change()).unwrap();
        assert_eq!(json_of(&changed, LEGACY_PINS)["value"], json!(["local_c", "local_b", "local_private"]));
    }

    #[test]
    fn the_fallback_order_retains_home_projects_and_other_sidebar_sections() {
        let changed = patch(&raw(), &change()).unwrap();
        let value = &json_of(&changed, SLICE)["value"];
        assert_eq!(value["homeProjectsPinnedOrder"], json!(["project"]));
        assert_eq!(value["pinnedOrder"], json!(["chat:remote", "code:local_c", "cowork:space", "code:local_b"]));
    }

    #[test]
    fn the_original_snapshot_stays_available_for_rollback() {
        let original = raw();
        let _ = patch(&original, &change()).unwrap();
        assert_eq!(json_of(&original, FRAME), frame());
    }

    #[test]
    fn clearing_every_shared_pin_clears_both_browser_fallback_lists() {
        let mut clear = change();
        clear.pins.clear();
        clear.order.clear();
        let result = patch(&raw(), &clear).unwrap();
        assert_eq!(pin_of(&result)["state"]["starredIds"], json!(["remote"]));
        assert_eq!(json_of(&result, LEGACY_PINS)["value"], json!(["local_private"]));
        assert_eq!(json_of(&result, FRAME)["state"]["pinnedOrder"], json!(["chat:remote", "cowork:space"]));
    }

    #[test]
    fn a_newer_unknown_pin_schema_is_refused() {
        let mut newer = raw();
        newer.pin = Some(json!({"version": 1, "state": {"starredIds": []}}).to_string());
        assert!(patch(&newer, &change()).is_err());
    }

    #[test]
    fn a_malformed_fallback_is_refused_before_any_write() {
        assert!(patch(&with(&raw(), SLICE, Some("{")), &change()).is_err());
    }

    #[test]
    fn an_ordering_request_cannot_add_a_chat_outside_the_shared_set() {
        let mut outside = change();
        outside.pins = vec!["local_private".into()];
        outside.order = vec!["local_private".into()];
        assert!(patch(&raw(), &outside).is_err());
    }

    #[test]
    fn a_repeated_pin_cannot_create_duplicate_sidebar_entries() {
        let mut repeated = change();
        repeated.order = vec!["local_b".into(), "local_b".into()];
        assert!(patch(&raw(), &repeated).is_err());
    }

    fn prefs() -> Value {
        json!({"mcpServers": {"keep": {"command": "example"}}, "preferences": {
            "bypassPermissionsOptInByAccount": {"own": true},
            "epitaxyPrefs": {"starred-local-code-sessions": ["local_a", "local_private"],
                "dframe-local-slice": {"pinnedOrder": ["code:local_a", "chat:remote"], "homeProjectsPinnedOrder": ["project"]},
                "epitaxy-auto-mode-consented.own": true}}})
    }

    #[test]
    fn desktop_preference_changes_preserve_mcp_definitions_and_permission_choices() {
        let original = prefs();
        let result = patch_preferences(&original, &change()).unwrap();
        let mut expected = prefs();
        expected["preferences"]["epitaxyPrefs"]["starred-local-code-sessions"] = json!(["local_c", "local_private", "local_b"]);
        expected["preferences"]["epitaxyPrefs"]["dframe-local-slice"]["pinnedOrder"] =
            json!(["code:local_c", "chat:remote", "code:local_b"]);
        assert_eq!(result, expected);
        assert_eq!(original["preferences"]["epitaxyPrefs"]["starred-local-code-sessions"][0], "local_a",
            "the settings read stay as they were for a rollback");
    }

    #[test]
    fn an_unreadable_desktop_preference_collection_is_refused() {
        assert!(patch_preferences(&json!({"preferences": {"epitaxyPrefs": "invalid"}}), &change()).is_err());
    }

    #[test]
    fn missing_fallback_settings_can_be_initialized_without_importing_another_account() {
        let result = patch_preferences(&json!({}), &change()).unwrap();
        let keys: Vec<&String> = result["preferences"].as_object().unwrap().keys().collect();
        assert_eq!(keys, vec!["epitaxyPrefs"]);
        assert_eq!(result["preferences"]["epitaxyPrefs"]["starred-local-code-sessions"], json!(change().order));
    }

    #[test]
    fn a_profile_that_never_pinned_anything_reads_as_an_empty_pin_set() {
        let mut unpinned = raw();
        unpinned.pin = None;
        assert!(snapshot_of(&unpinned).unwrap().pins.is_empty());
        let result = patch(&unpinned, &change()).unwrap();
        assert_eq!(pin_of(&result)["state"]["starredIds"], json!(["local_c", "local_b"]));
        assert_eq!(pin_of(&result)["version"], json!(0));
    }

    #[test]
    fn a_second_application_of_the_same_change_preserves_the_chosen_pins_and_order() {
        let changed = patch(&raw(), &change()).unwrap();
        let again = patch(&changed, &change()).unwrap();
        let a = snapshot_of(&changed).unwrap();
        let b = snapshot_of(&again).unwrap();
        assert_eq!((a.pins, a.order, a.sort), (b.pins, b.order, b.sort));
    }

    #[test]
    fn timestamps_move_forward_and_stay_whole_numbers() {
        let changed = patch_at(&raw(), &change(), 1_700_000_000_000.0).unwrap();
        assert_eq!(pin_of(&changed)["updatedAt"], json!(1_700_000_000_000i64));
        assert!(changed.get(SLICE).unwrap().contains("\"timestamp\":1700000000000"),
            "a timestamp is written as the browser writes one");
        let behind = patch_at(&raw(), &change(), 0.0).unwrap();
        assert_eq!(json_of(&behind, SLICE)["timestamp"], json!(11), "a clock behind the record still moves it on");
    }

    #[test]
    fn desktop_settings_written_back_keep_the_indent_and_final_newline_they_had() {
        let data = json!({"preferences": {"a": 1}});
        assert_eq!(preferences_text(Some("{\n    \"x\": 1\n}\n"), &data),
            "{\n    \"preferences\": {\n        \"a\": 1\n    }\n}\n");
        assert_eq!(preferences_text(Some("{\n\t\"x\": 1\n}"), &data),
            "{\n\t\"preferences\": {\n\t\t\"a\": 1\n\t}\n}");
        assert_eq!(preferences_text(Some("{\"x\":1}"), &data), "{\"preferences\":{\"a\":1}}");
    }

    #[test]
    fn a_settings_file_written_for_the_first_time_is_indented_by_two_spaces() {
        assert_eq!(preferences_text(None, &json!({"a": 1})), "{\n  \"a\": 1\n}");
    }

    #[test]
    fn a_fingerprint_changes_with_the_records_the_settings_or_the_origin() {
        let base = fingerprint(&raw(), Some("{}"), "https://claude.ai");
        assert_eq!(base, fingerprint(&raw(), Some("{}"), "https://claude.ai"));
        assert_ne!(base, fingerprint(&raw(), Some("{ }"), "https://claude.ai"));
        assert_ne!(base, fingerprint(&raw(), Some("{}"), "app://localhost"));
        assert_ne!(base, fingerprint(&with(&raw(), PENDING, Some("1")), Some("{}"), "https://claude.ai"));
    }
}
