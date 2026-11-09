//! The list of shortcuts the window edits, and what it can say about them
//! without asking the desktop or the process list.

use crate::installer;
use crate::profile::delete_profile;
use crate::sharing::Sharing;
use graft_core::{config, paths, IconPreset, Shortcut, Source};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use uuid::Uuid;

/// Another profile reading the same chat store, by the name a person knows
/// it by.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Neighbour {
    pub name: String,
    pub profile: PathBuf,
}

/// The shortcut list. Every change is saved as it is made, atomically and
/// with sorted keys (`config::save_shortcuts`): launchers read this file to
/// learn which profiles are this app's doing, and one that read a half-written
/// file would file records into fewer profiles than it should.
#[derive(Debug, Default)]
pub struct ShortcutStore {
    shortcuts: Vec<Shortcut>,
    /// Shortcuts already asked about the chats another profile holds for them
    /// while this run of the app has been up. Deliberately not saved:
    /// answering for good is a button of its own, and this only stops a second
    /// press of Open asking a second time.
    pub asked_about_chats: HashSet<Uuid>,
}

impl ShortcutStore {
    /// The list as saved, or an empty one when there is none.
    pub fn load() -> ShortcutStore {
        ShortcutStore { shortcuts: config::load_shortcuts(), asked_about_chats: HashSet::new() }
    }

    pub fn shortcuts(&self) -> &[Shortcut] {
        &self.shortcuts
    }

    pub fn set_shortcuts(&mut self, list: Vec<Shortcut>) {
        self.shortcuts = list;
        self.save();
    }

    /// Change one shortcut and save the list. False when there is no such
    /// shortcut.
    pub fn update(&mut self, id: Uuid, change: impl FnOnce(&mut Shortcut)) -> bool {
        let Some(shortcut) = self.shortcuts.iter_mut().find(|s| s.id == id) else { return false };
        change(shortcut);
        self.save();
        true
    }

    fn save(&self) {
        let _ = config::save_shortcuts(&self.shortcuts);
    }

    pub fn shortcut(&self, id: Uuid) -> Option<&Shortcut> {
        self.shortcuts.iter().find(|s| s.id == id)
    }

    /// A new shortcut on its own chats, under the next free name, in the first
    /// colour nothing wears. The colour is set once, here: an existing Claude
    /// keeps the colour it has, so nobody's dock changes under them.
    /// `colours_in_use` is every colour a Claude or a lent account wears.
    pub fn add_new(&mut self, colours_in_use: &[IconPreset]) -> Shortcut {
        let shortcut = Shortcut::new(&self.unique_name("Claude"), None, Source::Own, IconPreset::next(colours_in_use));
        self.shortcuts.push(shortcut.clone());
        self.save();
        shortcut
    }

    /// What Create and Update write back once the installer has succeeded:
    /// the name and folder that are now on disk, and the name the entry was
    /// installed under, which is what makes the shortcut no longer a draft.
    pub fn record_installed(&mut self, id: Uuid, name: &str, folder: &str) -> bool {
        self.update(id, |s| {
            s.name = name.to_string();
            s.folder = folder.to_string();
            s.installed_name = Some(name.to_string());
        })
    }

    /// What to call a profile on screen. Claude's own has no shortcut standing
    /// for it, and a folder name is not what anybody calls either of them.
    pub fn name_of_profile(&self, profile: &Path) -> String {
        if paths::is_main_profile(profile) {
            return "Claude".into();
        }
        self.shortcuts
            .iter()
            .find(|s| paths::same_path(&s.profile_dir(), profile))
            .map(|s| s.name.clone())
            .unwrap_or_else(|| profile.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default())
    }

    /// Remove a shortcut and the entry it installed. The profile folder, a
    /// login and a chat history, only goes when explicitly asked for, and
    /// never while another shortcut still points at it. Returns a message when
    /// the profile could not be removed.
    pub fn delete(&mut self, id: Uuid, deleting_profile: bool, sharing: &dyn Sharing) -> Option<String> {
        let shortcut = self.shortcut(id)?.clone();
        installer::uninstall(&shortcut, &self.shortcuts);

        // Ignoring case as well as by where they lead: `Claude-Work` and
        // `claude-work` look like one profile, and on a case-folding drive
        // are one, so deleting either one's would take the other's.
        let shared_with_another = self.shortcuts.iter().any(|o| {
            o.id != id
                && (paths::is_named(&o.folder, &shortcut.folder)
                    || paths::same_path(&o.profile_dir(), &shortcut.profile_dir()))
        });

        let mut problem = None;
        if deleting_profile {
            if shared_with_another {
                problem = Some("The profile folder was kept: another shortcut still uses it.".to_string());
            } else if let Err(e) = delete_profile(&shortcut.profile_dir(), sharing) {
                problem = Some(e.to_string());
            }
        }

        // Nothing runs this shortcut's launcher again, so nothing is left to
        // undo its mirroring for it. The copies stay, since they are chats and
        // the person kept the folder, but the two folders stop being squared
        // up against each other.
        if !shared_with_another {
            sharing.forget_mirrors(&shortcut.profile_dir());
        }
        self.shortcuts.retain(|s| s.id != id);

        // Anything that borrowed from it would otherwise fall back to its own
        // chats on its next launch without a word; saying so in the list is
        // what the window then shows.
        for other in &mut self.shortcuts {
            if other.source == Source::Shortcut(id) {
                other.source = Source::Own;
            }
        }
        self.save();
        problem
    }

