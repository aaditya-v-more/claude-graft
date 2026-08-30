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

## Commands

    ./build.sh                 app + launcher into build.noindex/, this arch only
    GRAFT_UNIVERSAL=1 ./build.sh   both arches, joined with lipo
    ./test.sh                  520 checks, all in a throwaway directory
    ./release.sh [--install]   tests, builds universal, draws the icon, signs, packages

## Invariants

**Never touch an app Graft did not create.** Bundles are identified by the
`graft.json` they carry, never by name. `Claude` and `Claude Graft` are reserved
names. This rule exists because an earlier version matched on name and deleted
`/Applications/Claude.app`.

**Never destroy what a profile owns.** Anything a link replaces is moved to a
hidden `.<name>.graft-own` sibling first, and restored when the shortcut goes
back to its own chats. A profile must never be grafted from itself — guarded in
`graft`, `relink` and `install`, because self-grafting stashes away every file
the profile has and leaves links pointing at their own names.

**A stash already sitting there is not proof of a duplicate.** Claude writes
`config.json`, and recreates chat directories, by renaming a temporary over
them, and a rename leaves a real file where the symlink was. The profile then
goes back to writing its own copy while the stash still holds the pre-graft
state. `stash` used to read that as drift and delete it, which threw away every
chat written since the graft; it now folds the two together and keeps the copy
the profile is actually using. `unstash` used to bail whenever something already
sat at the link, which is what left the stash there to arm the next graft.

**An unreadable `config.json` is not an empty one.** A profile that has never
been signed in has no file at all and can share the whole store safely. A file
that will not parse is one caught mid-rename, and treating that as "no account,
so the same account as the source" linked an entire store away. `readableConfigJSON`
tells the two apart; nothing may write that file, or decide where a profile's
chats go, without asking it.

**A mirrored folder holds both histories, and the stash says which was whose.**
Copying is the way round the rule below — a real folder is one a profile will
write a record into, a link is not — and it works by giving the profile its own
copy of the source's chats and carrying changes both ways. Sharing a history
merges the two rather than standing one in for the other, so the profile's own
chats go into that folder as well and both sidebars end up holding the union.

What keeps that reversible is the stash. On the first pass the profile's own
chats still go to the hidden `.<name>.graft-own` sibling a link would have moved
them to, and `seedOwnChats` copies them straight back into the shared set — the
stash keeping the copy that says which of the merged records this profile
brought, the folder getting the copy the person actually reads. Where its own
copy went depends on how the two are signed in, so `stashedCounterpart` looks
for both shapes: a cross-account graft stashes the organization folder itself,
two profiles on one account stash the whole store above it, and the same records
are then one level further down.

`unmirrorChatStores` is where that record is spent. A record goes only when the
other side holds the same bytes, which after the handover pass describes the
profile's own chats exactly as well as the borrowed ones — so the ones the stash
names are kept rather than removed, or going back would hand this profile's
whole history to the one it borrowed from and keep nothing back. Kept rather
than removed and fetched out of the stash afterwards, too, so that a chat of its
own the profile archived while the graft was up stays archived: restoring the
stashed copy over it would put the conversation back unarchived, which is the
symptom this app gets reported for most.

Seeding runs on the first pass alone, and it is the same clause that stops the
second launch stashing away everything the first one mirrored in. On every
launch it would fetch back every merged chat the person had since deleted,
which is that endless loop pointed the other way.

What is merged into the source stays there. Nothing here takes a chat out of the
profile holding it, so going back leaves those copies where they are, and the
merge is the one thing this app does that it cannot undo. That belongs in the
window rather than in a comment, and `ShortcutDetail.mergeNote` is where it is
said.

Which pass is the first one is asked of `mirrored-chats.json` and never of the
stash. `apply` runs on every launch, so a profile that had nothing to put away
— and therefore left no stash — would look new for ever, and looking new for
ever means the second launch stashes away everything the first one mirrored in
and fetches it all back. `openForMirror` is the rule, `firstPass` the clause.

**Going back is a handover, not a delete.** A link can simply be dropped; a
folder full of copies cannot, so `unmirrorChatStores` runs one last pass to
carry everything the profile did while it was mirroring — a chat started, one
archived, one deleted — over to the profile it borrowed from, and only then
takes the copies out. Going back to its own chats is the one way out that has
to do this, and `ungraft` is the only caller for that reason.

Which copies go is not a guess and does not trust that pass: a record is
removed only when the other side is holding the same bytes at that moment. A
source that has been deleted, a folder that could not be read, a disk with no
room — each leaves every copy where it is rather than taking a chat away on the
strength of a handover that did not happen.

**A folder that lost everything at once was moved, not cleared.** This is the
worst thing that has happened to this app, and it happened to a real machine
with real chats on it. Signing in again moves where a profile's chats live —
`<account>/<org>`, both halves — so the pair an earlier pass wrote down went on
naming a folder nobody wrote to any more, while `mirrorChatStores` computed a
new one, found no pair under it, and read that as a first pass. `openForMirror`
duly stashed the live mirrored set as though it were the profile's own chats.
The next `mirrorChatFolders` then saw one side holding none of the 150 names its
baseline described and carried all 150 across as deletions — into the profile
the chats had been borrowed from, which is where the only copy lived. One
sign-in, both sidebars, no warning.

So a side holding none of the names the baseline describes, while the other
still holds them, is a folder that was stashed, replaced or emptied wholesale,
and the baseline is forgotten rather than acted on: the survivors then look new
and the empty side is filled again, which is the opposite mistake and the
harmless one. A chat put back is a nuisance; a chat taken away for good is not.
`forgetStalePairs` is the other half, dropping the pair that names where a
profile's chats used to live before any launcher can square it up. It takes the
set of folders still being mirrored rather than one of them, because two
profiles on one account mirror every organization the account has and no single
one is the pair worth keeping — which is why that branch went without it.

**The baseline says what the filesystem did, never what a pass meant to do.**
Copying has always been written down only when the write landed; removing was
written down either way, so a removal the filesystem refused left a name on one
side with no baseline behind it — which is a new chat, and the next pass copied
the deleted conversation straight back to the side it had been deleted from. A
name gone from both sides is dropped too: the loop only visits what one of them
holds, so such an entry sat there for good, growing the file and standing ready
to read that name coming back on one side alone as a deletion to carry across.

