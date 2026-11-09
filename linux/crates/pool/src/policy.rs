//! Which account answers a request, and in what order to fall back.
//!
//! The account whose week resets soonest goes first, because what it has left
//! is the usage that expires soonest. An account is passed over once its
//! five-hour window or its week is all but spent, and stays passed over until
//! that window resets: utilisation only ever rises inside a window, so there
//! is no point at which it comes back early. The account a request came from is
//! always the last resort: when nobody has room, the request goes out exactly
//! as Claude sent it and the service decides.

use crate::limits::{header, PoolLimits};
use crate::{contains, Time};
use std::cmp::Ordering;

/// Short of a hundred because a request is checked against the limit as it
/// starts and not as it runs, so the last one in at 99% goes through and the
/// one after is refused. A refusal is caught and sent on to the next account
/// anyway; the margin only saves the round trip.
pub const FIVE_HOUR_CEILING: f64 = 0.98;
pub const WEEK_CEILING: f64 = 0.99;

/// A conversation's prompt cache lives on the account that wrote it, five
/// minutes from its last use unless the request asked for an hour. Moving the
/// conversation inside that time pays for its whole context again on the next
/// account; once it has lapsed nothing is lost by moving, so nothing holds the
/// conversation where it was.
pub const SHORT_CACHE: f64 = 5.0 * 60.0;
pub const LONG_CACHE: f64 = 60.0 * 60.0;

const WEEK: f64 = 7.0 * 24.0 * 60.0 * 60.0;

