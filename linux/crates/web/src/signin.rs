//! claude.ai's own sign-in page, in a window of Graft's, writing into the
//! store of the account being signed in. It closes itself once the page says
//! who is signed in.

use crate::api::{self, Identity, ACCOUNT_PATH, HOST, SIGN_IN_URL};
use crate::session::{self, arguments, host_of, parse_result, USER_AGENT};
use crate::ui::identified;
use adw::prelude::*;
use graft_core::diagnostics;
use gtk::glib;
use serde_json::{json, Value};
use std::cell::RefCell;
use std::rc::Rc;
use uuid::Uuid;
use webkit::prelude::*;

/// Under every Sign In button, since a sign-in that starts another way is one
/// Graft does not support.
pub const SIGN_IN_HINT: &str = "Use Continue with email and the code claude.ai sends you";

type Done = Box<dyn FnOnce(Identity)>;

struct Inner {
    window: Option<adw::Window>,
    view: Option<webkit::WebView>,
    popups: Vec<adw::Window>,
    finished: bool,
    checking: bool,
    ask_again: bool,
}

pub struct SignInWindow {
    pub account: Uuid,
    inner: RefCell<Inner>,
    done: RefCell<Option<Done>>,
}

impl SignInWindow {
    pub fn new(account: Uuid, done: impl FnOnce(Identity) + 'static) -> Rc<SignInWindow> {
        Rc::new(SignInWindow {
            account,
            inner: RefCell::new(Inner {
                window: None,
                view: None,
                popups: Vec::new(),
                finished: false,
                checking: false,
                ask_again: false,
            }),
            done: RefCell::new(Some(Box::new(done))),
        })
    }

    pub fn show(self: &Rc<Self>, parent: Option<&gtk::Window>) {
        let view = session::web_view(self.account);
        view.set_vexpand(true);
        view.set_hexpand(true);
        identified(&view, "signin.page", "claude.ai sign-in page");
        // claude.ai finishes a sign-in by moving to its chat page inside the
        // page already loaded, which ends no navigation. The address changing
        // is the one thing every way of signing in does, so that is what asks.
        // Waiting for a finished load alone left a person signed in, looking
        // at their chats in this window, and Graft still saying "Sign in".
        let weak = Rc::downgrade(self);
        view.connect_uri_notify(move |view| {
            if let Some(this) = weak.upgrade() {
                this.ask(view);
            }
        });
        let weak = Rc::downgrade(self);
        view.connect_load_changed(move |view, event| {
            if event == webkit::LoadEvent::Finished {
                if let Some(this) = weak.upgrade() {
                    this.ask(view);
                }
            }
        });
        let weak = Rc::downgrade(self);
        view.connect_create(move |view, _| weak.upgrade().map(|this| this.popup(view).upcast()));

        let hint = gtk::Label::new(Some(SIGN_IN_HINT));
        hint.add_css_class("dim-label");
        hint.add_css_class("caption");
        hint.set_ellipsize(gtk::pango::EllipsizeMode::End);
        hint.set_margin_top(6);
        hint.set_margin_bottom(6);
        hint.set_margin_start(12);
        hint.set_margin_end(12);
        identified(&hint, "signin.hint", SIGN_IN_HINT);
        let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
        content.append(&hint);
        content.append(&view);
        let toolbar = adw::ToolbarView::new();
        toolbar.add_top_bar(&adw::HeaderBar::new());
        toolbar.set_content(Some(&content));

        let window = adw::Window::builder()
            .title("Sign In to claude.ai")
            .default_width(520)
            .default_height(680)
            .content(&toolbar)
            .build();
        if let Some(parent) = parent {
            window.set_transient_for(Some(parent));
        }
        let weak = Rc::downgrade(self);
        window.connect_close_request(move |_| {
            if let Some(this) = weak.upgrade() {
                this.release();
            }
            glib::Propagation::Proceed
        });
        {
            let mut inner = self.inner.borrow_mut();
            inner.window = Some(window.clone());
            inner.view = Some(view.clone());
        }
        view.load_uri(SIGN_IN_URL);
        window.present();
    }

    pub fn close(&self) {
        self.release();
    }

    /// Lets go of every web view this window made, at once rather than when
    /// GTK next gets round to it, since the store they write into cannot be
    /// removed cleanly while one of them is alive.
    fn release(&self) {
        let (window, popups, view) = {
            let mut inner = self.inner.borrow_mut();
            (inner.window.take(), std::mem::take(&mut inner.popups), inner.view.take())
        };
        for popup in popups {
            popup.set_content(None::<&gtk::Widget>);
            popup.destroy();
        }
        drop(view);
        if let Some(window) = window {
            window.set_content(None::<&gtk::Widget>);
            window.destroy();
        }
    }

