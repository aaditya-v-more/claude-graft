# Working on Claude Graft

## Style, first

Comments say **why**, never what. Delete a comment that restates the line under it.

    // WRONG — increments the counter
    failures += 1

    // RIGHT — a refused call retried on the next tick becomes 120 attempts an
    // hour, which is how a client earns a rate limit rather than avoids one.
    failures += 1

A comment is for the non-obvious: a workaround, an edge-case quirk, a knotty
piece of regex or arithmetic, an external constraint. Never a narration of the
line beside it — no `// loop through items`, `// initialize variable`, `// check
if user exists`. Reach for a clearer name or shape before reaching for a
comment. Never leave a mark that the code was generated or changed — no
`// Added`, no `/* Updated logic */`, no changelog in the source. And leave the
docs and docstrings already here alone unless the task is to rework them.

And keep it short. A why-comment is a line, two at the outside; when it wants a
paragraph the reason has become an invariant, and invariants live in this file
or the README, not in a block beside the code. Porting from the mac build is
where this slips — that source is commented at length on purpose, so carry the
reason across, not the prose that surrounds it there.

Prose in the README, not bullet lists. Tests read as sentences — `check(…, "a
profile with no stored login has no token")`, not `"testTokenNil"`.

Nothing that leaves this machine may name a tool, a model or a pipeline. That
includes commit messages, the README and anything pushed. Write what a person
would have written by hand.

## Where working knowledge lives

In the repository, and nowhere else. A fact kept in a private notes store beside
it goes stale where nobody sees it: the private record of this machine's profile
layout went on saying the opposite of the truth for two months after the
profiles had changed.

This file holds the rules. The reasoning behind each one, the incident that
taught it and the measurements that settled it are in `notes/`, one file per
area, linked from the heading each rule sits under. **Read the note before
changing code in its area** — nearly every rule here exists because two
situations look identical from inside the code, and the note is what says how
to tell them apart. A new fact goes into the note it belongs to; a new rule gets
one line here as well.

## Where builds and tests run

Not on the development machine. There code is edited, committed and read, and
nothing else: no build, no suite, no layout or opening check, no launch of the
app and no release check. Mac builds and every Mac check run on the Mac mini or
the macOS VMs, Windows builds and checks on a Windows test PC, each from a
commit. The commands below are what those machines run.

For prototyping, debugging and live development a developer may work on a test
machine directly, over ssh or by driving it, holding that machine's lock for
the length of the work and leaving it as found: nothing device-wide changed, no
accounts, network or other products touched. What that shows is a development
check; a release and the everyday-use cases are still judged by the testers.

Steps that need a person on a real machine are cases in `Tests/manual/`. A
case says what must not be touched and how to put the machine back, and never
releases, commits or touches a chat. A keychain prompt, permission or
setting it meets is answered by whoever runs the case: Allow, never Always
Allow.

## Commands

    ./build.sh                     app + launcher into build.noindex/, this arch only
    GRAFT_UNIVERSAL=1 ./build.sh   both arches, joined with lipo
    ./test.sh                      the suite, all in a throwaway directory
    ./test-layout.sh               window layout from outside, both languages; needs Accessibility
    ./test-opening.sh              launch paths against disposable, signed-out profiles
    ./test-sidebar.sh              sidebar sync against real Chromium storage; needs Claude and Node
    ./test-relay.sh                the shared-usage relay over loopback, against a stand-in service
    ./release.sh [--install]       tests, builds universal, draws the icon, signs, packages

With only the Command Line Tools installed, the macOS 27 SDK they default to
cannot compile the app's SwiftUI; put
`SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk` in front of
any of these. The why is in [notes/updates-and-releases.md](notes/updates-and-releases.md).

## Rules

### Opening a profile — [notes/opening-profiles.md](notes/opening-profiles.md)