/// The longest lifetime anything in the body asked for, since that is how
/// long the most expensive part of it, the system prompt and tools at the
/// front, stays cached. Claude Code writes its JSON without spaces.
pub fn cache_lifetime(body: &[u8]) -> f64 {
    if contains(body, br#""ttl":"1h""#) {
        LONG_CACHE
    } else {
        SHORT_CACHE
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Account {
    pub id: String,
    pub limits: Option<PoolLimits>,
    /// Holds a token that has not been refused.
    pub usable: bool,
}

/// A window that reset since the reading was taken starts again from nothing,
/// and the week after it resets on the same weekday.
pub fn next_week_reset(reset: Option<Time>, now: Time) -> Option<Time> {
    let mut reset = reset?;
    if !reset.is_finite() {
        return None;
    }
    if reset <= now {
        let weeks = ((now - reset) / WEEK).floor() + 1.0;
        reset += weeks * WEEK;
    }
    Some(reset)
}

pub fn has_room(limits: Option<&PoolLimits>, now: Time) -> bool {
    let Some(limits) = limits else { return true };
    if limits.refused_until.is_some_and(|until| until > now) {
        return false;
    }
    if limits.five_hour >= FIVE_HOUR_CEILING && limits.five_hour_reset.unwrap_or(f64::INFINITY) > now {
        return false;
    }
    if limits.week >= WEEK_CEILING && limits.week_reset.unwrap_or(f64::INFINITY) > now {
        return false;
    }
    true
}

/// The accounts to try, best first. The one the request came from is in there
/// wherever it ranked, or last when it has no room either, since then the
/// request goes out as Claude sent it and the service decides. An account
/// nobody has heard from yet goes after every account with a known reset: all
/// that is known about it is that it has not said no.
///
/// `prefer` goes in front of the ranking, first one first, for as long as each
/// still has room: the account somebody chose for this conversation, then the
/// one already holding its cache.
pub fn order(own: &str, accounts: &[Account], prefer: &[String], now: Time) -> Vec<String> {
    let mut open: Vec<&Account> =
        accounts.iter().filter(|a| a.usable && has_room(a.limits.as_ref(), now)).collect();
    open.sort_by(|a, b| {
        let ra = next_week_reset(a.limits.as_ref().and_then(|l| l.week_reset), now);
        let rb = next_week_reset(b.limits.as_ref().and_then(|l| l.week_reset), now);
        match (ra, rb) {
            (Some(x), Some(y)) if x != y => x.partial_cmp(&y).unwrap_or(Ordering::Equal),
            (Some(_), None) => Ordering::Less,
            (None, Some(_)) => Ordering::Greater,
            _ => {
                // Equal, or both unknown: staying on the account the request
                // came from costs nothing, so it wins the tie.
                let (oa, ob) = (a.id == own, b.id == own);
                if oa != ob {
                    if oa {
                        Ordering::Less
                    } else {
                        Ordering::Greater
                    }
                } else {
                    a.id.cmp(&b.id)
                }
            }
        }
    });
    let mut ids: Vec<String> = open.into_iter().map(|a| a.id.clone()).collect();
    for id in prefer.iter().rev() {
        if let Some(index) = ids.iter().position(|i| i == id) {
            let moved = ids.remove(index);
            ids.insert(0, moved);
        }
    }
    if !ids.iter().any(|i| i == own) {
        ids.push(own.to_string());
    }
    ids
}

/// What to make of an answer, decided from its status line and headers alone,
/// before a byte of it reaches Claude Code.
#[derive(Debug, Clone, PartialEq)]
pub enum Outcome {
    Deliver,
    /// The account has no room. Try the next one.
    Exhausted { until: Time },
    /// A borrowed token was refused. It is not offered again until Graft hands
    /// over a different one.
    LoginRefused,
    /// The borrowed account cannot serve this particular request: a model or
    /// feature its plan lacks. It goes back to the account it came from.
    RefusedForAccount,
}

pub fn outcome(status: u16, headers: &[(String, String)], borrowed: bool, now: Time) -> Outcome {
    match status {
        429 => {
            let limits = PoolLimits::parse(headers, now);
            let retry = header(headers, "retry-after").and_then(|v| v.trim().parse::<f64>().ok());
            Outcome::Exhausted {
                until: limits.and_then(|l| l.refused_until).unwrap_or(now + retry.unwrap_or(60.0)),
            }
        }
        401 if borrowed => Outcome::LoginRefused,
        400 | 403 | 404 if borrowed => Outcome::RefusedForAccount,
        _ => Outcome::Deliver,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: Time = 1_800_000_000.0;
    const HOUR: f64 = 3600.0;
    const DAY: f64 = 24.0 * HOUR;

    fn limits(five: f64, five_reset: Option<f64>, week: f64, week_reset: Option<f64>, refused: Option<f64>) -> PoolLimits {
        PoolLimits {
            five_hour: five,
            five_hour_reset: five_reset.map(|o| NOW + o),
            week,
            week_reset: week_reset.map(|o| NOW + o),
            refused_until: refused.map(|o| NOW + o),
            observed: NOW,
        }
    }

    fn account(id: &str, limits: Option<PoolLimits>, usable: bool) -> Account {
        Account { id: id.into(), limits, usable }
    }

    fn order_of(accounts: &[Account], prefer: &[&str]) -> Vec<String> {
        let prefer: Vec<String> = prefer.iter().map(|s| s.to_string()).collect();
        order("own", accounts, &prefer, NOW)
    }

    fn soon() -> Account {
        account("soon", Some(limits(0.1, None, 0.1, Some(DAY), None)), true)
    }
    fn later() -> Account {
        account("later", Some(limits(0.1, None, 0.1, Some(5.0 * DAY), None)), true)
    }
    fn own() -> Account {
        account("own", Some(limits(0.1, None, 0.1, Some(3.0 * DAY), None)), true)
    }
    fn spent() -> Account {
        account("soon", Some(limits(0.99, Some(2.0 * HOUR), 0.1, Some(DAY), None)), true)
    }

    #[test]
    fn the_account_whose_week_resets_soonest_is_asked_first_its_own_included() {
        assert_eq!(order_of(&[later(), own(), soon()], &[]), ["soon", "own", "later"]);
    }

    #[test]
    fn an_account_with_its_five_hours_spent_is_passed_over_until_they_reset() {
        assert_eq!(order_of(&[later(), own(), spent()], &[]), ["own", "later"]);
        let rolled = account("soon", Some(limits(0.99, Some(-HOUR), 0.1, Some(DAY), None)), true);
        assert_eq!(order_of(&[later(), own(), rolled], &[])[0], "soon", "and is back at the front once the window it spent has reset");
    }

    #[test]
    fn a_spent_week_is_passed_over_too() {
        let gone = account("soon", Some(limits(0.1, None, 1.0, Some(DAY), None)), true);
        assert!(!order_of(&[own(), gone], &[]).contains(&"soon".to_string()));
    }

    #[test]
    fn an_account_the_service_refused_is_left_alone_until_the_time_it_named() {
        let refusing = account("soon", Some(limits(0.1, None, 0.1, Some(DAY), Some(30.0 * 60.0))), true);
        assert!(!order_of(&[own(), refusing], &[]).contains(&"soon".to_string()));
    }

    #[test]
    fn an_account_nobody_has_heard_from_goes_after_every_one_with_a_known_reset() {
        assert_eq!(order_of(&[account("unheard", None, true), later()], &[]), ["later", "unheard", "own"]);
    }

    #[test]
    fn with_nothing_known_about_anyone_a_request_stays_with_the_account_that_sent_it() {
        assert_eq!(order_of(&[account("other", None, true), account("own", None, true)], &[]), ["own", "other"]);
    }

    #[test]
    fn an_account_whose_login_cannot_be_used_lends_nothing() {
        let unusable = account("soon", Some(limits(0.1, None, 0.1, Some(DAY), None)), false);
        assert_eq!(order_of(&[own(), unusable], &[]), ["own"]);
    }

    #[test]
    fn a_sender_with_no_room_of_its_own_is_still_the_last_resort_so_the_service_decides() {
        let full = account("own", Some(limits(1.0, Some(HOUR), 0.1, Some(3.0 * DAY), None)), true);
        assert_eq!(order_of(&[full, later()], &[]), ["later", "own"]);
    }

    #[test]
    fn a_conversation_stays_on_the_account_holding_its_prompt_cache() {
        assert_eq!(order_of(&[later(), own(), soon()], &["later"])[0], "later");
        assert_eq!(order_of(&[own(), spent(), later()], &["soon"])[0], "own", "but not on one that has since run out of room");
    }

    #[test]
    fn a_conversation_moved_by_hand_goes_where_it_was_put_ahead_of_where_its_cache_is() {
        assert_eq!(order_of(&[later(), own(), soon()], &["own", "later"]), ["own", "later", "soon"]);
    }

    #[test]
    fn a_request_that_asked_for_an_hour_of_cache_keeps_its_conversation_where_it_is_for_an_hour() {
        assert_eq!(cache_lifetime(br#"{"cache_control":{"type":"ephemeral","ttl":"1h"}}"#), 3600.0);
        assert_eq!(cache_lifetime(br#"{"cache_control":{"type":"ephemeral"}}"#), 300.0, "and one that did not, for five minutes");
    }

    #[test]
    fn a_weekly_reset_that_has_passed_is_followed_by_one_a_week_later() {
        assert_eq!(next_week_reset(Some(NOW - DAY), NOW), Some(NOW + 6.0 * DAY));
        assert_eq!(next_week_reset(Some(NOW - 8.0 * DAY), NOW), Some(NOW + 6.0 * DAY), "however many weeks ago it was");
    }

    #[test]
    fn a_refusal_for_lack_of_room_moves_on_for_as_long_as_the_service_asked() {
        let retry = vec![("retry-after".to_string(), "30".to_string())];
        assert_eq!(outcome(429, &retry, true, NOW), Outcome::Exhausted { until: NOW + 30.0 });
        let named = vec![
            ("anthropic-ratelimit-unified-status".to_string(), "rejected".to_string()),
            ("anthropic-ratelimit-unified-reset".to_string(), format!("{}", (NOW + HOUR) as i64)),
        ];
        assert_eq!(outcome(429, &named, false, NOW), Outcome::Exhausted { until: NOW + HOUR }, "or until the reset it named");
    }

    #[test]
    fn a_borrowed_login_that_is_refused_is_not_lent_again() {
        assert_eq!(outcome(401, &[], true, NOW), Outcome::LoginRefused);
        assert_eq!(outcome(401, &[], false, NOW), Outcome::Deliver, "a sender's own refusal reaches it untouched");
    }

    #[test]
    fn a_request_another_account_cannot_serve_goes_back_to_the_one_it_came_from() {
        for status in [400, 403, 404] {
            assert_eq!(outcome(status, &[], true, NOW), Outcome::RefusedForAccount);
            assert_eq!(outcome(status, &[], false, NOW), Outcome::Deliver, "and on that account it is delivered as answered");
        }
    }

    #[test]
    fn an_answer_or_a_service_down_for_everyone_is_passed_straight_back() {
        for status in [200, 500, 529] {
            assert_eq!(outcome(status, &[], true, NOW), Outcome::Deliver);
        }
    }
}
