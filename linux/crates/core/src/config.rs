//! A grafted shortcut, as the window keeps it and as its launcher runs it.

use crate::{files, paths};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::path::{Path, PathBuf};
use uuid::Uuid;

/// What one launcher runs: where its profile lives and which profile, if any,
/// it borrows its Claude Code chats from.
///
/// This, in the shortcut's own `graft.json`, is what actually runs. The list
/// the window edits is a description of it, and where the two disagree the
/// launcher wins, because the dock and the app grid run the launcher and ask
/// Graft nothing.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GraftConfig {
    #[serde(rename = "profileDir")]
    pub profile_dir: String,
    #[serde(rename = "sourceDir", default, skip_serializing_if = "Option::is_none")]
    pub source_dir: Option<String>,
}

impl GraftConfig {
    pub fn profile(&self) -> PathBuf {
        PathBuf::from(&self.profile_dir)
    }

    pub fn source(&self) -> Option<PathBuf> {
        self.source_dir.as_deref().filter(|s| !s.is_empty()).map(PathBuf::from)
    }
}

/// The colour a Claude wears in the dock and the tray.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum IconPreset {
    #[default]
    Original,
    Blue,
    Violet,
    Green,
    Red,
    Pink,
    Teal,
    Graphite,
    Work,
    Personal,
    Code,
    Research,
}

impl IconPreset {
    pub const ALL: [IconPreset; 12] = [
        IconPreset::Original,
        IconPreset::Blue,
        IconPreset::Violet,
        IconPreset::Green,
        IconPreset::Red,
        IconPreset::Pink,
        IconPreset::Teal,
        IconPreset::Graphite,
        IconPreset::Work,
        IconPreset::Personal,
        IconPreset::Code,
        IconPreset::Research,
    ];

    /// The order colours are handed out in. Claude's own icon is the main
    /// profile's, so it is never handed out; plain colours go before the
    /// badged ones, which repeat a colour and differ only by the badge.
    pub const HANDED_OUT: [IconPreset; 11] = [
        IconPreset::Blue,
        IconPreset::Violet,
        IconPreset::Green,
        IconPreset::Red,
        IconPreset::Pink,
        IconPreset::Teal,
        IconPreset::Graphite,
        IconPreset::Work,
        IconPreset::Personal,
        IconPreset::Code,
        IconPreset::Research,
    ];

    /// The first colour nothing has yet. One Claude that looked like another
    /// was what made two of them indistinguishable, so a colour is shared only
    /// when a person picks it, or once every one is in use and the least used
    /// comes round.
    pub fn next(taken: &[IconPreset]) -> IconPreset {
        if let Some(free) = Self::HANDED_OUT.iter().find(|p| !taken.contains(p)) {
            return *free;
        }
        let count = |p: &IconPreset| taken.iter().filter(|t| *t == p).count();
        *Self::HANDED_OUT.iter().min_by_key(|p| count(p)).unwrap_or(&IconPreset::Blue)
    }

    pub fn title(self) -> &'static str {
        match self {
            IconPreset::Original => "Original",
            IconPreset::Blue => "Blue",
            IconPreset::Violet => "Violet",
            IconPreset::Green => "Green",
            IconPreset::Red => "Red",
            IconPreset::Pink => "Pink",
            IconPreset::Teal => "Teal",
            IconPreset::Graphite => "Graphite",
            IconPreset::Work => "Work",
            IconPreset::Personal => "Personal",
            IconPreset::Code => "Code",
            IconPreset::Research => "Research",
        }
    }

    /// Hue rotation in radians applied to Claude's icon, `None` for none, and
    /// the saturation it is drawn at.
    pub fn hue_angle(self) -> Option<f64> {
        match self {
            IconPreset::Original | IconPreset::Graphite => None,
            IconPreset::Red => Some(-0.45),
            IconPreset::Pink | IconPreset::Research => Some(-0.95),
            IconPreset::Blue | IconPreset::Work => Some(-2.8),
            IconPreset::Violet | IconPreset::Personal => Some(-1.75),
            IconPreset::Teal => Some(2.8),
            IconPreset::Green | IconPreset::Code => Some(2.25),
        }
    }

    pub fn saturation(self) -> f64 {
        if self == IconPreset::Graphite {
            0.0
        } else {
            1.0
        }
    }

    /// The accent as sRGB, matching the system colours the Mac build uses.
    pub fn accent_rgb(self) -> (f64, f64, f64) {
        match self {
            IconPreset::Original => (0.85, 0.36, 0.24),
            IconPreset::Red => (1.0, 0.231, 0.188),
            IconPreset::Pink | IconPreset::Research => (1.0, 0.176, 0.333),
            IconPreset::Blue | IconPreset::Work => (0.0, 0.478, 1.0),
            IconPreset::Violet | IconPreset::Personal => (0.686, 0.322, 0.871),
            IconPreset::Teal => (0.188, 0.69, 0.78),
            IconPreset::Green | IconPreset::Code => (0.204, 0.78, 0.349),
            IconPreset::Graphite => (0.557, 0.557, 0.576),
        }
    }

    /// The symbolic icon name drawn as a badge, from the Adwaita icon set.
    pub fn badge_icon(self) -> Option<&'static str> {
        match self {
            IconPreset::Work => Some("briefcase-symbolic"),
            IconPreset::Personal => Some("avatar-default-symbolic"),
            IconPreset::Code => Some("utilities-terminal-symbolic"),
            IconPreset::Research => Some("system-search-symbolic"),
            _ => None,
        }
    }
}

