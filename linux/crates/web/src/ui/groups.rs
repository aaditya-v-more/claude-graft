//! Groups on the left; the chosen one's name, and each person in it with what
//! they may do, on the right.

use super::*;
use crate::model::{parse_addresses, ArtifactGroup, ArtifactRole};
use adw::prelude::*;
use uuid::Uuid;

struct Editor {
    center: Rc<ArtifactCenter>,
    selection: Cell<Option<Uuid>>,
    list: gtk::ListBox,
    detail: gtk::Box,
    delete: gtk::Button,
    /// Set while the list is being drawn, so selecting the row that is drawn
    /// selected is not taken for a person choosing it.
    drawing: Cell<bool>,
}

pub fn present(center: &Rc<ArtifactCenter>, near: &impl IsA<gtk::Widget>) {
    let list = gtk::ListBox::new();
    list.add_css_class("navigation-sidebar");
    identified(&list, "groups.list", "Groups");
    let add = icon_button("list-add-symbolic", "groups.add", "New Group", "New Group");
    let delete = icon_button("list-remove-symbolic", "groups.delete", "Delete Group", "Delete Group");
    let buttons = gtk::Box::new(gtk::Orientation::Horizontal, 4);
    buttons.set_margin_top(8);
    buttons.set_margin_bottom(8);
    buttons.set_margin_start(8);
    buttons.append(&add);
    buttons.append(&delete);
    let side = gtk::Box::new(gtk::Orientation::Vertical, 0);
    side.set_size_request(170, -1);
    side.append(&gtk::ScrolledWindow::builder().child(&list).vexpand(true).hscrollbar_policy(gtk::PolicyType::Never).build());
    side.append(&buttons);

    let detail = gtk::Box::new(gtk::Orientation::Vertical, 8);
    detail.set_margin_top(12);
    detail.set_margin_bottom(12);
    detail.set_margin_start(12);
    detail.set_margin_end(12);
    detail.set_hexpand(true);
    let split = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    split.append(&side);
    split.append(&gtk::Separator::new(gtk::Orientation::Vertical));
    split.append(&gtk::ScrolledWindow::builder().child(&detail).hexpand(true).hscrollbar_policy(gtk::PolicyType::Never).build());
    split.set_vexpand(true);

    let foot = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    foot.set_margin_top(12);
    foot.set_margin_bottom(12);
    foot.set_margin_start(12);
    foot.set_margin_end(12);
    let note = one_line_note("Each person is invited with the role beside them", super::artifacts::NOTE, "Groups", "groups.note");
    note.set_hexpand(true);
    note.set_halign(gtk::Align::Start);
    foot.append(&note);
    let done = gtk::Button::with_label("Done");
    done.add_css_class("suggested-action");
    identified(&done, "groups.done", "Done");
    foot.append(&done);

    let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
    content.append(&split);
    content.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
    content.append(&foot);
    let toolbar = adw::ToolbarView::new();
    toolbar.add_top_bar(&adw::HeaderBar::new());
    toolbar.set_content(Some(&content));
    let dialog = adw::Dialog::builder().title("Groups").content_width(600).content_height(420).child(&toolbar).build();
    dialog.set_default_widget(Some(&done));

    let editor = Rc::new(Editor {
        center: Rc::clone(center),
        selection: Cell::new(center.groups().first().map(|g| g.id)),
        list,
        detail,
        delete,
        drawing: Cell::new(false),
    });
    let weak = Rc::downgrade(&editor);
    add.connect_clicked(move |_| {
        if let Some(editor) = weak.upgrade() {
            let id = editor.center.add_group("New Group");
            editor.selection.set(Some(id));
            editor.draw();
        }
    });
    let weak = Rc::downgrade(&editor);
    editor.delete.connect_clicked(move |_| {
        if let Some(editor) = weak.upgrade() {
            if let Some(id) = editor.selection.get() {
                editor.center.delete_group(id);
            }
            editor.selection.set(editor.center.groups().first().map(|g| g.id));
            editor.draw();
        }
    });
    let weak = Rc::downgrade(&editor);
    editor.list.connect_row_selected(move |_, row| {
        let Some(editor) = weak.upgrade() else { return };
        if editor.drawing.get() {
            return;
        }
        let groups = editor.center.groups();
        let chosen = row.and_then(|r| groups.get(r.index() as usize)).map(|g| g.id);
        if chosen != editor.selection.get() {
            editor.selection.set(chosen);
            editor.draw_detail();
        }
    });
    let shut = dialog.clone();
    done.connect_clicked(move |_| {
        shut.close();
    });
    let keep = Rc::clone(&editor);
    dialog.connect_closed(move |_| {
        let _ = &keep;
    });
    editor.draw();
    dialog.present(Some(near));
}

