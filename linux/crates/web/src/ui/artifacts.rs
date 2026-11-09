//! Every signed-in account's artifacts in one list, with who can open each and
//! a way to share it with a group in one go.

use super::*;
use crate::center::Change;
use crate::model::{Artifact, ArtifactAccess, ExportFormat};
use adw::prelude::*;

pub const NOTE: &str = "Graft signs in to claude.ai once for each account, in a window of its own, \
and keeps that sign-in in a web store of its own for that account. One sign-in serves Artifacts, \
Projects and Search Chats alike. It is used only by claude.ai's own pages and is never read by Graft \
or sent anywhere else. Signing out deletes it.\n\n\
Sign in with an emailed code: enter the account's address on claude.ai's page, choose Continue with \
email, and type the code claude.ai sends. That is the one way of signing in Graft supports.\n\n\
Groups are kept by Graft on this computer. Sharing with a group invites each person with the role the \
group gives them, and claude.ai emails each one, as it does when you invite them from the artifact's \
own Share panel. Where an artifact takes no commenters or editors, they are invited with the most it \
does take, never more.";

#[derive(Clone, Copy, PartialEq, Eq)]
enum Filter {
    All,
    Mine,
    Shared,
}

struct Page {
    center: Rc<ArtifactCenter>,
    header: Rc<Header>,
    body: gtk::Stack,
    filter: Cell<Filter>,
    search: gtk::SearchEntry,
    count: gtk::Label,
    list: gtk::ListBox,
    empty: gtk::Label,
    rows: gtk::Stack,
    foot: Rc<Foot>,
    groups: gtk::Button,
}

/// The Artifacts page. It asks claude.ai each time it is shown and when its
/// refresh button is pressed, never on a timer.
pub fn artifacts_page(center: &Rc<ArtifactCenter>) -> gtk::Widget {
    let weak = Rc::downgrade(center);
    let header = Header::new(
        center,
        "Artifacts",
        "view-paged-symbolic",
        NOTE,
        "artifacts",
        Some(Refresh {
            label: "Refresh Artifacts",
            help: "Ask claude.ai for every account's artifacts again",
            run: Box::new(move || {
                if let Some(center) = weak.upgrade() {
                    center.refresh();
                }
            }),
        }),
    );
    let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
    identified(&root, "artifacts", "Artifacts");
    root.append(&header.root);
    root.append(&gtk::Separator::new(gtk::Orientation::Horizontal));

    let filters = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    filters.add_css_class("graft-head");
    let show = gtk::DropDown::from_strings(&["All", "Yours", "Shared with You"]);
    identified(&show, "artifacts.filter", "Show");
    filters.append(&show);
    let search = gtk::SearchEntry::new();
    search.set_placeholder_text(Some("Search"));
    search.set_width_chars(24);
    identified(&search, "artifacts.search", "Search Artifacts");
    filters.append(&search);
    let count = gtk::Label::new(None);
    count.add_css_class("caption");
    count.add_css_class("dim-label");
    count.set_hexpand(true);
    count.set_xalign(1.0);
    identified(&count, "artifacts.count", "Artifacts shown");
    filters.append(&count);

    let list = gtk::ListBox::new();
    let empty = gtk::Label::new(None);
    let rows = list_or_empty(&list, &empty, "artifacts");
    let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
    content.append(&filters);
    content.append(&rows);

    let body = gtk::Stack::new();
    body.set_vexpand(true);
    body.add_named(
        &sign_in_prompt(center, "Sign in to see artifacts", "view-paged-symbolic", NOTE, "Artifacts", "artifacts"),
        Some("signed-out"),
    );
    body.add_named(&content, Some("accounts"));
    root.append(&body);

    let foot = Foot::new("artifacts.status");
    let groups = gtk::Button::new();
    identified(&groups, "artifacts.groups", "Groups");
    described(&groups, "Make and change the groups artifacts can be shared with");
    foot.set_end(&groups);
    root.append(&foot.root);

    let page = Rc::new(Page {
        center: Rc::clone(center),
        header,
        body,
        filter: Cell::new(Filter::All),
        search,
        count,
        list,
        empty,
        rows,
        foot,
        groups,
    });

    let weak = Rc::downgrade(&page);
    show.connect_selected_notify(move |dropdown| {
        if let Some(page) = weak.upgrade() {
            page.filter.set(match dropdown.selected() {
                1 => Filter::Mine,
                2 => Filter::Shared,
                _ => Filter::All,
            });
            page.draw_list();
        }
    });
    // Narrowing what is listed asks claude.ai nothing.
    let weak = Rc::downgrade(&page);
    page.search.connect_search_changed(move |_| {
        if let Some(page) = weak.upgrade() {
            page.draw_list();
        }
    });
    let weak = Rc::downgrade(&page);
    page.groups.connect_clicked(move |button| {
        if let Some(page) = weak.upgrade() {
            super::groups::present(&page.center, button);
        }
    });

    let weak = Rc::downgrade(&page);
    watch(center, &root, move |change| {
        let Some(page) = weak.upgrade() else { return };
        match change {
            Change::Status => page.draw_status(),
            Change::Groups => {
                page.draw_groups_button();
                page.draw_list();
            }
            Change::Busy => page.draw_list(),
            _ => page.draw(),
        }
    });
    let weak = Rc::downgrade(&page);
    root.connect_map(move |_| {
        if let Some(page) = weak.upgrade() {
            page.center.refresh();
        }
    });
    page.draw();
    page.draw_status();
    page.draw_groups_button();
    // Kept alive by the closures above for as long as the page lives.
    let keep = Rc::clone(&page);
    root.connect_destroy(move |_| {
        let _ = &keep;
    });
    root.upcast()
}

