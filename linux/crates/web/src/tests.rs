//! The rules of Artifacts, Projects and Search Chats that need no display and
//! no claude.ai, held as sentences.

use crate::api::{self, Identity, Permissions};
use crate::files::{self, Reading};
use crate::local_sessions::{self, TranscriptFacts, Transcripts};
use crate::model::*;
use crate::projects::*;
use graft_core::testing::Sandbox;
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use uuid::Uuid;

const ORG: &str = "8d22f6d4-6ac4-46cb-840d-3248ad76f2f7";

fn text(data: Vec<u8>) -> String {
    String::from_utf8(data).unwrap()
}

fn identity(account: &str, email: &str, org: &str) -> Identity {
    Identity { account_uuid: account.into(), email: email.into(), org: org.into() }
}

fn account(email: &str, uuid: &str, org: &str) -> ArtifactAccount {
    ArtifactAccount { id: Uuid::new_v4(), email: Some(email.into()), account_uuid: Some(uuid.into()), org: Some(org.into()) }
}

// MARK: - Artifacts

#[test]
fn every_artifact_request_carries_the_header_without_which_the_service_answers_404() {
    assert!(api::HEADERS.contains(&("X-Frame-CP", "go")));
    assert!(api::headers_for(&api::list_path("mine", ORG, None)).iter().any(|(k, v)| k == "X-Frame-CP" && v == "go"));
}

#[test]
fn the_artifact_services_headers_go_to_the_artifact_service_alone() {
    assert!(api::headers_for(api::ACCOUNT_PATH).is_empty());
    assert!(api::headers_for(&code_sessions_path(None)).is_empty());
    assert!(api::headers_for(&search_path(ORG)).is_empty());
    assert!(code_headers(ORG).iter().all(|(k, _)| k != "X-Frame-CP"));
}

#[test]
fn a_list_is_asked_for_exactly_as_claude_ais_own_page_asks_for_it() {
    assert_eq!(
        api::list_path("mine", ORG, None),
        format!("/api/frame/artifacts?rel=mine&empty=exclude&limit=48&org={ORG}&sort=last_viewed")
    );
}

#[test]
fn a_page_token_goes_back_encoded_so_one_with_a_plus_sign_is_not_read_as_a_space() {
    assert!(api::list_path("shared", ORG, Some("a+b=c")).ends_with("&page_token=a%2Bb%3Dc"));
}

#[test]
fn permissions_and_invitations_are_keyed_by_the_artifacts_uuid_under_its_organization() {
    assert_eq!(api::perm_path("aadd31ed-02a0", ORG), format!("/api/frame/perm/aadd31ed-02a0?org={ORG}"));
    assert_eq!(api::invite_path("aadd31ed-02a0", ORG), format!("/api/frame/invite/aadd31ed-02a0?org={ORG}"));
}

