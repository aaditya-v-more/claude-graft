//! What one profile's sidebar says about the Code sessions it shares, and how
//! two of them are brought to agree.

use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

/// The pins, their order and the Code sort mode, as one profile has them.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub pins: Vec<String>,
    pub order: Vec<String>,
    pub sort: String,
    #[serde(default)]
    pub order_time: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
    #[serde(default)]
    pub fingerprint: String,
}

impl Snapshot {
    pub fn new(pins: &[&str], order: &[&str], sort: &str) -> Snapshot {
        Snapshot {
            pins: pins.iter().map(|s| s.to_string()).collect(),
            order: order.iter().map(|s| s.to_string()).collect(),
            sort: sort.to_string(),
            ..Snapshot::default()
        }
    }

    /// Only the chats both sides of a pair hold; a pin on anything else is
    /// that profile's own business.
    pub fn restricted(&self, shared: &BTreeSet<String>) -> Snapshot {
        let members: BTreeSet<String> =
            self.pins.iter().filter(|id| shared.contains(*id)).cloned().collect();
        let mut seen = BTreeSet::new();
        let order = self
            .order
            .iter()
            .chain(self.pins.iter())
            .filter(|id| members.contains(*id) && seen.insert((*id).clone()))
            .cloned()
            .collect();
        Snapshot {
            pins: members.into_iter().collect(),
            order,
            sort: self.sort.clone(),
            order_time: self.order_time,
            scope: None,
            fingerprint: String::new(),
        }
    }

    /// This profile's choices with the shared part taken from `other`.
    pub fn replacing(&self, shared: &BTreeSet<String>, other: &Snapshot) -> Snapshot {
        let mut result = self.clone();
        let pins: BTreeSet<String> = self
            .pins
            .iter()
            .filter(|id| !shared.contains(*id))
            .chain(other.pins.iter())
            .cloned()
            .collect();
        result.pins = pins.into_iter().collect();
        result.order = replace_shared(&self.order, shared, &other.order);
        result.sort = other.sort.clone();
        result.order_time = self.order_time.max(other.order_time);
        result
    }

    pub fn same_choices(&self, other: &Snapshot) -> bool {
        let mine: BTreeSet<&String> = self.pins.iter().collect();
        let theirs: BTreeSet<&String> = other.pins.iter().collect();
        mine == theirs && self.order == other.order && self.sort == other.sort
    }
}

/// Unrelated entries keep their positions; only slots that belong to shared
/// Code sessions can be replaced or removed.
pub fn replace_shared(existing: &[String], shared: &BTreeSet<String>, wanted: &[String]) -> Vec<String> {
    let mut index = 0;
    let mut result: Vec<String> = Vec::new();
    for item in existing {
        if !shared.contains(item) {
            result.push(item.clone());
        } else if index < wanted.len() {
            result.push(wanted[index].clone());
            index += 1;
        }
    }
    result.extend(wanted.iter().skip(index).cloned());
    let mut seen = BTreeSet::new();
    result.into_iter().filter(|item| seen.insert(item.clone())).collect()
}

