//! Turns an artifact's files, already read onto this machine, into the file a
//! person asked for.
//!
//! Markdown, PDF and Word are made from the page as drawn, not its source,
//! because an artifact that builds itself with script has almost nothing in
//! its source to export. The page is drawn in a web view with an ephemeral
//! network session, holding no sign-in, never shown, and following no link
//! out of the folder: its files are served to it by Graft under a scheme of
//! its own, so the web process is handed only those files and reads nothing
//! else from the disk.

use crate::docx;
use crate::model::ExportFormat;
use futures_channel::oneshot;
use gtk::gio;
use gtk::glib;
use gtk::prelude::*;
use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::Duration;
use webkit::prelude::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExportFailure {
    Gone,
    Refused(u16),
    TooLarge,
    Unrenderable,
    Unwritable,
}

impl ExportFailure {
    pub fn message(self) -> String {
        match self {
            ExportFailure::Gone => "claude.ai no longer has that artifact".into(),
            ExportFailure::Refused(status) => format!("claude.ai wouldn't hand over its files ({status})"),
            ExportFailure::TooLarge => "That artifact is too large to export".into(),
            ExportFailure::Unrenderable => "That artifact couldn't be drawn for export".into(),
            ExportFailure::Unwritable => "The export couldn't be saved there".into(),
        }
    }
}

pub const MARKDOWN_SCRIPT: &str = include_str!("scripts/markdown.js");
pub const DOCUMENT_SCRIPT: &str = include_str!("scripts/document.js");

/// The save dialog, through the desktop's portal inside the sandbox, named
/// after the artifact.
pub async fn ask_where(title: &str, format: ExportFormat, single_file: bool, parent: Option<&gtk::Window>) -> Option<gio::File> {
    let filter = gtk::FileFilter::new();
    filter.add_mime_type(format.mime_type(single_file));
    filter.set_name(Some(format.label()));
    let filters = gio::ListStore::new::<gtk::FileFilter>();
    filters.append(&filter);
    let dialog = gtk::FileDialog::builder()
        .title("Export")
        .initial_name(ExportFormat::file_name(title, format, single_file))
        .filters(&filters)
        .default_filter(&filter)
        .modal(true)
        .build();
    dialog.save_future(parent).await.ok()
}

pub async fn write(folder: &Path, format: ExportFormat, single_file: bool, destination: &gio::File) -> Result<(), ExportFailure> {
    let data = match format {
        ExportFormat::Html if single_file => {
            std::fs::read(folder.join("index.html")).map_err(|_| ExportFailure::Unwritable)?
        }
        // A many-file archive is made off the main thread, where an archive of
        // many files would hold the whole window still.
        ExportFormat::Html => {
            let folder = folder.to_path_buf();
            gio::spawn_blocking(move || zip_folder(&folder))
                .await
                .map_err(|_| ExportFailure::Unwritable)?
                .ok_or(ExportFailure::Unwritable)?
        }
        ExportFormat::Markdown | ExportFormat::Pdf | ExportFormat::Word => render(folder, format).await?,
    };
    destination
        .replace_contents_future(data, None, false, gio::FileCreateFlags::REPLACE_DESTINATION)
        .await
        .map_err(|_| ExportFailure::Unwritable)?;
    Ok(())
}

/// Every file of the page, at the paths it was read into.
pub fn zip_folder(folder: &Path) -> Option<Vec<u8>> {
    let mut zip = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
    let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    let mut stack = vec![folder.to_path_buf()];
    let mut files: Vec<PathBuf> = Vec::new();
    while let Some(dir) = stack.pop() {
        for entry in std::fs::read_dir(&dir).ok()?.flatten() {
            let path = entry.path();
            // Only what the export itself wrote: a link in the folder would
            // send the archive somewhere else on the disk.
            let kind = entry.file_type().ok()?;
            if kind.is_dir() {
                stack.push(path);
            } else if kind.is_file() {
                files.push(path);
            }
        }
    }
    files.sort();
    for path in files {
        let name = path.strip_prefix(folder).ok()?.to_string_lossy().into_owned();
        zip.start_file(name, options).ok()?;
        zip.write_all(&std::fs::read(&path).ok()?).ok()?;
    }
    zip.finish().ok().map(|c| c.into_inner())
}

