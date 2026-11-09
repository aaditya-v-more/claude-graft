//! Exports, the usage sign-in, and the rules held against the source itself,
//! as the Mac suite holds them against its own.

use std::path::Path;

// MARK: - Exports

#[test]
fn an_export_serves_the_page_only_its_own_files_and_none_from_outside_its_folder() {
    let folder = tempfile::tempdir().unwrap();
    let inside = folder.path().join("export");
    std::fs::create_dir_all(inside.join("img")).unwrap();
    std::fs::write(inside.join("index.html"), "<p>hi</p>").unwrap();
    std::fs::write(inside.join("img/a b.png"), b"\x89PNG\r\n\x1a\n").unwrap();
    std::fs::write(folder.path().join("outside.html"), "secret").unwrap();
    let token = crate::export::serving(&inside);
    let (page, mime) = crate::export::serve(&format!("graft-export://{token}/index.html")).unwrap();
    assert_eq!(page, b"<p>hi</p>");
    assert_eq!(mime, "text/html");
    assert!(crate::export::serve(&format!("graft-export://{token}/img/a%20b.png")).is_some(), "a file is found by its address");
    assert!(crate::export::serve(&format!("graft-export://{token}/../outside.html")).is_none());
    assert!(crate::export::serve(&format!("graft-export://{token}/%2E%2E/outside.html")).is_none());
    assert!(crate::export::serve("graft-export://someone-else/index.html").is_none(), "nor another export's");
}

#[test]
fn an_export_is_drawn_following_no_link_out_of_its_folder() {
    assert!(crate::export::is_inside("graft-export://abc/index.html", "abc"));
    assert!(crate::export::is_inside("about:blank", "abc"));
    assert!(!crate::export::is_inside("https://example.com/", "abc"));
    assert!(!crate::export::is_inside("graft-export://other/index.html", "abc"));
    assert!(!crate::export::is_inside("file:///etc/passwd", "abc"));
}

fn unzip(bytes: &[u8]) -> std::collections::BTreeMap<String, String> {
    use std::io::Read;
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
    let mut out = std::collections::BTreeMap::new();
    for index in 0..archive.len() {
        let mut file = archive.by_index(index).unwrap();
        let mut text = String::new();
        file.read_to_string(&mut text).unwrap();
        out.insert(file.name().to_string(), text);
    }
    out
}

#[test]
fn a_page_of_several_files_is_zipped_at_the_paths_it_was_read_into() {
    let folder = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(folder.path().join("js")).unwrap();
    std::fs::write(folder.path().join("index.html"), "page").unwrap();
    std::fs::write(folder.path().join("js/app.js"), "code").unwrap();
    let files = unzip(&crate::export::zip_folder(folder.path()).unwrap());
    assert_eq!(files.keys().cloned().collect::<Vec<_>>(), vec!["index.html", "js/app.js"]);
    assert_eq!(files["js/app.js"], "code");
}

#[test]
fn a_word_export_is_a_document_word_can_open_holding_the_page_as_drawn() {
    let page: crate::docx::Page = serde_json::from_str(
        r#"{"title":"Plan & notes","blocks":[
            {"type":"heading","level":1,"runs":[{"text":"Q3 <plan>"}]},
            {"type":"paragraph","runs":[{"text":"Bold","bold":true},{"text":" and "},{"text":"a link","link":"https://example.com/?a=1&b=2"}]},
            {"type":"item","ordered":true,"level":0,"runs":[{"text":"first"}]},
            {"type":"item","ordered":true,"level":0,"runs":[{"text":"second"}]},
            {"type":"paragraph","runs":[{"text":"between"}]},
            {"type":"item","ordered":true,"level":0,"runs":[{"text":"again"}]},
            {"type":"code","text":"let x = 1;\nlet y = 2;"},
            {"type":"rule"},
            {"type":"table","rows":[[{"header":true,"runs":[{"text":"A"}]},{"runs":[{"text":"B"}]}]]}
        ]}"#,
    )
    .unwrap();
    let files = unzip(&crate::docx::document(&page));
    for part in [
        "[Content_Types].xml",
        "_rels/.rels",
        "word/document.xml",
        "word/styles.xml",
        "word/numbering.xml",
        "word/_rels/document.xml.rels",
    ] {
        assert!(files.contains_key(part), "{part} is in the package");
    }
    let document = &files["word/document.xml"];
    assert!(document.contains("Q3 &lt;plan&gt;"), "text is written as text, never markup");
    assert!(document.contains("<w:pStyle w:val=\"Heading1\"/>"));
    assert!(document.contains("<w:b/>"));
    assert!(document.contains("<w:hyperlink r:id=\"rIdLink1\">"));
    assert!(files["word/_rels/document.xml.rels"].contains("https://example.com/?a=1&amp;b=2"));
    assert!(
        document.contains("<w:numId w:val=\"2\"/>") && document.contains("<w:numId w:val=\"3\"/>"),
        "each ordered list counts from one"
    );
    assert!(document.contains("<w:tbl>"));
    assert!(files["docProps/core.xml"].contains("Plan &amp; notes"));
}

