//! What a profile records about its own plan usage, in
//! `plan-usage-history.json`, which Claude writes only while that profile
//! runs. The fallback under the live figure, never the figure itself when a
//! live one is to be had.

use serde_json::Value;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const HISTORY_FILE: &str = "plan-usage-history.json";

const FIVE_HOURS: Duration = Duration::from_secs(5 * 60 * 60);
const WEEK: Duration = Duration::from_secs(7 * 24 * 60 * 60);

/// How much of a plan's two windows has been spent. `fh` in the file is the
/// five-hour window, `sd` the seven-day one, both percentages already used.
#[derive(Debug, Clone, PartialEq)]
pub struct Usage {
    /// `None` on an account with no five-hour limit, never zero: zero is a
    /// window with nothing spent in it yet, which a limited account reads all
    /// through an idle day.
    pub five_hour: Option<i64>,
    pub week: i64,
    pub organization: Option<String>,
    pub sampled: SystemTime,
    /// Worked out from the history rather than reported: Claude does not
    /// record when a window closes, but a reset shows up as the figure
    /// dropping back to nothing.
    pub five_hour_reset: Option<SystemTime>,
    pub week_reset: Option<SystemTime>,
    pub fable: Option<i64>,
    pub fable_reset: Option<SystemTime>,
}

/// The one figure the tray gives an account.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Headline {
    pub percent: i64,
    pub is_weekly: bool,
}

impl Usage {
    pub fn new(five_hour: Option<i64>, week: i64, organization: Option<String>, sampled: SystemTime) -> Usage {
        Usage {
            five_hour,
            week,
            organization,
            sampled,
            five_hour_reset: None,
            week_reset: None,
            fable: None,
            fable_reset: None,
        }
    }

    /// Claude only writes its record while it is running, so an old sample
    /// says nothing useful about a five-hour window that has since rolled over.
    pub fn is_stale(&self) -> bool {
        self.is_stale_at(SystemTime::now())
    }

    pub fn is_stale_at(&self, now: SystemTime) -> bool {
        now.duration_since(self.sampled).map(|age| age > FIVE_HOURS).unwrap_or(false)
    }

    /// The five-hour window where there is one. An account without one can
    /// only run out of its week, and its five-hour figure would be a zero that
    /// never moves.
    pub fn headline(&self) -> Headline {
        match self.five_hour {
            Some(percent) => Headline { percent, is_weekly: false },
            None => Headline { percent: self.week, is_weekly: true },
        }
    }
}

struct Cached {
    stamp: Option<SystemTime>,
    size: u64,
    usage: Option<Usage>,
}

/// Parsed history, keyed by file and only re-read when the file changes.
/// Claude appends to it all day and it grows into the hundreds of kilobytes;
/// polling it meant parsing the lot every couple of seconds.
static CACHE: Mutex<Option<HashMap<PathBuf, Cached>>> = Mutex::new(None);

/// The most recent figure a profile recorded. `None` when Claude has never
/// run on it, or has not reported usage yet.
pub fn usage_of(profile: &Path) -> Option<Usage> {
    let file = profile.join(HISTORY_FILE);
    let meta = std::fs::symlink_metadata(&file).ok();
    let stamp = graft_core::files::modification_time(&file);
    let size = meta.map(|m| m.len()).unwrap_or(0);

    {
        let cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(hit) = cache.as_ref().and_then(|c| c.get(&file)) {
            if hit.stamp == stamp && hit.size == size {
                return hit.usage.clone();
            }
        }
    }

    let parsed = std::fs::read(&file).ok().and_then(|bytes| parse_usage(&bytes, SystemTime::now()));
    let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
    cache.get_or_insert_with(HashMap::new).insert(file, Cached { stamp, size, usage: parsed.clone() });
    parsed
}

#[derive(Debug, Clone)]
struct Sample {
    time: SystemTime,
    five_hour: Option<i64>,
    week: i64,
    org: Option<String>,
}