#[test]
fn an_invitation_from_a_group_makes_the_person_a_viewer_never_an_editor() {
    assert_eq!(text(api::invite_body("a@example.com", ArtifactRole::Reader)), r#"{"email":"a@example.com","role":"reader"}"#);
}

#[test]
fn a_commenter_and_an_editor_are_invited_by_the_role_names_claude_ais_invitations_use() {
    assert_eq!(
        text(api::invite_body("a@example.com", ArtifactRole::Commenter)),
        r#"{"email":"a@example.com","role":"commenter"}"#
    );
    assert_eq!(text(api::invite_body("a@example.com", ArtifactRole::Editor)), r#"{"email":"a@example.com","role":"editor"}"#);
}

#[test]
fn opening_an_artifact_to_anyone_with_the_link_and_closing_it_again_keeps_whoever_was_invited() {
    assert_eq!(text(api::read_mode_body(true, &["u1".into()])), r#"{"read":{"mode":"public"}}"#);
    assert_eq!(text(api::read_mode_body(false, &["u1".into()])), r#"{"read":{"mode":"users","users":["u1"]}}"#);
    assert_eq!(text(api::read_mode_body(false, &[])), r#"{"read":{"mode":"owner"}}"#, "and is private to the owner when nobody was");
}

#[test]
fn an_account_in_a_team_as_well_is_asked_about_under_the_organization_that_chats() {
    let answer = br#"{"uuid":"acc-1","email_address":"me@example.com","memberships":[
          {"organization":{"uuid":"team-org","capabilities":["raven"]}},
          {"organization":{"uuid":"chat-org","capabilities":["chat","claude_pro"]}}]}"#;
    assert_eq!(api::identity(answer), Some(identity("acc-1", "me@example.com", "chat-org")));
    assert_eq!(api::identity(br#"{"error":"unauthorized"}"#), None, "an answer that names no account is no sign-in");
}

#[test]
fn a_401_anywhere_is_signed_out_and_a_403_is_only_from_the_account_page() {
    assert!(api::means_signed_out(401, &api::list_path("mine", ORG, None)));
    assert!(api::means_signed_out(403, api::ACCOUNT_PATH));
    assert!(!api::means_signed_out(403, &api::perm_path("aadd31ed", ORG)));
    assert!(!api::means_signed_out(403, "/v1/code/sessions?limit=200"));
}

#[test]
fn a_403_from_anywhere_else_asks_the_account_page_once_before_it_is_taken_for_a_sign_out() {
    assert!(api::needs_account_check(403, &api::invite_path("aadd31ed", ORG)));
    assert!(!api::needs_account_check(403, api::ACCOUNT_PATH));
    assert!(!api::needs_account_check(404, &api::invite_path("aadd31ed", ORG)));
}

#[test]
fn signing_in_again_to_a_listed_account_keeps_the_new_store_in_the_accounts_place_and_lets_the_dead_one_go() {
    let first = account("a@example.com", "acc-a", "org-a");
    let second = account("b@example.com", "acc-b", "org-b");
    let fresh = Uuid::new_v4();
    let (list, retired) = signed_in(&[first.clone(), second.clone()], fresh, &identity("acc-a", "a@example.com", "org-a"));
    assert_eq!(list.iter().map(|a| a.id).collect::<Vec<_>>(), vec![fresh, second.id]);
    assert_eq!(list[0].account_uuid.as_deref(), Some("acc-a"));
    assert_eq!(retired, vec![first.id]);
}

#[test]
fn a_sign_in_to_an_account_not_yet_listed_adds_it_and_lets_nothing_go() {
    let first = account("a@example.com", "acc-a", "org-a");
    let fresh = Uuid::new_v4();
    let (list, retired) = signed_in(&[first.clone()], fresh, &identity("acc-b", "b@example.com", "org-b"));
    assert_eq!(list.iter().map(|a| a.id).collect::<Vec<_>>(), vec![first.id, fresh]);
    assert!(retired.is_empty());
}

#[test]
fn signing_in_again_to_one_account_as_another_listed_one_leaves_that_one_listed_once_on_the_store_now_holding_it() {
    let first = account("a@example.com", "acc-a", "org-a");
    let second = account("b@example.com", "acc-b", "org-b");
    let (list, retired) = signed_in(&[first.clone(), second.clone()], first.id, &identity("acc-b", "b@example.com", "org-b"));
    assert_eq!(list.iter().map(|a| a.id).collect::<Vec<_>>(), vec![first.id]);
    assert_eq!(list[0].account_uuid.as_deref(), Some("acc-b"));
    assert_eq!(retired, vec![second.id]);
}

#[test]
fn signing_in_again_to_an_account_as_itself_keeps_its_store_and_its_place() {
    let first = account("a@example.com", "acc-a", "org-a");
    let second = account("b@example.com", "acc-b", "org-b");
    let (list, retired) = signed_in(&[first.clone(), second.clone()], second.id, &identity("acc-b", "b@example.com", "org-b2"));
    assert_eq!(list.iter().map(|a| a.id).collect::<Vec<_>>(), vec![first.id, second.id]);
    assert_eq!(list[1].org.as_deref(), Some("org-b2"));
    assert!(retired.is_empty());
}

fn listing() -> &'static [u8] {
    br#"{"artifacts":[
          {"kind":"page","id":"a1","rel":"mine","title":"Pool check","audience":"owner",
           "open_url":"https://claude.ai/artifact/N6k75kB4","sort_time":"2026-11-07T10:00:00.000Z",
           "owner_account":"acc-1"},
          {"kind":"page","id":"a2","rel":"mine","title":"","audience":"public",
           "sort_time":"2026-11-08T10:00:00Z"}],
         "next_page_token":"next"}"#
}

#[test]
fn a_page_of_artifacts_is_read_with_the_link_claude_ai_gave_and_the_token_for_the_next_page() {
    let page = api::page(listing(), Uuid::new_v4(), ORG).unwrap();
    assert_eq!(page.artifacts.len(), 2);
    assert_eq!(page.next.as_deref(), Some("next"));
    assert_eq!(page.artifacts[0].open_url.as_deref(), Some("https://claude.ai/artifact/N6k75kB4"));
    assert!(page.artifacts[0].sort_time.is_some(), "a time with fractional seconds is read");
}

#[test]
fn an_artifact_with_no_title_is_still_listed_and_a_public_one_reads_as_open_to_anyone_with_the_link() {
    let page = api::page(listing(), Uuid::new_v4(), ORG).unwrap();
    assert_eq!(page.artifacts[1].title, "Untitled");
    assert_eq!(page.artifacts[1].access(), ArtifactAccess::AnyoneWithLink);
}

#[test]
fn each_audience_the_service_names_reads_as_who_can_open_it() {
    assert_eq!(ArtifactAccess::from_audience("users"), ArtifactAccess::Invited);
    assert_eq!(ArtifactAccess::from_audience("owner"), ArtifactAccess::OnlyYou);
    assert_eq!(ArtifactAccess::from_audience("org"), ArtifactAccess::Organization);
}

#[test]
fn an_artifact_shared_with_its_organization_checks_an_entry_of_its_own_never_the_invited_one() {
    assert_eq!(ArtifactAccess::Organization.menu_choice(), ArtifactAccess::Organization);
    assert_eq!(ArtifactAccess::OnlyYou.menu_choice(), ArtifactAccess::Invited);
    assert_eq!(ArtifactAccess::Invited.menu_choice(), ArtifactAccess::Invited);
    assert_eq!(ArtifactAccess::AnyoneWithLink.menu_choice(), ArtifactAccess::AnyoneWithLink);
}

#[test]
fn asking_an_artifact_to_be_what_it_already_is_changes_nothing_and_leaving_the_organization_is_a_change() {
    assert!(!ArtifactAccess::AnyoneWithLink.changes(true));
    assert!(!ArtifactAccess::Invited.changes(false));
    assert!(!ArtifactAccess::OnlyYou.changes(false));
    assert!(ArtifactAccess::Invited.changes(true));
    assert!(ArtifactAccess::AnyoneWithLink.changes(false));
    assert!(ArtifactAccess::Organization.changes(false));
}

#[test]
fn who_can_open_an_artifact_is_read_from_its_permissions_as_claude_ai_has_it_now() {
    assert_eq!(api::permissions(br#"{"read":{"mode":"org"}}"#).unwrap().mode.as_deref(), Some("org"));
    assert_eq!(api::permissions(br#"{"version":"v1"}"#).unwrap().mode, None);
}

#[test]
fn a_challenge_page_in_place_of_a_list_is_not_taken_for_an_empty_one() {
    assert!(api::page(b"<html>", Uuid::new_v4(), ORG).is_none());
}

#[test]
fn an_artifact_two_of_your_accounts_can_see_is_listed_once_under_the_one_that_owns_it_newest_first() {
    let mine = Uuid::new_v4();
    let other = Uuid::new_v4();
    let owned = Artifact {
        id: "x".into(),
        account: mine,
        org: ORG.into(),
        title: "Plan".into(),
        kind: "page".into(),
        open_url: None,
        audience: "users".into(),
        rel: "mine".into(),
        sort_time: Some(UNIX_EPOCH + Duration::from_secs(10)),
        owner_account: Some("acc-1".into()),
        renamable: false,
        duplicable: false,
        duplicate_available: true,
    };
    let mut shared_copy = owned.clone();
    shared_copy.account = other;
    shared_copy.rel = "shared".into();
    let mut newer = owned.clone();
    newer.id = "y".into();
    newer.sort_time = Some(UNIX_EPOCH + Duration::from_secs(20));
    let merged = api::merge(&[vec![shared_copy], vec![owned, newer]]);
    assert_eq!(merged.iter().map(|a| a.id.as_str()).collect::<Vec<_>>(), vec!["y", "x"]);
    assert_eq!(merged[1].account, mine);
}

#[test]
fn addresses_pasted_from_a_mail_header_are_read_once_each_and_what_is_not_an_address_is_dropped() {
    assert_eq!(
        parse_addresses("Ana <ANA@example.com>, bo@example.org; ana@example.com\nnot-an-address\nx@y"),
        vec!["ana@example.com", "bo@example.org"]
    );
}

#[test]
fn groups_are_kept_in_grafts_own_folder_and_read_back_as_they_were_saved() {
    let sandbox = Sandbox::new();
    let groups = vec![ArtifactGroup::new(Uuid::new_v4(), "Design", vec!["a@example.com".into()])];
    assert!(files::save(&groups, &files::groups_file()));
    assert!(files::groups_file().starts_with(sandbox.config().join("ClaudeGraft")));
    match files::read_each::<ArtifactGroup>(&files::groups_file()) {
        Reading::Read((back, lost)) => {
            assert_eq!(back, groups);
            assert_eq!(lost, 0);
        }
        other => panic!("read back as {other:?}"),
    }
}

#[test]
fn a_file_that_is_not_there_is_told_apart_from_one_that_is_there_and_will_not_decode() {
    let folder = tempfile::tempdir().unwrap();
    let absent = folder.path().join("absent.json");
    let garbled = folder.path().join("artifact-accounts.json");
    std::fs::write(&garbled, "{not json").unwrap();
    assert_eq!(files::read::<Vec<ArtifactAccount>>(&absent), Reading::Missing);
    assert_eq!(files::read::<Vec<ArtifactAccount>>(&garbled), Reading::Unreadable);
}

#[test]
fn an_unreadable_file_is_moved_aside_whole_beside_where_it_was_rather_than_saved_over() {
    let folder = tempfile::tempdir().unwrap();
    let garbled = folder.path().join("artifact-accounts.json");
    std::fs::write(&garbled, "{not json").unwrap();
    let aside = files::set_aside(&garbled, UNIX_EPOCH + Duration::from_secs(1_700_000_000)).unwrap();
    assert_eq!(aside.file_name().unwrap(), "artifact-accounts.unreadable-1700000000.json");
    assert!(!garbled.exists());
    assert_eq!(std::fs::read(&aside).unwrap(), b"{not json");
}

#[test]
fn one_group_that_will_not_decode_costs_that_group_not_every_group_in_the_file() {
    let folder = tempfile::tempdir().unwrap();
    let one_bad = folder.path().join("groups.json");
    std::fs::write(
        &one_bad,
        r#"[{"id":"6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1","name":"Design","emails":["a@example.com"]},
         {"id":"not a uuid","name":"Broken","emails":["b@example.com"]},
         {"id":"7E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1","name":"Review","emails":[]}]"#,
    )
    .unwrap();
    match files::read_each::<ArtifactGroup>(&one_bad) {
        Reading::Read((found, lost)) => {
            assert_eq!(found.iter().map(|g| g.name.as_str()).collect::<Vec<_>>(), vec!["Design", "Review"]);
            assert_eq!(lost, 1);
        }
        other => panic!("read as {other:?}"),
    }
}

#[test]
fn an_accounts_or_groups_file_that_will_not_read_is_set_aside_before_anything_is_saved_where_it_was() {
    let _sandbox = Sandbox::new();
    std::fs::create_dir_all(graft_core::paths::graft_folder()).unwrap();
    std::fs::write(files::accounts_file(), "{not json").unwrap();
    std::fs::write(
        files::groups_file(),
        r#"[{"id":"6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1","name":"Design","emails":[]},{"id":"bad","name":"x","emails":[]}]"#,
    )
    .unwrap();
    let now = UNIX_EPOCH + Duration::from_secs(1_700_000_000);
    let loaded = files::load(now);
    assert!(loaded.accounts.is_empty() && !loaded.accounts_were_read && loaded.may_write_accounts);
    assert!(graft_core::paths::graft_file("artifact-accounts.unreadable-1700000000.json").exists());
    assert_eq!(loaded.groups.len(), 1);
    assert!(graft_core::paths::graft_file("artifact-groups.unreadable-1700000000.json").exists(), "the whole file is kept aside");
    match files::read_each::<ArtifactGroup>(&files::groups_file()) {
        Reading::Read((back, lost)) => assert!(back.len() == 1 && lost == 0, "and the groups that read are written back at once"),
        other => panic!("groups file read as {other:?}"),
    }
}

#[test]
fn stores_no_listed_account_is_under_are_found_and_nothing_else_in_the_folder_is() {
    let _sandbox = Sandbox::new();
    let listed = Uuid::new_v4();
    let stray = Uuid::new_v4();
    let signing_in = Uuid::new_v4();
    for id in [listed, stray, signing_in] {
        std::fs::create_dir_all(files::store_data_folder(id)).unwrap();
    }
    std::fs::create_dir_all(files::stores_folder().join("not-a-store")).unwrap();
    let found = files::stray_stores(&[listed], |id| id == signing_in);
    assert_eq!(found, vec![stray], "a store a sign-in is writing into is not stray");
}

#[test]
fn an_accounts_file_keeps_each_store_under_the_id_a_mac_graft_writes() {
    let id = Uuid::parse_str("6e1f0f55-8c8b-4e64-8f73-6d3a50d3e5b1").unwrap();
    let record = ArtifactAccount::new(id);
    assert_eq!(serde_json::to_string(&record).unwrap(), r#"{"id":"6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1"}"#);
    let _sandbox = Sandbox::new();
    assert!(files::store_folder(id).ends_with("ClaudeGraft/artifact-stores/6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1"));
}

// MARK: - Artifact roles, actions and exports

fn permissions(json: &[u8]) -> Permissions {
    api::permissions(json).unwrap()
}

#[test]
fn an_artifacts_permissions_say_whether_it_takes_commenters_and_editors_and_silence_offers_neither() {
    let both = permissions(
        br#"{"version":"v7","read":{"mode":"users","users":["u1"]},"commentersEnabled":true,"editorsEnabled":true}"#,
    );
    let none = permissions(br#"{"version":"v7","read":{"mode":"owner"}}"#);
    let older = permissions(br#"{"version":3,"writersEnabled":true}"#);
    assert!(both.commenters && both.editors && both.version.as_deref() == Some("v7"));
    assert_eq!(both.invited, vec!["u1".to_string()]);
    assert!(!none.commenters && !none.editors);
    assert!(older.editors && older.version.as_deref() == Some("3"));
}

#[test]
fn where_an_artifact_does_not_take_a_role_the_person_gets_the_most_it_does_take_and_never_more() {
    let both = Permissions { commenters: true, editors: true, ..Default::default() };
    let none = Permissions::default();
    let comments_only = Permissions { commenters: true, ..Default::default() };
    assert_eq!(ArtifactRole::Editor.offered(&both), ArtifactRole::Editor);
    assert_eq!(ArtifactRole::Editor.offered(&comments_only), ArtifactRole::Commenter);
    assert_eq!(ArtifactRole::Editor.offered(&none), ArtifactRole::Reader);
    assert_eq!(ArtifactRole::Commenter.offered(&none), ArtifactRole::Reader);
    assert_eq!(ArtifactRole::Reader.offered(&both), ArtifactRole::Reader);
}

const OLD_GROUP: &str = r#"{"emails":["a@example.com","b@example.com"],"id":"6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1","name":"Design"}"#;

#[test]
fn a_group_saved_before_roles_existed_reads_back_with_everyone_a_viewer_as_it_was_shared_before() {
    let group: ArtifactGroup = serde_json::from_str(OLD_GROUP).unwrap();
    assert_eq!(group.emails, vec!["a@example.com", "b@example.com"]);
    assert_eq!(group.role_of("a@example.com"), ArtifactRole::Reader);
    assert_eq!(group.common_role(), Some(ArtifactRole::Reader));
}

#[test]
fn a_group_of_viewers_is_written_exactly_as_before_roles_so_an_older_graft_reads_it_unchanged() {
    let group: ArtifactGroup = serde_json::from_str(OLD_GROUP).unwrap();
    assert_eq!(serde_json::to_string(&group).unwrap(), OLD_GROUP);
}

fn mixed() -> ArtifactGroup {
    let id = Uuid::parse_str("6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1").unwrap();
    let mut group = ArtifactGroup::new(id, "Review", vec!["a@example.com".into(), "b@example.com".into(), "c@example.com".into()]);
    group.set_role(ArtifactRole::Editor, "a@example.com");
    group.set_role(ArtifactRole::Commenter, "b@example.com");
    group
}

#[test]
fn a_mixed_group_keeps_each_persons_role_through_a_save() {
    let group = mixed();
    let saved = serde_json::to_string(&group).unwrap();
    let back: ArtifactGroup = serde_json::from_str(&saved).unwrap();
    assert_eq!(back, group);
    assert_eq!(back.role_of("c@example.com"), ArtifactRole::Reader);
    assert_eq!(back.common_role(), None);
}

#[test]
fn an_older_graft_can_still_read_a_group_with_roles_as_viewers_only() {
    #[derive(serde::Deserialize)]
    struct LegacyGroup {
        #[allow(dead_code)]
        id: String,
        #[allow(dead_code)]
        name: String,
        emails: Vec<String>,
    }
    let group = mixed();
    let legacy: LegacyGroup = serde_json::from_str(&serde_json::to_string(&group).unwrap()).unwrap();
    assert_eq!(legacy.emails, group.emails);
}

#[test]
fn a_group_can_be_made_all_editors_or_all_viewers_again() {
    let mut editors = mixed();
    editors.set_role_for_everyone(ArtifactRole::Editor);
    let mut viewers = mixed();
    viewers.set_role_for_everyone(ArtifactRole::Reader);
    assert_eq!(editors.common_role(), Some(ArtifactRole::Editor));
    assert_eq!(viewers.common_role(), Some(ArtifactRole::Reader));
    assert!(viewers.roles.is_empty());
}

#[test]
fn a_role_this_graft_does_not_know_costs_that_one_person_their_role_not_the_group() {
    let from_newer: ArtifactGroup = serde_json::from_str(
        r#"{"emails":["a@example.com","b@example.com"],"id":"6E1F0F55-8C8B-4E64-8F73-6D3A50D3E5B1","name":"X","roles":{"a@example.com":"manager","b@example.com":"editor"}}"#,
    )
    .unwrap();
    assert_eq!(from_newer.role_of("a@example.com"), ArtifactRole::Reader);
    assert_eq!(from_newer.role_of("b@example.com"), ArtifactRole::Editor);
}

#[test]
fn an_address_missing_from_roles_is_a_viewer_and_pasted_addresses_join_with_the_role_everyone_has() {
    let mut group = ArtifactGroup::new(Uuid::new_v4(), "Editors", vec!["a@example.com".into()]);
    group.set_role_for_everyone(ArtifactRole::Editor);
    group.add_addresses("B@example.com, a@example.com");
    assert_eq!(group.emails, vec!["a@example.com", "b@example.com"]);
    assert_eq!(group.role_of("b@example.com"), ArtifactRole::Editor, "adding to a group of editors adds editors");
    group.remove("b@example.com");
    assert!(!group.roles.contains_key("b@example.com"), "a person taken out takes their role with them");
    assert_eq!(group.role_of("nobody@example.com"), ArtifactRole::Reader);
}

#[test]
fn renaming_copying_deleting_and_reading_an_artifact_go_to_the_artifact_services_own_paths() {
    assert_eq!(api::retitle_path("aadd31ed", ORG), format!("/api/frame/retitle/aadd31ed?org={ORG}"));
    assert_eq!(text(api::retitle_body("New \"name\"")), r#"{"title":"New \"name\""}"#);
    assert_eq!(api::duplicate_path("aadd31ed", ORG), format!("/api/frame/duplicate/aadd31ed?org={ORG}"));
    assert_eq!(api::artifact_path("aadd31ed", ORG), format!("/api/frame/aadd31ed?org={ORG}"));
}

#[test]
fn claude_ais_reason_for_a_refusal_is_read_wherever_it_puts_it() {
    assert_eq!(api::reason(br#"{"reason":"title_invalid"}"#).as_deref(), Some("title_invalid"));
    assert_eq!(api::reason(br#"{"error":{"reason":"org_mismatch"}}"#).as_deref(), Some("org_mismatch"));
}

#[test]
fn only_what_the_list_says_claude_ai_allows_is_offered_and_a_list_that_says_nothing_offers_nothing() {
    let rows = api::page(
        br#"{"artifacts":[
          {"kind":"page","id":"a1","rel":"mine","title":"T","audience":"owner","renamable":true,
           "duplicable":true,"duplicate_available":false},
          {"kind":"page","id":"a2","rel":"shared","title":"U","audience":"users"}]}"#,
        Uuid::new_v4(),
        ORG,
    )
    .unwrap()
    .artifacts;
    assert!(rows[0].renamable && rows[0].duplicable && !rows[0].duplicate_available);
    assert!(!rows[1].renamable && !rows[1].duplicable);
}

fn bootstrap() -> api::Source {
    api::source(
        br#"{"ver":"v12","assetToken":"a.b.c.1900000000","title":"Plan","files":[
          {"path":"index.html"},{"path":"app.js"},{"path":"img/a b.png"},
          {"path":"../escape.html"},{"path":"/etc/passwd"},{"path":"a//b"}]}"#,
    )
    .unwrap()
}

#[test]
fn an_artifacts_files_are_read_by_the_paths_it_lists_and_none_that_could_climb_out_of_the_folder() {
    let files = bootstrap();
    assert_eq!(files.files, vec!["index.html", "app.js", "img/a b.png"]);
    assert_eq!(files.version, "v12");
}

#[test]
fn an_artifact_that_gives_no_token_to_read_its_files_with_is_not_read() {
    assert!(api::source(br#"{"ver":"v1"}"#).is_none());
}

#[test]
fn a_file_is_read_from_the_artifacts_own_file_host_as_its_viewer_reads_it() {
    let files = bootstrap();
    assert_eq!(
        api::file_url("AADD31ED-02A0", &files, "img/a b.png").as_deref(),
        Some("https://aadd31ed-02a0.frame.claudeusercontent.com/_f/v12/_src/img/a%20b.png?__frame_t=a.b.c.1900000000")
    );
    assert!(api::file_url("x", &files, "../a").is_none(), "no file is read from outside the artifact");
}

#[test]
fn an_export_is_named_after_the_artifact_and_a_page_of_several_files_goes_out_zipped() {
    assert_eq!(ExportFormat::file_name("Q3 plan: draft/2", ExportFormat::Markdown, true), "Q3 plan- draft-2.md");
    assert_eq!(ExportFormat::file_name("  ", ExportFormat::Pdf, true), "Artifact.pdf");
    assert_eq!(ExportFormat::file_name("Site", ExportFormat::Html, false), "Site.zip");
    assert_eq!(ExportFormat::file_name("Site", ExportFormat::Word, false), "Site.docx");
}

// MARK: - Projects and search

const SEARCH_ORG: &str = "11111111-2222-4333-8444-555555555555";

#[test]
fn projects_are_asked_for_as_the_projects_page_asks_thirty_at_a_time() {
    assert_eq!(
        projects_path(SEARCH_ORG, 30),
        format!("/api/organizations/{SEARCH_ORG}/projects_v2?include_harmony_projects=true&limit=30&offset=30&order_by=latest_chat")
    );
}

#[test]
fn a_search_is_posted_with_the_query_the_count_and_the_snippet_size_the_search_box_sends() {
    assert_eq!(search_path(SEARCH_ORG), format!("/api/organizations/{SEARCH_ORG}/conversation/search/v2"));
    assert_eq!(text(search_body("pi \"zero\"")), r#"{"n":25,"query":"pi \"zero\"","target_snippet_size":100}"#);
}

#[test]
fn code_sessions_are_listed_two_hundred_at_a_time_the_cursor_carried_as_given() {
    assert_eq!(code_sessions_path(None), "/v1/code/sessions?limit=200");
    assert!(code_sessions_path(Some("a|b=")).ends_with("&cursor=a%7Cb%3D"));
}

#[test]
fn the_session_list_names_the_organization_and_its_beta_and_none_of_the_artifact_services_headers() {
    let headers = code_headers(SEARCH_ORG);
    let get = |name: &str| headers.iter().find(|(k, _)| k == name).map(|(_, v)| v.as_str());
    assert_eq!(get("x-organization-uuid"), Some(SEARCH_ORG));
    assert_eq!(get("anthropic-beta"), Some("ccr-byoc-2025-07-29"));
    assert_eq!(get("X-Frame-CP"), None);
}

#[test]
fn a_session_listed_as_cse_opens_as_session_under_the_same_name() {
    assert_eq!(code_session_url("cse_01WY8cUV"), "https://claude.ai/code/session_01WY8cUV");
}

fn project_page() -> &'static [u8] {
    br#"{"data":[
          {"uuid":"p-1","name":"Graft","description":"the app","is_private":true,"is_starred":false,
           "is_starter_project":false,"archived_at":null,"updated_at":"2026-11-07T10:00:00.000Z",
           "creator":{"uuid":"c","full_name":"Aaditya"}},
          {"uuid":"p-2","name":"","is_private":false,"is_starred":true,"is_starter_project":true,
           "archived_at":null,"updated_at":"2026-10-01T10:00:00Z"},
          {"uuid":"p-3","name":"Old","archived_at":"2026-09-01T00:00:00Z"}],
         "pagination":{"total":3,"limit":30,"offset":0,"has_more":true}}"#
}

#[test]
fn an_archived_project_is_left_off_as_on_the_projects_page_and_another_page_is_followed() {
    let (listed, more) = projects(project_page(), Uuid::new_v4()).unwrap();
    assert_eq!(listed.iter().map(|p| p.id.as_str()).collect::<Vec<_>>(), vec!["p-1", "p-2"]);
    assert!(more);
}

#[test]
fn a_project_with_no_name_is_still_listed_and_the_example_project_says_so() {
    let (listed, _) = projects(project_page(), Uuid::new_v4()).unwrap();
    assert_eq!(listed[1].name, "Untitled");
    assert!(listed[1].is_example);
    assert_eq!(listed[0].creator.as_deref(), Some("Aaditya"));
}

#[test]
fn a_page_that_is_not_a_project_list_is_refused_not_read_as_an_empty_one() {
    assert!(projects(b"<html>", Uuid::new_v4()).is_none());
}

#[test]
fn a_starred_project_comes_first_as_it_does_on_claude_ai_and_opens_at_its_own_page() {
    let (listed, _) = projects(project_page(), Uuid::new_v4()).unwrap();
    let merged = crate::projects::merge(&[listed]);
    assert_eq!(merged[0].id, "p-2");
    assert_eq!(merged[0].open_url(), "https://claude.ai/project/p-2");
}

fn search_page(account: Uuid) -> Vec<SearchHit> {
    chats(
        br#"{"data":[
          {"conversation":{"uuid":"c-2","name":"Camera Pi","updated_at":"2026-11-06T10:00:00Z",
                           "project_uuid":"p-1","is_archived":true,"is_starred":true},
           "matched_snippet":{"text":"a raspberry camera","matches":[{"start":2,"end":11}]},
           "title_matches":[],"semantic_rank":1},
          {"conversation":{"uuid":"c-1","name":"Raspberry Pi Pico","updated_at":"2026-11-07T10:00:00Z",
                           "project_uuid":null,"is_archived":false},
           "matched_snippet":{"text":"","matches":[]},"title_matches":[{"start":0,"end":9}],"semantic_rank":0}],
         "next_page_token":null,"executed_mode":"hybrid"}"#,
        account,
    )
    .unwrap()
}

#[test]
fn a_found_chat_keeps_its_project_its_archive_and_star_and_where_the_query_matched() {
    let chats = search_page(Uuid::new_v4());
    assert_eq!(chats.len(), 2);
    assert!(chats[0].is_archived && chats[0].is_starred);
    assert_eq!(chats[0].project.as_deref(), Some("p-1"));
    assert_eq!(chats[1].title_matches, vec![0..9]);
    assert_eq!(chats[0].snippet_matches, vec![2..11]);
    assert_eq!(chats[1].open_url.as_deref(), Some("https://claude.ai/chat/c-1"), "a chat opens at its own page");
}

fn session_page() -> Vec<RemoteCodeSession> {
    code_sessions(
        br#"{"data":[
          {"id":"cse_here","title":"Fix the graft build","status":"active","environment_kind":"bridge",
           "last_event_at":"2026-11-07T09:00:00Z","config":{"sources":[]}},
          {"id":"cse_cloud","title":"Graft release notes","status":"archived","environment_kind":"anthropic_cloud",
           "updated_at":"2026-11-01T09:00:00Z","config":{"sources":[{"url":"https://github.com/a/claude-graft"}]}},
          {"id":"cse_far","title":"Graft on the mini","status":"active","environment_kind":"bridge"},
          {"id":"cse_carried","title":"Graft carried over","status":"active","environment_kind":"bridge"},
          {"id":"cse_other","title":"Pico camera","status":"active","environment_kind":"bridge"}],
         "next_cursor":"MTc5"}"#,
    )
    .map(|(s, next)| {
        assert_eq!(next.as_deref(), Some("MTc5"));
        s
    })
    .unwrap()
}

#[test]
fn a_session_list_is_read_with_its_cursor_where_each_ran_and_its_repository() {
    let sessions = session_page();
    assert_eq!(sessions.len(), 5);
    assert!(sessions[1].in_cloud && sessions[1].is_archived);
    assert_eq!(sessions[1].repository.as_deref(), Some("https://github.com/a/claude-graft"));
}

fn local(bridge: &str, owner: &str, title: &str, folder: &str, transcript: &str, here: bool) -> LocalCodeSession {
    LocalCodeSession {
        bridge_ids: vec![bridge.into()],
        owner: owner.into(),
        organization: "org-chat".into(),
        title: title.into(),
        folder: folder.into(),
        transcript: PathBuf::from(transcript),
        last_active: SystemTime::now(),
        folder_is_here: here,
        carried_on: false,
    }
}

fn locals() -> Vec<LocalCodeSession> {
    vec![
        local("cse_here", "acct-mine", "Fix the graft build", "/home/shared/graft", "/tmp/here.jsonl", true),
        local("cse_carried", "acct-mine", "Graft carried over", "/home/someone-else/graft", "/tmp/c.jsonl", false),
        local("cse_deleted", "acct-mine", "Graft notes, deleted", "/home/shared/graft", "/tmp/d.jsonl", true),
        local("cse_someone", "acct-other", "Graft, not this account", "/home/shared/graft", "/tmp/o.jsonl", true),
    ]
}

fn hits_with(
    organization: Option<&str>,
    complete: bool,
    listed_at: SystemTime,
    local: &[LocalCodeSession],
) -> Vec<SearchHit> {
    let remote = session_page();
    let list = SessionList { sessions: &remote, complete, listed_at: Some(listed_at) };
    code_hits("graft", Uuid::nil(), Some("acct-mine"), organization, &list, local)
}

fn later() -> SystemTime {
    SystemTime::now() + Duration::from_secs(60)
}

#[test]
fn a_code_session_is_placed_on_this_machine_only_when_its_transcript_is_here_and_its_folder_could_be() {
    let hits = hits_with(Some("org-chat"), true, later(), &locals());
    let place = |id: &str| hits.iter().find(|h| h.id == id).and_then(|h| h.place);
    assert_eq!(place("cse_here"), Some(Place::ThisMachine));
    assert_eq!(place("cse_cloud"), Some(Place::Cloud));
    assert_eq!(place("cse_far"), Some(Place::OtherMachine));
    assert_eq!(place("cse_carried"), Some(Place::OtherMachine));
}

#[test]
fn a_session_found_here_carries_its_folder_and_the_word_that_matched_its_title() {
    let hits = hits_with(Some("org-chat"), true, later(), &locals());
    let here = hits.iter().find(|h| h.id == "cse_here").unwrap();
    assert_eq!(here.folder.as_deref(), Some("/home/shared/graft"));
    assert_eq!(here.title_matches, vec![8..13]);
}

#[test]
fn a_session_whose_title_and_folder_do_not_hold_every_word_is_left_out() {
    assert!(!hits_with(Some("org-chat"), true, later(), &locals()).iter().any(|h| h.id == "cse_other"));
}

#[test]
fn a_transcript_of_this_accounts_that_claude_ai_no_longer_lists_is_found_and_opens_in_the_file_manager() {
    let hits = hits_with(Some("org-chat"), true, later(), &locals());
    let gone: Vec<&SearchHit> = hits.iter().filter(|h| h.place == Some(Place::GoneFromClaude)).collect();
    assert_eq!(gone.len(), 1);
    assert_eq!(gone[0].title, "Graft notes, deleted");
    assert!(gone[0].open_url.is_none());
    assert_eq!(gone[0].transcript.as_deref(), Some(Path::new("/tmp/d.jsonl")));
}

#[test]
fn an_earlier_half_of_a_conversation_that_goes_on_is_not_called_gone_from_claude_ai() {
    let mut local = locals();
    local[2].carried_on = true;
    assert!(!hits_with(Some("org-chat"), true, later(), &local).iter().any(|h| h.place == Some(Place::GoneFromClaude)));
}

#[test]
fn nothing_is_called_gone_from_claude_ai_when_the_session_list_was_not_read_to_its_end() {
    assert!(!hits_with(Some("org-chat"), false, later(), &locals()).iter().any(|h| h.place == Some(Place::GoneFromClaude)));
}

#[test]
fn a_session_owned_under_another_organization_than_the_list_was_read_for_is_not_called_gone_from_claude_ai() {
    assert!(!hits_with(Some("org-team"), true, later(), &locals()).iter().any(|h| h.place == Some(Place::GoneFromClaude)));
    assert!(!hits_with(None, true, later(), &locals()).iter().any(|h| h.place == Some(Place::GoneFromClaude)));
}

#[test]
fn a_transcript_that_moved_on_after_its_session_list_was_read_is_not_called_gone_from_claude_ai() {
    let earlier = SystemTime::now() - Duration::from_secs(60);
    assert!(!hits_with(Some("org-chat"), true, earlier, &locals()).iter().any(|h| h.place == Some(Place::GoneFromClaude)));
}

#[test]
fn this_machines_transcripts_are_read_again_when_a_session_list_is_newer_than_them_or_they_are_two_minutes_old() {
    let list = SystemTime::now();
    let life = Duration::from_secs(120);
    assert!(local_read_serves(list + Duration::from_secs(1), list, list, life));
    assert!(!local_read_serves(list - Duration::from_secs(1), list, list, life));
    assert!(!local_read_serves(list, list, list + Duration::from_secs(121), life));
}

#[test]
fn every_word_must_be_found_in_any_order_in_the_title_or_the_folder() {
    assert!(code_match("mini graft", "Graft on the mini", &[]).is_some());
    assert!(code_match("graft cloud", "Graft release notes", &[Some("https://github.com/a/claude-graft")]).is_none());
}

#[test]
fn each_accounts_best_chat_comes_before_any_accounts_second_and_code_sessions_follow_the_chats() {
    let mine = Uuid::new_v4();
    let lent = Uuid::new_v4();
    let chats = search_page(mine);
    let hits = hits_with(Some("org-chat"), true, later(), &locals());
    let other = crate::projects::chats(br#"{"data":[{"conversation":{"uuid":"o-1","name":"Lent pico"},"semantic_rank":0}]}"#, lent).unwrap();
    let mut first = chats.clone();
    first.extend(hits);
    let order: Vec<String> = interleave(&[first, other]).into_iter().map(|h| h.id).collect();
    assert_eq!(&order[..3], &["c-1", "o-1", "c-2"]);
    assert!(order[3..].iter().all(|id| id.starts_with("cse_") || id.starts_with("local:")));
}

#[test]
fn matches_are_counted_in_utf16_as_the_page_counts_them_and_one_past_the_end_is_dropped() {
    assert_eq!(highlighted("\u{1F642} raspberry", &[3..12, 40..50]), "\u{1F642} <b>raspberry</b>");
    assert_eq!(highlighted("a<b", &[0..1]), "<b>a</b>&lt;b", "the text around a match is never read as markup");
    assert_eq!(highlighted("\u{1F642}x", &[1..3]), "\u{1F642}x", "a range that would split a character is dropped");
}

fn all_hits() -> (Uuid, Vec<SearchHit>) {
    let mine = Uuid::new_v4();
    let mut all = search_page(mine);
    all.extend(hits_with(Some("org-chat"), true, later(), &locals()));
    (mine, all)
}

fn ids(all: &[SearchHit], filters: &SearchFilters) -> Vec<String> {
    let now = SystemTime::now();
    let mut found: Vec<String> = all.iter().filter(|h| filters.admits(h, now)).map(|h| h.id.clone()).collect();
    found.sort();
    found
}

#[test]
fn a_search_starts_with_archived_chats_and_sessions_hidden_as_claude_ais_box_does() {
    let (_, all) = all_hits();
    let now = SystemTime::now();
    assert!(!all.iter().filter(|h| SearchFilters::default().admits(h, now)).any(|h| h.is_archived));
}

#[test]
fn archived_alone_shows_only_what_was_archived() {
    let (_, all) = all_hits();
    let filters = SearchFilters { status: StatusFilter::Archived, ..Default::default() };
    assert_eq!(ids(&all, &filters), vec!["c-2", "cse_cloud"]);
}

#[test]
fn asking_where_a_session_ran_leaves_chats_out_and_gone_from_claude_ai_can_be_asked_for_alone() {
    let (_, all) = all_hits();
    let here = SearchFilters { status: StatusFilter::Any, place: Some(Place::ThisMachine), ..Default::default() };
    assert_eq!(ids(&all, &here), vec!["cse_here"]);
    let gone = SearchFilters { status: StatusFilter::Any, place: Some(Place::GoneFromClaude), ..Default::default() };
    assert_eq!(ids(&all, &gone).len(), 1);
}

#[test]
fn no_project_means_a_chat_outside_every_project_not_a_code_session() {
    let (_, all) = all_hits();
    let filters = SearchFilters { status: StatusFilter::Any, project: ProjectChoice::None, ..Default::default() };
    assert_eq!(ids(&all, &filters), vec!["c-1"]);
}

#[test]
fn one_project_shows_its_chats_and_starred_only_shows_starred_chats() {
    let (_, all) = all_hits();
    let one = SearchFilters { status: StatusFilter::Any, project: ProjectChoice::Project("p-1".into()), ..Default::default() };
    assert_eq!(ids(&all, &one), vec!["c-2"]);
    let starred = SearchFilters { status: StatusFilter::Any, starred_only: true, ..Default::default() };
    assert_eq!(ids(&all, &starred), vec!["c-2"]);
}

#[test]
fn code_sessions_alone() {
    let (_, all) = all_hits();
    let filters = SearchFilters { kind: KindFilter::Code, status: StatusFilter::Any, ..Default::default() };
    let now = SystemTime::now();
    assert!(all.iter().filter(|h| filters.admits(h, now)).all(|h| h.kind == HitKind::Code));
}

#[test]
fn the_past_day_leaves_out_a_chat_two_days_old() {
    let (mine, _) = all_hits();
    let now = SystemTime::now();
    let mut aged = search_page(mine).remove(1);
    aged.updated = Some(now - Duration::from_secs(2 * 86_400));
    let filters = SearchFilters { status: StatusFilter::Any, age: AgeFilter::Day, ..Default::default() };
    assert!(!filters.admits(&aged, now));
}

#[test]
fn one_account_shows_only_what_that_account_found() {
    let mine = Uuid::new_v4();
    let lent = Uuid::new_v4();
    let mut all = search_page(mine);
    all.extend(crate::projects::chats(br#"{"data":[{"conversation":{"uuid":"o-1","name":"Lent pico"},"semantic_rank":0}]}"#, lent).unwrap());
    let filters = SearchFilters { status: StatusFilter::Any, account: Some(lent), ..Default::default() };
    assert_eq!(ids(&all, &filters), vec!["o-1"]);
}

#[test]
fn the_filters_know_when_they_narrow_anything() {
    assert!(!SearchFilters::default().is_narrowed());
    assert!(SearchFilters { starred_only: true, ..Default::default() }.is_narrowed());
}

// MARK: - This machine's transcripts

struct Stand {
    root: PathBuf,
    cached: Vec<(PathBuf, TranscriptFacts)>,
    parsed: std::sync::Mutex<Vec<PathBuf>>,
    homes: Vec<String>,
}

impl Transcripts for Stand {
    fn projects_root(&self) -> PathBuf {
        self.root.clone()
    }
    fn cached(&self, transcript: &Path, _: f64, _: u64) -> Option<TranscriptFacts> {
        self.cached.iter().find(|(p, _)| p == transcript).map(|(_, f)| f.clone())
    }
    fn parse(&self, transcript: &Path, cli: &str) -> Option<TranscriptFacts> {
        self.parsed.lock().unwrap().push(transcript.to_path_buf());
        Some(facts(&format!("cse_{cli}"), &format!("/home/gone/{cli}")))
    }
    fn folder_is_here(&self, cwd: &str) -> bool {
        self.homes.iter().any(|h| cwd.starts_with(h.as_str()))
    }
    fn carried_on(&self) -> HashSet<String> {
        ["earlier".to_string()].into_iter().collect()
    }
}

fn facts(bridge: &str, cwd: &str) -> TranscriptFacts {
    TranscriptFacts {
        bridge_ids: vec![bridge.into()],
        owner_account: "acct".into(),
        owner_organization: "org".into(),
        title: "Graft here".into(),
        cwd: cwd.into(),
        last_activity_ms: 1_762_000_000_000.0,
    }
}

#[test]
fn this_machines_transcripts_are_found_by_the_ids_claude_ai_lists_their_sessions_under() {
    let folder = tempfile::tempdir().unwrap();
    let root = folder.path().join("projects");
    let here = root.join("-home-me-graft");
    std::fs::create_dir_all(&here).unwrap();
    let cached_path = here.join("aaaa.jsonl");
    std::fs::write(&cached_path, "{}").unwrap();
    std::fs::write(here.join("earlier.jsonl"), "{}").unwrap();
    std::fs::write(here.join("notes.txt"), "not a transcript").unwrap();
    let stand = Stand {
        root,
        cached: vec![(cached_path.clone(), facts("cse_localone", "/home/me/graft"))],
        parsed: std::sync::Mutex::new(Vec::new()),
        homes: vec!["/home/me".into()],
    };
    let read = local_sessions::read(&stand);
    let found = read.iter().find(|s| s.bridge_ids == ["cse_localone"]).unwrap();
    assert!(found.folder_is_here && found.title == "Graft here" && found.organization == "org");
    let carried = read.iter().find(|s| s.bridge_ids == ["cse_earlier"]).unwrap();
    assert!(!carried.folder_is_here, "a transcript carried from a home this machine does not have is another machine's");
    assert!(carried.carried_on, "and one a record names as an earlier half is carried on");
    assert_eq!(*stand.parsed.lock().unwrap(), vec![here.join("earlier.jsonl")], "what the cache holds is not parsed again");
    assert_eq!(read.len(), 2, "only transcripts are read");
}