**Nothing goes to the stash before the source has been read.** The
same-account branch opened the destination first and consulted the source
second, so a source that turned out to hold nothing under this account — spelled
shorter, holding only `skills-plugin`, signed in as somebody else — put the
borrowing profile's whole history away and left an empty folder standing in its
place. No pair was written down either, so `firstPass` was true again and every
launch after did it again. What the source has is worked out first, and a source
with nothing to share leaves the destination exactly as it was.

**And the same emptiness must not be read as a deletion by the sweep.** The
stash leaves a real, readable, empty folder behind, so `contents.stores` holds
it, every record remembered there is missing, and two passes make that
permanent — 150 sessions across two profiles withdrawn in one afternoon, which
is a thing no later pass undoes. `mayFileRecords` had asked whether a stash sat
beside a folder on the way *in* since the day it was written; the withdrawal
pass never asked it on the way out. Both directions ask it now. Withdrawing is
forever, and forever deserves the cheaper mistake.

**The pairs are the keys of the state file, and every way out drops them.** A
shortcut being opened squares up its own pair, which covers the profile doing
the borrowing and nobody else — so archive a chat in the borrowing profile,
open the source, and the source is not the one running `apply`. `mirrorKnownPairs`
reads the pairs back out of the file instead, which is also all a launcher
needs to know. That file is read by every launcher, though, so a pair left in
it outlives the graft that made it: deleting the profile and deleting the
shortcut both call `forgetMirrors`, and undoing a graft drops the pairs it
settled, or two folders go on being squared up for a graft that no longer
exists.

**A pair has two halves and only one of them is undoing anything.** Every
question a graft asks of that file — is this a first pass, what should stop
being mirrored, which copies come back out — is about the borrowing half, and
asking `mirrorPairs(under:)` answers for both. `apply` calls `ungraft` on any
profile with no source of its own, which is every launch of the profile others
borrow from, and undoing a graft took every pair with a half underneath it. The
lender read its own folder as the borrowed one; a lender keeps no stash, so the
clause that holds back what a profile brought had nothing to name; and a merge
leaves both sides holding the same bytes, so the test for which copies to take
out matched all of them. Opening the source emptied the shared history out of
the sidebar it was about to build, forgot the pairs on the way past, and left
the borrower's next launch reading as a first pass — which stashes. Both
sidebars, on every launch, for as long as the shortcut existed, and the only
thing on screen to notice was a sidebar with one chat in it.

`mirrorPairs(borrowedBy:)` is the rule, and the roles come from the order the
key was written in: `mirrorChatFolders` is only ever called with the profile's
own folder ahead of the one it is borrowing from, and after a merge nothing on
disk tells a lender from a borrower. `forgetStalePairs` asks the same way, and
takes the pairs this pass is mirroring rather than a list of folders to find a
half in, since a source that moves leaves a pair as dead as a destination that
moves and only the destination half was ever checked.

**A first pass that finds a stash already there is a second one.** `stash` folds
a stash it finds back into the live folder before moving the lot aside — right
for a link the profile has written a real file over, wrong for a mirror, where
the live folder holds the merge and the stash is the only thing that says which
of those records the profile brought. Fold them together and that is gone for
good: this machine's stash went 155, 158, 159 while the count was the only
symptom. `dropBorrowedCopies` takes out what the source is holding byte for byte
that the stash does not already name, so the stash goes on naming exactly what
was brought and the borrowed copies come back on the pass that follows.

It runs only on a folder that resolves inside the profile. A shortcut left on
the shape an older version made has its own chats in the stash and a link where
they used to be, so the folder's contents are the source's own files and every
one of them matches the source byte for byte because it *is* the source —
deleting the lender's whole history and calling it tidying up. The link is not
always the folder either: a released version sharing one account linked the
entire store, so the organization folder under it is a real directory reached
through a symlinked parent and `isSymlink` answers no. Both shapes are the rule
the rest of this app is built on, which is whether the folder resolves inside
the profile at all.

**Credentials are borrowed, never taken.** Only the access token is decrypted,
never the refresh token: Anthropic rotates those and using one signs Claude
Desktop out. Nothing is written back to Claude's config, cookies or keychain.
The token goes to `api.anthropic.com` and nowhere else.

**The keychain is asked once, and only when asking is the way through.** Every
read tries silently first, so a build already on the item's ACL never sees a
dialog no matter who asked. When that read comes back shut the app asks rather
than falling back without saying why — once for the life of the process, never
again after a decline, and not while it is coming up hidden into the menu bar,
since a dialog thrown over a login is answered by nobody. The rule is
`ClaudeCredentials.mayRaiseDialog`, kept pure so the suite can drive it;
`UsageMonitor.mayPromptUnasked` carries the login case.

`SecKeychainSetUserInteractionAllowed` is what actually makes a read silent. The
`LAContext` the compiler suggests in its place governs items backed by an access
control, not an ACL of trusted applications, and was measured sailing straight
past the suppression and putting a dialog on screen from a background poll —
which is what the old "background polls never prompt" rule believed it had.

**Only a person starts a session.** `SessionStarter.start` runs because a
button was pressed and for no other reason: it defaults to non-interactive, so
a call from anywhere unaudited fails quietly instead of raising a keychain
prompt, and it claims the profile for the length of the call so the window and
the dropdown cannot both start the same account. The suite reads
`Sources/App/*.swift` and fails if a fourth caller appears or if `startSession`
turns up on a line with `onAppear`, `onReceive` or a timer — a view refreshes
every thirty seconds, and a session wired into one would open a five-hour
window on every account, over and over.

**⌘Q does not quit.** `applicationShouldTerminate` cancels an unasked-for
terminate and closes the window instead, because the menu bar item is the part
that does the reporting and closing a window is not asking for it to stop.
`AppDelegate.quit()`, wired only to Quit in the dropdown, is what really ends
it, along with a logout — refusing that stalls the shutdown on a dialog — and
along with an update. Sparkle starts the new version by asking this one to
terminate; refusing that left 1.0.0 running with 1.0.1 already staged, put the
window away on the way past, and wrote `mainWindowOpen` false, so every launch
after came up with no window. One refused terminate, three symptoms, none of
which named the cause. `Updater.isRelaunchingForUpdate` is the clause that
fixes it.

