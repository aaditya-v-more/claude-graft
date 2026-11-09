//! Reads a plan's live limits from Anthropic's own endpoint, the one Claude
//! Code uses. Better than the figures a profile writes to disk in every way
//! that matters: exact reset times rather than times worked out from history,
//! and current figures even for a profile that is not running.
//!
//! A borrowed token goes to `api.anthropic.com` and nowhere else, and that is
//! settled here by construction: a request is made from one of the two paths
//! below and the one base this module holds, never from a URL a caller hands
//! in, and a redirect is answered as a refusal rather than followed.

use serde_json::{Map, Value};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const HOST: &str = "api.anthropic.com";
const BASE: &str = "https://api.anthropic.com";
pub const USAGE_PATH: &str = "/api/oauth/usage";
pub const MESSAGES_PATH: &str = "/v1/messages";
pub const OAUTH_BETA: &str = "oauth-2025-04-20";

/// The port Graft's own relay listens on. A proxy setting naming it is the
/// one Graft put into a Claude's environment for Claude Code, inherited by a
/// Graft started from a terminal inside that Claude; the relay vouches for
/// this host with an authority this client does not trust, so going through
/// it would only ever fail.
const RELAY_PORT: u16 = 47823;

/// The production endpoint, for anything that wants to show or check it.
pub fn usage_endpoint() -> String {
    format!("{BASE}{USAGE_PATH}")
}

/// Where requests go. Only the suite can move it, and only onto loopback, so
/// a stand-in service answers and nothing a test does reaches the network.
fn base() -> String {
    // A test that forgot its stand-in is refused on loopback's discard port
    // rather than sent to the real service.
    #[cfg(test)]
    return stand_in::base().unwrap_or_else(|| "http://127.0.0.1:9".to_string());
    #[cfg(not(test))]
    BASE.to_string()
}

#[cfg(test)]
pub(crate) mod stand_in {
    use std::sync::Mutex;

    static BASE: Mutex<Option<String>> = Mutex::new(None);

    pub fn base() -> Option<String> {
        BASE.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    pub fn set(base: Option<String>) {
        if let Some(b) = &base {
            assert!(b.starts_with("http://127.0.0.1:"), "a stand-in service lives on loopback");
        }
        *BASE.lock().unwrap_or_else(|e| e.into_inner()) = base;
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Reading {
    /// `None` when the account has no five-hour limit.
    pub five_hour: Option<i64>,
    pub week: i64,
    pub five_hour_reset: Option<SystemTime>,
    pub week_reset: Option<SystemTime>,
    pub plan: Option<String>,
    pub fable: Option<i64>,
    pub fable_reset: Option<SystemTime>,
}

#[derive(Debug, Clone, PartialEq)]
pub enum ApiFailure {
    /// Carries `Retry-After` when the service sends one, so a caller can wait
    /// exactly as long as it was asked to.
    Http { code: u16, retry_after: Option<f64> },
    Unreadable,
    /// No answer at all: no network, a name that would not resolve, a timeout.
    Unreachable(String),
}

impl ApiFailure {
    pub fn message(&self) -> String {
        match self {
            ApiFailure::Http { code: 401 | 403, .. } => {
                "That login was refused. Open the profile once so Claude can renew it.".into()
            }
            ApiFailure::Http { code: 429, .. } => {
                "Anthropic is rate-limiting usage checks. The last figures stay on screen.".into()
            }
            ApiFailure::Http { code, .. } => format!("The usage service answered {code}."),
            ApiFailure::Unreadable => "The usage service sent something unexpected.".into(),
            ApiFailure::Unreachable(detail) => format!("Could not reach Anthropic: {detail}"),
        }
    }
}

impl std::fmt::Display for ApiFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message())
    }
}

impl std::error::Error for ApiFailure {}

/// What came back, before anyone has decided what it means.
pub(crate) struct Answer {
    pub status: u16,
    pub retry_after: Option<f64>,
    pub body: Vec<u8>,
}

/// One client per owner, made when it is needed and dropped with it. Nothing
/// holds one for the whole process, and the client keeps no cache of any
/// kind: on a Mac the shared session's cache wrote each request beside its
/// answer, `Authorization` header included, into a file anything running as
/// the person could read.
pub struct Session {
    agent: ureq::Agent,
}

impl Session {
    pub fn new(timeout: Duration) -> Session {
        let agent: ureq::Agent = ureq::Agent::config_builder()
            .timeout_global(Some(timeout))
            .http_status_as_error(false)
            .max_redirects(0)
            .max_redirects_will_error(false)
            .proxy(proxy())
            .build()
            .into();
        Session { agent }
    }

