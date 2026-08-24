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
    ./test.sh                  233 checks, all in a throwaway directory
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

**An update installs itself and says nothing.** Checked hourly and at launch
once that much has passed, downloaded, installed and restarted with nobody
asked. The gentle-reminder route was tried first and was wrong for this app:
there is usually no window to put a question in front of, so an update that
waits to be noticed is one that never gets installed. What it costs is the
status item blinking out and back.

That only works because `applicationShouldTerminate` lets it through, and
Sparkle's own header warns that `updaterWillRelaunchApplication` may not be
called. So `isRelaunchingForUpdate` is set at every hook before an install —
`willInstallUpdate`, `willInstallUpdateOnQuit` and the relaunch — and the suite
counts them, because one missed hook is an update that installs and never
starts, which happened twice before it was understood. `willInstallUpdateOnQuit`
also calls the immediate handler: Sparkle would otherwise wait for a quit that a
menu bar app may not see for weeks.

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

## Verification state

Working and checked end to end: shortcut creation, adopting an existing profile,
cross-account chat mapping, live usage for both accounts, memory steady around
106MB at zero idle CPU. Quitting too, from outside: with the item in the bar a
quit is cancelled, the window closes and `mainWindowOpen` goes to 0 with the
process still alive; with `showInMenuBar` off the same quit ends it, so the app
cannot become unquittable. Quit in the dropdown takes the second of those two
paths.

Unproven: **Start Session** has never successfully opened a window. It POSTs one
Haiku message with `max_tokens: 4` and Claude Code's system prompt, which these
tokens are minted for. If that turns out to be wrong the API's own error message
surfaces in the UI rather than being swallowed — read it rather than guessing.

`~/Library/Application Support/ClaudeGraft/usage-status.json` records what each
poll found, including whether the figures came from the API. Read it to check
the state of live usage instead of reading numbers off the screen.
