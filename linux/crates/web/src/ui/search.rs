//! Every signed-in account's chats and Code sessions searched at once, the way
//! claude.ai's own search box searches one account's, then narrowed by kind,
//! status, where a session ran, age, account, project and star.

use super::*;
use crate::center::Change;
use crate::project_center::{ProjectCenter, ProjectChange};
use crate::projects::{highlighted, labels, AgeFilter, HitKind, KindFilter, Place, ProjectChoice, SearchFilters, SearchHit, StatusFilter};

pub const NOTE: &str = "Searches the chats of every account signed in under Artifacts with claude.ai's \
own search, and their Code sessions by title and folder. Each search asks every account once, when \
you press Enter; the filters only narrow what came back.\n\n\
Where a Code session ran is read from this computer's own transcripts. One whose transcript is here but \
which claude.ai no longer lists is shown as gone from claude.ai, and opens in the file manager. A chat \
deleted on claude.ai is not kept anywhere and cannot be found.";

const KINDS: [KindFilter; 3] = [KindFilter::All, KindFilter::Chats, KindFilter::Code];
const STATUSES: [StatusFilter; 3] = [StatusFilter::Active, StatusFilter::Archived, StatusFilter::Any];
const PLACES: [Option<Place>; 5] =
    [None, Some(Place::ThisMachine), Some(Place::OtherMachine), Some(Place::Cloud), Some(Place::GoneFromClaude)];
const AGES: [AgeFilter; 5] = [AgeFilter::Any, AgeFilter::Day, AgeFilter::Week, AgeFilter::Month, AgeFilter::Year];

struct Page {
    center: Rc<ProjectCenter>,
    header: Rc<Header>,
    body: gtk::Stack,
    query: gtk::Entry,
    go: gtk::Button,
    filter_button: gtk::MenuButton,
    filter_box: gtk::Box,
    list: gtk::ListBox,
    empty: gtk::Label,
    rows: gtk::Stack,
    foot: Rc<Foot>,
    /// Set while the filter menu is drawn, so setting its own controls is
    /// not taken for a person changing them.
    drawing: Cell<bool>,
}

