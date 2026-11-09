//! Every signed-in account's projects in one list. It only lists: a project is
//! opened on claude.ai, and changed there.

use super::*;
use crate::center::Change;
use crate::project_center::{ProjectCenter, ProjectChange};
use crate::projects::ClaudeProject;

pub const NOTE: &str = "Projects are listed from every account signed in under Artifacts, with the \
same sign-in. Graft only lists them; opening one takes you to claude.ai, where it is changed.\n\n\
claude.ai is asked when this page is shown and when you refresh it, never in the background.";

struct Page {
    center: Rc<ProjectCenter>,
    header: Rc<Header>,
    body: gtk::Stack,
    filter: gtk::SearchEntry,
    count: gtk::Label,
    list: gtk::ListBox,
    empty: gtk::Label,
    rows: gtk::Stack,
    foot: Rc<Foot>,
}

/// The Projects page. It asks claude.ai each time it is shown and when its
/// refresh button is pressed, never on a timer.
pub fn projects_page(center: &Rc<ProjectCenter>) -> gtk::Widget {
    let accounts = Rc::clone(&center.accounts);
    let weak = Rc::downgrade(center);
    let header = Header::new(
        &accounts,
        "Projects",
        "folder-symbolic",
        NOTE,
        "projects",
        Some(Refresh {
            label: "Refresh Projects",
            help: "Ask claude.ai for every account's projects again",
            run: Box::new(move || {
                if let Some(center) = weak.upgrade() {
                    center.refresh_projects();
                }
            }),
        }),
    );
    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    identified(&root, "projects", "Projects");
    root.append(&header.root);
    root.append(&gtk::Separator::new(gtk::Orientation::Horizontal));

    let filters = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    filters.add_css_class("graft-head");
    let filter = gtk::SearchEntry::new();
    filter.set_placeholder_text(Some("Filter"));
    filter.set_width_chars(24);
    identified(&filter, "projects.filter", "Filter Projects");
    filters.append(&filter);
    let count = gtk::Label::new(None);
    count.add_css_class("caption");
    count.add_css_class("dim-label");
    count.set_hexpand(true);
    count.set_xalign(1.0);
    identified(&count, "projects.count", "Projects shown");
    filters.append(&count);
    let list = gtk::ListBox::new();
    let empty = gtk::Label::new(None);
    let rows = list_or_empty(&list, &empty, "projects");
    let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
    content.append(&filters);
    content.append(&rows);

    let body = gtk::Stack::new();
    body.set_vexpand(true);
    body.add_named(&sign_in_prompt(&accounts, "Sign in to see projects", "folder-symbolic", NOTE, "Projects", "projects"), Some("signed-out"));
    body.add_named(&content, Some("accounts"));
    root.append(&body);
    let foot = Foot::new("projects.status");
    root.append(&foot.root);

    let page = Rc::new(Page { center: Rc::clone(center), header, body, filter, count, list, empty, rows, foot });
    let weak = Rc::downgrade(&page);
    page.filter.connect_search_changed(move |_| {
        if let Some(page) = weak.upgrade() {
            page.draw_list();
        }
    });
    let weak = Rc::downgrade(&page);
    watch(&accounts, &root, move |change| {
        if let Some(page) = weak.upgrade() {
            if matches!(change, Change::Accounts | Change::States | Change::Names | Change::SignedIn(_)) {
                page.draw();
            }
        }
    });
    let weak = Rc::downgrade(&page);
    let id = center.subscribe(move |change| {
        if let Some(page) = weak.upgrade() {
            if matches!(change, ProjectChange::Projects | ProjectChange::ProjectStates) {
                page.draw();
            }
        }
    });
    let weak_center = Rc::downgrade(center);
    let keep = Rc::clone(&page);
    root.connect_destroy(move |_| {
        let _ = &keep;
        if let Some(center) = weak_center.upgrade() {
            center.unsubscribe(id);
        }
    });
    let weak = Rc::downgrade(&page);
    root.connect_map(move |_| {
        if let Some(page) = weak.upgrade() {
            page.center.refresh_projects();
        }
    });
    page.draw();
    root.upcast()
}

