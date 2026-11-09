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

The reserved names are compared ignoring case, because a Mac's usual volume
does. `claude` passed the old check and was Claude's own profile under another
spelling; `claudegraft` passed it too and put a profile in Graft's own state
folder, and deleting that profile would have taken every pair, baseline and
record with it. `deleteProfile` refuses Graft's folder outright, by path and by
name, since a path spelt in another case need not resolve to the same string,
and `ShortcutStore.delete` counts two shortcuts as sharing a profile when their
folders match ignoring case or resolve to the same place, not when the text is
identical. The one place that still lets `Claude` through, the manual-update
library for the main profile, asks the same way, so a profile reached through a
lower-case path is not refused there.

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

**The repair takes the source and never the profile.** The folder field was
bound straight to the list, so every keystroke was saved to `shortcuts.json`,
and the next start — a login, or an update's own relaunch — wrote whatever was
there into the bundle with no check at all. Somebody who edited the folder of a
created shortcut and moved to another page without pressing Update found it
opening a new, signed-out profile with no chats, the old one still on disk and
nothing pointing at it. Names Update refuses got through the same way: an empty
folder is Application Support itself, `Claude` is the main profile under
another name, `../x` is a folder outside it. Two looks that seem the same from
inside `refreshConfig` are not: a list naming another source than the bundle is
the bundle out of date, which is what the repair is for; a list naming another
folder is a profile change nobody has applied. So `refreshConfig` finds a bundle
by the profile it already opens, or under its name only when its `graft.json`
will not parse, and it makes Update's own checks besides, for a list edited by
hand. A created shortcut's folder edit stays in the window until Update succeeds
and is lost if the page is left, which is what not pressing Update means. A
shortcut not created yet still writes straight through, since nothing runs it
and its name goes on suggesting a folder.

**One lookup finds a shortcut's app.** `installedBundle` used to look only for
`<name>.app`, while Open had learned to try the saved names and then any bundle
whose `graft.json` named the profile. So a bundle renamed in Finder, or a name
edited and not applied, was opened correctly and missed by everything else:
the launcher was never replaced, so a shortcut from 1.1.8 went on running 1.1.8's
code from the Dock; its source was never repaired; and Delete found nothing,
removed the list entry and its pairs, and left the app in Applications still
naming a source, which re-grafted a profile nothing listed the next time it
was opened. `Installer.installedBundle(for:among:)` is Open's lookup and
`ProfileOpener` calls it. The list it is given matters for one case: two
shortcuts on one folder each have an app describing it, and only the name says
which is whose, so for those the scan by profile is not made. Replacing a
launcher and Delete also take `namedBundle`, a Graft bundle under one of the
shortcut's names that describes no other listed shortcut's profile, because
neither depends on which profile the app opens. Update writes the app under
the shortcut's name beside the one it replaces and removes the old one only
once the new one is in place, so a Finder rename comes back to the list's name
with no second copy.

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

## Linux

On Linux a shortcut is a `.desktop` file, `claude-graft-<id>.desktop` in
`~/.local/share/applications`, written by `graft-profiles`. It is Graft's
because it carries `X-Claude-Graft-Config`, the path of its `graft.json` in
`~/.local/share/claude-graft/shortcuts/<id>/`, and for no other reason: an
entry under that file name without the key is somebody else's, and install
refuses to write over it, as a Mac install refuses an app Graft did not make.
The entry names its icon by path, a PNG under
`~/.local/share/icons/hicolor/256x256/apps/`, and runs `graft-launch <id>`
(inside the Flatpak, `flatpak run --command=graft-launch <app id> <id>`, since
the desktop that runs it is on the host). Its `StartupWMClass` is
`ClaudeGraft.<id>`, which the launcher passes Claude as `--class`.

The file is named by id rather than by the shortcut's name, so a rename is
the `Name` key changing and nothing moves. What the Mac build does for a
bundle renamed in Finder still has a counterpart: an entry renamed or copied
by hand, or by a menu editor, is found by the profile its description opens,
with the same exception for two shortcuts on one folder, and Update writes the
entry back under its own file name and removes the other only once the new one
is in place. Delete removes every entry naming the same description, since a
copy left behind would go on opening a profile nothing lists.

A Mac bundle carries a copy of the launcher; an entry does not, so what a
shortcut runs is always the installed `graft-launch` and only the way the entry
reaches it can go stale. `refresh_launcher` rewrites `Exec`, the window class
and the `X-Claude-Graft-Version` stamp when the stamp differs, and nothing
else, editing the file a key at a time so a `Name` still waiting for its
rename and any key a menu editor added stay as they are. `refresh_config` is
the same repair as on a Mac, found by profile and never moving one, and it
writes only a `graft.json` inside Graft's own shortcuts folder: the path in an
entry is text anybody can edit, and is not leave to write anywhere else.
Nothing is signed, Launch Services has no counterpart, and the desktop notices
a new or changed entry by watching the folder, so nothing like
`update-desktop-database` is run.

The icon is drawn from Claude's own, read on the host from
`/usr/lib/<launcher name>/resources/icon.png` (or the package's hicolor icon),
since the sandbox's `/usr` is the runtime's. A machine where none can be found
gets the plain icon in `crates/profiles/assets/` instead of no shortcut. The
hue rotation and saturation are the SVG colour matrices for `hue-rotate` and
`saturate`, which turn Claude's orange the same way round for the same angle as
Core Image's filters, and the badge is drawn with the Mac's proportions from
glyphs kept in the code, since the desktop's icon theme has no briefcase.

Opening is where the two systems differ most. This build of Claude takes
Electron's single-instance lock, so a second start on an open profile hands its
arguments to the first, which shows its window: `reveal` starts the launcher
script again with the arguments the running Claude was given, and is refused
when nothing is open there, since then it would start one rather than show
one. The running check still comes first, as on a Mac. Open from the window
runs the shortcut's launcher with the description its entry names, the way
the desktop runs the entry, so the launcher's own check is never skipped; a
missing entry is `MissingShortcut`, never Claude's own profile. The steps
around a launch that belong to other areas (the sweep, the sidebar sync, the
shared-usage lines) are handed in as `LaunchSteps` at the points the Mac build
takes them, under the state lock and then the launch lock, never both at once.

Claude's updates come from the package that installed it, so the update
policy a Mac launch checks first, and the manual-update library an install
synchronizes, have no Linux counterpart and are not ported.

`tests/opening.rs` is `test-opening.sh` made safe to run anywhere: it points
`GRAFT_CLAUDE_LAUNCHER` at a stand-in script that records its arguments,
becomes a process `ps` takes for Claude's main binary, and hands over and exits
when started a second time on a profile, as Claude does. Inside the SDK the
suite runs host commands directly (`host::set_run_directly`), so `ps` lists
only the suite's own sandbox and no real Claude can be seen or touched.