pub fn search_page(center: &Rc<ProjectCenter>) -> gtk::Widget {
    let accounts = Rc::clone(&center.accounts);
    let header = Header::new(&accounts, "Search Chats", "system-search-symbolic", NOTE, "search", None);
    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    identified(&root, "search", "Search Chats");
    root.append(&header.root);
    root.append(&gtk::Separator::new(gtk::Orientation::Horizontal));

    let bar = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    bar.add_css_class("graft-head");
    let query = gtk::Entry::new();
    query.set_placeholder_text(Some("Search every account's chats"));
    query.set_hexpand(true);
    query.set_text(&center.searched().unwrap_or_default());
    identified(&query, "search.query", "Search Chats");
    bar.append(&query);
    let go = gtk::Button::with_label("Search");
    go.add_css_class("suggested-action");
    identified(&go, "search.go", "Search");
    bar.append(&go);
    let (popover, filter_box) = menu_column();
    filter_box.set_margin_start(8);
    filter_box.set_margin_end(8);
    let filter_button = icon_menu("funnel-symbolic", "search.filters", "Filters", "Filter the results", &popover);
    bar.append(&filter_button);

    let list = gtk::ListBox::new();
    let empty = gtk::Label::new(None);
    let rows = list_or_empty(&list, &empty, "search");
    let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
    content.append(&bar);
    content.append(&rows);
    let body = gtk::Stack::new();
    body.set_vexpand(true);
    body.add_named(&sign_in_prompt(&accounts, "Sign in to search chats", "system-search-symbolic", NOTE, "Search Chats", "search"), Some("signed-out"));
    body.add_named(&content, Some("accounts"));
    root.append(&body);
    let foot = Foot::new("search.status");
    root.append(&foot.root);

    let page = Rc::new(Page {
        center: Rc::clone(center),
        header,
        body,
        query,
        go,
        filter_button,
        filter_box,
        list,
        empty,
        rows,
        foot,
        drawing: Cell::new(false),
    });

    // Return held in the field starts no second search while one is running,
    // as the button starts none. Nothing is asked as the query is typed: the
    // field's changes only say whether there is anything to search for.
    let weak = Rc::downgrade(&page);
    page.query.connect_activate(move |entry| {
        if let Some(page) = weak.upgrade() {
            if !page.center.searching() {
                page.center.search(&entry.text());
            }
        }
    });
    let weak = Rc::downgrade(&page);
    page.go.connect_clicked(move |_| {
        if let Some(page) = weak.upgrade() {
            if !page.center.searching() {
                page.center.search(&page.query.text());
            }
        }
    });
    let weak = Rc::downgrade(&page);
    page.query.connect_changed(move |_| {
        if let Some(page) = weak.upgrade() {
            page.draw_button();
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
        let Some(page) = weak.upgrade() else { return };
        match change {
            ProjectChange::Results | ProjectChange::Filters => page.draw_results(),
            ProjectChange::SearchStates => page.draw(),
            ProjectChange::Projects => page.draw_filters(),
            ProjectChange::ProjectStates => {}
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
            page.query.grab_focus();
        }
    });
    page.draw();
    root.upcast()
}

impl Page {
    fn draw(self: &Rc<Self>) {
        let accounts = &self.center.accounts;
        self.header.update(accounts, false);
        self.body.set_visible_child_name(if accounts.accounts().is_empty() { "signed-out" } else { "accounts" });
        self.draw_button();
        self.draw_filters();
        self.draw_results();
    }

    fn draw_button(&self) {
        let searching = self.center.searching();
        self.go.set_sensitive(!self.query.text().trim().is_empty() && !searching);
        self.go.set_label(if searching { "Searching..." } else { "Search" });
    }

    fn summary(&self) -> Option<String> {
        if self.center.searched().is_none() || self.center.searching() {
            return None;
        }
        let shown = self.center.shown().len();
        let all = self.center.results().len();
        Some(if shown == all { format!("{shown} results") } else { format!("{shown} of {all} results") })
    }

    fn draw_results(self: &Rc<Self>) {
        let trouble = self.center.trouble(false);
        let foot = if trouble.is_empty() { self.summary() } else { Some(trouble.join(" \u{b7} ")) };
        self.foot.set(foot.as_deref());
        let filters = self.center.filters();
        let narrowed = filters.is_narrowed();
        self.filter_button.set_icon_name(if narrowed { "funnel-outline-symbolic" } else { "funnel-symbolic" });
        if narrowed {
            self.filter_button.add_css_class("accent");
        } else {
            self.filter_button.remove_css_class("accent");
        }
        let description = if narrowed { labels::describe(&filters) } else { "Filter the results".into() };
        described(&self.filter_button, &description);

        while let Some(row) = self.list.row_at_index(0) {
            self.list.remove(&row);
        }
        let shown = self.center.shown();
        if shown.is_empty() {
            self.empty.set_text(if self.center.searching() {
                "Asking claude.ai..."
            } else if self.center.searched().is_none() {
                "Type and press Enter"
            } else if self.center.results().is_empty() {
                "Nothing matches"
            } else {
                "Nothing matches these filters"
            });
            self.rows.set_visible_child_name("empty");
            return;
        }
        for hit in &shown {
            self.list.append(&self.row(hit));
        }
        self.rows.set_visible_child_name("list");
    }

    /// Every filter behind one small button beside the search button, marked
    /// while any of them narrows the list. All of them narrow what came back
    /// and ask claude.ai nothing.
    fn draw_filters(self: &Rc<Self>) {
        self.drawing.set(true);
        clear(&self.filter_box);
        let filters = self.center.filters();
        let accounts = self.center.accounts.accounts();
        self.choice(
            "Kind",
            "search.filters.kind",
            &KINDS.map(labels::kind),
            KINDS.iter().position(|k| *k == filters.kind).unwrap_or(0),
            |f, i| f.kind = KINDS[i],
        );
        self.choice(
            "Status",
            "search.filters.status",
            &STATUSES.map(labels::status),
            STATUSES.iter().position(|s| *s == filters.status).unwrap_or(0),
            |f, i| f.status = STATUSES[i],
        );
        self.choice(
            "Where",
            "search.filters.place",
            &PLACES.map(labels::place_filter),
            PLACES.iter().position(|p| *p == filters.place).unwrap_or(0),
            |f, i| f.place = PLACES[i],
        );
        self.choice(
            "Changed",
            "search.filters.age",
            &AGES.map(labels::age),
            AGES.iter().position(|a| *a == filters.age).unwrap_or(0),
            |f, i| f.age = AGES[i],
        );
        let mut names = vec!["Every Account".to_string()];
        names.extend(accounts.iter().map(|a| self.center.accounts.name_of(a.id)));
        let ids: Vec<_> = accounts.iter().map(|a| a.id).collect();
        let selected = filters.account.and_then(|a| ids.iter().position(|i| *i == a)).map(|p| p + 1).unwrap_or(0);
        let account_ids = ids.clone();
        self.choice_owned("Account", "search.filters.account", names, selected, move |f, i| {
            f.account = if i == 0 { None } else { account_ids.get(i - 1).copied() };
        });
        let projects: Vec<_> = self
            .center
            .projects()
            .into_iter()
            .filter(|p| filters.account.is_none_or(|a| p.account == a))
            .collect();
        let mut project_names = vec!["Any Project".to_string(), "No Project".to_string()];
        project_names.extend(projects.iter().map(|p| p.name.clone()));
        let project_ids: Vec<String> = projects.iter().map(|p| p.id.clone()).collect();
        let selected = match &filters.project {
            ProjectChoice::Any => 0,
            ProjectChoice::None => 1,
            ProjectChoice::Project(id) => project_ids.iter().position(|p| p == id).map(|p| p + 2).unwrap_or(0),
        };
        self.choice_owned("Project", "search.filters.project", project_names, selected, move |f, i| {
            f.project = match i {
                0 => ProjectChoice::Any,
                1 => ProjectChoice::None,
                n => project_ids.get(n - 2).cloned().map(ProjectChoice::Project).unwrap_or(ProjectChoice::Any),
            };
        });
        let starred = gtk::CheckButton::with_label("Starred Only");
        starred.set_active(filters.starred_only);
        identified(&starred, "search.filters.starred", "Starred Only");
        let weak = Rc::downgrade(self);
        starred.connect_toggled(move |check| {
            if let Some(page) = weak.upgrade() {
                if !page.drawing.get() {
                    let mut f = page.center.filters();
                    f.starred_only = check.is_active();
                    page.center.set_filters(f);
                }
            }
        });
        self.filter_box.append(&starred);
        self.filter_box.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
        let reset = gtk::Button::with_label("Reset Filters");
        reset.add_css_class("flat");
        reset.set_sensitive(filters.is_narrowed());
        identified(&reset, "search.filters.reset", "Reset Filters");
        let weak = Rc::downgrade(self);
        reset.connect_clicked(move |_| {
            if let Some(page) = weak.upgrade() {
                page.center.set_filters(SearchFilters::default());
                let again = Rc::clone(&page);
                glib::idle_add_local_once(move || again.draw_filters());
            }
        });
        self.filter_box.append(&reset);
        self.drawing.set(false);
    }

    fn choice(self: &Rc<Self>, title: &str, id: &str, labels: &[&str], selected: usize, set: fn(&mut SearchFilters, usize)) {
        self.choice_owned(title, id, labels.iter().map(|s| s.to_string()).collect(), selected, set);
    }

    fn choice_owned(
        self: &Rc<Self>,
        title: &str,
        id: &str,
        labels: Vec<String>,
        selected: usize,
        set: impl Fn(&mut SearchFilters, usize) + 'static,
    ) {
        let row = gtk::Box::new(gtk::Orientation::Horizontal, 8);
        let name = gtk::Label::new(Some(title));
        name.set_xalign(0.0);
        name.set_hexpand(true);
        row.append(&name);
        let strings: Vec<&str> = labels.iter().map(String::as_str).collect();
        let picker = gtk::DropDown::from_strings(&strings);
        picker.set_selected(selected as u32);
        identified(&picker, id, title);
        let weak = Rc::downgrade(self);
        picker.connect_selected_notify(move |picker| {
            let Some(page) = weak.upgrade() else { return };
            if page.drawing.get() {
                return;
            }
            let mut filters = page.center.filters();
            set(&mut filters, picker.selected() as usize);
            page.center.set_filters(filters);
            // The project list follows the account chosen.
            let again = Rc::clone(&page);
            glib::idle_add_local_once(move || again.draw_filters());
        });
        row.append(&picker);
        self.filter_box.append(&row);
    }

    fn subtitle(&self, hit: &SearchHit) -> String {
        let mut parts = vec![self.center.accounts.name_of(hit.account)];
        if hit.kind == HitKind::Code {
            parts.push("Code".into());
        }
        if let Some(place) = labels::place_of(hit) {
            parts.push(place.into());
        }
        if let Some(project) = self.center.project_name(hit.project.as_deref(), hit.account) {
            parts.push(project);
        }
        if hit.is_archived {
            parts.push("Archived".into());
        }
        if let Some(time) = hit.updated {
            parts.push(clock(time));
        }
        parts.join(" \u{b7} ")
    }

    fn row(&self, hit: &SearchHit) -> gtk::ListBoxRow {
        let token = hit.token();
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        line.set_margin_top(4);
        line.set_margin_bottom(4);
        line.set_margin_start(12);
        line.set_margin_end(8);
        let icon = gtk::Image::from_icon_name(labels::icon_of(hit));
        icon.add_css_class("dim-label");
        icon.set_valign(gtk::Align::Start);
        icon.set_accessible_role(gtk::AccessibleRole::Presentation);
        line.append(&icon);
        let column = gtk::Box::new(gtk::Orientation::Vertical, 2);
        column.set_hexpand(true);
        let title = gtk::Label::new(None);
        let star = if hit.is_starred { "\u{2605} " } else { "" };
        title.set_markup(&format!("{star}{}", highlighted(&hit.title, &hit.title_matches)));
        title.set_xalign(0.0);
        title.set_ellipsize(gtk::pango::EllipsizeMode::End);
        column.append(&title);
        if !hit.snippet.is_empty() {
            let snippet = gtk::Label::new(None);
            snippet.set_markup(&highlighted(&hit.snippet, &hit.snippet_matches));
            snippet.add_css_class("dim-label");
            snippet.set_xalign(0.0);
            if hit.kind == HitKind::Code {
                snippet.add_css_class("monospace");
                snippet.add_css_class("caption");
                snippet.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
            } else {
                snippet.set_wrap(true);
                snippet.set_lines(2);
                snippet.set_ellipsize(gtk::pango::EllipsizeMode::End);
            }
            column.append(&snippet);
        }
        let subtitle = self.subtitle(hit);
        let under = gtk::Label::new(Some(&subtitle));
        under.add_css_class("caption");
        under.add_css_class("dim-label");
        under.set_xalign(0.0);
        under.set_ellipsize(gtk::pango::EllipsizeMode::End);
        column.append(&under);
        identified(&column, &format!("hit.{token}"), &hit.title);
        column.update_property(&[gtk::accessible::Property::Description(&subtitle)]);
        line.append(&column);

        let (icon_name, label, help) = match &hit.open_url {
            Some(_) => ("adw-external-link-symbolic", format!("Open {}", hit.title), "Open in the browser"),
            None => ("folder-open-symbolic", format!("Show {} in Files", hit.title), "Show the transcript in the file manager"),
        };
        let open = icon_button(icon_name, &format!("hit.{token}.open"), &label, help);
        let target = hit.clone();
        open.connect_clicked(move |button| open_hit(&target, button));
        open.set_valign(gtk::Align::Center);
        line.append(&open);
        let row = gtk::ListBoxRow::new();
        row.set_child(Some(&line));
        let click = gtk::GestureClick::new();
        let target = hit.clone();
        click.connect_pressed(move |gesture, presses, _, _| {
            if presses == 2 {
                if let Some(widget) = gesture.widget() {
                    open_hit(&target, &widget);
                }
            }
        });
        row.add_controller(click);
        row
    }
}

/// A session claude.ai no longer has is shown where its transcript is.
pub fn open_hit(hit: &SearchHit, widget: &impl IsA<gtk::Widget>) {
    if let Some(url) = &hit.open_url {
        open_url(url, widget);
    } else if let Some(transcript) = &hit.transcript {
        show_in_files(transcript, widget);
    }
}