    /// A provider's sign-in opens in a window of its own and hands its answer
    /// back to the page that opened it, so it gets a real one. Loading it over
    /// claude.ai instead left that answer with nowhere to go. A view made
    /// related to the opener shares its network session, so the popup writes
    /// into the same store.
    fn popup(self: &Rc<Self>, opener: &webkit::WebView) -> webkit::WebView {
        let popup = webkit::WebView::builder().related_view(opener).build();
        if let Some(settings) = WebViewExt::settings(&popup) {
            settings.set_user_agent(Some(USER_AGENT));
        }
        let window = adw::Window::builder().title("Sign In to claude.ai").default_width(500).default_height(640).build();
        let toolbar = adw::ToolbarView::new();
        toolbar.add_top_bar(&adw::HeaderBar::new());
        toolbar.set_content(Some(&popup));
        window.set_content(Some(&toolbar));
        if let Some(parent) = self.inner.borrow().window.as_ref() {
            window.set_transient_for(Some(parent));
        }
        let shown = window.clone();
        popup.connect_ready_to_show(move |_| shown.present());
        let weak = Rc::downgrade(self);
        let closing = window.clone();
        popup.connect_close(move |_| {
            let Some(this) = weak.upgrade() else { return };
            this.inner.borrow_mut().popups.retain(|p| p != &closing);
            closing.set_content(None::<&gtk::Widget>);
            closing.destroy();
            // Its answer has reached the page by now, and the page may not
            // move again on its own.
            let main = this.inner.borrow().view.clone();
            if let Some(main) = main {
                this.ask(&main);
            }
        });
        let weak = Rc::downgrade(self);
        let gone = window.clone();
        window.connect_close_request(move |_| {
            if let Some(this) = weak.upgrade() {
                this.inner.borrow_mut().popups.retain(|p| p != &gone);
            }
            glib::Propagation::Proceed
        });
        self.inner.borrow_mut().popups.push(window);
        popup
    }

    fn ask(self: &Rc<Self>, view: &webkit::WebView) {
        let this = Rc::clone(self);
        let view = view.clone();
        glib::MainContext::default().spawn_local(async move { this.check(&view).await });
    }

    /// Asked whenever the page moves on claude.ai, since the page that ends a
    /// sign-in is not one that can be named in advance. One question at a
    /// time: a move made while one is out is asked about once it comes back.
    async fn check(self: &Rc<Self>, view: &webkit::WebView) {
        {
            let mut inner = self.inner.borrow_mut();
            let current = inner.view.as_ref() == Some(view);
            let on_claude = view.uri().is_some_and(|u| host_of(&u) == Some(HOST));
            if inner.finished || !current || !on_claude {
                return;
            }
            if inner.checking {
                inner.ask_again = true;
                return;
            }
            inner.checking = true;
        }
        let identity = self.question(view).await;
        let again = {
            let mut inner = self.inner.borrow_mut();
            inner.checking = false;
            if identity.is_some() {
                inner.finished = true;
            }
            let again = inner.ask_again && !inner.finished;
            inner.ask_again = false;
            again
        };
        if let Some(identity) = identity {
            // The window and its pages go first, and the sign-in is handed on
            // only once this question has let go of its page too. Handed on
            // first, a store removed because of it was still in use.
            self.release();
            let done = self.done.borrow_mut().take();
            if let Some(done) = done {
                glib::idle_add_local_once(move || done(identity));
            }
        } else if again {
            self.ask(view);
        }
    }

    async fn question(&self, view: &webkit::WebView) -> Option<Identity> {
        let script = r#"
            const response = await fetch(path, {credentials: "same-origin"});
            return {status: response.status, body: await response.text()};
        "#;
        let args = arguments(&[("path", ACCOUNT_PATH.to_string())]);
        // The page moving on mid-question fails it, and that move asks again.
        let answer = parse_result(view.call_async_javascript_function_future(script, Some(&args), Some("claude-graft"), None).await).ok()?;
        // Until the sign-in is through, the page is answered 401 or 403 on
        // every move, and that is not worth writing down.
        if answer.get("status").and_then(Value::as_u64) != Some(200) {
            return None;
        }
        let body = answer.get("body").and_then(Value::as_str)?;
        let identity = api::identity(body.as_bytes());
        if identity.is_none() {
            // Signed in and still not read is the one failure nothing on
            // screen shows, so it is the one written down. The shape, never
            // the body.
            diagnostics::note("artifacts.signin unreadable", &[("keys", json!(api::answer_keys(body.as_bytes())))]);
        }
        identity
    }
}