const SCHEME: &str = "graft-export";

thread_local! {
    static FOLDERS: RefCell<HashMap<String, PathBuf>> = RefCell::new(HashMap::new());
    static REGISTERED: Cell<bool> = const { Cell::new(false) };
}

/// The scheme is served by Graft from the one folder its address names, and
/// only the files inside it: a path that climbs out, or a folder no export is
/// drawing, is not found.
fn register_scheme() {
    if REGISTERED.with(Cell::get) {
        return;
    }
    REGISTERED.with(|r| r.set(true));
    let Some(context) = webkit::WebContext::default() else { return };
    if let Some(security) = context.security_manager() {
        security.register_uri_scheme_as_secure(SCHEME);
    }
    context.register_uri_scheme(SCHEME, |request| {
        let uri = request.uri().map(|u| u.to_string()).unwrap_or_default();
        match serve(&uri) {
            Some((bytes, mime)) => {
                let length = bytes.len() as i64;
                let stream = gio::MemoryInputStream::from_bytes(&glib::Bytes::from_owned(bytes));
                request.finish(&stream, length, Some(&mime));
            }
            None => {
                let mut error = glib::Error::new(gio::IOErrorEnum::NotFound, "not part of this export");
                request.finish_error(&mut error);
            }
        }
    });
}

/// The bytes and type of `graft-export://<token>/<path>`, or `None` for
/// anything that is not a file of that export.
pub fn serve(uri: &str) -> Option<(Vec<u8>, String)> {
    let rest = uri.strip_prefix(&format!("{SCHEME}://"))?;
    let rest = rest.split(['?', '#']).next()?;
    let (token, path) = rest.split_once('/')?;
    let path = decode(path)?;
    if !crate::api::is_safe_file_path(&path) {
        return None;
    }
    let folder = FOLDERS.with(|f| f.borrow().get(token).cloned())?;
    let file = folder.join(&path);
    let bytes = std::fs::read(&file).ok()?;
    let (guess, _) = gio::content_type_guess(Some(&file), &bytes[..]);
    let mime = gio::content_type_get_mime_type(&guess).map(|m| m.to_string()).unwrap_or_else(|| "application/octet-stream".into());
    Some((bytes, mime))
}

fn decode(path: &str) -> Option<String> {
    let bytes = path.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            let hex = std::str::from_utf8(bytes.get(i + 1..i + 3)?).ok()?;
            out.push(u8::from_str_radix(hex, 16).ok()?);
            i += 3;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    String::from_utf8(out).ok()
}

/// Whether a navigation stays inside the export being drawn.
pub fn is_inside(uri: &str, token: &str) -> bool {
    uri.starts_with(&format!("{SCHEME}://{token}/")) || uri.starts_with("about:")
}

/// Letter, as the page would print, for the PDF.
const PAGE_WIDTH: i32 = 816;
const PAGE_HEIGHT: i32 = 1056;
const LOAD_LIMIT: Duration = Duration::from_secs(20);
/// A page that draws itself with script has usually finished a moment after
/// it loads; this is the moment.
const SETTLE: Duration = Duration::from_millis(600);

pub async fn render(folder: &Path, format: ExportFormat) -> Result<Vec<u8>, ExportFailure> {
    register_scheme();
    let token = serving(folder);
    let result = render_in(&token, format).await;
    FOLDERS.with(|f| f.borrow_mut().remove(&token));
    result
}

/// Puts a folder behind an address of its own, unguessable by the page, so
/// one export cannot be reached from another.
pub(crate) fn serving(folder: &Path) -> String {
    let token = uuid::Uuid::new_v4().simple().to_string();
    FOLDERS.with(|f| f.borrow_mut().insert(token.clone(), folder.to_path_buf()));
    token
}