Window bookkeeping stops once a terminate is allowed. `AppDelegate.isTerminating`
guards it, because windows closing on the way out are the app shutting down, not
someone putting a window away, and recording it as the latter brings the next
launch — an update's own relaunch above all — up hidden. The
rule lives in `QuitPolicy` so the suite can drive it, and its last clause is
the way out: with nothing reachable in the bar, `MenuBarController.isShowing`
reads false and a quit is a quit. Ask that, not the setting; the setting says
what was wanted, not whether the bar had room.

**A status item that exists is not a status item you can click.** macOS drops
items it has no room for — a full bar, and a notch taking the middle — without
telling the app, and the dropped item is still an object that answers
`isVisible` true. `isShowing` used to be `item != nil`, which said yes to a bar
with nowhere to put it, so the clause above would have cancelled a quit and left
Force Quit as the only way out. What actually gives it away is placement:
filling the bar with throwaway items put the overflow at x of -71, -115, -160
and -204. `MenuBarPlacement.isReachable` is the rule, horizontal only because
the bar sits above `NSScreen.frame` and a full containment test rejects every
item there is. Seen for real: Graft vanished from the bar with the process alive
and polling, and came back when another app's item was removed.

**The version is written down once.** `VERSION` at the repo root, stamped into
the bundle by `build.sh`. `Resources/Info.plist` carries `0.0.0` and nothing
else, so a bundle still holding that number was never stamped; the suite fails
if the real number reappears there. `release.sh` refuses to build a version
whose tag already exists — two binaries answering to one number is a thing no
update feed can tell apart, and it is only noticed after the upload.

The same is true of a version built twice without a bump, which is a thing only
a development loop does. `Installer.refreshLauncher` replaces a shortcut's
launcher when the version stamped in its bundle differs from Graft's own, so
rebuilding at the same number leaves every shortcut running the previous binary
while claiming to be current. A change to launcher code needs the bump or it
reaches nobody, and the symptom is a fix that works in the app and not from the
Dock.

**The version reaches a second repository, and that one has no build to fail.**
`VERSION` drives the bundle and the appcast, both of them here and both checked
before the upload. The Homebrew cask is in `homebrew-claude-graft` and was
edited by hand, so it sat at 1.0.6 while the feed served 1.0.10 — four releases
where `brew install` fetched a build four versions old and then had Sparkle
replace it within the hour, which is the long way round to the current version
and reads from outside like the project stopped shipping. `release.sh` now
clones the tap into `dist/`, rewrites the cask against the checksum of the disk
image that run actually built, commits it, and names the push beside the other
two. Nothing here pushes, as before; what changed is that no number is carried
across by hand and the printed instructions are no longer half a publish.

**A release carries both architectures.** `build.sh` compiles one slice per
architecture and `lipo`s them; a plain `./build.sh` stays single-slice because a
development build only has to run here. `release.sh` sets `GRAFT_UNIVERSAL=1`
and then asks the bundle what it actually got, because an Intel Mac handed an
arm64-only app reports nothing more useful than a bounce in the Dock.

**The build directory is called `build.noindex` for the suffix.** Spotlight
skips any directory whose name ends in it, and a development build needs that:
what comes out is a complete, launchable `Claude Graft.app`, so an ordinary
`build/` puts a second Graft in Spotlight beside the installed one with nothing
to tell them apart. Opening the wrong one gives two instances polling the same
profiles, racing for the same keychain prompt, and a Sparkle that updates a
bundle nobody installed. Seen for real, which is why the directory was renamed.

**Every route that opens a profile asks first.** Two Claudes on one chat store
both write the same files, so the same conversation open in two of them can lose
messages — the one way this app can cost you something. The check lived inside
`ShortcutDetail.open`, so it covered exactly one of the three buttons that open
a profile: the dropdown's Open went straight to `openApplication`, and Claude's
own profile had no shortcut to ask on its behalf, so nothing ever looked. That
last one is the case it matters for, since every shortcut grafted from main
reads precisely the files main is about to open. `ChatConflict` is the rule and
`chatStoreNeighbours(of:)` takes an optional now, nil meaning main. The suite
reads all three views and fails if one of them stops naming `ChatConflict`.

The dropdown asks with an `NSAlert`, not a `confirmationDialog`. It is an
`NSPopover`, which gives up key window the moment anything else appears and
takes any sheet it was hosting down with it.

**Opening a profile that is already open shows the Claude that is there.**
Claude Desktop never calls `requestSingleInstanceLock`, so nothing on its side
refuses a second process on one `--user-data-dir`: `open -n` twice was measured
giving two live Claudes on one profile, both reading and writing the same chat
store. That is the loss `ChatConflict` exists to warn about, reached by pressing
Open twice on the same shortcut — and it is worse than the case that rule covers,
since it is not two accounts sharing chats but one profile's own files written
over twice. `Graft.open` and `Graft.run` ask `isRunning` before anything else
and hand the pid to `reveal`. The suite fails if anything in `Sources/App` calls
`Graft.launch` directly.

**A shortcut behaves like the Graft that built it.** The bundle carries its own
copy of the launcher, nothing re-saves a shortcut on its own, and the routes
this rule matters most for never touch the app at all — the Dock, Finder and
Spotlight run the binary in the bundle and ask Graft nothing. So a change to how
opening works would have reached nobody who already had shortcuts, which is
everybody. `Installer.refreshLaunchers` replaces every stale one at launch,
recognising them by the version stamped in the bundle. Only the binary and that
stamp are rewritten: regenerating the whole plist would write the shortcut's
current name into a bundle that may still be sitting under its old one, waiting
for the save that renames it.

The conflict question goes quiet on that path, and `sharersToAskAbout` is the
clause. A profile already open is not about to be opened; the Claude being
brought forward has been reading those chats all along, so the warning would
describe a situation rather than one the press is about to create — and it
would arrive attached to a button that creates nothing.

**What the bundle says is what happens, so the list is not the record.** A
shortcut is written down twice: in `shortcuts.json`, which the window edits,
and in the bundle's own `graft.json`, which is what actually runs. The Dock,
Finder and Spotlight start the binary in the bundle and ask Graft nothing, so
where the two disagree the bundle wins and the window is quietly describing a
shortcut that does not exist.

They disagreed on this machine. The list had a profile back on its own chats
while the bundle still named a source, so opening it from the Dock grafted it
again, and the first mirror pass put all 152 of that profile's own chats into
the stash where a first pass puts them — a sidebar holding one record, a stash
holding the rest, and nothing anywhere saying why. `Installer.refreshConfig` is
the repair, run over every shortcut at launch beside `refreshLaunchers`.

