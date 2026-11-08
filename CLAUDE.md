# Working on Claude Graft

## Style, first

Comments say **why**, never what. Delete a comment that restates the line under it.

    // WRONG — increments the counter
    failures += 1

    // RIGHT — a refused call retried on the next tick becomes 120 attempts an
    // hour, which is how a client earns a rate limit rather than avoids one.
    failures += 1

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

## Testing that can end the session doing it

Work on this app is done from inside a Claude that Graft itself manages, so a
test that quits or relaunches Graft, swaps in a development build, opens, quits
or brings forward a Claude, or installs an update can stop the session running
it halfway through. Those are written up as a plan in `dist/<topic>/HANDOFF.md`
and run from outside it: the exact steps, what each should show and where to
check it, what must not be touched, how to put the installed Graft back, and a
table for the results. `dist/` because it is ignored and `release.sh` clears
only what it builds there; every `./build.sh` clears `build.noindex/`, which has
already taken one plan and its results with it.

Done in place is everything that cannot disturb a running Claude: the suite, the
layout checks, builds, read-only runs over this machine's data, and sandboxes
cloned from it. A plan never releases, commits or touches a chat. A keychain
prompt it meets is answered by the person whose keychain it is.

The development machine has Claude's own profile and one shortcut, Claude 2,
grafted from it. A plan may bring forward a Claude that is already open; it
opens and quits none.

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
- **Profile folders are validated:** one plain component inside Application Support, never `Claude`, never a path.
- **Every route that opens a profile asks first** through `ChatConflict`; the suite fails if one of the three views stops naming it.
- **A missing shortcut is never permission to open another profile.** `ProfileOpener` reports an error instead.
- **Opening a profile that is already open shows the Claude that is there.** Nothing in `Sources/App` calls `Graft.launch` directly.
- **A shortcut behaves like the Graft that built it.** `Installer.refreshLaunchers` replaces stale launchers by version stamp, so launcher changes need a version bump.
- **The bundle's `graft.json` is what runs, not `shortcuts.json`.** `Installer.refreshConfig` repairs the bundle at every launch.
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
- **Nothing goes to the stash before the source has been read.**
- **A stash beside a folder blocks withdrawal as well as filing** (`mayFileRecords`, both directions).
- **Every way out drops the pairs**, and a pair is read by its borrowing half: `mirrorPairs(borrowedBy:)`.
- **A first pass that finds a stash already there is a second one;** `dropBorrowedCopies` runs only on a folder resolving inside the profile.
- **A source that moved is a re-pair, not a first pass:** `firstPass` is asked before any pair is dropped, and `repair` hands the old source its copies back first.
- **Chats an account switch left behind are copied, never moved,** and nothing moves while any Claude is up.
- **That offer is silenced at the door, never in the window.**
- **A new Claude reads its own chats;** only `ContentView.add()` says so. `Shortcut`'s default and decoding stay `.main`, so no saved Claude changes what it reads.
- **A store may spell account and organization by their first eight characters** — `counterpartDirectory`. Every walk asks `isDirectory`; `skills-plugin` is not an account.

### Session records — [notes/session-records.md](notes/session-records.md)