- **Never touch an app Graft did not create.** Bundles are known by the `graft.json` they carry, never by name; `Claude` and `Claude Graft` are reserved.
- **Profile folders are validated:** one plain component inside Application Support, never `Claude`, never a path. Reserved names and shared folders are compared as the disk compares them, ignoring case (`Graft.isNamed`), and `deleteProfile` refuses Graft's own folder.
- **One profile folder, one shortcut.** A new shortcut is never offered a name whose folder another shortcut kept through a rename or that is still on disk (`uniqueName`), typing its name never gives it such a folder (`suggestedFolder`), and `Installer.install` refuses to put a new or moved shortcut on a folder another one uses. A shortcut's app is looked up with the whole list (`among:`) and its figures by shortcut, never by folder alone; a name another shortcut was created as is that shortcut's app, never looked up or written over for another.
- **Every route that opens a profile asks first** through `ChatConflict`; the suite fails if one of the three views stops naming it.
- **A missing shortcut is never permission to open another profile.** `ProfileOpener` reports an error instead.
- **Opening a profile that is already open shows the Claude that is there.** Nothing in `Sources/App` calls `Graft.launch` directly.
- **A shortcut behaves like the Graft that built it.** `Installer.refreshLaunchers` replaces stale launchers by version stamp, so launcher changes need a version bump.
- **The bundle's `graft.json` is what runs, not `shortcuts.json`.** `Installer.refreshConfig` repairs the bundle at every launch.
- **A shortcut's app is found by the profile it opens,** its names first: `Installer.installedBundle(for:among:)`, the one lookup Open, the repairs and Delete share.
- **`refreshConfig` repairs the source and never moves the profile.** A folder reaches the bundle only through Update; until then the window holds a created shortcut's folder edit back.
- **The pid comes from `ps`, never pgrep;** `isClaudeProcess` picks the browser process out of the helpers.
- **A bundle is not an instance:** `launch` passes `-n`, and no `--user-data-dir` for the main profile.
- **Every `pgrep` on `user-data-dir=` is anchored with `([[:space:]]|$)`.**

### Sharing chats — [notes/chat-sharing.md](notes/chat-sharing.md)

- **Never destroy what a profile owns.** What a link replaces goes to `.<name>.graft-own` first; a profile is never grafted from itself.
- **A stash already sitting there is not proof of a duplicate.** `stash` folds the two together.
- **An unreadable `config.json` is not an empty one.** Ask `readableConfigJSON` before writing it or deciding where chats go.
- **A mirrored folder holds both histories, and the stash says which was whose.** Seeding runs on the first pass alone, and `mirrored-chats.json` decides which pass that is.
- **Going back is a handover, not a delete.** A copy goes only when the other side holds the same bytes at that moment.
- **A folder that lost everything at once was moved, not cleared.** Forget the baseline; `forgetStalePairs` drops dead pairs.
- **The baseline says what the filesystem did, never what a pass meant to do.**
- **Two sides that both moved are merged field by field** against the fields at the baseline (`mergeRecordFields`, kept in `ClaudeGraft/mirror-fields/`), so an archive or an unarchive survives the other side merely showing the chat. A field both changed is settled as the whole record used to be, and so is a record whose `lastActivityAt` went back, except that an archive state both changed comes out unarchived; `isArchived` counts as decided where `resumedAt` moved.
- **A chat both sides carried on is split, never merged:** both `lastAssistantUuid` moved to different values and both `lastActivityAt` forward, and only with neither Claude open. The borrower's half takes a new name; both are titled with their Claude's name (`claudeName(of:)`). An archive, a rename or a look never splits a chat.
- **A readable record beats one that is not,** and a pass goes round its pairs until a round moves nothing.
- **One pass at a time, across processes:** `Graft.withStateLock` holds `ClaudeGraft/state.lock` over every read-modify-write of `mirrored-chats.json` and `session-records.json`, and is let go before `launch` takes the launch lock. On Windows a pass that cannot get it in time does not run (`StateBusyException`).
- **Nothing goes to the stash before the source has been read.**
- **A stash beside a folder blocks withdrawal as well as filing** (`mayFileRecords`, both directions).
- **Every way out drops the pairs**, and a pair is read by its borrowing half: `mirrorPairs(borrowedBy:)`.
- **A first pass that finds a stash already there is a second one;** `dropBorrowedCopies` runs only on a folder resolving inside the profile.
- **A source that moved is a re-pair, not a first pass:** `firstPass` is asked before any pair is dropped, and `repair` hands the old source its copies back first.
- **Chats an account switch left behind are copied, never moved,** and nothing moves while any Claude is up.
- **That offer is silenced at the door, never in the window.**
- **A new Claude reads its own chats;** only `ContentView.add()` says so, and on Windows `ProfileEditor.Edit` for a new draft. `Shortcut`'s default and decoding stay `.main`, so no saved Claude changes what it reads.
- **A store may spell account and organization by their first eight characters** — `counterpartDirectory`. Every walk asks `isDirectory`; `skills-plugin` is not an account.
- **An unfiled chat is unfiled only under the profile reading it.** Claude compares `originCwd` with its own `scratch-workspaces`, so a mirrored scratch record gets a local alias on each side and its `originCwd` stays out of the digest.

