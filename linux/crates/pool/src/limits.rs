//! What an account has left, as the service last reported it.

use crate::Time;
use serde::{Deserialize, Serialize};

/// Utilisation is a fraction, the way the response headers carry it, not a
/// percentage.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolLimits {
    pub five_hour: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub five_hour_reset: Option<Time>,
    pub week: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub week_reset: Option<Time>,
    /// Set when the service said no outright, until the time it named.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refused_until: Option<Time>,
    pub observed: Time,
}

pub(crate) fn header<'a>(headers: &'a [(String, String)], name: &str) -> Option<&'a str> {
    headers.iter().find(|(n, _)| n.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
}

impl PoolLimits {
    /// Every answer from `/v1/messages` carries the account's unified limits,
    /// so the relay learns an account's state from the very requests it sends
    /// there. `None` when the answer carried none, which is any request that
    /// never reached the model.
    pub fn parse(headers: &[(String, String)], now: Time) -> Option<PoolLimits> {
        let prefix = "anthropic-ratelimit-unified-";
        let number = |name: &str| header(headers, &format!("{prefix}{name}")).and_then(|v| v.trim().parse::<f64>().ok());
        let five = number("5h-utilization");
        let week = number("7d-utilization");
        let status = header(headers, &format!("{prefix}status"));
        if five.is_none() && week.is_none() && status.is_none() {
            return None;
        }
        let mut limits = PoolLimits {
            five_hour: five.unwrap_or(0.0),
            five_hour_reset: number("5h-reset"),
            week: week.unwrap_or(0.0),
            week_reset: number("7d-reset"),
            refused_until: None,
            observed: now,
        };
        if status == Some("rejected") {
            limits.refused_until = Some(number("reset").unwrap_or(now + 60.0));
        }
        Some(limits)
    }

    /// A reading from the usage endpoint, which reports percentages. An
    /// account with no five-hour window can never be full on one, so it counts
    /// as nothing spent there.
    pub fn from_percent(
        five_hour_percent: Option<i64>,
        five_hour_reset: Option<Time>,
        week_percent: i64,
        week_reset: Option<Time>,
        observed: Time,
    ) -> PoolLimits {
        PoolLimits {
            five_hour: five_hour_percent.unwrap_or(0) as f64 / 100.0,
            five_hour_reset,
            week: week_percent as f64 / 100.0,
            week_reset,
            refused_until: None,
            observed,
        }
    }

    /// Nothing known but that the service said no until `until`.
    pub fn refused(until: Time, now: Time) -> PoolLimits {
        PoolLimits {
            five_hour: 0.0,
            five_hour_reset: None,
            week: 0.0,
            week_reset: None,
            refused_until: Some(until),
            observed: now,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: Time = 1_800_000_000.0;
    const HOUR: f64 = 3600.0;
    const DAY: f64 = 24.0 * HOUR;

    fn h(name: &str, value: &str) -> (String, String) {
        (name.to_string(), value.to_string())
    }

    fn stamp(offset: f64) -> String {
        format!("{}", (NOW + offset) as i64)
    }

    #[test]
    fn an_answers_headers_give_the_accounts_two_windows_as_fractions() {
        let p = "anthropic-ratelimit-unified-";
        let read = PoolLimits::parse(
            &[
                h(&format!("{p}5h-utilization"), "0.42"),
                h(&format!("{p}5h-reset"), &stamp(2.0 * HOUR)),
                h(&format!("{p}7d-utilization"), "0.9"),
                h(&format!("{p}7d-reset"), &stamp(3.0 * DAY)),
                h(&format!("{p}status"), "allowed"),
            ],
            NOW,
        )
        .unwrap();
        assert_eq!((read.five_hour, read.week), (0.42, 0.9));
        assert_eq!(read.week_reset, Some(NOW + 3.0 * DAY), "and when each resets, in seconds since 1970");
        assert_eq!(read.five_hour_reset, Some(NOW + 2.0 * HOUR));
        assert_eq!(read.refused_until, None, "an allowed answer leaves the account open");
    }

    #[test]
    fn a_refusal_that_names_no_reset_closes_the_account_for_a_minute() {
        let read = PoolLimits::parse(&[h("anthropic-ratelimit-unified-status", "rejected")], NOW).unwrap();
        assert_eq!(read.refused_until, Some(NOW + 60.0));
    }

    #[test]
    fn an_answer_that_never_reached_the_model_teaches_nothing_about_the_account() {
        assert_eq!(PoolLimits::parse(&[h("content-type", "application/json")], NOW), None);
    }

    #[test]
    fn header_names_are_matched_whatever_their_case() {
        let read = PoolLimits::parse(&[h("Anthropic-RateLimit-Unified-5h-Utilization", "0.5")], NOW).unwrap();
        assert_eq!(read.five_hour, 0.5);
    }

    #[test]
    fn the_usage_endpoints_percentages_become_the_same_fractions() {
        let read = PoolLimits::from_percent(Some(34), None, 71, None, NOW);
        assert!((read.five_hour - 0.34).abs() < 1e-9 && (read.week - 0.71).abs() < 1e-9);
    }
}