It is not gated on the version the way the launcher is, because a bundle can
fall out of step with the list at any version and it was the current one that
did. Only the two paths are rewritten: the name, the icon and the version stamp
are somebody else's business, and rewriting the plist here would stamp a
renamed shortcut's new name into a bundle still sitting under its old one.
Writing into a signed bundle breaks the signature, so it is signed again on the
way out.

`reveal` sends two things because neither does the other's job. Claude answers
`window-all-closed` with an empty handler, so closing the last window leaves the
process alive with nothing on screen; its `activate` handler builds the main
window back when it finds none, and the reopen Apple event — `aevt`/`rapp`,
aimed at a pid — is what fires that handler. Bringing the app forward is the
other half: a reopen on its own was measured leaving the app exactly where it
was in the stacking order, three times out of three, and
`NSRunningApplication.activate` is what moves it. The reopen needs no automation
consent and no `com.apple.security.automation.apple-events` entitlement: it was
measured going through unchanged from an ad-hoc bundle, from one signed
`--options runtime`, and from one signed with the entitlement. The scripting
activate event (`misc`/`actv`) is delivered too and does nothing, Electron
having no handler for it.

Measuring any of this needs care, and three wrong conclusions came out of not
taking it. `NSWorkspace.frontmostApplication` is kept current by notifications
on the run loop, so a poll loop that sleeps rather than pumping one reads its
own startup value for as long as it runs and reports that nothing ever changed.
That is what made a working launcher look like it needed an `NSApplication` of
its own, and then like it needed to stay alive after asking. It needs neither;
it does what it always did and exits.

**The pid comes from `ps`, never from the pgrep that answers `isRunning`.**
Every helper process Claude starts repeats the profile's `--user-data-dir`, so
the first pid pgrep offers is a renderer, and only the browser process answers
to being shown. `isClaudeProcess` is what tells them apart. pgrep also leaves
out its own ancestors, which Graft never notices — it runs as its own app — and
anything run from inside a Claude notices immediately: that Claude is the one
process pgrep will not report.

**A bundle is not an instance.** `NSWorkspace.openApplication` with the default
configuration reopens whichever instance of the bundle started first, measured
with three copies of one app running and the oldest picked every time. So Open
on Claude's own profile brought a grafted profile forward instead, since with
any shortcut running every instance of `Claude.app` is somebody else's profile.
`launch` passes `-n` for that reason, and passes no `--user-data-dir` at all for
the main profile, whose only mark is that absence.

**An update installs itself and says nothing.** Checked hourly and at launch
once that much has passed, downloaded, installed and restarted with nobody
asked. The gentle-reminder route was tried first and was wrong for this app:
there is usually no window to put a question in front of, so an update that
waits to be noticed is one that never gets installed. What it costs is the
status item blinking out and back.

That goes for a press as much as for the schedule. `checkForUpdatesInBackground`
is what the dropdown calls, never `checkForUpdates` — Sparkle's interactive
check puts up its own panel and waits on Install and Relaunch, so the scheduled
check installed silently while the button asked, which is one app behaving two
ways. Pressing a line that already reads "Version 1.0.6 is available" is not a
request to be asked whether you want it. The suite fails if the interactive call
reappears.

That only works because `applicationShouldTerminate` lets it through, and
Sparkle's own header warns that `updaterWillRelaunchApplication` may not be
called. So `isRelaunchingForUpdate` is set at every hook before an install —
`willInstallUpdate`, `willInstallUpdateOnQuit` and the relaunch — and the suite
counts them, because one missed hook is an update that installs and never
starts, which happened twice before it was understood. `willInstallUpdateOnQuit`
also calls the immediate handler: Sparkle would otherwise wait for a quit that a
menu bar app may not see for weeks.

**One update asks, and the asking lives in the feed rather than the code.**
Every release installs itself without a word, which is the rule above and still
the rule. 1.1.0 is the exception: it merges two chat histories into one and that
cannot be undone, so it is not a thing to do to somebody who was not consulted.
`sparkle:minimumAutoupdateVersion` is what says so — Sparkle prompts anybody
below that version and installs silently for anybody at or above it, and its own
header is plain about the first half: "if the requirement is not met, the user is
always prompted to install the update."

Which makes it a floor and not a mark on one release. Set it to 1.1.0 on every
entry from 1.1.0 onwards and the behaviour is exactly "ask once, quiet
afterwards": a machine already on 1.1.0 meets the requirement and updates
silently for ever, while one still on 1.0.x is asked until it crosses. Leave it
off a later 1.1.x and that release installs itself on a 1.0.x machine and skips
the question 1.1.0 exists to ask — same person, same merge, no dialog.
`release.sh` counts the entries carrying it the way it counts the items, one
more each run, which catches both the new entry missing it and
`generate_appcast` dropping it from an old one on a later pass. It is written in
after generation because there is no option for it, which is safe only because
nothing signs the appcast itself: the signatures in it are over the archives.

No 1.0.x release was needed to arm any of this. The floor is read by the Sparkle
already inside every build that has ever shipped, so a 1.0.0 machine that has
never been opened since honours it too. What a 1.0.x release could have bought is
the dialog arriving in front rather than behind — for a menu bar app Sparkle
shows a scheduled update alert immediately but, in its own words, "behind other
running applications" — and that is a delay, not a silent merge, which is the
half that matters.

The release the floor names has to bring a note, and `release.sh` refuses to
build it otherwise: it is the one release that puts a dialog in front of
somebody, and a dialog with nothing in it is worse than no dialog. Notes live in
`docs/release-notes/<version>.html` and are copied beside the archive, which is
where `generate_appcast` looks for them; carrying no DOCTYPE or body tags is what
makes them embed rather than become a link to somewhere that has to stay up.

**The feed keeps every release it has ever carried.** `generate_appcast` prunes
to a handful of entries per branch point unless told otherwise, so each release
silently dropped the oldest — 1.0.0 and 1.0.1 had already gone before anyone
looked. Nothing breaks when they go, since a client is offered the newest entry
regardless, but the signature in an entry is over an archive that is never built
again, so a dropped entry cannot be regenerated, only recovered out of git.
`release.sh` passes `--maximum-versions 0` and checks the count went up by
exactly one; the old check was "no fewer than before", which adding one and
losing one satisfies perfectly.

