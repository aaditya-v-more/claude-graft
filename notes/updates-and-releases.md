# Updates and releases

Graft's own updates through Sparkle, Claude Desktop's updates and the recovery after them, and what `release.sh` checks. Read this before changing `Updater`, `UpdatePolicy`, `UpdateRecovery`, `ManualUpdates`, `ClaudeDesktopUpdater`, the appcast or the release script.

## Versions and builds

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

## Claude Desktop's own updates

**An update restart is not permission to undo a quit.** Claude's ShipIt
relaunches the bundle without `--user-data-dir`, so a shortcut's profile stays
closed and the default Claude appears instead. `UpdateRecovery` requires a
profile observed running and a new, fresh `stealth-relaunch` marker written
before that process disappeared. A marker already there when monitoring began,
a navigation-only marker, and an ordinary quit authorize nothing. The marker
stays untouched so Claude can consume its saved window and navigation state.

`UpdateRecoveryMonitor` has its own five-second timer because a usage request
or keychain dialog can stall for longer than a restart. It waits fifteen seconds
after the exit and until ShipIt is gone, then asks who is running again before
launching in the background. Only a returning process counts as success, and
one marker can claim only one launch. A new reader of shared chats requires a
decision; a reader already present before the update does not. An extra default
instance is only offered for closing, with its pid and launch date checked
again when the person accepts. Graft stopping at logout stops this monitor too.

**Recovery cannot preserve a workflow the updater already killed.** Each
Claude instance asks its own Code and Cowork managers whether work is active;
there is no shared idle check across profiles. Waiting for permission or input
can count as idle even with an unfinished turn and open terminals. Once the
idle update fires, Claude kills those terminals before ShipIt takes over.

Manual update mode prevents that path using Claude's `disableAutoUpdates`
policy. It is opt-in, applies at the next Claude launch, and covers the default
profile as well as every shortcut. Its configuration library lives at
`<profile>-3p/configLibrary`, even when the profile uses Claude.ai; a policy
without inference settings does not switch providers. Verified against the
installed binary. A device-managed or remote configuration is refused because
it can override the local policy and make a protection switch a false promise.

`ManualUpdates` journals only the keys it changes before writing them, with a
lock shared by launchers and the app. Existing provider settings, credentials,
configuration selections and login files keep their owners. Disabling restores
the saved update values, preserving other edits made meanwhile; an empty
configuration created solely for this mode is then removed. A repeated launch
does not rewrite an already protected configuration. Nothing restarts a Claude
to apply this setting: a staged update in an existing process is precisely why
the interface says to finish work before reopening it.

**Checking for a release never closes a Claude.** `ClaudeDesktopUpdater` reads
the public Desktop feed at launch and hourly, whether Claude is running or not.
It has its own empty profile and device identifier, so checking needs no account
credentials and follows the same rollout as its eventual native updater. The
window and dropdown observe the same model. An available release also changes
the status item to an exclamation mark; Graft's own Sparkle update stays named
separately.

The line that reports it is one line, two when the words need them. It sits on
top of the dropdown and the window, above everything either is opened for, and
as three lines and a button it pushed every account down. Red is kept for a
release waiting to be installed, the one state asking somebody to act. The
switches in the dropdown explain themselves on hover for the same reason: a
note shown while a switch is on stays for as long as it is on. Only a failure
stays on screen. The layout suite measures the line in every state, in both
languages, at the window's sizes and at the dropdown's 320 points.

The update button first warns that all current Claude instances and workflows
will close. Only confirmation authorizes a fresh feed check and graceful quit,
using the approved pids and launch dates. A new process is not covered by that
approval. All instances must exit before the dedicated updater launches. Its
download is installed by Claude's own Squirrel updater on quit; Graft never
copies a replacement over Claude.app. A prepared bundle alone is not success:
the installed version must change and ShipIt must finish.