async fn render_in(token: &str, format: ExportFormat) -> Result<Vec<u8>, ExportFailure> {
    let session = webkit::NetworkSession::new_ephemeral();
    let view = webkit::WebView::builder().network_session(&session).build();
    view.set_size_request(PAGE_WIDTH, PAGE_HEIGHT);
    let inside = token.to_string();
    view.connect_decide_policy(move |_, decision, kind| {
        if kind == webkit::PolicyDecisionType::Response {
            return false;
        }
        let uri = decision
            .downcast_ref::<webkit::NavigationPolicyDecision>()
            .and_then(|d| d.navigation_action())
            .and_then(|a| a.request())
            .and_then(|r| r.uri())
            .map(|u| u.to_string())
            .unwrap_or_default();
        if is_inside(&uri, &inside) {
            decision.use_();
        } else {
            decision.ignore();
        }
        true
    });
    let (done, loaded) = oneshot::channel::<bool>();
    let done = RefCell::new(Some(done));
    let finish = std::rc::Rc::new(move |ok: bool| {
        if let Some(done) = done.borrow_mut().take() {
            let _ = done.send(ok);
        }
    });
    let on_load = std::rc::Rc::clone(&finish);
    view.connect_load_changed(move |_, event| {
        if event == webkit::LoadEvent::Finished {
            on_load(true);
        }
    });
    let on_fail = std::rc::Rc::clone(&finish);
    view.connect_load_failed(move |_, _, _, _| {
        on_fail(false);
        false
    });
    view.load_uri(&format!("{SCHEME}://{token}/index.html"));
    let timeout = std::rc::Rc::clone(&finish);
    glib::MainContext::default().spawn_local(async move {
        glib::timeout_future(LOAD_LIMIT).await;
        timeout(false);
    });
    if !loaded.await.unwrap_or(false) {
        return Err(ExportFailure::Unrenderable);
    }
    glib::timeout_future(SETTLE).await;
    match format {
        ExportFormat::Markdown => {
            let value = view
                .call_async_javascript_function_future(MARKDOWN_SCRIPT, None, None, None)
                .await
                .map_err(|_| ExportFailure::Unrenderable)?;
            Ok(value.to_str().as_bytes().to_vec())
        }
        ExportFormat::Word => {
            let value = view
                .call_async_javascript_function_future(DOCUMENT_SCRIPT, None, None, None)
                .await
                .map_err(|_| ExportFailure::Unrenderable)?;
            let page: docx::Page =
                serde_json::from_str(value.to_str().as_str()).map_err(|_| ExportFailure::Unrenderable)?;
            let bytes = docx::document(&page);
            if bytes.is_empty() {
                Err(ExportFailure::Unrenderable)
            } else {
                Ok(bytes)
            }
        }
        ExportFormat::Pdf => pdf(&view).await,
        ExportFormat::Html => Err(ExportFailure::Unrenderable),
    }
}

/// WebKit's own printing, to a file and never to a printer, on Letter.
async fn pdf(view: &webkit::WebView) -> Result<Vec<u8>, ExportFailure> {
    let target = std::env::temp_dir().join(format!("graft-export-{}.pdf", uuid::Uuid::new_v4()));
    let settings = gtk::PrintSettings::new();
    settings.set_printer("Print to File");
    settings.set(gtk::PRINT_SETTINGS_OUTPUT_FILE_FORMAT, Some("pdf"));
    settings.set(gtk::PRINT_SETTINGS_OUTPUT_URI, Some(gio::File::for_path(&target).uri().as_str()));
    let setup = gtk::PageSetup::new();
    setup.set_paper_size(&gtk::PaperSize::new(Some(gtk::PAPER_NAME_LETTER)));
    let operation = webkit::PrintOperation::new(view);
    operation.set_print_settings(&settings);
    operation.set_page_setup(&setup);
    let (done, finished) = oneshot::channel::<bool>();
    let done = std::rc::Rc::new(RefCell::new(Some(done)));
    let on_finish = std::rc::Rc::clone(&done);
    operation.connect_finished(move |_| {
        if let Some(done) = on_finish.borrow_mut().take() {
            let _ = done.send(true);
        }
    });
    let on_fail = std::rc::Rc::clone(&done);
    operation.connect_failed(move |_, _| {
        if let Some(done) = on_fail.borrow_mut().take() {
            let _ = done.send(false);
        }
    });
    operation.print();
    let ok = finished.await.unwrap_or(false);
    let bytes = std::fs::read(&target).ok();
    let _ = std::fs::remove_file(&target);
    match (ok, bytes) {
        (true, Some(bytes)) if bytes.starts_with(b"%PDF") => Ok(bytes),
        _ => Err(ExportFailure::Unrenderable),
    }
}