/// A whole number however the file spells it. JSON has one number type, and a
/// figure written as `94.0` is still 94.
fn whole(value: Option<&Value>) -> Option<i64> {
    let value = value?;
    if let Some(n) = value.as_i64() {
        return Some(n);
    }
    let f = value.as_f64()?;
    (f.fract() == 0.0).then_some(f as i64)
}

/// The history's newest figure, read from the file's bytes. `now` is only
/// used to decide which reset times are still ahead.
pub fn parse_usage(bytes: &[u8], now: SystemTime) -> Option<Usage> {
    let root: Value = serde_json::from_slice(bytes).ok()?;
    let raw = root.get("samples")?.as_array()?;

    let mut samples = Vec::with_capacity(raw.len());
    for sample in raw {
        let Some(stamp) = sample.get("t").and_then(Value::as_f64) else { continue };
        let Some(values) = sample.get("u").filter(|v| v.is_object()) else { continue };
        let Some(week) = whole(values.get("sd")) else { continue };
        samples.push(Sample {
            time: UNIX_EPOCH + Duration::from_secs_f64((stamp / 1000.0).max(0.0)),
            five_hour: whole(values.get("fh")),
            week,
            org: sample.get("org").and_then(Value::as_str).map(str::to_string),
        });
    }
    let newest = samples.last()?.clone();

    if has_no_five_hour_limit(&samples) {
        let mut usage = Usage::new(None, newest.week, newest.org.clone(), newest.time);
        usage.week_reset = week_reset(&samples, now);
        return Some(usage);
    }
    // A limited account's sample without the figure is a gap rather than an
    // empty window. Read as zero it dropped the bar to nothing, and the zero
    // then passed for the window reopening, moving its reset.
    let complete: Vec<Sample> = samples.iter().filter(|s| s.five_hour.is_some()).cloned().collect();
    let latest = complete.iter().rev().find(|s| s.org == newest.org)?;
    let mut usage = Usage::new(latest.five_hour, latest.week, latest.org.clone(), latest.time);
    usage.five_hour_reset = five_hour_reset(&complete, now);
    usage.week_reset = week_reset(&complete, now);
    Some(usage)
}

/// Since November 2026 an account with no five-hour limit leaves `fh` out of
/// every sample, while a limited one still writes it on each, a zero
/// included. Taking a limited account for one without costs it its five-hour
/// bar and its Start Session, so one sample without the figure is not enough:
/// the account must have left it out twice running, and written none in the
/// five hours before. Only that account's own samples count, so one signed in
/// after another is not judged by what the other wrote.
fn has_no_five_hour_limit(samples: &[Sample]) -> bool {
    let Some(newest) = samples.last() else { return false };
    if newest.five_hour.is_some() {
        return false;
    }
    let own: Vec<&Sample> = samples.iter().filter(|s| s.org == newest.org).collect();
    if own.len() > 1 && own[own.len() - 2].five_hour.is_some() {
        return false;
    }
    !own.iter().any(|s| {
        s.five_hour.is_some() && newest.time.duration_since(s.time).map(|d| d < FIVE_HOURS).unwrap_or(true)
    })
}

/// The window opened at the first sample after the figure was last zero, and
/// closes five hours later. Nothing to report when none is open, or when the
/// history starts partway through one.
fn five_hour_reset(samples: &[Sample], now: SystemTime) -> Option<SystemTime> {
    let latest = samples.last()?;
    if latest.five_hour.unwrap_or(0) <= 0 {
        return None;
    }
    for index in (1..samples.len()).rev() {
        if samples[index - 1].five_hour == Some(0) && samples[index].five_hour.unwrap_or(0) > 0 {
            let reset = samples[index].time + FIVE_HOURS;
            return (reset > now).then_some(reset);
        }
    }
    None
}

/// Weekly resets are a cycle, so the last one seen can be rolled forward even
/// across stretches where Claude was not running to record it.
fn week_reset(samples: &[Sample], now: SystemTime) -> Option<SystemTime> {
    let last = samples.windows(2).rev().find(|pair| pair[1].week < pair[0].week - 2).map(|pair| pair[1].time)?;
    let mut reset = last + WEEK;
    let mut rolls = 0;
    while reset <= now && rolls < 520 {
        reset += WEEK;
        rolls += 1;
    }
    Some(reset)
}