/// Where a shortcut reads its Claude Code chats from. Encoded the way the Mac
/// build's list encodes it, so a list carried between the two reads the same.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum Source {
    Own,
    #[default]
    Main,
    Shortcut(Uuid),
}

impl Serialize for Source {
    fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        let value = match self {
            Source::Own => serde_json::json!({"own": {}}),
            Source::Main => serde_json::json!({"main": {}}),
            Source::Shortcut(id) => {
                serde_json::json!({"shortcut": {"_0": id.to_string().to_uppercase()}})
            }
        };
        value.serialize(s)
    }
}

impl<'de> Deserialize<'de> for Source {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let value = Value::deserialize(d)?;
        let object = value.as_object().ok_or_else(|| serde::de::Error::custom("source"))?;
        if object.contains_key("own") {
            return Ok(Source::Own);
        }
        if object.contains_key("main") {
            return Ok(Source::Main);
        }
        if let Some(inner) = object.get("shortcut") {
            let id = inner
                .get("_0")
                .and_then(Value::as_str)
                .and_then(|s| Uuid::parse_str(s).ok())
                .ok_or_else(|| serde::de::Error::custom("shortcut source"))?;
            return Ok(Source::Shortcut(id));
        }
        Err(serde::de::Error::custom("unknown source"))
    }
}

/// One extra Claude: a name, a profile folder of its own, and where its
/// Claude Code chats come from.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Shortcut {
    #[serde(with = "upper_uuid")]
    pub id: Uuid,
    pub name: String,
    pub folder: String,
    #[serde(default)]
    pub source: Source,
    #[serde(rename = "iconPreset", default)]
    pub icon_preset: IconPreset,
    /// Name the shortcut was last installed under, so a rename can clean up.
    #[serde(rename = "installedName", default, skip_serializing_if = "Option::is_none")]
    pub installed_name: Option<String>,
    /// The account this shortcut has been told to stop asking about when it is
    /// opened. It silences the question, not the offer.
    #[serde(rename = "stopAskingChatsFor", default, skip_serializing_if = "Option::is_none")]
    pub stop_asking_chats_for: Option<String>,
}

mod upper_uuid {
    use serde::{Deserialize, Deserializer, Serializer};
    use uuid::Uuid;

    pub fn serialize<S: Serializer>(id: &Uuid, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&id.to_string().to_uppercase())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Uuid, D::Error> {
        let text = String::deserialize(d)?;
        Uuid::parse_str(&text).map_err(serde::de::Error::custom)
    }
}

impl Shortcut {
    pub fn new(name: &str, folder: Option<&str>, source: Source, icon: IconPreset) -> Shortcut {
        Shortcut {
            id: Uuid::new_v4(),
            name: name.to_string(),
            folder: folder.map(str::to_string).unwrap_or_else(|| Self::folder_name(name)),
            source,
            icon_preset: icon,
            installed_name: None,
            stop_asking_chats_for: None,
        }
    }

    /// "Work Account" -> "Claude-Work-Account", "Claude 2" -> "Claude-2".
    pub fn folder_name(name: &str) -> String {
        let mut words: Vec<&str> =
            name.split(|c: char| !c.is_alphanumeric()).filter(|w| !w.is_empty()).collect();
        if words.first().map(|w| w.to_lowercase() == "claude").unwrap_or(false) {
            words.remove(0);
        }
        let cleaned = words.join("-");
        format!("Claude-{}", if cleaned.is_empty() { "Profile" } else { &cleaned })
    }