### Session records — [notes/session-records.md](notes/session-records.md)

- **A profile writes no record into a folder that resolves outside itself.** That, not the account, is why grafted profiles lose sessions and archives.
- **Ownership is `ownerAccountUuid` on the `bridge-session` line,** set by the machine's command line login and never read back.
- **The sweep writes the record** and keeps it current until Claude rewrites it in its own shape.
- **A filed record never grants more than its transcript says:** no `chromePermissionMode`, `permissionMode` falls back to `default`, and a record in the older shape is rewritten (`olderSessionRecordData`).
- **A record you cannot see is neither deleted nor unfiled.** A store must have been read (`StoreContents.stores`) and missing twice (`vanished`) before a withdrawal; `isStashedAway` checks every folder up to the store, and a record the walk found is in sight whatever stash sits beside it.
- **A record remembered where nothing exists any more is a pair to drop.**
- **A conversation carried on or asked again is not a second one** — `superseded`, `resumedCopies`. **A fork is**, and only its record says so: keep `forks` in `session-records.json`.
- **A Claude this app did not make is read, never written to** — `recordFilingProfiles`.
- **A session from another Mac is not a missing one:** `sessionFolderIsHere` asks whether its home folder or drive exists here, never the folder itself.
- **The sweep runs on no timer:** at a launcher's open, at Graft's start, when the dropdown opens and a few seconds after any Claude quits. A cold pass must stay cheap enough to stand in front of a window opening.

### Usage and credentials — [notes/usage-and-credentials.md](notes/usage-and-credentials.md)

- **Credentials are borrowed, never taken.** Only the access token is decrypted; nothing is written back; a token goes to `api.anthropic.com` alone, `platform.claude.com` sees only a pane sign-in's one-time code, and claude.ai sees only an artifact sign-in made in Graft's own window, which Graft never reads.
- **A borrowed token touches no cache:** it goes out through `UsageAPI.session`, ephemeral with no `URLCache`; nothing names `URLSession.shared`, and a launch empties its cache.
- **The keychain is asked once, and only when asking is the way through** — `ClaudeCredentials.mayRaiseDialog`, made silent by `SecKeychainSetUserInteractionAllowed`. A pass told no asks no other profile (`UsageMonitor.prompting`), and Start Session's follow-up refresh asks nobody.
- **Only a person starts a session.** The suite fails on a fourth caller or one wired to `onAppear`, `onReceive` or a timer.
- **One account, one figure:** both detail views read `UsageMonitor.entry(for:)`.
- **No five-hour limit is not 0%:** `fiveHour` is nil for such an account, never zero; the menu bar shows its week as `W`, and Start Session is hidden.
- **Polling:** `mayUseCache` gives five minutes to a tick, two for an open profile, a minute for the dropdown, two seconds for a press; `arrival` queues rather than drops; only the service's failures back off (`cameFromService`), a local one is retried next pass under its own name, and `Retry-After` wins until a failure arrives without one.

### Shared usage — [notes/shared-usage.md](notes/shared-usage.md)

