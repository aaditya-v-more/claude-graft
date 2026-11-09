use super::*;
use crate::sharing::NoSharing;
use crate::testing::{make_profile, World};
use graft_core::{IconPreset, Source};
use std::cell::RefCell;

#[derive(Default)]
pub(crate) struct Recorder {
    pub applied: RefCell<Vec<GraftConfig>>,
    pub forgotten: RefCell<Vec<PathBuf>>,
}

impl Sharing for Recorder {
    fn apply(&self, config: &GraftConfig) {
        self.applied.borrow_mut().push(config.clone());
    }
    fn forget_mirrors(&self, profile: &Path) {
        self.forgotten.borrow_mut().push(profile.to_path_buf());
    }
}

fn shortcut(name: &str, source: Source) -> Shortcut {
    Shortcut::new(name, None, source, IconPreset::Original)
}

fn foreign_entry(at: &Path) {
    std::fs::create_dir_all(at.parent().unwrap()).unwrap();
    std::fs::write(at, "[Desktop Entry]\nType=Application\nName=Pages\nExec=pages\n").unwrap();
}

fn foreign_intact(at: &Path) -> bool {
    std::fs::read_to_string(at).map(|t| t.contains("Exec=pages")).unwrap_or(false)
}

fn source_of(entry: &Path) -> Option<String> {
    described_config(entry).and_then(|c| c.source_dir)
}

fn entries_in_applications() -> Vec<String> {
    files::listing(&applications_dir()).unwrap_or_default()
}

fn age(entry: &Path) {
    let text = std::fs::read_to_string(entry).unwrap();
    let aged = text
        .replace(&format!("{VERSION_KEY}={}", graft_version()), &format!("{VERSION_KEY}=0.0.1"))
        .lines()
        .map(|l| if l.starts_with("Exec=") { "Exec=/old/graft-launch stale".to_string() } else { l.to_string() })
        .collect::<Vec<_>>()
        .join("\n");
    std::fs::write(entry, aged).unwrap();
}

#[test]
fn an_entry_graft_did_not_write_is_never_taken_for_one_of_its_own() {
    let _world = World::new();
    let pages = shortcut("Pages", Source::Own);
    let foreign = entry_path(pages.id);
    foreign_entry(&foreign);

    assert!(installed_entry(&pages, &[]).is_none(), "a launcher Graft did not create is not seen as installed");
    assert_eq!(
        install(&pages, None, None, &[], &NoSharing),
        Err(InstallError::NameTaken(foreign.clone())),
        "installing over another launcher is refused"
    );
    assert!(foreign_intact(&foreign), "that launcher is untouched");
    uninstall(&pages, &[]);
    assert!(foreign_intact(&foreign), "uninstall leaves a foreign launcher of the same file name alone");

    for name in ["Claude", "Claude Graft", "claude"] {
        assert!(
            matches!(install(&shortcut(name, Source::Own), None, None, &[], &NoSharing), Err(InstallError::ReservedName(_))),
            "the name {name} is refused outright"
        );
    }
    assert_eq!(install(&shortcut("  ", Source::Own), None, None, &[], &NoSharing), Err(InstallError::MissingName), "and so is no name at all");
}