    pub fn profile_dir(&self) -> PathBuf {
        paths::profile_dir(&self.folder)
    }
}

/// The shortcut list as saved, or empty when there is none or it will not
/// parse. Only the window writes it; everything else reads it.
pub fn load_shortcuts() -> Vec<Shortcut> {
    match files::read_json(&paths::shortcuts_file()) {
        files::JsonRead::Value(value) => serde_json::from_value(value).unwrap_or_default(),
        _ => Vec::new(),
    }
}

pub fn save_shortcuts(list: &[Shortcut]) -> std::io::Result<()> {
    let value = serde_json::to_value(list).map_err(std::io::Error::other)?;
    files::write_json(&paths::shortcuts_file(), &value)
}

/// The folder of every shortcut this app has made, read from the list as
/// loose JSON rather than decoded, so a launcher reading a list written by a
/// newer Graft still finds every folder in it.
pub fn shortcut_profiles() -> Vec<PathBuf> {
    let files::JsonRead::Value(Value::Array(list)) = files::read_json(&paths::shortcuts_file())
    else {
        return Vec::new();
    };
    list.iter()
        .filter_map(|item| item.get("folder").and_then(Value::as_str))
        .filter(|folder| paths::validate_folder(folder).is_none())
        .map(paths::profile_dir)
        .collect()
}

/// Every profile in `~/.config` with a Claude Code chat store, Claude's own
/// and grafted ones and any other Claude profile a person made by hand.
pub fn session_store_profiles() -> Vec<PathBuf> {
    let root = paths::config_home();
    files::listing(&root)
        .unwrap_or_default()
        .into_iter()
        .map(|name| root.join(name))
        .filter(|p| p.join("claude-code-sessions").is_dir())
        .collect()
}

/// The profiles a sweep may write a record into: whatever the caller named,
/// Claude's own, and the ones this app made. A Claude profile this app did not
/// make belongs to whatever made it, and is read but never written.
pub fn record_filing_profiles(named: &[PathBuf]) -> Vec<PathBuf> {
    let mut seen: Vec<PathBuf> = Vec::new();
    let mut out = Vec::new();
    for profile in named.iter().cloned().chain([paths::main_profile()]).chain(shortcut_profiles()) {
        let key = paths::normalize(&profile);
        if !seen.contains(&key) {
            seen.push(key);
            out.push(profile);
        }
    }
    out
}

/// Whether a path is one of the profiles this app made.
pub fn is_shortcut_profile(profile: &Path) -> bool {
    shortcut_profiles().iter().any(|p| paths::same_path(p, profile))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folder_names_drop_a_leading_claude() {
        assert_eq!(Shortcut::folder_name("Work Account"), "Claude-Work-Account");
        assert_eq!(Shortcut::folder_name("Claude 2"), "Claude-2");
        assert_eq!(Shortcut::folder_name("!!"), "Claude-Profile");
    }

    #[test]
    fn a_source_round_trips_in_the_mac_shape() {
        let id = Uuid::new_v4();
        let text = serde_json::to_string(&Source::Shortcut(id)).unwrap();
        assert!(text.contains("\"_0\""), "a shortcut source is keyed as the Mac build keys it");
        assert_eq!(serde_json::from_str::<Source>(&text).unwrap(), Source::Shortcut(id));
        assert_eq!(serde_json::from_str::<Source>("{\"own\":{}}").unwrap(), Source::Own);
    }

    #[test]
    fn colours_go_to_the_first_free_one_then_the_least_used() {
        assert_eq!(IconPreset::next(&[]), IconPreset::Blue);
        assert_eq!(IconPreset::next(&[IconPreset::Blue]), IconPreset::Violet);
        let mut all = IconPreset::HANDED_OUT.to_vec();
        all.push(IconPreset::Blue);
        assert_eq!(IconPreset::next(&all), IconPreset::Violet, "once all are used the least used comes round");
    }

    #[test]
    fn a_list_entry_missing_newer_fields_still_decodes() {
        let text = r#"[{"id":"7C2B3A1E-0000-4000-8000-000000000001","name":"Claude 2","folder":"Claude-2"}]"#;
        let list: Vec<Shortcut> = serde_json::from_str(text).unwrap();
        assert_eq!(list[0].source, Source::Main, "a missing source reads as the main Claude's chats");
        assert_eq!(list[0].icon_preset, IconPreset::Original);
    }
}
