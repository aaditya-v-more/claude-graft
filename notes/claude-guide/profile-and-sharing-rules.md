# Profile and sharing rules

[Back to CLAUDE.md](../../CLAUDE.md)

## Rules

### Opening a profile — [notes/opening-profiles.md](../../notes/opening-profiles.md)

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

### Sharing chats — [notes/chat-sharing.md](../../notes/chat-sharing.md)

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
- **An unfiled chat is unfiled only under the profile reading it.** Claude compares `originCwd` with its own `scratch-workspaces`, so a mirrored scratch record gets a local alias on each side and its `originCwd` stays out of the digest. Only a record holding both `"originCwd"` and `scratch-workspaces` as bytes is parsed to ask (on Windows, the folder in any case).
- **A walk that reads records whole lets each go before the next:** an `autoreleasepool` per record in the mirror, the sweep, the menu bar's look and sidebar sync. Nothing else drains on their queues, and 2.2.0 grew past 40 GB on a 66,000-record history.
- **A pass that moves nothing writes nothing:** `mirrored-chats.json` holds every pair and is saved only when a pass changed its own. The suite measures the walks' heap, writes and CPU against a large history.

### Session records — [notes/session-records.md](../../notes/session-records.md)

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

### Usage and credentials — [notes/usage-and-credentials.md](../../notes/usage-and-credentials.md)

- **Credentials are borrowed, never taken.** Only the access token is decrypted; nothing is written back; a token goes to `api.anthropic.com` alone, `platform.claude.com` sees only a pane sign-in's one-time code, and claude.ai sees only an artifact sign-in made in Graft's own window, which Graft never reads.
- **A borrowed token touches no cache:** it goes out through `UsageAPI.session`, ephemeral with no `URLCache`; nothing names `URLSession.shared`, and a launch empties its cache.
- **The keychain is asked once, and only when asking is the way through** — `ClaudeCredentials.mayRaiseDialog`, made silent by `SecKeychainSetUserInteractionAllowed`. A pass told no asks no other profile (`UsageMonitor.prompting`), and Start Session's follow-up refresh asks nobody.
- **Only a person starts a session.** The suite fails on a fourth caller or one wired to `onAppear`, `onReceive` or a timer.
- **One account, one figure:** both detail views read `UsageMonitor.entry(for:)`.
- **No five-hour limit is not 0%:** `fiveHour` is nil for such an account, never zero; the menu bar shows its week as `W`, and Start Session is hidden.
- **Polling:** `mayUseCache` gives five minutes to a tick, two for an open profile, a minute for the dropdown, two seconds for a press; `arrival` queues rather than drops; only the service's failures back off (`cameFromService`), a local one is retried next pass under its own name, and `Retry-After` wins until a failure arrives without one.

### Shared usage — [notes/shared-usage.md](../../notes/shared-usage.md)

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