**The feed URL is unchangeable once shipped.** A copy out in the world polls the
URL it was born with, and GitHub gives Pages no redirect if the account is ever
renamed — repositories redirect, Pages does not. `Resources/Info.plist` and the
`--download-url-prefix` in `release.sh` both name the account, and the suite
checks they agree, because a rename that touched one would leave the feed
advertising downloads nobody can fetch.

**One account, one figure.** The window read `plan-usage-history.json` for
itself while the menu bar showed what the endpoint had just answered, so one
account was reported from two sources that do not agree. That file is only
written while that Claude runs, so a profile closed for days had the window
showing figures from before a weekly reset — 92% of a week the bar correctly
had at 30%, side by side on one screen. Both detail views take
`UsageMonitor.entry(for:)` now, which is live where live is available and falls
back to the same file underneath; the suite fails if `Graft.usage(` reappears
in either of them. Their Start Session buttons `invalidate` too — that rule was
written for the dropdown alone, and the moment a window showed the monitor's
figure it needed it for the same reason.

**Profile folders are validated.** One plain component inside Application
Support, never `Claude`, never a path. Without this, typing `Claude` into the
folder field pointed a shortcut at Claude's own profile.

## SwiftUI rules this app learned the hard way

**No `MenuBarExtra`.** Alongside a `WindowGroup` it loops on macOS 26 — every
scene update rebuilds the main menu, which invalidates the scene. 100% CPU and
about a gigabyte a minute, but only while a window is open. Bisected: not the
label, not the content, not the style. The status item is an AppKit
`NSStatusItem` in `MenuBarController`.

**Nothing blocking in a view body.** No filesystem, no process spawn, no
network. `Graft.isRunning` once used `waitUntilExit()` inside a body; that spins
the run loop, re-enters AppKit layout mid-update, and segfaults with a null
program counter. Every process wait now uses a semaphore, and status is read on
a background queue into `@State`.

**Long-lived objects live in `enum Shared`, not `@StateObject` on the App.**
Observing them from the App value invalidates the whole scene on every poll.

## Facts about Claude Desktop that the code depends on

Chat history is `<store>/<accountUuid>/<orgUuid>/`, in both
`claude-code-sessions` and `local-agent-mode-sessions`. An instance reads only
the account it is signed into, so linking a whole store does nothing across two
accounts — the link is made one level deeper, mapping this profile's
`<account>/<org>` onto the source's active one.

**Except that a store does not always spell those two in full.** A profile in
local mode was measured naming both halves by their first eight characters —
`local-agent-mode-sessions/ed417e0f/00000000` against
`claude-code-sessions/ed417e0f-5edd-…/00000000-0000-…`, the same account and the
same organization in the same profile, one store each way. Such a profile
carries `<org>.profile-origin.json` saying `{"mode":"local"}`, which is the only
thing that marks it out, and asking a store like that for the full uuid finds
nothing at all. `counterpartDirectory` is the rule: an exact name, or the one
directory there whose name is a prefix of what was asked for — at least eight
characters of it, and only where there is exactly one, since nothing may put one
account's chats into another's folder. Anything ambiguous comes back nil and the
caller falls back to the name it was given.

**`<org>.profile-origin.json` is a plain file sitting beside the organization
folders,** written as the organization is created, which makes it for a moment
the newest thing in the account directory. `newestChild` returned it as an
organization, and the graft that followed stashed the profile's own copy of the
file and built a directory where it had been. Every walk of an account
directory asks `isDirectory`.

**`skills-plugin` sits beside the account directories without being one.** It
is keyed by organization, so a walk that took it for an account reported its
organization folders as chat stores. `nonAccountStoreItems` names it, and both
the sweep's walk and the state report skip it. Grafting links it, on either
path — it used to be linked only across accounts, having been written after the
same-account branch's early exit — and the link is made after the store is
opened, since a same-account graft puts the whole store away and builds it back
and would take the link to the stash with everything else.

**A profile will not write a record into a folder that resolves outside
itself, and the graft link is exactly such a folder.** This is the rule the
whole session sweep was built to work around, and it was misread for months as
being about accounts. A grafted profile shows a conversation in its window and
writes nothing at all: on close the session is gone from both profiles, leaving
a transcript nobody filed a record for. It reads exactly like the other profile
deleting it, which is how it was first reported.

The account has nothing to do with it. Measured directly: a profile signed into
one account, given a real organization folder of its own, created a session,
wrote its record, and archived it — and that session was stamped
`ownerAccountUuid` of the *other* account on the `bridge-session` line of
`~/.claude/projects/<encoded cwd>/<cliSessionId>.jsonl`. Same profile, same
account, same machine login, with the folder linked instead of real: nothing
written, ever. The one variable that moves is whether the directory the record
would land in resolves inside the profile.

Every earlier reading follows from that. Rewriting the owner stamp to name the
grafted profile's account changed nothing, because the stamp is not consulted —
it is the command line's note of who it was, not a permission. Sessions a
profile owned before it was grafted have records; sessions it started after do
not. A profile outside every shortcut, with a real store, writes its records
without trouble. And the reason the source profile always worked is that its
own folder is the real one.

Measured five ways before the behaviour was believed, and every one of those
five had a link in place, which is how the cause stayed hidden behind the
account for so long. Linking the owner's account path into the grafted profile
so the record had somewhere to land changed nothing — it was another link;
neither did closing the other Claude for the whole test, nor leaving the app
open two minutes rather than thirty seconds. The record is never written. The
absent directory was the rule showing through, not the cause, and the link that
looked like the fix was reverted rather than shipped.

**Ownership is written in one place, and the command line is what puts it
there.** A record carries no account field at all — its account is the
directory it sits in — so the only thing on disk saying who a conversation
belongs to is the `ownerAccountUuid` on its `bridge-session` line. Nor is that
line written once at the start: one session here had twelve of them, the last
at the very end of the file, each with a fresh `bridgeSessionId` and every one
naming the same owner.

Which owner is not the profile's, however much it looks like it should be.
Claude Desktop keeps a copy of Claude Code inside every profile —
`<profile>/claude-code/<version>/claude.app`, which is what it spawns for a
bridged session — and hands it that profile's own token in
`CLAUDE_CODE_OAUTH_TOKEN`, alongside `CLAUDE_CODE_ENTRYPOINT=claude-desktop`.
Read that and you would expect each profile to own what it starts. It does not:
a session started in a grafted profile, with no other profile running, came
back stamped with the source account. Identity comes from the machine login —
`oauthAccount` in `~/.claude.json`, behind one keychain item under
`Claude Code-credentials` — and every copy of Claude Code shares it, the ones
inside profiles and the standalone one on `PATH` alike. The token pays for the
inference; it does not say whose the conversation is.