    /// Never created, and holding no data: safe to drop without asking. The
    /// entry is looked for last, since finding none means reading every
    /// launcher on the machine.
    pub fn is_draft(&self, shortcut: &Shortcut) -> bool {
        shortcut.installed_name.is_none()
            && !shortcut.profile_dir().exists()
            && installer::installed_entry(shortcut, &self.shortcuts).is_none()
    }

    /// Where a shortcut actually reads its chats from, following one hop.
    pub fn source_dir(&self, shortcut: &Shortcut) -> Option<PathBuf> {
        match shortcut.source {
            Source::Own => None,
            Source::Main => Some(paths::main_profile()),
            Source::Shortcut(id) => self.shortcut(id).map(Shortcut::profile_dir),
        }
    }

    /// Every shortcut with the source it reads from, as the repairs at start
    /// take them.
    pub fn with_sources(&self) -> Vec<(Shortcut, Option<PathBuf>)> {
        self.shortcuts.iter().map(|s| (s.clone(), self.source_dir(s))).collect()
    }

    /// The profile whose chat store a shortcut ends up reading, following the
    /// chain of sources to its end.
    pub fn chat_root(&self, shortcut: &Shortcut) -> PathBuf {
        let mut seen: HashSet<Uuid> = HashSet::new();
        let mut current = shortcut.clone();
        loop {
            match current.source {
                Source::Own => return current.profile_dir(),
                Source::Main => return paths::main_profile(),
                Source::Shortcut(id) => {
                    let next = self.shortcut(id).cloned();
                    match next {
                        Some(next) if seen.insert(current.id) => current = next,
                        _ => return current.profile_dir(),
                    }
                }
            }
        }
    }

    /// Everything else reading the same chat store. Whether any of them is
    /// open is a separate, slower question, asked off the main thread.
    ///
    /// `None` asks on behalf of Claude's own profile, which has no shortcut to
    /// stand for it and was therefore the one profile that could never find
    /// out who else was on its chats: the case the question gets asked about
    /// most, since every shortcut grafted from main reads exactly those files.
    pub fn chat_store_neighbours(&self, shortcut: Option<&Shortcut>) -> Vec<Neighbour> {
        let root = shortcut.map(|s| self.chat_root(s)).unwrap_or_else(paths::main_profile);
        let mut found = Vec::new();
        if shortcut.is_some() && paths::same_path(&root, &paths::main_profile()) {
            found.push(Neighbour { name: "Claude".into(), profile: paths::main_profile() });
        }
        for other in &self.shortcuts {
            if Some(other.id) == shortcut.map(|s| s.id) {
                continue;
            }
            if paths::same_path(&self.chat_root(other), &root) {
                found.push(Neighbour { name: other.name.clone(), profile: other.profile_dir() });
            }
        }
        found
    }

    pub fn label(&self, source: Source) -> String {
        match source {
            Source::Own => "Its own chats".into(),
            Source::Main => "Main Claude".into(),
            Source::Shortcut(id) => {
                self.shortcut(id).map(|s| s.name.clone()).unwrap_or_else(|| "Removed shortcut".into())
            }
        }
    }

    /// Sources that will not form a loop back to `shortcut`.
    pub fn available_sources(&self, shortcut: &Shortcut) -> Vec<Source> {
        let mut options = vec![Source::Own, Source::Main];
        for other in &self.shortcuts {
            if other.id != shortcut.id && !self.leads_back(other.id, shortcut.id, 0) {
                options.push(Source::Shortcut(other.id));
            }
        }
        options
    }

    fn leads_back(&self, start: Uuid, target: Uuid, depth: usize) -> bool {
        if depth > self.shortcuts.len() {
            return false;
        }
        let Some(node) = self.shortcut(start) else { return false };
        match node.source {
            Source::Shortcut(next) => next == target || self.leads_back(next, target, depth + 1),
            _ => false,
        }
    }