- **Only `POST /v1/messages` changes accounts.** Everything else a conversation owns keeps the sender's token, and the Chat tab is never shared.
- **What cannot change accounts goes out as it came,** byte for byte: an upgrade is spliced after its `101`, a chunked body forwarded as it arrives. Remote Control needs no syncing.
- **Lending belongs to the Claude that borrows** — `usage-sharing.json`. One account is counted once, under the first of its names that can lend; the borrower's own account adds nothing.
- **Claude Code is reached through `HTTPS_PROXY` and `NODE_EXTRA_CA_CERTS`, never an address,** and the port never moves from 47823.
- **The lines go only into a closed Claude,** under `SidebarSync.withLaunchLock`; a launcher swaps only the exact value `pool/environment.json` expects. `PoolEnvironment.swift` alone names the file.
- **The lines take nothing away:** a proxy or certificate a person set is left alone, and copies in `scopedEnvVars` come out whenever the profile is found closed. A name holding exactly Graft's value is Graft's whatever sits beside it: it comes out at the next close and keeps the relay up until then.
- **No relay is worse than no sharing.** `graft-relay` outlives Graft; a launcher that cannot reach it opens without the lines, and the lines come out before the relay goes.
- **A relay is replaced only by a Graft that can configure its successor** (`relayStep`): with the key shut nothing is configured, the relay there is kept with what it has, and the dropdown says lending is paused. One whose status will not decode is sent a bare quit.
- **Graft going takes the lines with it.** `graft-launch --stop-sharing`, for the cask's uninstall, and the relay, once its own file is missing at two looks a minute apart, put each closed Claude's plain value back from the journal (`PoolEnvironment.takeOutLines`) and mark every entry unwanted; the relay stops lending and leaves once none still holds them.
- **The authority vouches for `api.anthropic.com` alone,** its key deleted once it has signed and the identity imported with `kSecImportToMemoryOnly`, which is why sharing needs macOS 15. Only the relay holding `relay.lock` renews it.
- **A sender is the SHA-256 of its bearer** against its profile's token cache; a request nobody owns passes through, and a pane sign-in's refresh token is never read. A config caught mid-write lends the last login read whole, ten minutes at most (`steady`).
- **Which account answers:** room is under 98% of five hours and 99% of the week, soonest weekly reset first; a conversation stays while its cache lives, and a choice from the dropdown outranks both until a day of quiet.
- **A conversation carrying reasoning stays where it was written:** `carriesBoundReasoning` holds it past its cache and moves it only when that account has no room, then leaves it there; a model is bound unless `reasoningTravels` names it. The holder is mapped to the name its account is counted under, and one no longer lent sends it home.
- **A thread stays where it is stored:** a `continuesThread` request is held like reasoning, whatever the model, and only answers to requests carrying a thread say where a conversation is.
- **A borrowed login's refusal is retried, never shown:** a 429 moves on, a 401 marks that login refused, and a 400, 403 or 404 sends the request home — except `thread_not_found` on a moved thread, which Claude Code must hear to start it again where it went. Home is the sender's next attempt, and every plan ends with one (`PoolState.plan`), so a lent account's answer is never the one delivered.
- **Some requests never move:** uploaded-file and server-side MCP references, encoded bodies, and a top-level API container; a tool argument named `container` in conversation history is not one.
- **The dropdown's front Claude is `FrontmostClaude`'s,** and its watch on the relay ends in `popoverDidClose`, never in the view.
- **The conversation on screen runs across the bar** while it is there, drawn at a fixed width (`NewChatTicker`): the one Claude's own records focused last (`lastFocusedAt`, owned by the store it was first seen in, since the mirror copies it), by its title alone if the relay never carried it, and only where no record says, the one typed into last (`promptedAt`, a turn on a thread). `StoreWatch` wakes a look when a store changes. The text gives way when it would push the item out of the bar (`keepReachable`).
- **Graft never moves the text itself:** text too long for the bar leaves a blank in the title and runs in `NewChatTicker.Strip`, a layer Core Animation steps; no timer, no redraw of the button, and the layer is drawn only when the appearance or scale changes. The strip goes where a marked drawing of the button shows the blank (`Picture.place`), never where a layout of the title predicts it.
- **The two-second walk dates each record with one `lstat`** (`Graft.modificationDate`), never `attributesOfItem`.
- **A conversation archived or deleted leaves the bar and the dropdown** (`lookUpRecords`): its own Claude's records decide, a record is gone only after missing twice, and deleting the one on screen keeps its focus.

### Mods — [notes/mods.md](notes/mods.md)

- **A per-Claude mod list replaces the one in settings, never joins it,** so the value Graft writes carries `~/.claude/settings.json`'s folders too. It goes into a closed Claude only, under the launch lock, ahead of the sharing pass, and a value Graft did not write is never replaced (`ModEnvironment`). An empty list is a list: it is how a settings mod is left out of one Claude.
- **The mod list is read when Browse is shown or refreshed, never on a timer,** and a mod is installed at the commit its scan read, after asking and after Claude Code's validator passes it (`ModCatalog.Install`).

