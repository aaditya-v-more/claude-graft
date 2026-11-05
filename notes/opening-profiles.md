# Opening a profile

Every route that opens a Claude, and the rules that keep two of them off one chat store. Read this before changing `Graft.open`, `Graft.run`, `ProfileOpener`, `ChatConflict`, the launcher or `Installer`.

## Apps and folders this app may touch

**Never touch an app Graft did not create.** Bundles are identified by the
`graft.json` they carry, never by name. `Claude` and `Claude Graft` are reserved
names. This rule exists because an earlier version matched on name and deleted
`/Applications/Claude.app`.

**Profile folders are validated.** One plain component inside Application
Support, never `Claude`, never a path. Without this, typing `Claude` into the
folder field pointed a shortcut at Claude's own profile.

## Opening, revealing and the shortcut bundle

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

**A missing shortcut is never permission to open another profile.** The menu
used to fall through to the default Claude when a renamed shortcut could not
be found, producing a sidebar pass without any `launcher.run` for the requested
profile. `ProfileOpener` checks the saved names and then `graft.json`'s profile
path in the application directories; an unreadable or missing shortcut reports
an error. Both shortcut Open buttons use it. LaunchServices is asked for a fresh
launcher instance, leaving the launcher to reveal the existing Claude on that
exact profile. `./test-opening.sh` exercises their launch path with the installed
Claude and disposable profiles that are not signed in.

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

## Recognising a running Claude

Claude launched the ordinary way carries **no `--user-data-dir` at all**, so the
main profile is recognised by the absence of one. Profile paths are prefixes of
each other, so every `pgrep` on `user-data-dir=` must be anchored with
`([[:space:]]|$)` or one instance makes every shorter-named profile look like it
is running.