- **A profile writes no record into a folder that resolves outside itself.** That, not the account, is why grafted profiles lose sessions and archives.
- **Ownership is `ownerAccountUuid` on the `bridge-session` line,** set by the machine's command line login and never read back.
- **The sweep writes the record** and keeps it current until Claude rewrites it in its own shape.
- **A record you cannot see is neither deleted nor unfiled.** A store must have been read (`StoreContents.stores`) and missing twice (`vanished`) before a withdrawal; `isStashedAway` checks every folder up to the store.
- **A record remembered where nothing exists any more is a pair to drop.**
- **A conversation carried on or asked again is not a second one** — `superseded`, `resumedCopies`. **A fork is**, and only its record says so: keep `forks` in `session-records.json`.
- **A Claude this app did not make is read, never written to** — `recordFilingProfiles`.
- **A session from another Mac is not a missing one:** `sessionFolderIsHere` asks whether its home folder or drive exists here, never the folder itself.
- **The sweep runs on no timer:** at a launcher's open, at Graft's start and when the dropdown opens. A cold pass must stay cheap enough to stand in front of a window opening.

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
- **The lines take nothing away:** a proxy or certificate a person set is left alone, and copies in `scopedEnvVars` come out whenever the profile is found closed.
- **No relay is worse than no sharing.** `graft-relay` outlives Graft; a launcher that cannot reach it opens without the lines, and the lines come out before the relay goes.
- **A relay is replaced only by a Graft that can configure its successor** (`relayStep`): with the key shut nothing is configured, the relay there is kept with what it has, and the dropdown says lending is paused. One whose status will not decode is sent a bare quit.
- **The authority vouches for `api.anthropic.com` alone,** its key deleted once it has signed and the identity imported with `kSecImportToMemoryOnly`, which is why sharing needs macOS 15.
- **A sender is the SHA-256 of its bearer** against its profile's token cache; a request nobody owns passes through, and a pane sign-in's refresh token is never read. A config caught mid-write lends the last login read whole, ten minutes at most (`steady`).
- **Which account answers:** room is under 98% of five hours and 99% of the week, soonest weekly reset first; a conversation stays while its cache lives, and a choice from the dropdown outranks both until a day of quiet.
- **A conversation carrying reasoning stays where it was written:** `carriesBoundReasoning` holds it past its cache and moves it only when that account has no room, then leaves it there; a model is bound unless `reasoningTravels` names it.
- **A thread stays where it is stored:** a `continuesThread` request is held like reasoning, whatever the model, and only answers to requests carrying a thread say where a conversation is.
- **A borrowed login's refusal is retried, never shown:** a 429 moves on, a 401 marks that login refused, and a 400, 403 or 404 sends the request home — except `thread_not_found` on a moved thread, which Claude Code must hear to start it again where it went.
- **Some requests never move:** a body referring to a file, container or MCP server by value (`accountBoundMarks`, never the bare key, which a tool schema can carry), or one that is encoded.
- **The dropdown's front Claude is `FrontmostClaude`'s,** and its watch on the relay ends in `popoverDidClose`, never in the view.
- **The conversation on screen runs across the bar** while it is there, drawn at a fixed width (`NewChatTicker`): the one Claude's own records focused last (`lastFocusedAt`, owned by the store it was first seen in, since the mirror copies it), by its title alone if the relay never carried it, and only where no record says, the one typed into last (`promptedAt`, a turn on a thread). `StoreWatch` wakes a look when a store changes. The text gives way when it would push the item out of the bar (`keepReachable`).
- **A conversation archived or deleted leaves the bar and the dropdown** (`lookUpRecords`): its own Claude's records decide, a record is gone only after missing twice, and deleting the one on screen keeps its focus.

### Artifacts — [notes/artifacts.md](notes/artifacts.md)

- **An artifact sign-in stays in its web store.** One `WKWebsiteDataStore` per account, loaded only with claude.ai; requests go out from a claude.ai page by `callAsyncJavaScript`, never from `URLSession`, and nothing reads `httpCookieStore`. Signing out removes the store. Claude profiles' cookies are never decrypted.
- **Every `/api/frame` request carries `X-Frame-CP: go`,** or the service answers 404 as though the artifact did not exist.
- **Artifacts are asked for when the list is shown or refreshed, never on a timer.**
- **Groups are Graft's,** in `artifact-groups.json`; each person is invited with the group's role for them, an address missing from `roles` is a `reader`, and only the owner is offered a change.
- **A role an artifact does not take is lowered, never raised** — `ArtifactRole.offered(by:)`, asked of the permissions before every share.
- **Only what the list allows is offered:** rename and delete to the owner, duplicate where `duplicable` says so.
- **An export is drawn where no sign-in is.** Files come from the file host with `credentials: "omit"` into a temporary folder, and `ArtifactRenderer` draws them in a non-persistent store that follows no link out.
- **Sign-in is by emailed code alone,** and every way into one says so (`ArtifactsView.signInHint`).

