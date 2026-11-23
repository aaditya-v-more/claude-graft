# Artifacts, interface and release rules

[Back to CLAUDE.md](../../CLAUDE.md)

### Artifacts — [notes/artifacts.md](../../notes/artifacts.md)

- **An artifact sign-in stays in its web store.** One `WKWebsiteDataStore` per account, loaded only with claude.ai; requests go out from a claude.ai page by `callAsyncJavaScript`, never from `URLSession`, and nothing reads `httpCookieStore`. Signing out removes the store. Claude profiles' cookies are never decrypted.
- **A second sign-in to a listed account keeps the new store;** the record moves to it and the old store goes (`ArtifactAccounts.signedIn`).
- **A store is removed only once nothing holds it:** `retire` closes the session and waits for `released()`, a sign-in window lets go before acting, a failed removal is logged and retried, and stores not in a readable `artifact-accounts.json` go at launch.
- **WebKit is made ready before a store is listed or removed** (`prepareStores`): on macOS 27 either call crashes a process in which no store has done any work.
- **Every `/api/frame` request carries `X-Frame-CP: go`,** or the service answers 404 as though the artifact did not exist.
- **Only a 401, or a 403 from `/api/account`, is signed out;** any other 403 asks `/api/account` once and otherwise goes back as a refusal (`ArtifactAPI.meansSignedOut`).
- **Artifacts are asked for when the list is shown or refreshed, never on a timer.**
- **Groups are Graft's,** in `artifact-groups.json`; each person is invited with the group's role for them, an address missing from `roles` is a `reader`, and only the owner is offered a change.
- **An unreadable groups or accounts file is set aside, never saved over** (`ArtifactFiles.read`), and groups are read one by one (`readEach`).
- **A role an artifact does not take is lowered, never raised** — `ArtifactRole.offered(by:)`, asked of the permissions before every share.
- **Only what the list allows is offered:** rename and delete to the owner, duplicate where `duplicable` says so.
- **An access change is sent only when it changes something** (`ArtifactAccess.changes`, asked of the list and of `read.mode`); an organization-shared artifact checks its own entry (`menuChoice`).
- **An export is drawn where no sign-in is.** Files come from the file host with `credentials: "omit"` into a temporary folder, and `ArtifactRenderer` draws them in a non-persistent store that follows no link out.
- **Sign-in is by emailed code alone,** and every way into one says so (`ArtifactsView.signInHint`).

### Projects and search — [notes/projects-and-search.md](../../notes/projects-and-search.md)

- **One sign-in serves Artifacts, Projects and Search Chats.** Accounts live in `ArtifactCenter` alone; `ProjectCenter` follows `$accounts` and `$lastSignIn` and keeps no sign-in of its own.
- **The `X-Frame-*` headers go to `/api/frame/` alone;** the rest of claude.ai is asked as its pages ask it.
- **A search goes out on Return or the button, never as the query is typed,** and a filter asks nothing.
- **A Code session is placed by `bridgeSessionId`,** the same `cse_…` the server lists, and its folder by `sessionFolderIsHere`.
- **Gone from claude.ai is claimed only after the account's whole session list was read.** A deleted chat cannot be found and is never claimed.
- **Gone is claimed against the list it was read with:** the transcript's `ownerOrganizationUuid` must be that list's organization and its last line older than the list; the local read is never older than the list it stands beside (`localReadServes`).
- **An earlier half of a conversation still going is never gone** — a transcript a record names in `priorCliSessionIds` is `carriedOn`.
- **The sweep's transcript cache is read, never written,** from a search.

### Interface — [notes/interface.md](../../notes/interface.md)

- **⌘Q does not quit** while the status item is reachable; `QuitPolicy` lets through Quit, logout and an update's relaunch.
- **A status item that exists is not one you can click:** `MenuBarPlacement.isReachable`, which counts the notch as out of reach.
- **The Dock icon goes only when the item can be reached** — `WindowPolicy`, asked when a window closes and again `placementGrace` after a hidden launch.
- **The window's root is a `NavigationSplitView` with a real sidebar;** notices go inside the detail column, never above the split view. The suite fails if the sidebar loses its behaviour or full height, or the title crosses its edge.
- **Every control is named and identified by hand** with `identified`, on the control, never on a stack. Never call `NSApp.setAccessibilityWindows`.
- **No `MenuBarExtra`. Nothing blocking in a view body. Long-lived objects live in `enum Shared`.**
- **The window fits at 820×560 without scrolling,** every page, and Settings fits its own: a note is one line (`OneLineNote`), its paragraph behind an `InfoButton`.
- **The dropdown never runs past its screen:** only the account list scrolls (`DropdownColumn`), and only once it has to; the switches and Quit stay in view.
- **A running Claude keeps its own Dock icon and name;** the menu bar says which one is in front instead.
- **A colour is handed out once and never twice** (`IconPreset.next`): a new Claude or lent account takes the first free one; nothing recolours what exists, and an account that is a Claude here wears that Claude's.
- **On Windows, no English reaches the screen except through `L10n` or `local:Localized`;** a new string needs its Russian in `windows/ClaudeGraft.Core/Localization/ru.json`, and the suite fails without it.