#[test]
fn an_installed_shortcut_is_found_kept_in_step_and_renamed_in_place() {
    let world = World::new();
    let main = make_profile("Claude", Some("AAAA"));
    let mut work = shortcut("Work", Source::Main);
    let sharing = Recorder::default();

    let entry = install(&work, Some(&main), None, &[], &sharing).unwrap();
    assert_eq!(entry, entry_path(work.id), "the entry is written under the shortcut's id");
    let text = std::fs::read_to_string(&entry).unwrap();
    let launcher = world.sandbox.root.path().join("bin/graft-launch");
    assert!(text.contains(&format!("Exec={} {}", launcher.display(), work.id.hyphenated())), "it runs the launcher for this shortcut: {text}");
    assert!(text.contains(&format!("StartupWMClass=ClaudeGraft.{}", work.id.hyphenated())), "under a window class of its own");
    assert!(text.contains(&format!("Icon={}", icon_path(work.id).display())), "with its own icon");
    assert!(icon::is_readable_png(&icon_path(work.id)), "which is a PNG the desktop can show");
    assert_eq!(built_by(&entry).as_deref(), Some(graft_version()), "stamped with the version of Graft that wrote it, which is how a stale one is spotted");
    assert!(!refresh_launcher(&work, &[]), "so a shortcut this version wrote is left alone");

    // An entry left behind by an older Graft goes on reaching the launcher
    // the way that Graft did, since the dock runs it and asks nothing.
    age(&entry);
    assert_eq!(built_by(&entry).as_deref(), Some("0.0.1"), "an entry remembers which Graft wrote it");
    assert!(refresh_launcher(&work, &[]), "and an older one is brought up to date");
    assert_eq!(built_by(&entry).as_deref(), Some(graft_version()), "restamped, so the next start leaves it alone");
    assert!(!std::fs::read_to_string(&entry).unwrap().contains("stale"), "reaching this version's launcher rather than the old one");
    assert_eq!(source_of(&entry), Some(paths::text(&main)), "and nothing else about the shortcut was rewritten");

    assert!(is_graft_entry(&entry), "the entry is recognisable as ours");
    assert_eq!(installed_entry(&work, &[]), Some(entry.clone()), "it is found again afterwards");
    assert!(work.profile_dir().is_dir(), "the profile folder was created");
    assert_eq!(sharing.applied.borrow().last().and_then(|c| c.source_dir.clone()), Some(paths::text(&main)),
        "and with the profile closed its description was put into effect at once");

    let again = install(&work, Some(&main), None, &[], &sharing).unwrap();
    assert_eq!(again, entry, "updating writes to the same entry");
    assert_eq!(entries_in_applications().len(), 1, "updating does not leave a second copy behind");

    install(&work, None, None, &[], &sharing).unwrap();
    assert_eq!(source_of(&entry), None, "switching to its own chats clears the source");
    assert_eq!(sharing.applied.borrow().last().map(|c| c.source_dir.clone()), Some(None), "and hands the borrowed chats back");

    work.name = "Work Two".into();
    let renamed = install(&work, Some(&main), None, &[], &sharing).unwrap();
    assert_eq!(renamed, entry, "a rename keeps the file, which is named by id");
    assert_eq!(DesktopEntry::read(&renamed).unwrap().get("Name").as_deref(), Some("Work Two"), "and shows the new name");

    // Renamed by hand, the entry is still this shortcut's, and every repair
    // made at start has to reach it.
    let by_hand = applications_dir().join("moved-by-hand.desktop");
    std::fs::rename(&renamed, &by_hand).unwrap();
    assert_eq!(installed_entry(&work, &[]), Some(by_hand.clone()), "an entry renamed by hand is found by the profile it opens");
    age(&by_hand);
    assert!(refresh_launcher(&work, &[]) && built_by(&by_hand).as_deref() == Some(graft_version()), "so it is brought up to date at start");
    assert!(refresh_config(&work, None, &[]) && source_of(&by_hand).is_none(), "and so is the source it borrows from");

    // Two shortcuts on one folder each have an entry describing it, so a
    // guess by profile would hand one shortcut's entry to the other's Delete.
    let twin = Shortcut::new("Twin", Some(&work.folder), Source::Own, IconPreset::Original);
    let both = [work.clone(), twin.clone()];
    assert!(installed_entry(&twin, &both).is_none(), "a profile two shortcuts share is not searched for by what it opens");
    uninstall(&twin, &both);
    assert!(by_hand.exists(), "so deleting one of them leaves the other's renamed entry alone");

    let mut edited = DesktopEntry::read(&by_hand).unwrap();
    edited.set("X-Added-By-Hand", "kept");
    std::fs::write(&by_hand, edited.text()).unwrap();
    let back = install(&work, Some(&main), None, &[], &sharing).unwrap();
    assert!(back == entry && !by_hand.exists(), "Update puts the entry back under the shortcut's own file, with no second copy");
    assert_eq!(entries_in_applications().len(), 1, "and only that one");
    assert!(icon_path(work.id).exists() && config_path(work.id).exists(), "keeping the icon and description it now names");
    assert_eq!(DesktopEntry::read(&back).unwrap().get("X-Added-By-Hand").as_deref(), Some("kept"), "and a key somebody added by hand");

    // A new folder typed into the window is a different profile, and nothing
    // describes it yet; the entry to replace is the one the old folder opens.
    let mut moved = work.clone();
    moved.folder = "Claude-Work-Moved".into();
    let refolded = install(&moved, None, Some(&work), &[], &sharing).unwrap();
    assert!(refolded == back && described_profile(&refolded).map(|p| paths::same_path(&p, &moved.profile_dir())) == Some(true),
        "Update applies a new folder to the same entry rather than making another");
    install(&work, Some(&main), Some(&moved), &[], &sharing).unwrap();

    // Gone with its shortcut wherever it went, or it is left in the app grid
    // still naming a source, and opening it grafts a profile nothing lists.
    let hidden = applications_dir().join("hidden-away.desktop");
    std::fs::rename(entry_path(work.id), &hidden).unwrap();
    uninstall(&work, std::slice::from_ref(&work));
    assert!(!hidden.exists(), "deleting a shortcut removes its entry even after a rename by hand");
    assert!(!icon_path(work.id).exists() && !config_path(work.id).exists(), "and its icon and description with it");
    assert!(work.profile_dir().exists(), "but never its profile");
}