    pub(crate) fn get(&self, path: &'static str, headers: &[(&str, &str)]) -> Result<Answer, ApiFailure> {
        let mut request = self.agent.get(format!("{}{path}", base()));
        for (name, value) in headers {
            request = request.header(*name, *value);
        }
        answer(request.call())
    }

    pub(crate) fn post(&self, path: &'static str, headers: &[(&str, &str)], body: &[u8]) -> Result<Answer, ApiFailure> {
        let mut request = self.agent.post(format!("{}{path}", base()));
        for (name, value) in headers {
            request = request.header(*name, *value);
        }
        answer(request.send(body))
    }

    /// Blocking; call it off the main thread.
    pub fn fetch(&self, token: &str) -> Result<Reading, ApiFailure> {
        let bearer = format!("Bearer {}", token.trim());
        let answer = self.get(
            USAGE_PATH,
            &[("Authorization", &bearer), ("Accept", "application/json"), ("anthropic-beta", OAUTH_BETA)],
        )?;
        if answer.status != 200 {
            return Err(ApiFailure::Http { code: answer.status, retry_after: answer.retry_after });
        }
        let body: Map<String, Value> = serde_json::from_slice(&answer.body).map_err(|_| ApiFailure::Unreadable)?;
        reading(&body).ok_or(ApiFailure::Unreadable)
    }
}

/// Blocking, with a session of its own that goes when the call does.
pub fn fetch(token: &str) -> Result<Reading, ApiFailure> {
    Session::new(Duration::from_secs(10)).fetch(token)
}

fn answer(result: Result<ureq::http::Response<ureq::Body>, ureq::Error>) -> Result<Answer, ApiFailure> {
    let mut response = result.map_err(|e| ApiFailure::Unreachable(e.to_string()))?;
    let status = response.status().as_u16();
    // Seconds only: an HTTP date is rare here and would be read wrongly sooner
    // than it would be read right.
    let retry_after = response
        .headers()
        .get("retry-after")
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.trim().parse::<f64>().ok());
    let body = response.body_mut().with_config().limit(1024 * 1024).read_to_vec().unwrap_or_default();
    Ok(Answer { status, retry_after, body })
}

/// The person's own proxy, as the desktop session sets it, except the one
/// that points at Graft's relay.
fn proxy() -> Option<ureq::Proxy> {
    #[cfg(test)]
    {
        None
    }
    #[cfg(not(test))]
    {
        ureq::Proxy::try_from_env().filter(|p| !is_relay(p.host(), p.port()))
    }
}

#[cfg_attr(test, allow(dead_code))]
fn is_relay(host: &str, port: u16) -> bool {
    port == RELAY_PORT && matches!(host, "127.0.0.1" | "localhost" | "::1" | "[::1]")
}

/// Separated from the request so the shape of the answer can be tested.
pub fn reading(body: &Map<String, Value>) -> Option<Reading> {
    // Since November 2026 an account with no five-hour limit is sent
    // `five_hour: null`, or no key at all, while a limited one gets the window
    // even idle, at zero. Anything else there is an answer this cannot read,
    // and reading it as no limit would hide a real one.
    let sent = body.get("five_hour").map(|v| !v.is_null()).unwrap_or(false);
    let session = window(body.get("five_hour"));
    if sent && session.is_none() {
        return None;
    }
    let weekly = window(body.get("seven_day"));
    if session.is_none() && weekly.is_none() {
        return None;
    }
    let fable = fable_window(body.get("limits"));
    Some(Reading {
        five_hour: session.map(|s| s.0),
        week: weekly.map(|w| w.0).unwrap_or(0),
        five_hour_reset: session.and_then(|s| s.1),
        week_reset: weekly.and_then(|w| w.1),
        plan: body.get("subscription_type").and_then(Value::as_str).map(capitalized),
        fable: fable.map(|f| f.0),
        fable_reset: fable.and_then(|f| f.1),
    })
}

type Window = (i64, Option<SystemTime>);

fn window(value: Option<&Value>) -> Option<Window> {
    let object = value?.as_object()?;
    let used = percentage(object.get("utilization"))?;
    Some((used, date(object.get("resets_at"))))
}

fn fable_window(value: Option<&Value>) -> Option<Window> {
    let limits = value?.as_array()?;
    let limit = limits.iter().find(|limit| {
        limit.get("kind").and_then(Value::as_str) == Some("weekly_scoped")
            && limit
                .pointer("/scope/model/display_name")
                .and_then(Value::as_str)
                .map(|name| name.to_lowercase().contains("fable"))
                .unwrap_or(false)
    })?;
    let used = percentage(limit.get("percent")).or_else(|| percentage(limit.get("utilization")))?;
    Some((used, date(limit.get("resets_at"))))
}