impl Page {
    fn draw(self: &Rc<Self>) {
        self.header.update(&self.center, self.center.any_loading());
        self.body.set_visible_child_name(if self.center.accounts().is_empty() { "signed-out" } else { "accounts" });
        self.draw_list();
    }

    fn draw_status(&self) {
        self.foot.set(self.center.status().as_deref());
    }

    fn draw_groups_button(&self) {
        let content = adw::ButtonContent::builder()
            .icon_name("system-users-symbolic")
            .label(format!("Groups ({})", self.center.groups().len()))
            .build();
        self.groups.set_child(Some(&content));
    }

    fn shown(&self) -> Vec<Artifact> {
        let query = self.search.text().trim().to_lowercase();
        self.center
            .artifacts()
            .into_iter()
            .filter(|a| match self.filter.get() {
                Filter::All => true,
                Filter::Mine => a.is_own(),
                Filter::Shared => !a.is_own(),
            })
            .filter(|a| query.is_empty() || a.title.to_lowercase().contains(&query))
            .collect()
    }

    fn draw_list(self: &Rc<Self>) {
        let shown = self.shown();
        self.count.set_text(&format!("{} artifacts", shown.len()));
        while let Some(row) = self.list.row_at_index(0) {
            self.list.remove(&row);
        }
        if shown.is_empty() {
            self.empty.set_text(if self.center.any_loading() {
                "Asking claude.ai..."
            } else if self.search.text().is_empty() {
                "No artifacts"
            } else {
                "Nothing matches"
            });
            self.rows.set_visible_child_name("empty");
            return;
        }
        for artifact in &shown {
            self.list.append(&self.row(artifact));
        }
        self.rows.set_visible_child_name("list");
    }

    fn subtitle(&self, artifact: &Artifact) -> String {
        let who = self.center.name_of(artifact.account);
        let owner = if artifact.is_own() { who } else { format!("Shared with {who}") };
        match artifact.sort_time {
            Some(time) => format!("{owner} \u{b7} {}", clock(time)),
            None => owner,
        }
    }

    fn row(self: &Rc<Self>, artifact: &Artifact) -> gtk::ListBoxRow {
        let token = artifact.token();
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 10);
        line.set_margin_top(4);
        line.set_margin_bottom(4);
        line.set_margin_start(12);
        line.set_margin_end(8);
        let icon = gtk::Image::from_icon_name(if artifact.kind == "page" { "text-x-generic-symbolic" } else { "view-paged-symbolic" });
        icon.add_css_class("dim-label");
        icon.set_accessible_role(gtk::AccessibleRole::Presentation);
        line.append(&icon);
        line.append(&title_and_subtitle(&artifact.title, &self.subtitle(artifact), false, &format!("artifact.{token}")));
        line.append(&self.access(artifact));
        line.append(&self.share_menu(artifact));