#[test]
fn a_menu_editor_s_copy_goes_with_the_shortcut() {
    let _world = World::new();
    let work = shortcut("Copied", Source::Own);
    let entry = install(&work, None, None, &[], &NoSharing).unwrap();
    let copy = applications_dir().join("menulibre-copied.desktop");
    std::fs::copy(&entry, &copy).unwrap();
    uninstall(&work, &[]);
    assert!(!entry.exists() && !copy.exists(), "a copy naming the same description is the same shortcut");
}

#[test]
fn a_preset_is_drawn_into_the_icon_and_a_failed_icon_changes_nothing() {
    let world = World::new();
    let mut styled = Shortcut::new("Styled Icon", None, Source::Own, IconPreset::Work);
    let entry = install(&styled, None, None, &[], &NoSharing).unwrap();
    let icon = icon_path(styled.id);
    let work_icon = std::fs::read(&icon).unwrap();
    let decoded = image::load_from_memory(&work_icon).unwrap();
    assert_eq!((decoded.width(), decoded.height()), (256, 256), "a preset is written as a 256 pixel icon where hicolor looks");

    styled.icon_preset = IconPreset::Blue;
    install(&styled, None, None, &[], &NoSharing).unwrap();
    assert_ne!(std::fs::read(&icon).unwrap(), work_icon, "updating the shortcut replaces the badged icon with the newly selected preset");

    let sealed = [entry.clone(), config_path(styled.id), icon.clone()];
    let before: Vec<Vec<u8>> = sealed.iter().map(|p| std::fs::read(p).unwrap()).collect();
    icon::set_source_override(Some(world.sandbox.root.path().join("unreadable-icon.png")));
    let mut changed = styled.clone();
    changed.folder = "Claude-Failed-Icon".into();
    assert_eq!(install(&changed, None, None, &[], &NoSharing), Err(InstallError::MissingIcon), "an unreadable icon refuses the update");
    let after: Vec<Vec<u8>> = sealed.iter().map(|p| std::fs::read(p).unwrap()).collect();
    assert_eq!(after, before, "a failed icon update keeps the entry, its description and its icon unchanged");
    assert!(!changed.profile_dir().exists(), "and does not create the new profile");

    changed.name = "Failed Icon Rename".into();
    assert!(install(&changed, None, Some(&styled), &[], &NoSharing).is_err(), "an unreadable icon refuses the rename");
    assert_eq!(DesktopEntry::read(&entry).unwrap().get("Name").as_deref(), Some("Styled Icon"), "leaving the working entry under its original name");
    assert!(entries_in_applications().iter().all(|n| !n.starts_with('.')), "and no half-written file behind it");
}

#[test]
fn an_entry_from_before_version_stamps_gets_a_current_launcher() {
    let _world = World::new();
    let legacy = shortcut("Legacy", Source::Own);
    let entry = entry_path(legacy.id);
    std::fs::create_dir_all(applications_dir()).unwrap();
    std::fs::write(
        &entry,
        format!(
            "[Desktop Entry]\nType=Application\nName=Legacy Name\nExec=/somewhere/old-launch\n{CONFIG_KEY}={}\n",
            config_path(legacy.id).display()
        ),
    )
    .unwrap();
    files::write_json(&config_path(legacy.id), &serde_json::json!({"profileDir": paths::text(&legacy.profile_dir())})).unwrap();

    assert!(refresh_launcher(&legacy, &[]), "a legacy shortcut gets a fresh launcher");
    let read = DesktopEntry::read(&entry).unwrap();
    assert!(!read.raw("Exec").unwrap().contains("old-launch"), "the Exec it named is the one replaced");
    assert_eq!(read.get("StartupWMClass"), Some(window_class(legacy.id)), "it gains the window class that gives it its own dock icon");
    assert_eq!(built_by(&entry).as_deref(), Some(graft_version()), "and carries the current Graft version");
    assert_eq!(read.get("Name").as_deref(), Some("Legacy Name"), "while its name stays whatever it was");
}

