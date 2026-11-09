//! The tray item: a StatusNotifierItem, which KDE shows and GNOME shows with
//! the AppIndicator extension.
//!
//! The item lives on a thread of its own, with the bus connection the
//! protocol needs, and is told what to show by the main loop through a
//! channel, on a second thread, so the main loop never waits on the bus.
//! What it says is worked out in `title` from what the window knows; the
//! pictures are drawn here, off the main loop.
//!
//! A tray host draws the item's text and lays the item out itself, so where
//! the Mac moves the conversation's title across the bar a picture at a time,
//! here the text is handed over once per conversation and never on a timer:
//! a new title on every tick would be a signal on the bus and a relayout in
//! the host each time, which is the cost the Mac's ticker was built to avoid.

use crate::icons;
use crate::reach::Reach;
use crate::settings::APP_ID;
use crate::title::TrayLook;
use crate::ui::on_main;
use graft_core::{host, IconPreset};
use ksni::blocking::TrayMethods;
use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::mpsc;
use std::sync::Arc;

pub struct GraftTray {
    look: TrayLook,
    icon: Vec<ksni::Icon>,
    overlay: Vec<ksni::Icon>,
    reach: Arc<Reach>,
}

impl ksni::Tray for GraftTray {
    /// The one handle a script has on the item: its title is a figure that
    /// changes on every pass.
    fn id(&self) -> String {
        "claude-graft".into()
    }

    fn title(&self) -> String {
        if self.look.title.is_empty() {
            "Claude Graft".into()
        } else {
            self.look.title.clone()
        }
    }

    fn category(&self) -> ksni::Category {
        ksni::Category::ApplicationStatus
    }

    fn icon_name(&self) -> String {
        if self.look.icon.is_none() {
            APP_ID.into()
        } else {
            String::new()
        }
    }

    fn icon_pixmap(&self) -> Vec<ksni::Icon> {
        self.icon.clone()
    }

    fn overlay_icon_pixmap(&self) -> Vec<ksni::Icon> {
        self.overlay.clone()
    }

    /// Pictures read out as nothing, so the words go here: whose figure it
    /// is, which account answers, and the conversation on screen.
    fn tool_tip(&self) -> ksni::ToolTip {
        ksni::ToolTip { title: self.look.spoken.clone(), description: self.look.lines.join("\n"), ..Default::default() }
    }

    fn activate(&mut self, _x: i32, _y: i32) {
        on_main(crate::dropdown::toggle);
    }

    /// The same choices as the conversation's row in the tray's window,
    /// reached without opening it, where the Mac's right click shows them;
    /// and the way into Graft for a host that shows a menu on every click.
    fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
        use ksni::menu::{RadioGroup, RadioItem, StandardItem};
        let mut items: Vec<ksni::MenuItem<Self>> = Vec::new();
        if let Some(chat) = &self.look.chat {
            items.push(StandardItem { label: chat.heading.replace('_', "__"), enabled: false, ..Default::default() }.into());
            items.push(ksni::MenuItem::Separator);
            items.push(
                RadioGroup {
                    selected: chat.selected,
                    select: Box::new(|tray: &mut Self, index: usize| {
                        let Some(chat) = tray.look.chat.clone() else { return };
                        let Some((key, _)) = chat.choices.get(index).cloned() else { return };
                        on_main(move || crate::sharing::change(move |pool| pool.assign(&chat.id, key.as_deref())));
                    }),
                    options: chat
                        .choices
                        .iter()
                        .map(|(_, label)| RadioItem { label: label.replace('_', "__"), ..Default::default() })
                        .collect(),
                }
                .into(),
            );
            items.push(ksni::MenuItem::Separator);
        }
        items.push(
            StandardItem {
                label: "Open Claude Graft".into(),
                activate: Box::new(|_: &mut Self| on_main(crate::app::show_main_window)),
                ..Default::default()
            }
            .into(),
        );
        items.push(
            StandardItem {
                label: "Refresh Usage".into(),
                activate: Box::new(|_: &mut Self| on_main(|| crate::shared::shared().monitor.refresh(graft_usage::Refresh::pressed()))),
                ..Default::default()
            }
            .into(),
        );
        items.push(
            StandardItem {
                label: "Quit Claude Graft".into(),
                activate: Box::new(|_: &mut Self| on_main(crate::app::quit)),
                ..Default::default()
            }
            .into(),
        );
        items
    }

    fn watcher_online(&self) {
        self.reach.registered.store(true, Ordering::SeqCst);
        on_main(crate::app::reach_changed);
    }

    fn watcher_offline(&self, _reason: ksni::OfflineReason) -> bool {
        self.reach.registered.store(false, Ordering::SeqCst);
        on_main(crate::app::reach_changed);
        true
    }
}

/// The item, as the main loop holds it.
pub struct Tray {
    sender: mpsc::Sender<Option<TrayLook>>,
    last: Option<TrayLook>,
}

impl Tray {
    /// Registers the item on a thread of its own. Inside the Flatpak the item
    /// owns no well-known name: the sandbox may talk to the watcher but not
    /// own a name of its own, which the specification allows for.
    pub fn start(reach: Arc<Reach>) -> Tray {
        let (sender, receiver) = mpsc::channel::<Option<TrayLook>>();
        let _ = std::thread::Builder::new().name("graft-tray".into()).spawn(move || {
            let own = icons::own_tray_icon().into_iter().collect::<Vec<_>>();
            let tray = GraftTray { look: TrayLook::default(), icon: own.clone(), overlay: Vec::new(), reach: reach.clone() };
            let handle = match tray.disable_dbus_name(host::in_flatpak()).assume_sni_available(true).spawn() {
                Ok(handle) => handle,
                Err(e) => {
                    graft_core::diagnostics::note("tray.failed", &[("reason", e.to_string().into())]);
                    reach.registered.store(false, Ordering::SeqCst);
                    on_main(crate::app::reach_changed);
                    return;
                }
            };
            reach.registered.store(true, Ordering::SeqCst);
            on_main(crate::app::reach_changed);
            let mut drawn: HashMap<IconPreset, Vec<ksni::Icon>> = HashMap::new();
            let mut picture = |preset: Option<IconPreset>| -> Vec<ksni::Icon> {
                match preset {
                    None => Vec::new(),
                    Some(p) => drawn.entry(p).or_insert_with(|| icons::tray_icon(p).into_iter().collect()).clone(),
                }
            };
            while let Ok(message) = receiver.recv() {
                let Some(look) = message else { break };
                let icon = if look.icon.is_some() { picture(look.icon) } else { own.clone() };
                let overlay = picture(look.overlay);
                handle.update(move |tray| {
                    tray.look = look;
                    tray.icon = icon;
                    tray.overlay = overlay;
                });
            }
            handle.shutdown().wait();
            reach.registered.store(false, Ordering::SeqCst);
            on_main(crate::app::reach_changed);
        });
        Tray { sender, last: None }
    }

    /// Hands the item a new look, and nothing when it would say the same.
    pub fn show(&mut self, look: TrayLook) {
        if self.last.as_ref() == Some(&look) {
            return;
        }
        self.last = Some(look.clone());
        let _ = self.sender.send(Some(look));
    }

    pub fn stop(self) {
        let _ = self.sender.send(None);
    }
}