    /// Numbering starts at two, since the stock app is the first one.
    pub fn unique_name(&self, base: &str) -> String {
        let mut n = 2;
        loop {
            let candidate = format!("{base} {n}");
            if !self.shortcuts.iter().any(|s| s.name == candidate) {
                return candidate;
            }
            n += 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::installer::tests::Recorder;
    use crate::sharing::NoSharing;
    use crate::testing::{make_profile, World};

    fn named(name: &str, folder: &str, source: Source) -> Shortcut {
        Shortcut::new(name, Some(folder), source, IconPreset::Original)
    }

    #[test]
    fn deleting_a_profile_another_shortcut_uses_keeps_it() {
        let _world = World::new();
        let _main = make_profile("Claude", Some("AAAA"));
        let work = make_profile("Claude-Work", Some("BBBB"));
        let keeper = named("Work", "Claude-Work", Source::Main);
        let same_folder = named("Work Copy", "Claude-Work", Source::Main);
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![keeper.clone(), same_folder.clone()]);

        assert!(store.delete(keeper.id, true, &NoSharing).is_some(), "deleting a profile another shortcut uses is reported");
        assert!(work.exists(), "and the profile survives");
        assert_eq!(store.shortcuts().len(), 1, "the shortcut itself is still removed");

        assert_eq!(store.delete(same_folder.id, true, &NoSharing), None, "the last shortcut may delete its profile");
        assert!(!work.exists(), "the profile folder is gone");
        assert!(store.shortcuts().is_empty(), "and so is the shortcut");

        let kept = make_profile("Claude-Kept", Some("CCCC"));
        let keep = named("Keep", "Claude-Kept", Source::Own);
        store.set_shortcuts(vec![keep.clone()]);
        store.delete(keep.id, false, &NoSharing);
        assert!(kept.exists(), "deleting the shortcut alone keeps the profile");

        let cased = make_profile("Claude-Cased", Some("DDDD"));
        let upper = named("Cased", "Claude-Cased", Source::Own);
        let lower = named("cased", "claude-cased", Source::Own);
        store.set_shortcuts(vec![upper, lower.clone()]);
        assert!(store.delete(lower.id, true, &NoSharing).is_some(), "a shortcut whose folder differs from another's only in case is sharing it");
        assert!(cased.exists(), "so the profile both read survives");
    }

    #[test]
    fn deleting_a_shortcut_named_after_another_launcher_spares_that_launcher() {
        let _world = World::new();
        let pages = named("Pages", "Claude-Pages", Source::Own);
        let foreign = installer::entry_path(pages.id);
        std::fs::create_dir_all(foreign.parent().unwrap()).unwrap();
        std::fs::write(&foreign, "[Desktop Entry]\nName=Pages\nExec=pages\n").unwrap();
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![pages.clone()]);
        store.delete(pages.id, false, &NoSharing);
        assert!(foreign.exists(), "deleting a shortcut spares a launcher Graft did not write");
        assert!(store.shortcuts().is_empty(), "the shortcut is still removed from the list");
    }