### Updates and releases — [notes/updates-and-releases.md](../../notes/updates-and-releases.md), [notes/update-relaunch.md](../../notes/update-relaunch.md)

- **The version is written down once,** in `VERSION`; `Resources/Info.plist` keeps `0.0.0`. `release.sh` refuses an existing tag and rewrites the Homebrew cask.
- **One version, one release, both systems.** `VERSION` numbers the Windows build too; a `v*` tag runs `windows-release.yml`, which adds the Windows installer and feed to that version's release (making it, as a prerelease, if the Mac has not) and needs `windows/release-notes/<version>.md`. No portable copy: only an installed Graft updates itself.
- **A release carries both architectures. The build directory is `build.noindex`** so Spotlight skips it.
- **A local build is signed with the Mac's Apple Development certificate, a release never is** — `Tools/signing-identity.sh` for `build.sh` alone; `release.sh` always passes its own identity.
- **An update restart is not permission to undo a quit** — `UpdateRecovery` needs a fresh `stealth-relaunch` marker.
- **Checking for a Claude release never closes a Claude;** installing one asks first and never force quits.
- **A resumed update takes the lock before it reads anything,** and decides on the first snapshot that reads (`ClaudeUpdateResume`); a stopping updater is asked again every ten seconds and shown after a minute (`ClaudeUpdateStop`).
- **Claude's updater runs hidden and signed out** — `UpdaterProfile.reset` trashes a profile someone signed into.
- **Graft's own update installs itself and says nothing,** a press included (`checkForUpdatesInBackground`); a hold is off unless turned on.
- **`sparkle:minimumAutoupdateVersion` 1.1.0 is a floor** on every entry from 1.1.0 on, and **every release needs a note that says what upgrading from 1.0.x does**: a 1.0.x Mac is asked about the newest one.
- **The feed keeps every release** (`--maximum-versions 0`), **and its URL never changes.**
- **The feed is built and checked in `dist/sparkle`** — signature, length and floor asked of the new `<item>` itself — and reaches `docs/` only after the cask.
- **Nothing a person downloads or runs ships unchecked.** A release is staged first as a prerelease, which neither updater offers and the site does not call latest, with the cask on the tap's `release-<version>` branch. It is published only after `Tools/check-distribution.py --tag` (the Distribution Checks workflow) and `Tests/manual/release-install.md` on clean Mac and Windows machines pass, using the very files and commands people get. The same checks run daily on what is live.
- **Every release runs the everyday-use cases** (`Tests/manual/everyday-*.md`) on Mac and Windows, besides the scripted ones: someone doing a person's tasks and reporting anything that would stop or irritate them.
- **A bug a person hit gets a test that would have caught it** before its fix ships, in the suite where that code is tested.

### Sidebar and desktop settings — [notes/sidebar-and-settings.md](../../notes/sidebar-and-settings.md)

- **Pins live in IndexedDB and local storage, not session records.** `SidebarSync` runs only with every connected profile closed and never writes Chromium files directly.
- **No pin record means no pins**, not an uninitialized profile. The helper seals its own integrity digest into its cloned framework, since Electron 44 checks the dictionary against one.
- **The helper cache holds one key** — `pruneHelperCache` after every `prepareHelper`, excluded from backups — and a damaged copy is set aside for a fresh one, never a refusal.
- **`claude_desktop_config.json` belongs to each profile;** `copyDesktopServers` adds only missing MCP definitions.
- **The helper rolls that file back byte for byte** and writes it forward in its own indent and mode (`preferencesText`, `atomicWrite`).
- **Project order is per account, and Claude displays it from the desktop preferences.** `epitaxyPrefs["code-projects-order.<account>"]` is what the sidebar shows; the `LSS-persisted.code-projects-order.<account>` copy carries the time. `synchronizeProjectOrder` writes the newer order into both, preferences first and through the helper, translating `preset::` ids, before an open and, for every group, when the last Claude quits; after a quit, Claude's own profile, which the Dock opens without waiting for the lock, gets the preferences alone.

### Website and search — [notes/website-and-search.md](../../notes/website-and-search.md)

- **Nothing tells Google a site exists but you.** `docs/robots.txt` names `docs/sitemap.xml`, and every page at the top of `docs/` is in it; the suite fails otherwise.
- **The site's address is `graft.aadityamore.com`,** in the canonical, the structured data, the README and the repository's homepage. The github.io address only redirects, and only the feed keeps it.
- **A new page or address goes to Search Console the day it ships:** the sitemap submitted in the `aadityamore.com` Domain property, the page sent through URL Inspection, and the submission recorded in the note and in the portfolio's `docs/seo.md`.
- **`release-notes/` stays out of the index;** they are fragments of the update dialog, not pages.
- **The page is found by what it does:** the title and the one `<h1>` name Claude Graft and say "multiple Claude accounts", and every question in the `FAQPage` data is on the page word for word.

### Diagnostics and verification — [notes/diagnostics-and-verification.md](../../notes/diagnostics-and-verification.md)

Each pass appends to `ClaudeGraft/diagnostics.log` and rewrites
`ClaudeGraft/state-report.txt`; read those before guessing. The note also says
what has been seen working on a real machine and what is still unproven.