So which account a chat is stamped with is settled by whoever `claude` was
logged in as when it started, and by nothing about the window it was typed
into. What the stamp does not do is decide who may write the record: one
session was rewritten to name the grafted profile's account, with an untouched
session beside it as a control, and the grafted profile still would not archive
it — while the same profile, given a real folder, archived a session stamped
with the other account without complaint. The stamp is the command line's note
of who it was. Nothing reads it back.

**The same rule governs every write to a record, not only the first one.** Archiving
a borrowed conversation from the grafted side takes it out of that window's
sidebar and puts nothing on disk, so the record still reads
`isArchived: false` and the chat is back the next time the profile starts. It
was reported as archived conversations unarchiving themselves, which is what it
looks like from the sidebar and is a different thing entirely from the sweep
refiling them — the two were only told apart by watching the store while it
happened. Every write to both profiles' stores was logged across the window: two
conversations archived from the owner's profile were on disk within a second,
`isArchived: true`; two archived from the grafted profile produced no write
anywhere at all, their records untouched since the night before. The grafted
profile was then given its organization folder back as a real directory,
started a chat, and archived it, and that one stuck — the record written by the
profile itself, in its own shape, for a session stamped with the other
account's uuid. One variable moved between the two halves of that test, and it
was not the account.

Nothing here can repair it. What a sweep files is a record that was never
written; an archive that was never written leaves nothing behind to find, so
there is no counterpart to `fileMissingSessionRecords` to be had. Chat state on
the grafted side — archiving above all — belongs to whichever profile holds the
account, and that is worth saying in the README rather than working around.

What is Graft's to repair is the record, and the repair is to write it.
`Graft.fileMissingSessionRecords` sweeps the transcripts for bridge lines,
asks which profile the owner's account lives on now, and writes
`local_<cliSessionId>.json` into that profile's `<ownerAccount>/<ownerOrg>` —
where the session's own window and every profile grafted onto it lists it,
a linked organization included. Every field comes out of the transcript, so
nothing is invented: the id from the filename, the owner and organization
from the bridge line, the title from the title line, the times from the first
and last stamps. The bridge id is renamed on the way — `cse_…` in a
transcript is `session_…` in a record, measured against a session that had
both written down — and the name is fixed by the session, so a later pass
can only ever find a record where an earlier one left it, and a delete in a
sidebar writes a marker naming it the same way.

Four things hold the sweep in. A transcript written moments ago waits out a
quiet window first, a minute of it — but only while a Claude signed into the
owner's account is actually running, because that is the only thing the wait
is for. Such a window writes the record itself within moments of a session
opening, measured at about a second, so a minute is far more than that wait
needs; with no such Claude up there is nobody to wait for, and waiting anyway
made closing a chat and reaching straight for the other profile the one move
guaranteed to miss it. A sidebar is built once, at launch, and that launch is
the very press being waited out. A record already on disk is never written
over by a pass either way. What a pass files it keeps current: the
sweep remembers the facts behind every record it wrote, in
`ClaudeGraft/session-records.json`, and while the record on disk still holds
exactly those bytes it is known to be one of ours to refresh as the
transcript moves — a title landing late, a turn finishing. The moment the
bytes stop matching, Claude has rewritten the record in its own shape, and
the sweep retires its version for good, because a parsed transcript flattened
over the host's record would lose everything the transcript cannot recover,
its MCP server config for one. A session still being written grows past the
quiet window and files later. And a transcript with no prompt in it is never
filed at all — an open-and-close with nothing said never had a conversation
to lose.

**A record you cannot see is not a record somebody deleted.** A session taken
out of a sidebar stays out, and there are two ways of knowing which one went.
Graft names its records for their session, so the marker Claude leaves behind
carries the session's own id — a deletion read rather than inferred, needing
no memory of earlier passes at all. For a record Claude named itself, by an id
no transcript carries, the sweep goes on its own memory of which store each
record sat in: one gone from a store the pass actually read is one somebody
deleted — and two things have to hold before a pass will say so.

The store has to have been read. Every directory in the walk is taken with a
`try?`, so a profile that has been deleted, or stashed behind a graft, or is
waiting on a permission, comes back holding nothing, and reading that as a
sidebar cleared by hand withdrew every session it held, for good, which is a
thing no later pass undoes. Graft stashes organization folders itself, so its
own graft was the surest way to trigger it. `StoreContents.stores` is the set
of directories a walk really got into; a record in a store missing from it
stays remembered exactly where it was.

And the record has to be missing twice over. Claude re-files a session under a
new record name as it runs — a record named for one session was found on this
machine holding a session filed under another name an hour earlier — so there
is a moment when the old name has gone and the new one has not landed, and a
pass falling into it reads a deletion that never happened. `vanished` carries
the sessions one pass found missing so the next can agree, and nothing missing
is filed while that question is open: filing it would put the record back
under this app's own name, the pass after would find it there, and a chat
taken out of a sidebar would be quietly restored by the very thing meant to
notice it going.

The one guess left is a marker written before the first pass ever ran, which
names a record no longer there and can only be read by timing — the session
that had just gone quiet in the minute before the press.

**And a record you cannot see is not a record nobody filed.** That reading was
applied on the way out and not on the way in, so a store the walk could not get
into withdrew nothing — the rule above — and was then filed into as though it
were empty, which is the same mistake pointing the other way and the more
expensive of the two. Everything in an unreadable store looks like a session
that closed without a record, and a record written fresh says
`isArchived: false`, so a conversation archived by hand comes back into the
sidebar. Graft stashes organization folders itself, so its own graft was again
the surest way to trigger it. Reproduced with one folder stashed and a single
pass run over it: the whole history refiled, the organization folder rebuilt
over the top, and the stash left with nowhere to go — a second copy of every
chat, none of them archived, and the profile's real store orphaned beside it. `outOfSight` is the clause for
a session Graft has a record remembered for, `mayFileRecords` for the store
itself, and its two halves are the whole distinction: a folder that is there
must be one this pass actually read, and a folder that is not there must not
have a stash sitting beside it. A folder that is simply absent is still filed
into, since that is how a profile gets its first record.