    #[test]
    fn deleting_a_shortcut_stops_its_folders_being_squared_up_but_keeps_its_chats() {
        let _world = World::new();
        let src = make_profile("Claude-Bye-Src", Some("BYSRC"));
        let bye = make_profile("Claude-Bye", Some("BYOWN"));
        let shortcut = named("Bye", "Claude-Bye", Source::Own);
        let sharing = Recorder::default();
        installer::install(&shortcut, Some(&src), None, &[], &sharing).unwrap();
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![shortcut.clone()]);
        store.delete(shortcut.id, false, &sharing);
        assert_eq!(sharing.forgotten.borrow().as_slice(), std::slice::from_ref(&bye), "deleting the shortcut stops the two folders being squared up");
        assert!(bye.exists(), "and the folder, with whatever it holds, is left where it is");
        assert!(installer::installed_entry(&shortcut, &[]).is_none(), "while its entry is gone");
    }

    #[test]
    fn deleting_a_source_leaves_its_followers_on_their_own_chats() {
        let _world = World::new();
        let target = named("Target", "Claude-T", Source::Own);
        let follower = named("Follower", "Claude-F", Source::Shortcut(target.id));
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![target.clone(), follower]);
        store.delete(target.id, false, &NoSharing);
        assert_eq!(store.shortcuts()[0].source, Source::Own, "deleting a source leaves its followers on their own chats, not silently un-grafted");
        assert_eq!(ShortcutStore::load().shortcuts()[0].source, Source::Own, "and the list on disk says so too");
    }

    #[test]
    fn names_are_suggested_from_two_and_sources_never_loop() {
        let _world = World::new();
        let mut store = ShortcutStore::default();
        assert_eq!(store.unique_name("Claude"), "Claude 2", "the first suggestion is Claude 2");
        store.set_shortcuts(vec![Shortcut::new("Claude 2", None, Source::Main, IconPreset::Original)]);
        assert_eq!(store.unique_name("Claude"), "Claude 3", "the next one steps past it");

        let mut a = Shortcut::new("A", None, Source::Main, IconPreset::Original);
        let b = Shortcut::new("B", None, Source::Shortcut(a.id), IconPreset::Original);
        store.set_shortcuts(vec![a.clone(), b.clone()]);
        assert!(!store.available_sources(&b).contains(&Source::Shortcut(b.id)), "a shortcut is not offered itself as a source");
        a.source = Source::Shortcut(b.id);
        store.set_shortcuts(vec![a.clone(), b.clone()]);
        assert!(!store.available_sources(&b).contains(&Source::Shortcut(a.id)), "nor one that would close a loop");
    }

    #[test]
    fn a_new_shortcut_reads_its_own_chats_in_a_colour_nobody_wears() {
        let _world = World::new();
        let mut store = ShortcutStore::default();
        let first = store.add_new(&[IconPreset::Blue]);
        assert_eq!(first.source, Source::Own, "a new Claude reads its own chats");
        assert_eq!(first.icon_preset, IconPreset::Violet, "in the first colour nothing else wears");
        assert_eq!(first.name, "Claude 2");
        assert!(store.is_draft(&first), "and until it is created it is a draft");
        store.record_installed(first.id, "Claude 2", &first.folder);
        assert!(!store.is_draft(store.shortcut(first.id).unwrap()), "after which it is not");
    }

    #[test]
    fn everything_on_one_chat_store_is_a_neighbour_of_everything_else_on_it() {
        let _world = World::new();
        let from_main = named("From Main", "Claude-A", Source::Main);
        let also_main = named("Also Main", "Claude-B", Source::Main);
        let alone = named("Alone", "Claude-C", Source::Own);
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![from_main.clone(), also_main, alone.clone()]);

        let names = |n: Vec<Neighbour>| n.into_iter().map(|n| n.name).collect::<Vec<_>>();
        let neighbours = names(store.chat_store_neighbours(Some(&from_main)));
        assert!(neighbours.contains(&"Claude".to_string()), "the main profile counts as a neighbour");
        assert!(neighbours.contains(&"Also Main".to_string()), "so does another shortcut on the same source");
        assert!(!neighbours.contains(&"Alone".to_string()), "one with its own chats does not");
        assert!(!neighbours.contains(&"From Main".to_string()), "and neither does the shortcut itself");
        assert!(store.chat_store_neighbours(Some(&alone)).is_empty(), "a profile on its own has no neighbours");

        let chained = named("Chained", "Claude-D", Source::Shortcut(from_main.id));
        let mut list = store.shortcuts().to_vec();
        list.push(chained.clone());
        store.set_shortcuts(list);
        assert!(paths::same_path(&store.chat_root(&chained), &paths::main_profile()), "a chain of sources resolves to the profile at its end");
        assert!(names(store.chat_store_neighbours(Some(&chained))).contains(&"From Main".to_string()), "so everything along it shares the same store");

        let of_main = names(store.chat_store_neighbours(None));
        assert!(of_main.contains(&"From Main".to_string()) && of_main.contains(&"Also Main".to_string()), "Claude's own profile can ask who else is on its chats");
        assert!(!of_main.contains(&"Claude".to_string()), "without counting itself among them");
        assert!(!of_main.contains(&"Alone".to_string()), "and a profile keeping its own chats is still nobody's neighbour");
    }

    #[test]
    fn a_profile_is_called_by_the_name_a_person_knows_it_by() {
        let _world = World::new();
        let work = named("Work", "Claude-Work", Source::Own);
        let mut store = ShortcutStore::default();
        store.set_shortcuts(vec![work.clone()]);
        assert_eq!(store.name_of_profile(&paths::main_profile()), "Claude");
        assert_eq!(store.name_of_profile(&work.profile_dir()), "Work");
        assert_eq!(store.name_of_profile(&paths::profile_dir("Claude-Other")), "Claude-Other", "and a profile nothing lists by its folder");
        assert_eq!(store.label(Source::Shortcut(uuid::Uuid::new_v4())), "Removed shortcut");
    }
}