#[test]
fn an_awkward_name_reads_back_as_typed() {
    let _world = World::new();
    let ben = Shortcut::new("Ben & Co <work>\n", Some("Claude-Ben"), Source::Own, IconPreset::Original);
    let entry = install(&ben, None, None, &[], &NoSharing).unwrap();
    let read = DesktopEntry::read(&entry).unwrap();
    assert_eq!(read.get("Name").as_deref(), Some("Ben & Co <work>\n"), "the entry still reads back as the name typed");
    assert!(std::fs::read_to_string(&entry).unwrap().lines().all(|l| !l.starts_with("<work>")), "with no line of its own for the part after a newline");
}

#[test]
fn a_shortcut_cannot_borrow_from_itself_or_live_outside_the_config_folder() {
    let world = World::new();
    let profile = make_profile("Claude-Loop", Some("BBBB"));
    let looped = Shortcut::new("Loop", Some("Claude-Loop"), Source::Own, IconPreset::Original);
    assert_eq!(install(&looped, Some(&profile), None, &[], &NoSharing), Err(InstallError::SelfSource),
        "installing a shortcut sourced from its own profile is refused");

    let escapee = Shortcut::new("Escapee", Some("../Escaped"), Source::Own, IconPreset::Original);
    assert!(matches!(install(&escapee, None, None, &[], &NoSharing), Err(InstallError::BadFolder(_))),
        "a shortcut with a path for a folder cannot be installed");
    assert!(!world.sandbox.home().join("Escaped").exists(), "and nothing is created outside the config folder");
}

#[test]
fn an_entry_is_kept_in_step_with_the_list_without_ever_moving_its_profile() {
    let _world = World::new();
    let src = make_profile("Claude-Saved-Src", Some("VSRC"));
    let profile = make_profile("Claude-Saved", Some("VOWN"));
    let saved = Shortcut::new("Saved", Some("Claude-Saved"), Source::Own, IconPreset::Original);
    install(&saved, Some(&src), None, &[], &NoSharing).unwrap();
    let file = config_path(saved.id);
    assert_eq!(described_config(&entry_path(saved.id)).and_then(|c| c.source_dir), Some(paths::text(&src)),
        "an installed shortcut names its source in its description");

    assert!(!refresh_config(&saved, Some(&src), &[]), "an entry that already agrees with the list is left alone");
    assert!(refresh_config(&saved, None, &[]), "one that disagrees is brought back into line");
    let after = described_config(&entry_path(saved.id)).unwrap();
    assert_eq!(after.source_dir, None, "so a shortcut put back on its own chats stops borrowing from the dock too");
    assert_eq!(after.profile_dir, paths::text(&saved.profile_dir()), "and the profile it opens is untouched");

    let before = std::fs::read(&file).unwrap();
    for (typed, what) in [
        ("Claude-Saved-Typed", "a folder typed but never applied"),
        ("", "an empty folder, which is the config folder itself"),
        ("Claude", "Claude's own folder"),
        ("../Elsewhere", "a path out of the config folder"),
    ] {
        let mut edited = saved.clone();
        edited.folder = typed.into();
        assert!(!refresh_config(&edited, None, &[]) && std::fs::read(&file).unwrap() == before,
            "the next start does not move a shortcut onto {what}");
    }
    assert!(!refresh_config(&saved, Some(&profile), &[]) && std::fs::read(&file).unwrap() == before,
        "nor onto borrowing from its own profile");

    std::fs::write(&file, "not json").unwrap();
    assert!(refresh_config(&saved, None, &[]), "a graft.json that will not parse is rewritten rather than trusted");
    assert!(described_config(&entry_path(saved.id)).is_some(), "and reads again");

    // The path an entry names is text anybody can edit, and is not leave to
    // write somewhere Graft never put a description.
    let elsewhere = profile.join("graft.json");
    let mut entry = DesktopEntry::read(&entry_path(saved.id)).unwrap();
    entry.set(CONFIG_KEY, &paths::text(&elsewhere));
    std::fs::write(entry_path(saved.id), entry.text()).unwrap();
    assert!(!refresh_config(&saved, Some(&src), &[]) && !elsewhere.exists(), "a description outside Graft's own folder is never written");
}

#[test]
fn every_installed_shortcut_is_brought_up_to_date_at_start() {
    let _world = World::new();
    let a = shortcut("A", Source::Own);
    let b = shortcut("B", Source::Own);
    install(&a, None, None, &[], &NoSharing).unwrap();
    install(&b, None, None, &[], &NoSharing).unwrap();
    age(&entry_path(a.id));
    let src = make_profile("Claude-Src", None);
    let list = vec![(a.clone(), None), (b.clone(), Some(src.clone()))];
    assert_eq!(refresh_launchers(&list), 2, "one with a stale launcher and one with a stale source are both repaired");
    assert_eq!(refresh_launchers(&list), 0, "and the next start finds nothing to do");
}