**A stash is not always a sibling, and asking only for the sibling caught half
of them.** There are two shapes and `stashedCounterpart` has known both since
the day merging was written: a cross-account graft puts the organization folder
away and leaves `.<org>.graft-own` beside it, while two profiles on one account
put the whole store away and the same records end up one level further down.
`mayFileRecords` only ever asked about the sibling — and after a same-account
graft there is no folder left for a sibling to stand next to, so the absence
read as a profile that had never filed anything and the sweep rebuilt
`<account>/<org>` inside the emptied store and refiled the lot, unarchived,
with the real store orphaned in the stash beside it. Which is the same incident
as the paragraph above, reached by the half of the case its fix did not cover.
`isStashedAway` asks about every folder up to the store, and everything that
reasons about an absent folder goes through it.

**A record remembered where nothing exists any more is a pair to drop, not a
fact to act on.** `state.records` was only ever added to, so a folder that
stopped existing — the profile deleted, or a sign-in moving `<account>/<org>` —
went on being remembered for ever, and a session remembered there is out of
sight on every pass from then on. Out of sight blocks filing, so the chat was
in nobody's sidebar with its transcript whole on disk, and no finding named it
either, since a report cannot describe a folder that is not there. Forgetting
is what `forgetStalePairs` does for a mirror and for the same reason: the
session then looks new and lands where the account lives now. Told apart from
the unreadable case by `exists`, and only after `isStashedAway` has said this
app did not put it away itself.

**The timing guess is for the markers that cannot be read any other way.** A
marker naming a record Claude wrote carries an id no transcript has, so the
only trace of which session went is which one had just gone quiet. That guess
used to see every marker on the machine, including the ones named for a session
outright — and markers accumulate and are never pruned, so one deletion went on
suppressing whatever else had happened to go quiet in the minute before it, for
as long as the marker existed. Only markers naming no transcript reach it now.

**A conversation carried on is not a conversation lost.** Claude gives a
conversation a new command line session as it goes — a compaction, a resume —
and the record names the ones it grew out of in `priorCliSessionIds`. Those
earlier transcripts stay on disk, whole, with no record naming them, which is
precisely what a session that closed without one looks like. Filing them put
the same conversation in the sidebar twice, same title and same folder, the
second copy carrying none of what had been done to the first — so archiving the
conversation left its earlier half sitting there in plain sight. Five such
records were on this machine, against three conversations. `StoreContents.superseded`
is what a record says it has taken over, and nothing in it is ever filed.

**A Claude this app did not make is read, never written to.** Every profile in
Application Support holding a chat store is worth reading, because the question
a sweep asks is whether any Claude anywhere is already listing the session, and
a record in a profile outside every shortcut still means one is. Writing is a
different question with a different answer, and the two were one list: the
owner's account matching was enough to have records filed into a profile Graft
had never heard of, in somebody else's sidebar, under names of Graft's
choosing. `recordFilingProfiles` is the filing list — what the caller named,
Claude's own profile, and the folders in `ClaudeGraft/shortcuts.json`, which is
the only thing on the machine that says which profiles are this app's doing. A
launcher reads that list too; it knows its own profile and its source and
nothing else, and the widening it needs is to Graft's other profiles, not to
everybody's.

Claude launched the ordinary way carries **no `--user-data-dir` at all**, so the
main profile is recognised by the absence of one. Profile paths are prefixes of
each other, so every `pgrep` on `user-data-dir=` must be anchored with
`([[:space:]]|$)` or one instance makes every shorter-named profile look like it
is running.

`config.json` holds `oauth:tokenCacheV2` in Chromium safe storage: keychain item
service `Claude Safe Storage`, account `Claude Key`, PBKDF2-HMAC-SHA1 with salt
`saltysalt` over 1003 iterations, AES-128-CBC, IV of sixteen spaces, `v10`
prefix.

That item's decrypt ACL names each trusted build by code hash, so an ad-hoc
signature earns a fresh entry every time the app is rebuilt — nine entries for
the same `build.noindex/Claude Graft.app` path were counted on the development machine,
alongside ten `cdhash:` entries in the item's partition list. A Developer ID
would be one `teamid:` entry that survives every version. Both a suppressed read
and a declined dialog answer `errSecAuthFailed`, so the two are told apart by
which call was made, never by the status.

Sparkle ships its framework already ad-hoc signed, and its validator accepts an
EdDSA signature alone — the header comment on `validateUpdateForHost` says it
"allows change of Code Signing identity", which is exactly what an ad-hoc build
does on every compile. Elsewhere it says outright that "if no Apple Code Signing
certificate is available, adhoc signing can be used at minimum". `SUFileManager`
strips `com.apple.quarantine` from what it installs, so only the first install
meets Gatekeeper. The XPC services are removed from the embedded copy: they need
entitlements this app does not carry, and launchd refuses them for a
non-sandboxed app. The signing key lives in the login keychain under
`https://sparkle-project.org`; `vendor/bin/generate_keys -x` exports it if CI
ever needs it.

Live usage is `GET https://api.anthropic.com/api/oauth/usage` with a bearer
token and `anthropic-beta: oauth-2025-04-20`. It answers with `five_hour` and
`seven_day` objects carrying `utilization` and `resets_at`, plus
`subscription_type`, a `limits` array of `weekly_scoped` per-model windows, and
`extra_usage` credits. The last two are parsed but not shown anywhere yet.

`plan-usage-history.json` is the fallback: samples of `{t, org, u:{fh, sd}}`
where `fh` is the five-hour percentage used and `sd` the seven-day one, written
only while that instance runs. It is ~180KB and grows, so it is parsed once per
change and cached against modification time and size.

Claude Code's command line keeps **one** login for the whole machine in the
keychain, so `CLAUDE_CONFIG_DIR` does not isolate accounts. That is why Start
Session goes through each profile's own borrowed token rather than the CLI.

## Polling budget

The thirty-second timer reads local files and checks what is running. The API
has three rungs, and `mayUseCache` is the rule: five minutes for a tick nobody
asked for, a minute for somebody looking — that is opening the dropdown — and
two seconds for somebody who pressed something. The last rung was missing, and
its absence is why Refresh Usage felt broken: opening the dropdown starts a pass
of its own, so the figure was always a few seconds old, always inside the
minute, and the button handed it straight back without asking anyone anything.
Two seconds is only wide enough to swallow a double-click.