impl Editor {
    fn draw(self: &Rc<Self>) {
        self.drawing.set(true);
        while let Some(row) = self.list.row_at_index(0) {
            self.list.remove(&row);
        }
        for group in self.center.groups() {
            let line = gtk::Box::new(gtk::Orientation::Horizontal, 6);
            let name = gtk::Label::new(Some(&group.name));
            name.set_ellipsize(gtk::pango::EllipsizeMode::End);
            name.set_hexpand(true);
            name.set_xalign(0.0);
            let count = gtk::Label::new(Some(&group.emails.len().to_string()));
            count.add_css_class("dim-label");
            count.add_css_class("numeric");
            line.append(&name);
            line.append(&count);
            let row = gtk::ListBoxRow::new();
            row.set_child(Some(&line));
            row.update_property(&[
                gtk::accessible::Property::Label(&group.name),
                gtk::accessible::Property::Description(&format!("{} people, {}", group.emails.len(), group.role_summary())),
            ]);
            self.list.append(&row);
            if Some(group.id) == self.selection.get() {
                self.list.select_row(Some(&row));
            }
        }
        self.drawing.set(false);
        self.draw_detail();
    }

    fn group(&self) -> Option<ArtifactGroup> {
        let id = self.selection.get()?;
        self.center.groups().into_iter().find(|g| g.id == id)
    }

    fn change(self: &Rc<Self>, f: impl FnOnce(&mut ArtifactGroup)) {
        if let Some(id) = self.selection.get() {
            self.center.update_group(id, f);
        }
    }

    fn draw_detail(self: &Rc<Self>) {
        clear(&self.detail);
        self.delete.set_sensitive(self.selection.get().is_some());
        let Some(group) = self.group() else {
            let none = gtk::Label::new(Some(if self.center.groups().is_empty() { "No groups yet" } else { "Select a group" }));
            none.add_css_class("dim-label");
            none.set_vexpand(true);
            self.detail.append(&none);
            return;
        };
        let form = gtk::ListBox::new();
        form.add_css_class("boxed-list");
        form.set_selection_mode(gtk::SelectionMode::None);
        let name = adw::EntryRow::builder().title("Name").text(&group.name).build();
        identified(&name, "groups.name", "Name");
        let weak = Rc::downgrade(self);
        name.connect_changed(move |row| {
            if let Some(editor) = weak.upgrade() {
                let text = row.text().to_string();
                editor.change(|g| g.name = text);
                editor.redraw_names();
            }
        });
        form.append(&name);

        let mut choices: Vec<&str> = ArtifactRole::ALL.iter().map(|r| r.label()).collect();
        let common = group.common_role();
        if common.is_none() {
            choices.push("Mixed");
        }
        let everyone = adw::ComboRow::builder().title("Everyone").model(&gtk::StringList::new(&choices)).build();
        everyone.set_selected(match common {
            Some(role) => ArtifactRole::ALL.iter().position(|r| *r == role).unwrap_or(0) as u32,
            None => ArtifactRole::ALL.len() as u32,
        });
        everyone.set_sensitive(!group.emails.is_empty());
        identified(&everyone, "groups.everyone", "Everyone");
        let weak = Rc::downgrade(self);
        everyone.connect_selected_notify(move |row| {
            let Some(editor) = weak.upgrade() else { return };
            let Some(role) = ArtifactRole::ALL.get(row.selected() as usize).copied() else { return };
            if editor.group().and_then(|g| g.common_role()) == Some(role) {
                return;
            }
            editor.change(|g| g.set_role_for_everyone(role));
            glib::idle_add_local_once(move || editor.draw_detail());
        });
        form.append(&everyone);
        self.detail.append(&form);

        let people = gtk::Label::new(Some(&format!("People \u{b7} {}", group.emails.len())));
        people.add_css_class("heading");
        people.set_xalign(0.0);
        people.set_margin_top(8);
        self.detail.append(&people);
        let members = gtk::ListBox::new();
        members.add_css_class("boxed-list");
        members.set_selection_mode(gtk::SelectionMode::None);
        for (position, email) in group.emails.iter().enumerate() {
            members.append(&self.member(email, position, group.role_of(email)));
        }
        let adding = gtk::Box::new(gtk::Orientation::Horizontal, 6);
        adding.set_margin_top(6);
        adding.set_margin_bottom(6);
        adding.set_margin_start(6);
        adding.set_margin_end(6);
        let entry = gtk::Entry::new();
        entry.set_placeholder_text(Some("Add addresses"));
        entry.set_hexpand(true);
        identified(&entry, "groups.new-addresses", "Add addresses");
        let add = gtk::Button::with_label("Add");
        add.set_sensitive(false);
        identified(&add, "groups.add-addresses", "Add");
        adding.append(&entry);
        adding.append(&add);
        let row = gtk::ListBoxRow::new();
        row.set_activatable(false);
        row.set_child(Some(&adding));
        members.append(&row);
        self.detail.append(&members);

        let button = add.clone();
        entry.connect_changed(move |entry| button.set_sensitive(!parse_addresses(&entry.text()).is_empty()));
        let add_now = {
            let weak = Rc::downgrade(self);
            let entry = entry.clone();
            move || {
                let Some(editor) = weak.upgrade() else { return };
                let text = entry.text().to_string();
                if parse_addresses(&text).is_empty() {
                    return;
                }
                editor.change(|g| g.add_addresses(&text));
                glib::idle_add_local_once(move || {
                    editor.draw();
                });
            }
        };
        let again = add_now.clone();
        entry.connect_activate(move |_| again());
        add.connect_clicked(move |_| add_now());
    }