        let copy = icon_button("edit-copy-symbolic", &format!("artifact.{token}.copy-link"), &format!("Copy Link to {}", artifact.title), "Copy link");
        copy.set_sensitive(artifact.open_url.is_some());
        let weak = Rc::downgrade(self);
        let target = artifact.clone();
        copy.connect_clicked(move |_| {
            if let Some(page) = weak.upgrade() {
                page.center.copy_link(&target);
            }
        });
        line.append(&copy);

        let open = icon_button("adw-external-link-symbolic", &format!("artifact.{token}.open"), &format!("Open {}", artifact.title), "Open in the browser");
        open.set_sensitive(artifact.open_url.is_some());
        let url = artifact.open_url.clone();
        open.connect_clicked(move |button| {
            if let Some(url) = &url {
                open_url(url, button);
            }
        });
        line.append(&open);
        line.append(&self.actions(artifact));

        let row = gtk::ListBoxRow::new();
        row.set_activatable(false);
        row.set_child(Some(&line));
        row
    }

    /// Only the owner can change who opens it; for anyone else it is said, not
    /// offered.
    fn access(self: &Rc<Self>, artifact: &Artifact) -> gtk::Widget {
        let current = artifact.access();
        let token = artifact.token();
        if !artifact.is_own() {
            let image = gtk::Image::from_icon_name(current.icon());
            image.add_css_class("dim-label");
            image.set_tooltip_text(Some(current.label()));
            image.set_accessible_role(gtk::AccessibleRole::Presentation);
            return image.upcast();
        }
        let (popover, column) = menu_column();
        let mut entries = vec![(ArtifactAccess::Invited, "Only People Invited")];
        if current == ArtifactAccess::Organization {
            entries.push((ArtifactAccess::Organization, "Your Organization"));
        }
        entries.push((ArtifactAccess::AnyoneWithLink, "Anyone with the Link"));
        let mut group: Option<gtk::CheckButton> = None;
        for (choice, label) in entries {
            let check = gtk::CheckButton::with_label(label);
            check.set_group(group.as_ref());
            check.set_active(choice == current.menu_choice());
            identified(&check, &format!("artifact.{token}.access.{}", match choice {
                ArtifactAccess::Invited => "invited",
                ArtifactAccess::Organization => "organization",
                _ => "public",
            }), label);
            group.get_or_insert_with(|| check.clone());
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            let pop = popover.clone();
            check.connect_toggled(move |check| {
                // The organization entry only says what it is; Graft does not
                // share with an organization. The entry already checked sends
                // nothing.
                if !check.is_active() || choice == current.menu_choice() || choice == ArtifactAccess::Organization {
                    return;
                }
                pop.popdown();
                if let Some(page) = weak.upgrade() {
                    page.center.set_public(&target, choice == ArtifactAccess::AnyoneWithLink);
                }
            });
            column.append(&check);
        }
        let button = icon_menu(current.icon(), &format!("artifact.{token}.access"), &format!("Who Can Open {}", artifact.title), current.label(), &popover);
        button.update_property(&[gtk::accessible::Property::Description(current.label())]);
        button.set_sensitive(!self.center.is_busy(&format!("access-{}", artifact.id)));
        button.upcast()
    }

    fn share_menu(self: &Rc<Self>, artifact: &Artifact) -> gtk::MenuButton {
        let token = artifact.token();
        let busy = self.center.is_busy(&format!("share-{}", artifact.id));
        let (popover, column) = menu_column();
        let groups = self.center.groups();
        if groups.is_empty() {
            let none = gtk::Label::new(Some("No groups yet"));
            none.add_css_class("dim-label");
            none.set_margin_start(8);
            none.set_margin_end(8);
            column.append(&none);
        }
        for (index, group) in groups.iter().enumerate() {
            let label = format!("{} ({}) \u{b7} {}", group.name, group.emails.len(), group.role_summary());
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            let chosen = group.clone();
            let item = menu_item(&column, &popover, &label, &format!("artifact.{token}.share.{index}"), move |_| {
                if let Some(page) = weak.upgrade() {
                    page.center.share(&target, &chosen);
                }
            });
            item.set_sensitive(!group.emails.is_empty());
        }
        let help = if artifact.is_own() { "Share with a group" } else { "Only its owner can share it" };
        let button = icon_menu("contact-new-symbolic", &format!("artifact.{token}.share"), &format!("Share {} with a Group", artifact.title), help, &popover);
        button.set_sensitive(artifact.is_own() && !busy);
        button
    }

    /// What claude.ai's own menu offers for an artifact, each only where the
    /// list says claude.ai would allow it.
    fn actions(self: &Rc<Self>, artifact: &Artifact) -> gtk::MenuButton {
        let token = artifact.token();
        let working = ["rename", "duplicate", "delete", "export"]
            .iter()
            .any(|k| self.center.is_busy(&format!("{k}-{}", artifact.id)));
        let (popover, column) = menu_column();
        if artifact.is_own() && artifact.renamable {
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            menu_item(&column, &popover, "Rename...", &format!("artifact.{token}.rename"), move |button| {
                if let Some(page) = weak.upgrade() {
                    page.ask_rename(&target, button);
                }
            });
        }
        if artifact.duplicable {
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            let item = menu_item(&column, &popover, "Duplicate", &format!("artifact.{token}.duplicate"), move |_| {
                if let Some(page) = weak.upgrade() {
                    page.center.duplicate(&target);
                }
            });
            item.set_sensitive(artifact.duplicate_available);
        }
        let heading = gtk::Label::new(Some("Export"));
        heading.add_css_class("dim-label");
        heading.add_css_class("caption-heading");
        heading.set_xalign(0.0);
        heading.set_margin_start(8);
        column.append(&heading);
        for format in ExportFormat::ALL {
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            menu_item(&column, &popover, &format!("{}...", format.label()), &format!("artifact.{token}.export.{}", format.raw()), move |button| {
                if let Some(page) = weak.upgrade() {
                    page.center.export(&target, format, window_of(button));
                }
            });
        }
        if artifact.is_own() {
            column.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
            let weak = Rc::downgrade(self);
            let target = artifact.clone();
            let item = menu_item(&column, &popover, "Delete...", &format!("artifact.{token}.delete"), move |button| {
                if let Some(page) = weak.upgrade() {
                    page.ask_delete(&target, button);
                }
            });
            item.add_css_class("destructive-action");
        }
        let button = icon_menu(
            if working { "content-loading-symbolic" } else { "view-more-symbolic" },
            &format!("artifact.{token}.more"),
            &format!("More for {}", artifact.title),
            "Rename, duplicate, export or delete",
            &popover,
        );
        button.set_sensitive(!working);
        button
    }

    fn ask_rename(self: &Rc<Self>, artifact: &Artifact, near: &gtk::Button) {
        let token = artifact.token();
        let entry = gtk::Entry::new();
        entry.set_text(&artifact.title);
        entry.set_activates_default(true);
        identified(&entry, &format!("artifact.{token}.new-title"), "Title");
        let dialog = adw::AlertDialog::new(Some("Rename Artifact"), None);
        dialog.set_extra_child(Some(&entry));
        dialog.add_responses(&[("cancel", "Cancel"), ("rename", "Rename")]);
        dialog.set_response_appearance("rename", adw::ResponseAppearance::Suggested);
        dialog.set_default_response(Some("rename"));
        dialog.set_close_response("cancel");
        let weak = Rc::downgrade(self);
        let target = artifact.clone();
        dialog.connect_response(Some("rename"), move |_, _| {
            if let Some(page) = weak.upgrade() {
                page.center.rename(&target, &entry.text());
            }
        });
        dialog.present(window_of(near).as_ref().or(window_of(&self.list).as_ref()));
    }

    /// claude.ai keeps no bin for artifacts, so the question says so.
    fn ask_delete(self: &Rc<Self>, artifact: &Artifact, near: &gtk::Button) {
        let dialog = adw::AlertDialog::new(
            Some(&format!("Delete \"{}\"?", artifact.title)),
            Some("claude.ai keeps no copy, and everyone it was shared with loses it too."),
        );
        dialog.add_responses(&[("cancel", "Cancel"), ("delete", "Delete")]);
        dialog.set_response_appearance("delete", adw::ResponseAppearance::Destructive);
        dialog.set_close_response("cancel");
        dialog.set_widget_name(&format!("artifact.{}.confirm-delete", artifact.token()));
        let weak = Rc::downgrade(self);
        let target = artifact.clone();
        dialog.connect_response(Some("delete"), move |_, _| {
            if let Some(page) = weak.upgrade() {
                page.center.delete(&target);
            }
        });
        dialog.present(window_of(near).as_ref().or(window_of(&self.list).as_ref()));
    }
}