A press that lands while a pass is running is queued, not dropped. `arrival` is
the rule. `inFlight` used to refuse everything, and the pass it most often
refused was the one the dropdown had just started itself — so the obvious move,
open the dropdown and press Refresh because the number looks stale, was the one
guaranteed to do nothing.

Starting a session is the one thing here that changes the number itself, so it
calls `invalidate` for that profile before refreshing rather than trusting any
cache window. That is ordered against the refresh by the serial queue both go
through. Without it, a five-hour window would open and the bar would go on
showing the figure from before it.

Opening the dropdown asks for a current figure but does not skip the backoff,
because a failing endpoint would then be asked again on every open. Failures
back off 60/120/300/900/1800 seconds and a success clears the count; a
`Retry-After` from the service wins and is the one wait a person pressing
refresh cannot skip.

The session-record sweep is on no timer at all. A record is read when a Claude
starts and at no other moment — it builds its sidebar as it comes up — so the
pass that matters is the one a shortcut runs on its way to opening one, in
`Graft.run`, after the links and before the launch. It runs on the way past a
profile that is already open too, which is the one case nothing else covers:
with no timer, that press is not going to start anything, and without a pass
on it, going looking for a chat that had not arrived yet was the surest way to
leave it not arriving. Graft itself runs one when it starts and one when the
dropdown opens, which is where the line about what was recovered is shown. The thirty-second timer this replaced filed records
that nobody would read for hours, and the one thing it was really buying —
being current when a profile opened — is bought better by the profile's own
launch.

That only works because a pass is cheap enough to stand in front of a window
opening. Two hundred megabytes of transcript is read in about a fifth of a
second: each line's bytes are walked once and every `"key":"value"` pair taken
off it as it goes, rather than the line being searched again for each of a
dozen fields, which took twelve seconds and made the sweep something that
could only ever happen in the background. Strings are walked the way JSON
writes them, escapes and all, since a value that ends early takes the rest of
the line with it. What has been parsed is remembered against each file's size
and modification time in `ClaudeGraft/transcript-cache.json`, so the pass a
launcher actually pays is forty milliseconds — in memory alone that cache
would be worth nothing to a launcher, which is a process that exits the moment
it has handed over.

One field is not read the way the rest are. An answer is written
`{"parentUuid":…,"message":{…,"type":"message",…},"type":"assistant"}`, so the
first `type` on the line belongs to the message nested inside it and the line's
own comes last — taking the first, which is right for every other field, quietly
put every recovered session back at the default model and effort.

## Diagnosing it afterwards

Every rule above turns on telling two indistinguishable situations apart: a
folder read and found empty against one that could not be read; a stash holding
a duplicate against a stash holding the only copy; a record missing because
somebody deleted it against one missing because it is out of sight. Get one
wrong and the symptom arrives hours later naming none of it — chats gone from a
sidebar, a history back with its archive flags cleared, two profiles that look
like they are syncing. Both of the worst incidents here were understood only
because somebody happened to be watching the store while it happened, which is
not a technique.

So each pass writes down what it saw. `Diagnostics.note` appends one JSON line
to `ClaudeGraft/diagnostics.log`, opened `O_APPEND` per write rather than held,
because the app and any number of launchers write to it at once and every one
of them is a short-lived process that may be killed mid-pass. The launchers are
the reason it exists rather than a print: a launcher is what runs the sweep in
front of a window opening, it runs when nobody is watching a terminal, and it
has exited by the time anyone thinks to ask. The events are the decisions that
were previously silent — a store the walk could not get into, a refusal to
file and which half of `mayFileRecords` refused, every withdrawal and its
grounds, a stash going out and coming back with its counts, a mirror pass that
did nothing because a side would not read.

`ClaudeGraft/state-report.txt` is the other half, rewritten in full each pass:
what is true now rather than what happened. It leads with the command line
login, because that is the fact behind the question this app gets asked most.
`stateFindings` then names what is wrong in the words the symptom arrives in —
a profile signed into one account while the command line is logged in as
another, a stash holding more than the folder beside it, records remembered
where none are. Every one of those has been reported as something else.

The report is built into a string so the suite can drive it, and the login and
the device file are read through overrides for the same reason.

## Verification state

Working and checked end to end: shortcut creation, adopting an existing profile,
cross-account chat mapping, live usage for both accounts, memory steady around
106MB at zero idle CPU. Quitting too, from outside: with the item in the bar a
quit is cancelled, the window closes and `mainWindowOpen` goes to 0 with the
process still alive; with `showInMenuBar` off the same quit ends it, so the app
cannot become unquittable. Quit in the dropdown takes the second of those two
paths.

The session-record sweep has run for real on this machine: forty-six sessions
that had closed without records — every test conversation plus months of
history back to the migration — were filed into Claude-3's store in one pass,
and Claude 3 launched on that store with the hand-written prototype record
among them and left it exactly where it was put. The recovered sessions were
then opened from Claude 3's sidebar by a person, and Claude 3 accepted them
well enough to rewrite their records in its own shape seconds later — which
is the takeover that retires a filed record, seen from both sides in one
night. A live test closed the loop end to end: a session started from
Claude 2 after the install filed itself on schedule, five minutes of quiet
after it closed, and was in Claude 3's store the next time it opened. The
first look missed it, inside that quiet window, which is what shrank the
window to a minute and taught the sweep to keep its records current instead
of freezing them at first write.

The rewrite that moved filing into the launcher was checked against this
machine's own history rather than against fixtures. The parser that walks
bytes was run beside the one that searched strings over all 184 transcripts
in `~/.claude/projects`, 199MB of them, and the facts came out identical for
every single one — 79 sessions parsed, 105 correctly declined for having no
bridge line — at 0.19 seconds against 11.85. A pass with nothing cached
anywhere costs 536ms; a fresh process with the cache already on disk, which
is what a launcher is, costs 42ms.

Unproven: **Start Session** has never successfully opened a window. It POSTs one
Haiku message with `max_tokens: 4` and Claude Code's system prompt, which these
tokens are minted for. If that turns out to be wrong the API's own error message
surfaces in the UI rather than being swallowed — read it rather than guessing.

`~/Library/Application Support/ClaudeGraft/usage-status.json` records what each
poll found, including whether the figures came from the API. Read it to check
the state of live usage instead of reading numbers off the screen.