fn percentage(value: Option<&Value>) -> Option<i64> {
    let value = value?;
    if let Some(n) = value.as_i64() {
        return Some(n);
    }
    value.as_f64().map(|f| f.round() as i64)
}

/// `resets_at` comes back as an ISO timestamp, occasionally as epoch seconds.
fn date(value: Option<&Value>) -> Option<SystemTime> {
    let value = value?;
    if let Some(text) = value.as_str() {
        let parsed = time::OffsetDateTime::parse(text, &time::format_description::well_known::Rfc3339).ok()?;
        let nanos = parsed.unix_timestamp_nanos();
        return (nanos >= 0).then(|| UNIX_EPOCH + Duration::from_nanos(nanos as u64));
    }
    let seconds = value.as_f64()?;
    (seconds >= 0.0).then(|| UNIX_EPOCH + Duration::from_secs_f64(seconds))
}

/// "max" reads "Max", the way the plan is named everywhere else.
fn capitalized(text: &str) -> String {
    text.split(' ')
        .map(|word| {
            let mut chars = word.chars();
            match chars.next() {
                Some(first) => first.to_uppercase().chain(chars.flat_map(char::to_lowercase)).collect(),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::StandIn;
    use serde_json::json;

    fn read(body: Value) -> Option<Reading> {
        reading(body.as_object().unwrap())
    }

    fn iso(text: &str) -> SystemTime {
        date(Some(&json!(text))).unwrap()
    }

    #[test]
    fn a_full_answer_is_read() {
        let r = read(json!({
            "five_hour": {"utilization": 42, "resets_at": "2026-08-24T09:30:00Z"},
            "seven_day": {"utilization": 71.4, "resets_at": "2026-08-25T22:51:00.000Z"},
            "limits": [{
                "kind": "weekly_scoped",
                "percent": 37.6,
                "resets_at": "2026-08-26T22:51:00.000Z",
                "scope": {"model": {"display_name": "Fable"}}
            }],
            "subscription_type": "max"
        }))
        .unwrap();
        assert_eq!(r.five_hour, Some(42), "the five-hour figure is read");
        assert_eq!(r.week, 71, "a fractional weekly figure rounds");
        assert_eq!(r.fable, Some(38), "the scoped weekly figure is read");
        assert_eq!(r.plan.as_deref(), Some("Max"), "the plan name is tidied up");
        assert!(r.week_reset.is_some(), "a reset time with fractional seconds parses");
        assert!(r.fable_reset.is_some(), "the scoped reset comes from its own window");
        // Reset times come from the service rather than being worked out,
        // which is the whole point of preferring it over the file on disk.
        let sent = time::macros::datetime!(2026-08-24 09:30 UTC).unix_timestamp() as u64;
        assert_eq!(
            r.five_hour_reset.unwrap().duration_since(UNIX_EPOCH).unwrap().as_secs(),
            sent,
            "the reset time is exactly what was sent"
        );
        let week = time::macros::datetime!(2026-08-25 22:51 UTC).unix_timestamp() as u64;
        assert_eq!(
            r.week_reset.unwrap().duration_since(UNIX_EPOCH).unwrap().as_secs(),
            week,
            "and one with fractional seconds lands on the same instant"
        );
        assert_eq!(iso("2026-08-25T22:51:00Z"), r.week_reset.unwrap());
    }

    #[test]
    fn the_scoped_window_accepts_the_same_utilization_spelling_as_the_others() {
        let r = read(json!({
            "five_hour": {"utilization": 1},
            "limits": [{
                "kind": "weekly_scoped",
                "percent": null,
                "utilization": 12.4,
                "scope": {"model": {"display_name": "Fable"}}
            }]
        }))
        .unwrap();
        assert_eq!(r.fable, Some(12));
    }

    #[test]
    fn an_answer_without_the_five_hour_window_is_an_account_with_no_five_hour_limit() {
        let week_only = read(json!({
            "five_hour": null,
            "seven_day": {"utilization": 94, "resets_at": "2026-11-09T18:00:00.000Z"},
            "subscription_type": "max"
        }))
        .unwrap();
        assert!(week_only.week == 94 && week_only.week_reset.is_some(), "an answer without the five-hour window is still read");
        assert!(
            week_only.five_hour.is_none() && week_only.five_hour_reset.is_none(),
            "as an account with no five-hour limit, not one with nothing used"
        );
        assert_eq!(
            read(json!({"seven_day": {"utilization": 10}})).map(|r| r.five_hour),
            Some(None),
            "and so is one that leaves the key out altogether"
        );
        let idle = read(json!({"five_hour": {"utilization": 0, "resets_at": null}, "seven_day": {"utilization": 12}})).unwrap();
        assert!(
            idle.five_hour == Some(0) && idle.five_hour_reset.is_none(),
            "a limited account's idle window reads as nothing used, and the limit stays"
        );
    }

    #[test]
    fn a_window_that_cannot_be_read_is_refused() {
        assert_eq!(
            read(json!({"five_hour": {"utilization": null}, "seven_day": {"utilization": 12}})),
            None,
            "a five-hour window that cannot be read is refused rather than taken for no limit"
        );
        assert_eq!(read(json!({"five_hour": "none", "seven_day": {"utilization": 12}})), None, "and so is anything else under that key");
        assert_eq!(read(json!({"five_hour": null})), None, "an answer with neither window is refused");
        assert_eq!(read(json!({})), None, "and so is an empty one");
    }

    #[test]
    fn epoch_seconds_and_a_missing_week_are_read() {
        assert!(read(json!({"five_hour": {"utilization": 5, "resets_at": 1_800_000_000}})).unwrap().five_hour_reset.is_some(), "epoch seconds parse too");
        let missing_week = read(json!({"five_hour": {"utilization": 5}})).unwrap();
        assert_eq!(missing_week.week, 0, "a missing weekly window reads as nothing used, not as a failure");
        assert_eq!(missing_week.fable, None, "an account without a scoped limit does not grow a made-up one");
    }

    #[test]
    fn usage_is_read_from_anthropic_over_https() {
        let endpoint = usage_endpoint();
        assert!(endpoint.starts_with("https://"), "over https");
        let host = endpoint.trim_start_matches("https://").split('/').next().unwrap();
        assert_eq!(host, HOST, "usage is read from Anthropic");
        assert_eq!(HOST, "api.anthropic.com");
    }

    #[test]
    fn the_relay_is_never_taken_for_the_persons_proxy() {
        assert!(is_relay("127.0.0.1", 47823));
        assert!(is_relay("localhost", 47823));
        assert!(!is_relay("proxy.example.com", 3128), "a proxy the person set is kept");
        assert!(!is_relay("127.0.0.1", 3128));
    }

    #[test]
    fn a_fetch_sends_the_token_with_the_oauth_headers_and_reads_the_answer() {
        let stand_in = StandIn::start();
        stand_in.answer(200, &[], r#"{"five_hour":{"utilization":7},"seven_day":{"utilization":9}}"#);
        let reading = fetch("  tok-1\n").unwrap();
        assert_eq!(reading.five_hour, Some(7));
        let seen = stand_in.requests();
        assert_eq!(seen.len(), 1);
        assert_eq!(seen[0].method, "GET");
        assert_eq!(seen[0].path, USAGE_PATH);
        assert_eq!(seen[0].header("authorization").as_deref(), Some("Bearer tok-1"), "the token goes out trimmed");
        assert_eq!(seen[0].header("anthropic-beta").as_deref(), Some(OAUTH_BETA));
    }

    #[test]
    fn a_refusal_carries_the_wait_the_service_asked_for() {
        let stand_in = StandIn::start();
        stand_in.answer(429, &[("Retry-After", "900")], "{}");
        assert_eq!(fetch("t"), Err(ApiFailure::Http { code: 429, retry_after: Some(900.0) }));
        stand_in.answer(500, &[], "{}");
        assert_eq!(fetch("t"), Err(ApiFailure::Http { code: 500, retry_after: None }));
        stand_in.answer(200, &[], "not json");
        assert_eq!(fetch("t"), Err(ApiFailure::Unreadable));
    }

    #[test]
    fn a_redirect_is_a_refusal_and_the_token_never_follows_it() {
        let elsewhere = StandIn::listen_only();
        let stand_in = StandIn::start();
        let target = format!("http://127.0.0.1:{}/steal", elsewhere.port());
        stand_in.answer(302, &[("Location", &target)], "");
        assert_eq!(fetch("secret"), Err(ApiFailure::Http { code: 302, retry_after: None }));
        assert_eq!(elsewhere.connections(), 0, "nothing was sent to where the redirect pointed");
    }

    #[test]
    fn a_test_without_a_stand_in_reaches_nothing() {
        let _none = StandIn::none();
        assert_eq!(base(), "http://127.0.0.1:9", "the suite's default is a port nothing answers on");
        assert!(matches!(fetch("t"), Err(ApiFailure::Unreachable(_))));
    }

    #[test]
    fn no_answer_at_all_is_unreachable() {
        let stand_in = StandIn::start();
        stand_in.hang_up();
        assert!(matches!(fetch("t"), Err(ApiFailure::Unreachable(_))));
    }
}