// MARK: - The rules held against the source

fn sources() -> Vec<(String, String)> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    let mut out = Vec::new();
    let mut stack = vec![root.clone()];
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(&dir).unwrap().flatten() {
            let path = entry.path();
            let name = path.strip_prefix(&root).unwrap().to_string_lossy().into_owned();
            if path.is_dir() {
                stack.push(path);
            } else if (name.ends_with(".rs") || name.ends_with(".js")) && name != "tests.rs" && name != "rule_tests.rs" {
                out.push((name, std::fs::read_to_string(&path).unwrap()));
            }
        }
    }
    out
}

fn source(name: &str) -> String {
    sources().into_iter().find(|(n, _)| n == name).map(|(_, s)| s).unwrap_or_default()
}

#[test]
fn the_claude_ai_sign_in_is_never_read_out_of_its_web_store() {
    let all = sources();
    assert!(all.len() > 10);
    for (name, text) in all {
        assert!(!text.contains("cookie_manager") && !text.contains("CookieManager"), "{name} reads no cookie");
    }
}

#[test]
fn artifact_requests_go_from_claude_ais_own_page_never_from_a_request_graft_builds_itself() {
    let session = source("session.rs");
    assert!(session.contains(r#"credentials: "same-origin""#));
    assert!(session.contains("call_async_javascript_function_future"));
    for (name, text) in sources() {
        if name == "pane_signin.rs" {
            continue;
        }
        assert!(
            !text.contains("soup::Session") && !text.contains("reqwest") && !text.contains("ureq"),
            "{name} builds no request of its own"
        );
    }
}

#[test]
fn the_artifact_services_headers_are_added_by_path_in_the_one_place_requests_go_out() {
    let mut users: Vec<String> = sources().into_iter().filter(|(_, s)| s.contains("headers_for(")).map(|(n, _)| n).collect();
    users.sort();
    assert_eq!(users, vec!["api.rs", "session.rs"]);
    assert!(source("session.rs").contains("api::headers_for(path)"));
}

#[test]
fn an_export_is_drawn_where_no_sign_in_is_and_its_files_are_read_with_no_cookie() {
    let export = source("export.rs");
    assert!(export.contains("NetworkSession::new_ephemeral()") && !export.contains("network_session(account"));
    assert!(export.contains("connect_decide_policy"));
    assert!(source("session.rs").contains(r#"credentials: "omit""#));
}

#[test]
fn an_export_file_larger_than_what_is_left_of_the_budget_is_refused_in_the_page_before_it_is_encoded_there() {
    let session = source("session.rs");
    assert!(session.contains(r#"response.headers.get("Content-Length")"#) && session.contains("if (size > limit)"));
    assert!(source("center.rs").contains("download(&url, api::MAXIMUM_EXPORT_BYTES.saturating_sub(total))"));
}

#[test]
fn artifacts_projects_and_searches_are_asked_for_when_a_person_asks_never_on_a_timer() {
    for name in ["center.rs", "project_center.rs", "ui/mod.rs", "ui/artifacts.rs", "ui/projects.rs", "ui/search.rs", "ui/groups.rs"] {
        let text = source(name);
        assert!(!text.is_empty(), "{name} is read");
        assert!(!text.contains("timeout_add") && !text.contains("timeout_future"), "{name} keeps no timer");
    }
}

#[test]
fn a_search_goes_out_on_enter_or_the_button_never_as_the_query_is_typed() {
    let search = source("ui/search.rs");
    assert_eq!(search.matches("page.center.search(").count(), 2);
    assert_eq!(
        search.matches("if !page.center.searching() {\n                page.center.search(").count(),
        2,
        "Enter held in the field starts no second search while one is running, as the button starts none"
    );
    let changed = search.split("page.query.connect_changed").nth(1).unwrap_or("");
    let handler = changed.split("});").next().unwrap_or("");
    assert!(!handler.contains(".search("), "typing only says whether there is anything to search for");
}

#[test]
fn a_page_whose_process_died_is_dropped_and_none_waits_for_good() {
    let session = source("session.rs");
    assert!(session.contains("connect_web_process_terminated"));
    assert!(session.contains("!inner.loading.is_empty() && !inner.closed"));
    assert!(session.contains("if inner.serial != serial || inner.view.is_none()"));
    assert!(session.contains("glib::timeout_future(LOAD_LIMIT)"));
}

#[test]
fn a_store_is_removed_only_once_every_page_on_it_has_let_go_and_nothing_builds_a_new_one_on_it_meanwhile() {
    let center = source("center.rs");
    let retire = center.split("fn retire(").nth(1).unwrap_or("");
    let close = retire.find("session.close()").unwrap();
    let released = retire.find("session.released().await").unwrap();
    let forget = retire.find("session::forget(id).await").unwrap();
    assert!(close < released && released < forget);
    assert!(source("session.rs").contains("if inner.closed {\n                return Err(Failure::SignedOut);"));
    assert!(center.contains("if self.account(id).is_none() {\n            session.close();"));
}

#[test]
fn a_sign_in_window_lets_go_of_its_pages_before_its_sign_in_is_acted_on_and_signing_out_closes_it() {
    let signin = source("signin.rs");
    let release = signin.find("self.release();\n            let done = self.done.borrow_mut().take();").unwrap();
    assert!(signin[release..].contains("glib::idle_add_local_once(move || done(identity))"));
    assert!(signin.contains("connect_uri_notify"), "a sign-in that ends by moving inside claude.ai's page is noticed");
    assert!(signin.contains("connect_create") && signin.contains("connect_close"), "a provider's sign-in gets a window of its own");
    assert!(source("center.rs").contains("if state.sign_in.as_ref().is_some_and(|w| w.account == id) {"));
}

#[test]
fn a_store_that_will_not_be_removed_is_written_down_and_tried_again_never_dropped_in_silence() {
    assert!(source("session.rs").contains(r#""artifacts.store removal failed""#));
}

#[test]
fn stores_no_listed_account_is_under_are_removed_at_launch_and_only_against_an_accounts_file_that_was_read() {
    let center = source("center.rs");
    assert!(center.contains("if loaded.accounts_were_read {") && center.contains("files::stray_stores("));
}

#[test]
fn a_list_a_sign_in_has_since_superseded_writes_nothing_over_the_one_that_followed_it() {
    let center = source("center.rs");
    assert!(center.contains(
        "if state.generations.get(&id) != Some(&generation) || !state.accounts.iter().any(|a| a.id == id) {"
    ));
    assert!(center.contains("this.load(id, true).await"));
}

#[test]
fn a_refused_invitation_is_counted_and_the_rest_still_go_out_only_a_sign_out_stops_them() {
    let center = source("center.rs");
    let share = center.split("async fn share_now").nth(1).unwrap_or("");
    assert!(share.contains("Err(Failure::SignedOut) => return self.message(Failure::SignedOut, artifact.account),"));
    assert!(share.contains("refusal.get_or_insert(\"unanswered\".into());"));
}

#[test]
fn choosing_the_entry_already_checked_sends_nothing_and_an_organizations_access_is_never_dropped_unasked() {
    let page = source("ui/artifacts.rs");
    assert!(page.contains("check.set_active(choice == current.menu_choice());"));
    assert!(page.contains("choice == current.menu_choice() || choice == ArtifactAccess::Organization"));
    let center = source("center.rs");
    assert!(center.contains("if !artifact.access().changes(public) || !self.begin(&key) {"));
    assert!(center.contains("if !ArtifactAccess::from_audience(mode).changes(public) {"));
}

#[test]
fn every_way_into_a_sign_in_says_to_use_an_emailed_code() {
    assert!(source("ui/mod.rs").contains("SIGN_IN_HINT") && source("signin.rs").contains("SIGN_IN_HINT"));
}

#[test]
fn one_sign_in_serves_every_page_and_projects_keep_no_sign_in_of_their_own() {
    let projects = source("project_center.rs");
    assert!(projects.contains("Change::Accounts => this.accounts_changed()"));
    assert!(projects.contains("Change::SignedIn(id) => this.signed_in(id)"));
    for name in ["project_center.rs", "ui/projects.rs", "ui/search.rs"] {
        assert!(!source(name).contains("SignInWindow"), "{name} opens no sign-in of its own");
    }
}

#[test]
fn an_account_signed_out_while_its_list_or_search_was_running_does_not_come_back_with_what_it_found() {
    let projects = source("project_center.rs");
    assert!(projects.contains("if this.inner.borrow().search_generation != generation || !this.listed(id) {"));
    assert!(projects.contains("// Signed out while this ran: what it found goes with the account.\n        if !self.listed(id) {"));
}

#[test]
fn a_search_reads_the_sweeps_transcript_cache_and_never_writes_it() {
    let local = source("local_sessions.rs");
    assert!(!local.contains("fs::write") && !local.contains("atomic_write") && !local.contains("File::create"));
}

#[test]
fn an_account_whose_code_session_list_is_refused_still_has_its_chats_searched_and_is_not_signed_out_for_it() {
    let projects = source("project_center.rs");
    let code = projects.split("async fn code_sessions").nth(1).unwrap_or("");
    let walk = code.split("async fn local_sessions").next().unwrap_or("");
    assert!(!walk.contains("?;") && !walk.contains("SignedOut"));
}

#[test]
fn every_control_is_named_by_hand_with_an_identifier_of_its_own() {
    for name in ["ui/artifacts.rs", "ui/projects.rs", "ui/search.rs", "ui/groups.rs"] {
        let text = source(name);
        let made = text.matches("gtk::Button::").count()
            + text.matches("gtk::DropDown::").count()
            + text.matches("gtk::Entry::new").count()
            + text.matches("gtk::CheckButton::").count()
            + text.matches("gtk::SearchEntry::new").count();
        let named = text.matches("identified(&").count();
        assert!(named >= made, "{name}: {named} named of {made} made");
    }
}

// MARK: - Signing an account in for its usage

#[test]
fn a_sign_ins_secrets_are_thirty_two_random_bytes_written_the_way_a_url_can_carry_them() {
    let verifier = crate::pane_signin::random_token();
    assert_eq!(verifier.len(), 43);
    assert!(!verifier.contains(['+', '/', '=']));
    assert_ne!(verifier, crate::pane_signin::random_token());
}

#[test]
fn an_account_is_signed_in_for_model_requests_alone_back_to_this_machine() {
    use crate::pane_signin::*;
    let verifier = random_token();
    let url = authorize_url("http://localhost:4321/callback", &verifier, "st");
    assert!(url.starts_with("https://claude.com/cai/oauth/authorize?"));
    let query = callback_query(&format!("GET /callback?{} HTTP/1.1\r\n\r\n", url.split_once('?').unwrap().1)).unwrap();
    let item = |k: &str| query.get(k).and_then(|v| v.as_str()).map(str::to_string);
    assert_eq!(item("scope").as_deref(), Some("user:inference"));
    assert_eq!(item("redirect_uri").as_deref(), Some("http://localhost:4321/callback"));
    assert_eq!(item("state").as_deref(), Some("st"));
    assert_eq!(item("client_id").as_deref(), Some(CLIENT_ID));
    assert_eq!(
        item("code_challenge"),
        Some(challenge(&verifier)),
        "the code that comes back is worth nothing without the secret that stayed here"
    );
    assert_eq!(item("code_challenge_method").as_deref(), Some("S256"));
    assert!(!url.contains(&verifier), "which never leaves this machine");
    assert!(!url.contains("localhost:4321/"), "a slash in a value is never left bare");
}

#[test]
fn the_page_the_browser_comes_back_to_shows_text_never_markup() {
    assert!(crate::pane_signin::page("<b>&").contains("&lt;b&gt;&amp;"));
}

#[test]
fn only_the_callback_is_answered_and_a_favicon_is_not() {
    assert!(crate::pane_signin::callback_query("GET /favicon.ico HTTP/1.1\r\n\r\n").is_none());
    assert!(crate::pane_signin::callback_query("POST /callback?code=x HTTP/1.1\r\n\r\n").is_none());
}

#[test]
fn the_renewing_token_a_sign_in_returns_is_never_read() {
    assert!(!source("pane_signin.rs").contains("\"refresh_token\""));
}

#[test]
fn a_page_is_asked_from_only_when_it_is_claude_ais() {
    assert_eq!(crate::session::host_of("https://claude.ai/api/account"), Some("claude.ai"));
    assert_eq!(crate::session::host_of("https://claude.ai.evil.example/x"), Some("claude.ai.evil.example"));
    assert_eq!(crate::session::host_of("https://user@claude.ai:443/"), Some("claude.ai"));
    assert_eq!(crate::session::host_of("about:blank"), None);
}