### Artifacts — [notes/artifacts.md](notes/artifacts.md)

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

### Projects and search — [notes/projects-and-search.md](notes/projects-and-search.md)

- **One sign-in serves Artifacts, Projects and Search Chats.** Accounts live in `ArtifactCenter` alone; `ProjectCenter` follows `$accounts` and `$lastSignIn` and keeps no sign-in of its own.
- **The `X-Frame-*` headers go to `/api/frame/` alone;** the rest of claude.ai is asked as its pages ask it.
- **A search goes out on Return or the button, never as the query is typed,** and a filter asks nothing.
- **A Code session is placed by `bridgeSessionId`,** the same `cse_…` the server lists, and its folder by `sessionFolderIsHere`.
- **Gone from claude.ai is claimed only after the account's whole session list was read.** A deleted chat cannot be found and is never claimed.
- **Gone is claimed against the list it was read with:** the transcript's `ownerOrganizationUuid` must be that list's organization and its last line older than the list; the local read is never older than the list it stands beside (`localReadServes`).
- **An earlier half of a conversation still going is never gone** — a transcript a record names in `priorCliSessionIds` is `carriedOn`.
- **The sweep's transcript cache is read, never written,** from a search.

### Interface — [notes/interface.md](notes/interface.md)

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

### Updates and releases — [notes/updates-and-releases.md](notes/updates-and-releases.md), [notes/update-relaunch.md](notes/update-relaunch.md)

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
- **Every release runs the everyday-use cases** (`Tests/manual/everyday-*.md`) on Mac and Windows, besides the scripted ones: a tester doing a person's tasks and reporting anything that would stop or irritate them.
- **A bug a person hit gets a test that would have caught it** before its fix ships, in the suite where that code is tested.

### Sidebar and desktop settings — [notes/sidebar-and-settings.md](notes/sidebar-and-settings.md)

- **Pins live in IndexedDB and local storage, not session records.** `SidebarSync` runs only with every connected profile closed and never writes Chromium files directly.
- **No pin record means no pins**, not an uninitialized profile. The helper seals its own integrity digest into its cloned framework, since Electron 44 checks the dictionary against one.
- **The helper cache holds one key** — `pruneHelperCache` after every `prepareHelper`, excluded from backups — and a damaged copy is set aside for a fresh one, never a refusal.
- **`claude_desktop_config.json` belongs to each profile;** `copyDesktopServers` adds only missing MCP definitions.
- **The helper rolls that file back byte for byte** and writes it forward in its own indent and mode (`preferencesText`, `atomicWrite`).
- **Project order is per account, and Claude displays it from the desktop preferences.** `epitaxyPrefs["code-projects-order.<account>"]` is what the sidebar shows; the `LSS-persisted.code-projects-order.<account>` copy carries the time. `synchronizeProjectOrder` writes the newer order into both, preferences first and through the helper, translating `preset::` ids, before an open and, for every group, when the last Claude quits; after a quit, Claude's own profile, which the Dock opens without waiting for the lock, gets the preferences alone.

### Website and search — [notes/website-and-search.md](notes/website-and-search.md)

- **Nothing tells Google a site exists but you.** `docs/robots.txt` names `docs/sitemap.xml`, and every page at the top of `docs/` is in it; the suite fails otherwise.
- **The site's address is `graft.aadityamore.com`,** in the canonical, the structured data, the README and the repository's homepage. The github.io address only redirects, and only the feed keeps it.
- **A new page or address goes to Search Console the day it ships:** the sitemap submitted in the `aadityamore.com` Domain property, the page sent through URL Inspection, and the submission recorded in the note and in the portfolio's `docs/seo.md`.
- **`release-notes/` stays out of the index;** they are fragments of the update dialog, not pages.
- **The page is found by what it does:** the title and the one `<h1>` name Claude Graft and say "multiple Claude accounts", and every question in the `FAQPage` data is on the page word for word.

### Diagnostics and verification — [notes/diagnostics-and-verification.md](notes/diagnostics-and-verification.md)

Each pass appends to `ClaudeGraft/diagnostics.log` and rewrites
`ClaudeGraft/state-report.txt`; read those before guessing. The note also says
what has been seen working on a real machine and what is still unproven.