/// A baseline makes unpinning a change, rather than an invitation to put the
/// other profile's pin back. Simultaneous reorders prefer the newer ordering;
/// a tie and conflicting sort choices prefer the chat source.
pub fn merge(borrower: &Snapshot, source: &Snapshot, baseline: Option<&Snapshot>) -> Snapshot {
    let left: BTreeSet<&String> = borrower.pins.iter().collect();
    let right: BTreeSet<&String> = source.pins.iter().collect();
    let old: BTreeSet<&String> = baseline.map(|b| b.pins.iter().collect()).unwrap_or_default();
    let mut pins: BTreeSet<String> = BTreeSet::new();
    for id in left.iter().chain(right.iter()).chain(old.iter()) {
        let a = left.contains(id);
        let b = right.contains(id);
        let pinned = if baseline.is_some() {
            if a == b {
                a
            } else if a == old.contains(id) {
                b
            } else {
                a
            }
        } else {
            a || b
        };
        if pinned {
            pins.insert((*id).clone());
        }
    }
    let preferred = match baseline {
        Some(base) if borrower.order == base.order => source,
        Some(base) if source.order == base.order => borrower,
        _ if borrower.order_time > source.order_time => borrower,
        _ => source,
    };
    let mut seen = BTreeSet::new();
    let order: Vec<String> = preferred
        .order
        .iter()
        .chain(source.order.iter())
        .chain(borrower.order.iter())
        .chain(pins.iter())
        .filter(|id| pins.contains(*id) && seen.insert((*id).clone()))
        .cloned()
        .collect();
    let sort = match baseline {
        Some(base) if source.sort == base.sort => borrower.sort.clone(),
        _ => source.sort.clone(),
    };
    Snapshot {
        pins: pins.into_iter().collect(),
        order,
        sort,
        order_time: borrower.order_time.max(source.order_time),
        scope: None,
        fingerprint: String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn set(items: &[&str]) -> BTreeSet<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    fn strings(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn pinned_chats_merge_the_way_a_person_would_expect() {
        let old = Snapshot::new(&["local_a", "local_b"], &["local_a", "local_b"], "recency");
        let unpinned = Snapshot::new(&["local_b"], &["local_b"], "recency");
        let mut reordered = Snapshot::new(&["local_a", "local_b"], &["local_b", "local_a"], "alpha");
        reordered.order_time = 20.0;

        let first = merge(&unpinned, &old, None);
        assert_eq!(set(&["local_a", "local_b"]), first.pins.iter().cloned().collect(),
            "the first sidebar sync keeps pins brought by either profile");
        assert_eq!(merge(&unpinned, &old, Some(&old)).pins, strings(&["local_b"]),
            "unpinning in the borrower is carried to the source");
        assert_eq!(merge(&old, &unpinned, Some(&old)).pins, strings(&["local_b"]),
            "unpinning in the source is carried to the borrower");
        let none = Snapshot::new(&[], &[], "recency");
        assert!(merge(&none, &old, Some(&old)).pins.is_empty(),
            "unpinning the last chats does not restore them from the other profile");
        assert_eq!(merge(&old, &reordered, Some(&old)).order, reordered.order,
            "a reordered pinned section follows the source");
        assert_eq!(merge(&reordered, &old, Some(&old)).order, reordered.order,
            "a reordered pinned section follows the borrower");
        assert_eq!(merge(&reordered, &old, Some(&old)).sort, "alpha",
            "changing the Code sort mode propagates back to the source");

        let mut other = Snapshot::new(&["local_a", "local_c"], &["local_c", "local_a"], "created");
        other.order_time = 30.0;
        let both = merge(&reordered, &other, Some(&old));
        assert_eq!(both.pins, strings(&["local_a", "local_c"]),
            "a concurrent new pin survives an unpin in the other profile");
        assert_eq!(both.order, strings(&["local_c", "local_a"]),
            "concurrent reorders use the more recently saved order");
        assert_eq!(both.sort, "created", "conflicting sort changes consistently prefer the chat source");
        assert!(merge(&both, &both, Some(&both)).same_choices(&both),
            "another sync does not change an agreed sidebar");
        assert_eq!(old.restricted(&set(&["local_b"])).order, strings(&["local_b"]),
            "a pair only considers chats shared by both profiles");
    }

    #[test]
    fn reordering_shared_pins_preserves_the_positions_of_unrelated_sidebar_entries() {
        let result = replace_shared(
            &strings(&["remote", "local_a", "project", "local_b"]),
            &set(&["local_a", "local_b"]),
            &strings(&["local_b", "local_a"]),
        );
        assert_eq!(result, strings(&["remote", "local_b", "project", "local_a"]));
    }

    #[test]
    fn a_snapshot_written_by_the_mac_build_reads_back_the_same() {
        let text = r#"{"pins":["local_a"],"order":["local_a"],"sort":"alpha","orderTime":3,"fingerprint":""}"#;
        let snapshot: Snapshot = serde_json::from_str(text).unwrap();
        assert_eq!(snapshot.pins, strings(&["local_a"]));
        assert_eq!(snapshot.order_time, 3.0);
        assert_eq!(snapshot.scope, None);
    }
}