### Projects and search — [notes/projects-and-search.md](notes/projects-and-search.md)

- **One sign-in serves Artifacts, Projects and Search Chats.** Accounts live in `ArtifactCenter` alone; `ProjectCenter` follows `$accounts` and `$lastSignIn` and keeps no sign-in of its own.
- **The `X-Frame-*` headers go to `/api/frame/` alone;** the rest of claude.ai is asked as its pages ask it.
- **A search goes out on Return or the button, never as the query is typed,** and a filter asks nothing.
- **A Code session is placed by `bridgeSessionId`,** the same `cse_…` the server lists, and its folder by `sessionFolderIsHere`.
- **Gone from claude.ai is claimed only after the account's whole session list was read.** A deleted chat cannot be found and is never claimed.
- **The sweep's transcript cache is read, never written,** from a search.

### Interface — [notes/interface.md](notes/interface.md)

- **⌘Q does not quit** while the status item is reachable; `QuitPolicy` lets through Quit, logout and an update's relaunch.
- **A status item that exists is not one you can click:** `MenuBarPlacement.isReachable`, which counts the notch as out of reach.
- **The window's root is a `NavigationSplitView` with a real sidebar;** notices go inside the detail column, never above the split view. The suite fails if the sidebar loses its behaviour or full height, or the title crosses its edge.
- **Every control is named and identified by hand** with `identified`, on the control, never on a stack. Never call `NSApp.setAccessibilityWindows`.
- **No `MenuBarExtra`. Nothing blocking in a view body. Long-lived objects live in `enum Shared`.**
- **The window fits at 820×560 without scrolling,** every page, and Settings fits its own: a note is one line (`OneLineNote`), its paragraph behind an `InfoButton`.
- **A running Claude keeps its own Dock icon and name;** the menu bar says which one is in front instead.
- **A colour is handed out once and never twice** (`IconPreset.next`): a new Claude or lent account takes the first free one; nothing recolours what exists, and an account that is a Claude here wears that Claude's.

### Updates and releases — [notes/updates-and-releases.md](notes/updates-and-releases.md), [notes/update-relaunch.md](notes/update-relaunch.md)

- **The version is written down once,** in `VERSION`; `Resources/Info.plist` keeps `0.0.0`. `release.sh` refuses an existing tag and rewrites the Homebrew cask.
- **A release carries both architectures. The build directory is `build.noindex`** so Spotlight skips it.
- **An update restart is not permission to undo a quit** — `UpdateRecovery` needs a fresh `stealth-relaunch` marker.
- **Checking for a Claude release never closes a Claude;** installing one asks first and never force quits.
- **Claude's updater runs hidden and signed out** — `UpdaterProfile.reset` trashes a profile someone signed into.
- **Graft's own update installs itself and says nothing,** a press included (`checkForUpdatesInBackground`); a hold is off unless turned on.
- **`sparkle:minimumAutoupdateVersion` 1.1.0 is a floor** on every entry from 1.1.0 on, and that release needs a note.
- **The feed keeps every release** (`--maximum-versions 0`), **and its URL never changes.**

### Sidebar and desktop settings — [notes/sidebar-and-settings.md](notes/sidebar-and-settings.md)

- **Pins live in IndexedDB and local storage, not session records.** `SidebarSync` runs only with every connected profile closed and never writes Chromium files directly.
- **No pin record means no pins**, not an uninitialized profile. The helper seals its own integrity digest into its cloned framework, since Electron 44 checks the dictionary against one.
- **`claude_desktop_config.json` belongs to each profile;** `copyDesktopServers` adds only missing MCP definitions.

### Diagnostics and verification — [notes/diagnostics-and-verification.md](notes/diagnostics-and-verification.md)

Each pass appends to `ClaudeGraft/diagnostics.log` and rewrites
`ClaudeGraft/state-report.txt`; read those before guessing. The note also says
what has been seen working on a real machine and what is still unproven.