/// "2d 3h 40m", "3h 40m", "12m": days only when there is at least one, and
/// `None` once the time has passed.
pub fn countdown(to: SystemTime, from: SystemTime) -> Option<String> {
    let remaining = match to.duration_since(from) {
        Ok(d) => d.as_secs() as i64,
        Err(_) => return None,
    };
    if remaining <= 0 {
        return None;
    }
    let days = remaining / 86_400;
    let hours = (remaining % 86_400) / 3_600;
    let minutes = (remaining % 3_600) / 60;
    if days > 0 {
        return Some(format!("{days}d {hours}h {minutes}m"));
    }
    if hours > 0 {
        return Some(format!("{hours}h {minutes}m"));
    }
    Some(format!("{}m", minutes.max(1)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use graft_core::testing::Sandbox;
    use serde_json::json;

    fn millis(t: SystemTime) -> f64 {
        t.duration_since(UNIX_EPOCH).unwrap().as_secs_f64() * 1000.0
    }

    const HOUR: f64 = 3_600_000.0;

    fn write_usage(profile: &Path, samples: &[(f64, i64, i64)]) {
        let samples: Vec<Value> =
            samples.iter().map(|(t, fh, sd)| json!({"t": t, "org": "ORG1", "u": {"fh": fh, "sd": sd}})).collect();
        write_samples(profile, samples);
    }

    fn write_samples(profile: &Path, samples: Vec<Value>) {
        std::fs::create_dir_all(profile).unwrap();
        let body = json!({"version": 2, "samples": samples});
        std::fs::write(profile.join(HISTORY_FILE), serde_json::to_vec(&body).unwrap()).unwrap();
    }

    fn profile(sandbox: &Sandbox, name: &str) -> PathBuf {
        let p = sandbox.config().join(name);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    fn secs_until(t: Option<SystemTime>) -> f64 {
        t.and_then(|t| t.duration_since(SystemTime::now()).ok()).map(|d| d.as_secs_f64()).unwrap_or(0.0)
    }

    #[test]
    fn a_profile_with_no_history_reports_nothing() {
        let sandbox = Sandbox::new();
        assert_eq!(usage_of(&profile(&sandbox, "Claude-Usage")), None);
    }

    #[test]
    fn the_newest_sample_is_the_one_reported() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        write_usage(&p, &[(now - 60_000.0, 10, 20), (now, 45, 69)]);
        let usage = usage_of(&p).unwrap();
        assert_eq!(usage.five_hour, Some(45), "the newest five-hour figure is the one reported");
        assert_eq!(usage.week, 69, "and so is the weekly one");
        assert_eq!(usage.organization.as_deref(), Some("ORG1"), "the organization comes through");
        assert!(!usage.is_stale(), "a fresh sample is not stale");

        write_usage(&p, &[(now - 6.0 * HOUR, 90, 90)]);
        assert!(usage_of(&p).unwrap().is_stale(), "a sample older than the five-hour window is stale");
    }

    #[test]
    fn an_account_that_stopped_writing_the_five_hour_figure_has_no_five_hour_limit() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        write_samples(
            &p,
            vec![
                json!({"t": now - 8.0 * 24.0 * HOUR, "org": "ORG1", "u": {"fh": 50, "sd": 55}}),
                json!({"t": now - 2.0 * HOUR, "org": "ORG1", "u": {"sd": 93}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 94}}),
            ],
        );
        let unlimited = usage_of(&p).unwrap();
        assert_eq!(unlimited.five_hour, None, "an account that has stopped writing the five-hour figure has no five-hour limit");
        assert!(
            unlimited.week == 94 && !unlimited.is_stale() && unlimited.five_hour_reset.is_none(),
            "and its newest weekly figure is the one reported, with no five-hour reset to count down to"
        );
        assert_eq!(
            unlimited.headline(),
            Headline { percent: 94, is_weekly: true },
            "and its week is the figure it gives the tray"
        );

        write_samples(&p, vec![json!({"t": now, "org": "ORG1", "u": {"sd": 3}})]);
        let whole = usage_of(&p).unwrap();
        assert!(whole.five_hour.is_none() && whole.week == 3, "so has an account whose whole history is without it");
    }

    #[test]
    fn a_limited_account_with_nothing_spent_reads_zero_and_keeps_its_limit() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        write_usage(&p, &[(now - HOUR, 0, 40), (now, 0, 41)]);
        assert_eq!(usage_of(&p).unwrap().five_hour, Some(0));
        assert_eq!(
            usage_of(&p).unwrap().headline(),
            Headline { percent: 0, is_weekly: false },
            "and still gives the tray its five-hour figure"
        );
    }

    #[test]
    fn a_limited_accounts_sample_without_the_figure_is_a_gap() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        write_samples(
            &p,
            vec![
                json!({"t": now - 2.0 * HOUR, "org": "ORG1", "u": {"fh": 30, "sd": 40}}),
                json!({"t": now - HOUR, "org": "ORG1", "u": {"fh": 35, "sd": 41}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 42}}),
            ],
        );
        let gap = usage_of(&p).unwrap();
        assert!(gap.five_hour == Some(35) && gap.week == 41, "the last complete sample stands");
        assert!((millis(gap.sampled) - (now - HOUR)).abs() < 1.0, "dated by when that one was taken");

        write_samples(
            &p,
            vec![
                json!({"t": now - 3.0 * HOUR, "org": "ORG1", "u": {"fh": 30, "sd": 40}}),
                json!({"t": now - HOUR, "org": "ORG1", "u": {"sd": 41}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 42}}),
            ],
        );
        assert_eq!(
            usage_of(&p).unwrap().five_hour,
            Some(30),
            "nor do two such samples end the limit while the figure was written in the five hours before"
        );

        write_samples(
            &p,
            vec![
                json!({"t": now - 30.0 * HOUR, "org": "ORG1", "u": {"fh": 20, "sd": 40}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 42}}),
            ],
        );
        assert_eq!(
            usage_of(&p).unwrap().five_hour,
            Some(20),
            "and one on its own after a long silence is not enough to call the limit gone"
        );
    }

    #[test]
    fn an_account_is_judged_by_its_own_samples_alone() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        write_samples(
            &p,
            vec![
                json!({"t": now - 2.0 * HOUR, "org": "ORG2", "u": {"fh": 60, "sd": 70}}),
                json!({"t": now - HOUR, "org": "ORG1", "u": {"sd": 20}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 21}}),
            ],
        );
        let u = usage_of(&p).unwrap();
        assert!(
            u.five_hour.is_none() && u.week == 21 && u.organization.as_deref() == Some("ORG1"),
            "an account signed in after a limited one is judged by its own samples alone"
        );

        write_samples(
            &p,
            vec![
                json!({"t": now - 2.0 * HOUR, "org": "ORG1", "u": {"sd": 20}}),
                json!({"t": now - HOUR, "org": "ORG2", "u": {"fh": 60, "sd": 70}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 21}}),
            ],
        );
        let u = usage_of(&p).unwrap();
        assert!(
            u.five_hour.is_none() && u.organization.as_deref() == Some("ORG1"),
            "including when the other account's sample comes between its own"
        );
    }

    #[test]
    fn a_corrupt_or_empty_history_is_ignored_and_a_rewritten_one_is_read_again() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Usage");
        let now = millis(SystemTime::now());
        std::fs::write(p.join(HISTORY_FILE), b"not json").unwrap();
        assert_eq!(usage_of(&p), None, "a corrupt history is ignored rather than fatal");

        // The parse is cached against the file's stamp and size; a later write
        // has to be picked up rather than served from that cache.
        write_usage(&p, &[(now, 1, 2)]);
        assert_eq!(usage_of(&p).unwrap().five_hour, Some(1), "a rewritten history is re-read");
        write_usage(&p, &[(now, 3, 4)]);
        assert_eq!(usage_of(&p).unwrap().five_hour, Some(3), "and re-read again when it changes");

        write_samples(&p, vec![]);
        assert_eq!(usage_of(&p), None, "so is an empty one");
    }

    #[test]
    fn the_five_hour_window_closes_five_hours_after_it_opened() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Resets");
        let now = millis(SystemTime::now());

        write_usage(&p, &[(now - 3.0 * HOUR, 0, 40), (now - 2.0 * HOUR, 5, 41), (now, 30, 45)]);
        let remaining = secs_until(usage_of(&p).unwrap().five_hour_reset);
        assert!(remaining > 2.9 * 3600.0 && remaining < 3.1 * 3600.0, "opened two hours ago, three hours left");

        write_usage(&p, &[(now - HOUR, 0, 40), (now, 0, 40)]);
        assert_eq!(usage_of(&p).unwrap().five_hour_reset, None, "nothing to report when no window is open");

        write_usage(&p, &[(now - HOUR, 20, 40), (now, 25, 41)]);
        assert_eq!(usage_of(&p).unwrap().five_hour_reset, None, "nor when the history never saw the window open");

        // Read as zero, a sample without the figure looked like the window
        // closing and the next one like it opening again, an hour and a half
        // late.
        write_samples(
            &p,
            vec![
                json!({"t": now - 3.0 * HOUR, "org": "ORG1", "u": {"fh": 0, "sd": 40}}),
                json!({"t": now - 2.0 * HOUR, "org": "ORG1", "u": {"fh": 5, "sd": 41}}),
                json!({"t": now - HOUR, "org": "ORG1", "u": {"sd": 42}}),
                json!({"t": now - 0.5 * HOUR, "org": "ORG1", "u": {"fh": 30, "sd": 45}}),
            ],
        );
        let across = secs_until(usage_of(&p).unwrap().five_hour_reset);
        assert!(
            across > 2.9 * 3600.0 && across < 3.1 * 3600.0,
            "a sample without the five-hour figure does not move when a limited account's window closes"
        );
    }

    #[test]
    fn weekly_resets_roll_forward_across_stretches_claude_was_not_running() {
        let sandbox = Sandbox::new();
        let p = profile(&sandbox, "Claude-Resets");
        let now = millis(SystemTime::now());
        let day = 24.0 * HOUR;
        write_usage(&p, &[(now - 20.0 * day, 0, 60), (now - 19.9 * day, 0, 2), (now, 10, 30)]);
        let week = usage_of(&p).unwrap().week_reset;
        assert!(week.is_some(), "a weekly reset seen once gives the next one");
        assert!(week.unwrap() > SystemTime::now(), "and it is always in the future");
        assert!(secs_until(week) < 7.0 * 24.0 * 3600.0, "within one cycle, however long ago it was seen");

        write_samples(
            &p,
            vec![
                json!({"t": now - 20.0 * day, "org": "ORG1", "u": {"fh": 0, "sd": 60}}),
                json!({"t": now - 19.9 * day, "org": "ORG1", "u": {"sd": 2}}),
                json!({"t": now, "org": "ORG1", "u": {"sd": 30}}),
            ],
        );
        let unlimited = usage_of(&p).unwrap();
        assert!(
            unlimited.five_hour.is_none() && unlimited.week_reset.map(|r| r > SystemTime::now()) == Some(true),
            "an account with no five-hour limit still has its weekly reset worked out"
        );
    }

    #[test]
    fn countdowns_read_in_days_hours_and_minutes() {
        let now = SystemTime::now();
        let text = |s: i64| {
            let to = if s >= 0 { now + Duration::from_secs(s as u64) } else { now - Duration::from_secs((-s) as u64) };
            countdown(to, now)
        };
        assert_eq!(text(2 * 86_400 + 3 * 3_600 + 40 * 60).as_deref(), Some("2d 3h 40m"), "days, hours and minutes together");
        assert_eq!(text(3 * 3_600 + 40 * 60).as_deref(), Some("3h 40m"), "hours and minutes once a day is gone");
        assert_eq!(text(40 * 60).as_deref(), Some("40m"), "minutes alone under the hour");
        assert_eq!(text(20).as_deref(), Some("1m"), "the last seconds round up rather than reading zero");
        assert_eq!(text(-60), None, "a time already past reads as nothing");
    }
}