The cross-process `ClaudeUpdateGate` blocks shortcut launches and update
recovery during that operation. The journal allows a Graft relaunch to follow
an updater already running, but never authorizes another round of quitting
working profiles. Manual policy stays in place throughout. A refusal to quit,
bad process read, failed download, or failed install is a visible error, never
permission to force quit or claim success.

**The updater stays out of sight and signed out.** `open -g -j` asks for a
Claude launched behind everything, and Claude shows its window regardless. On
6 November that window came up during an update with Claude's sign-in screen,
which reads as Claude asking for the account again, so it was signed into; the
update itself had never needed it, since every update from September on ran in
a profile nobody had signed into. Hiding it once is not enough: in a live run
from 4.16336.0, polling found the updater four seconds after it started, and
five seconds later Claude unhid itself and came to the front to show its
window. So Graft identifies the updater the moment it is announced as launched
or active, and from then on watches its hidden, active and finished-launching
flags and hides it again as each one changes. A second run showed what that
can and cannot do. Claude cannot be hidden until it has finished launching,
about five seconds into a cold start, and until then it reads as visible while
owning no window at all; the first hide lands at that moment. The window it
then tried to show four seconds later was hidden again between two samples a
third of a second apart, and none of 132 screenshots caught it. So the
`visible` flag is the wrong measure for a run: what a person can see is a
window on screen, and that is what to count. A third run counted them from the
window server eighteen times a second: across the whole update the updater put
a window on screen twice, 600 by 600, each time for less than an eighth of a
second, both right as Claude unhid itself and Graft hid it again. None of 116
screenshots caught either. That is the floor for hiding from outside the app:
Claude shows the window before anything outside it can react.

**Never write `stealth-relaunch` into the updater's profile.** Claude writes
that file before restarting itself for an update, and a launch that finds it
fresh with `windowVisible` false comes up hidden. Tried in place it looked like
the cure: the cold-start window never appeared. A fourth live run showed what it
costs. Claude honoured the file and started with the app hidden; about five
seconds in, once it decided nobody was signed in, it put its sign-in window on
screen anyway, without unhiding the app. System Events went on reporting the
app hidden and Graft had nothing left to hide, so the window stood for the
whole sixty-one seconds the updater ran, and sixty-one of 150 screenshots
caught it. A hidden app and an app whose window is on screen look the same
from `isHidden`; only the window server tells them apart. Before launching it,
a profile that holds a login, remembers an account, has an account folder in
either session store or has a config that will not parse is moved to the Trash
and replaced by one holding only `ant-did`. The Trash, not a delete, because
whoever signed in may have done something there; the same identifier, so the
feed keeps this machine in the same rollout. A reset that fails is logged and
the update goes on hidden, since a leftover login is no reason to leave Claude
out of date.

## Graft's updates

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

**A hold is the one way to stop that, and it is off unless somebody turns it
on.** `holdUpdates` is read with a plain `bool(forKey:)`, so every machine that
arrives by updating — none of which has the key — goes on installing silently
as before. On, Sparkle's own schedule is turned off, because it downloads
whatever it finds, and an hourly `checkForUpdateInformation` probe stands in for
it: the feed is read, the version shown in the dropdown and on the status item,
and nothing fetched. A press on that line runs the same background check the
schedule would have, so there is still exactly one way a version arrives and it
still asks nothing. The controller is built with `startingUpdater: false` and
started only after the hold is applied, since Sparkle schedules its launch check
as it starts and a hold arriving a moment late would let that check install the
very thing being held. `UpdatePolicy` carries the rule.

That press needs `SUAllowsAutomaticUpdates` in `Info.plist`. Without the key
Sparkle allows background downloading only while its own schedule is on, so
under a hold `automaticallyDownloadsUpdates` read false whatever had been set.
The press then took the ask-first path and showed nothing: tested for real, it
read the feed and stayed on the old version twice. With the schedule on, the
key changes nothing, which is why the default path never showed the problem.

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

## Signing

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