impl Page {
    fn draw(self: &Rc<Self>) {
        let accounts = &self.center.accounts;
        self.header.update(accounts, self.center.loading_projects());
        self.body.set_visible_child_name(if accounts.accounts().is_empty() { "signed-out" } else { "accounts" });
        let trouble = self.center.trouble(true).join(" \u{b7} ");
        self.foot.set(if trouble.is_empty() { None } else { Some(&trouble) });
        self.draw_list();
    }

    fn shown(&self) -> Vec<ClaudeProject> {
        let query = self.filter.text().trim().to_lowercase();
        self.center
            .projects()
            .into_iter()
            .filter(|p| query.is_empty() || p.name.to_lowercase().contains(&query) || p.summary.to_lowercase().contains(&query))
            .collect()
    }

    fn draw_list(self: &Rc<Self>) {
        let shown = self.shown();
        self.count.set_text(&format!("{} projects", shown.len()));
        while let Some(row) = self.list.row_at_index(0) {
            self.list.remove(&row);
        }
        if shown.is_empty() {
            self.empty.set_text(if self.center.loading_projects() {
                "Asking claude.ai..."
            } else if self.filter.text().is_empty() {
                "No projects"
            } else {
                "Nothing matches"
            });
            self.rows.set_visible_child_name("empty");
            return;
        }
        for project in &shown {
            self.list.append(&self.row(project));
        }
        self.rows.set_visible_child_name("list");
    }

    fn subtitle(&self, project: &ClaudeProject) -> String {
        let mut parts = vec![self.center.accounts.name_of(project.account)];
        if project.is_example {
            parts.push("Example".into());
        }
        if let Some(time) = project.updated {
            parts.push(clock(time));
        }
        parts.join(" \u{b7} ")
    }

    fn row(&self, project: &ClaudeProject) -> gtk::ListBoxRow {
        let token: String = project.id.chars().take(8).collect();
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        line.set_margin_top(4);
        line.set_margin_bottom(4);
        line.set_margin_start(12);
        line.set_margin_end(8);
        let icon = gtk::Image::from_icon_name(if project.is_starred { "starred-symbolic" } else { "folder-symbolic" });
        if project.is_starred {
            icon.add_css_class("warning");
        } else {
            icon.add_css_class("dim-label");
        }
        icon.set_accessible_role(gtk::AccessibleRole::Presentation);
        line.append(&icon);
        let text = title_and_subtitle(&project.name, &self.subtitle(project), false, &format!("project.{token}"));
        if !project.summary.is_empty() {
            text.set_tooltip_text(Some(&project.summary));
        }
        line.append(&text);
        if !project.is_private {
            let shared = gtk::Image::from_icon_name("system-users-symbolic");
            shared.add_css_class("dim-label");
            shared.set_tooltip_text(Some("Shared with your organization"));
            shared.set_accessible_role(gtk::AccessibleRole::Presentation);
            line.append(&shared);
        }
        let open = icon_button("adw-external-link-symbolic", &format!("project.{token}.open"), &format!("Open {}", project.name), "Open in the browser");
        let url = project.open_url();
        open.connect_clicked(move |button| open_url(&url, button));
        line.append(&open);
        let row = gtk::ListBoxRow::new();
        row.set_child(Some(&line));
        // A double click opens it, as a row of claude.ai's own list does.
        let click = gtk::GestureClick::new();
        let url = project.open_url();
        click.connect_pressed(move |gesture, presses, _, _| {
            if presses == 2 {
                if let Some(widget) = gesture.widget() {
                    open_url(&url, &widget);
                }
            }
        });
        row.add_controller(click);
        row
    }
}