    /// The left column's names follow the one being typed, without the field
    /// being drawn again under the cursor.
    fn redraw_names(&self) {
        for (index, group) in self.center.groups().iter().enumerate() {
            let Some(row) = self.list.row_at_index(index as i32) else { continue };
            if let Some(name) = row.child().and_downcast::<gtk::Box>().and_then(|b| b.first_child()).and_downcast::<gtk::Label>() {
                name.set_text(&group.name);
            }
            row.update_property(&[gtk::accessible::Property::Label(&group.name)]);
        }
    }

    fn member(self: &Rc<Self>, email: &str, position: usize, role: ArtifactRole) -> gtk::ListBoxRow {
        let line = gtk::Box::new(gtk::Orientation::Horizontal, 6);
        line.set_margin_top(4);
        line.set_margin_bottom(4);
        line.set_margin_start(10);
        line.set_margin_end(6);
        let address = gtk::Label::new(Some(email));
        address.add_css_class("monospace");
        address.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
        address.set_tooltip_text(Some(email));
        address.set_hexpand(true);
        address.set_xalign(0.0);
        line.append(&address);
        let labels: Vec<&str> = ArtifactRole::ALL.iter().map(|r| r.label()).collect();
        let picker = gtk::DropDown::from_strings(&labels);
        picker.set_selected(ArtifactRole::ALL.iter().position(|r| *r == role).unwrap_or(0) as u32);
        identified(&picker, &format!("groups.member.{position}.role"), &format!("Role for {email}"));
        let weak = Rc::downgrade(self);
        let who = email.to_string();
        picker.connect_selected_notify(move |picker| {
            let Some(editor) = weak.upgrade() else { return };
            let Some(role) = ArtifactRole::ALL.get(picker.selected() as usize).copied() else { return };
            let who = who.clone();
            editor.change(|g| g.set_role(role, &who));
            glib::idle_add_local_once(move || editor.draw_detail());
        });
        line.append(&picker);
        let remove = icon_button("list-remove-symbolic", &format!("groups.member.{position}.remove"), &format!("Remove {email}"), "Remove from the group");
        let weak = Rc::downgrade(self);
        let who = email.to_string();
        remove.connect_clicked(move |_| {
            let Some(editor) = weak.upgrade() else { return };
            let who = who.clone();
            editor.change(|g| g.remove(&who));
            glib::idle_add_local_once(move || editor.draw());
        });
        line.append(&remove);
        let row = gtk::ListBoxRow::new();
        row.set_activatable(false);
        row.set_child(Some(&line));
        row
    }
}
