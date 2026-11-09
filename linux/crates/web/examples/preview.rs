//! The three pages at the smallest window, drawn from sample rows with no
//! claude.ai behind them, for a person checking how they fit:
//!
//!     ./sdk.sh cargo run -p graft-web --example preview
//!
//! Nothing here signs in, loads a page or touches a real profile; the centers
//! are the offline ones the layout checks use.

#![allow(clippy::single_range_in_vec_init)]

use adw::prelude::*;
use graft_web::model::{Artifact, ArtifactAccount, ArtifactGroup, ArtifactRole};
use graft_web::projects::{ClaudeProject, HitKind, Place, SearchHit};
use graft_web::{ArtifactCenter, ProjectCenter};
use std::time::{Duration, SystemTime};
use uuid::Uuid;

fn main() {
    let app = adw::Application::builder().application_id("io.github.claudegraft.WebPreview").build();
    app.connect_activate(|app| {
        let mine = Uuid::new_v4();
        let lent = Uuid::new_v4();
        let accounts = vec![
            ArtifactAccount { id: mine, email: Some("me@example.com".into()), account_uuid: Some("a".into()), org: Some("o".into()) },
            ArtifactAccount { id: lent, email: Some("work@example.com".into()), account_uuid: Some("b".into()), org: Some("p".into()) },
        ];
        let now = SystemTime::now();
        let artifacts: Vec<Artifact> = (0..14)
            .map(|n| Artifact {
                id: format!("{n:08}-0000-4000-8000-000000000000"),
                account: if n % 3 == 0 { lent } else { mine },
                org: "o".into(),
                title: format!("Sample artifact number {n} with a longer title than most"),
                kind: "page".into(),
                open_url: Some("https://claude.ai/artifact/sample".into()),
                audience: ["owner", "users", "public", "org"][n % 4].into(),
                rel: if n % 5 == 0 { "shared".into() } else { "mine".into() },
                sort_time: Some(now - Duration::from_secs(3600 * n as u64)),
                owner_account: None,
                renamable: true,
                duplicable: n % 2 == 0,
                duplicate_available: true,
            })
            .collect();
        let mut group = ArtifactGroup::new(Uuid::new_v4(), "Design", vec!["a@example.com".into(), "b@example.com".into()]);
        group.set_role(ArtifactRole::Editor, "a@example.com");
        let center = ArtifactCenter::offline(accounts, artifacts, vec![group]);
        let projects: Vec<ClaudeProject> = (0..10)
            .map(|n| ClaudeProject {
                id: format!("p{n:07}"),
                account: if n % 2 == 0 { mine } else { lent },
                name: format!("Project {n}"),
                summary: "What it is for".into(),
                is_private: n % 3 != 0,
                is_starred: n == 1,
                is_example: n == 9,
                creator: None,
                updated: Some(now - Duration::from_secs(86_400 * n as u64)),
            })
            .collect();
        let hits: Vec<SearchHit> = (0..12)
            .map(|n| SearchHit {
                id: format!("c-{n}"),
                account: if n % 2 == 0 { mine } else { lent },
                kind: if n % 3 == 0 { HitKind::Code } else { HitKind::Chat },
                title: format!("Raspberry Pi camera, part {n}"),
                snippet: "set up the raspberry camera on the pico".into(),
                snippet_matches: vec![13..22],
                title_matches: vec![0..9],
                project: None,
                updated: Some(now - Duration::from_secs(7200 * n as u64)),
                is_archived: false,
                is_starred: n == 2,
                place: (n % 3 == 0).then_some(Place::ThisMachine),
                folder: None,
                transcript: None,
                rank: n,
                open_url: Some("https://claude.ai/chat/sample".into()),
            })
            .collect();
        let project_center = ProjectCenter::offline(center.clone(), projects, hits, Some("raspberry".into()));

        let stack = adw::ViewStack::new();
        stack.add_titled_with_icon(&graft_web::ui::artifacts_page(&center), Some("artifacts"), "Artifacts", "view-paged-symbolic");
        stack.add_titled_with_icon(&graft_web::ui::projects_page(&project_center), Some("projects"), "Projects", "folder-symbolic");
        stack.add_titled_with_icon(&graft_web::ui::search_page(&project_center), Some("search"), "Search Chats", "system-search-symbolic");
        let switcher = adw::ViewSwitcher::builder().stack(&stack).policy(adw::ViewSwitcherPolicy::Wide).build();
        let header = adw::HeaderBar::builder().title_widget(&switcher).build();
        let toolbar = adw::ToolbarView::new();
        toolbar.add_top_bar(&header);
        toolbar.set_content(Some(&stack));
        let window = adw::ApplicationWindow::builder()
            .application(app)
            .title("Graft web pages")
            .default_width(820)
            .default_height(560)
            .content(&toolbar)
            .build();
        window.present();
    });
    app.run_with_args::<&str>(&[]);
}
