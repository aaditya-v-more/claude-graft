# The update restart, and the Claude it brings back

Notes from the investigation of 5–6 September 2026. Everything here was measured
on this machine unless it says otherwise, and the measurements are listed so the
next person can check them rather than take them on trust.

## Where this came from

A user wrote in:

> Unfortunately I have experienced two crashes on the second instance in the
> past 12 hours; causing my sessions to get interrupted. When it crashes, it
> opens the first instance by default (only the second was running). Having
> happened twice in the middle of a lot of work, this solution wont work for me.

It happens here too — four times over two days, all four caught in the logs, one
of them within two minutes of it happening. Their account is accurate in every
particular.

## What we are trying to settle

Whether Graft causes it, what actually happens, and what Graft should do. As
things stand Graft notices none of it: nothing anywhere looks at Claude going
down, or at the instance that turns up afterwards.

Graft does not cause it. The initial investigation treated recovery as the only
option; the manual update mode described below supersedes that conclusion.

## What actually happens

Claude Desktop installs updates by quitting and restarting itself once it has
been idle a while. It calls this a stealth update and says so:

    15:13:55 [stealth-update] Triggering stealth update after idle timeout
    15:13:55 [CCD] Killing 4 PTY process tree(s) on quit
    15:13:55 beforeQuitForUpdate handler fired, going down for update

Nothing crashed. The "crash" in the report is the app quitting itself
mid-session, and the interrupted sessions are literal — that middle line is
Claude killing the command line processes behind every open session on the way
down. No dialog, no warning, no crash report.

The restart is then done by Squirrel's ShipIt, which launches the bundle:

    15:14:02 On main thread and launching: file:///Applications/Claude.app/
    15:14:02 Attempting to launch app on 11.0 or higher
    15:14:07 Successfully launched application at file:///Applications/Claude.app/

There is nowhere in a ShipIt request to put a command line.
`~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/ShipItState.plist`
carries `bundleIdentifier`, `launchAfterInstallation`, `targetBundleURL`,
`updateBundleURL` and `useUpdateBundleName`, and nothing else. So what comes
back has no arguments at all.

**And a profile is nothing but an argument.** `Graft.launchArguments(for:)`
gives a shortcut `--user-data-dir=<profile>` and gives the main profile nothing,
and `Graft.isDefaultInstance` recognises main by that absence. A Claude restarted
by its own updater is therefore, by construction, the first instance. The
grafted profile does not come back at all.

Four events, and the pattern does not vary:

    stealth update      went down    argument-free instance    profile back
    4 Sep 14:13:13      Claude-2     14:28:30                  14:48:31
    4 Sep 17:42:18      Claude-3     17:42:31                  17:47:32
    5 Sep 03:10:31      Claude-2     03:10:43                  14:00:15
    5 Sep 15:13:55      Claude-2     15:14:09                  15:14:22

Every one of those last times is a `launcher.run` in
`ClaudeGraft/diagnostics.log` — a person clicking the shortcut, every single
time. The profile never returns by itself. The third row is the one to look at:
the update landed at 03:10 and the profile stayed down for close to eleven hours,
until somebody sat down and opened it.

## The relaunch that would have worked, and is not the one used

Claude has a relaunch of its own that rebuilds the command line it is running
with:

    function pKt(e = []) {
      let t = process.defaultApp ? 2 : 1
      let n = [...process.argv.slice(1, t),
               ...process.argv.slice(t).filter(e => !fKt(e)),
               ...e]
      ...
      o.app.relaunch({ args: n })
    }

`fKt` drops deep links, paths ending `.dxt` or `.mcpb`, anything starting
`--os-entry=`, absolute paths, `--relaunched-after-gpu-crash-loop` and
`--claude-hybrid-switch-org=`. It does not drop `--user-data-dir`. (One of its
two predicates, `Wc`, was not pinned down — the minifier reuses the name across
chunks — and it looks like a URL test.)

So restarting without losing the profile is possible, and Claude does it for
config changes and preview loads. The update path is not one of them: it ends at
`o.autoUpdater.quitAndInstall()`, which hands the job to ShipIt and the bundle.
That is the whole of the bug.

## Why the stray instance is a different Claude on every machine

A Claude with no `--user-data-dir` takes its profile from its own configuration.
With a third-party provider configured it uses a separate identity:

    var aW = "-3p", oW = `Claude${aW}`      // "Claude-3p"

which gives it `~/Library/Application Support/Claude-3p` and
`~/Library/Logs/Claude-3p`. Here that is the profile pointed at a local model, so
the stray instance comes up talking to `127.0.0.1:11436`:

    15:14:09 [custom-3p] Credentials loaded from managed config { provider: 'gateway' }
    15:14:09 [custom-3p] 3P mode active { provider: 'gateway' }
    15:14:11 [custom-3p] inference apiHost=http://127.0.0.1:11436

With no such configuration it is the plain main profile, which is what the
reporter saw and called the first instance. Same mechanism, different landing
spot — and that difference cost most of a day, because the main profile here has
had no writes since 29 August and looked like proof the argument-free launch
never happened. It had happened, four times, one directory over.

Seen in the same neighbourhood and not followed up: `CLAUDE_USER_DATA_DIR` is
read somewhere near the `-3p` naming. Worth understanding before designing any
fix that turns on the command line.

## The marker, which is the part worth keeping

Before going down, Claude writes a marker into the user data directory of the
profile that is going down — `<profile>/stealth-relaunch`, holding a timestamp,
whether the window was visible, whether another app was full screen, and the
saved navigation history. It is consumed on the next start of that profile, so
you will not find one sitting there unless you catch it mid-flight; its fate
shows up in the next `[startup-perf]` block as
`update_relaunch_marker: 'none' | 'stale'`.

That marker is the only thing on disk telling "the updater took this profile
down and meant to bring it back" apart from "somebody quit it". Any recovery
Graft attempts has to turn on that distinction, because bringing back an app
somebody deliberately closed is worse than the problem being solved.

## What was ruled out, so nobody redoes it

It is not a crash. `~/Library/Logs/DiagnosticReports` holds nothing for Claude
and the Crashpad directories in the profiles are empty for the relevant days.

It is not Graft. Graft launched nothing at any of the four moments; the only
launcher runs are the recoveries afterwards.

It is not something Graft could avoid by launching differently. The stray
instance is started by LaunchServices against the bundle, from a process Graft
has no part in.

It is not the main profile on this machine. That is the `-3p` redirect above, and
checking `~/Library/Application Support/Claude` for signs of it will keep
answering no.

## Where to look next time

Claude's logs are shared by every profile launched from `/Applications/Claude.app`
and live in `~/Library/Logs/Claude/main.log`; a profile in third-party mode logs
to `~/Library/Logs/Claude-3p/main.log` instead. Which profile a given startup
belongs to is settled by its `[oauth] token cache location:` line, which names
the `config.json` it loaded. `Starting app` marks each launch.

`~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/ShipIt_stderr.log` is the
updater's own account of what it installed and what it launched.

`ClaudeGraft/diagnostics.log` says whether Graft launched anything, and
`ClaudeGraft/state-report.txt` says what was true at the last pass.

**Watch the clocks.** Graft's diagnostics are UTC with a `Z`; Claude's logs are
local time. They were 5½ hours apart here, which is wide enough to make one event
look like two, and it did — a grep narrowed to the wrong half hour is what
produced the first, wrong version of this document, in which the profile appeared
to come back on its own.

## What Graft could do

The vocabulary is already there. `Graft.isDefaultInstance`
(`Sources/Shared/GraftCore.swift:2784`) recognises an argument-free Claude
exactly, `Graft.isRunning` (`:2764`) knows which profiles are up, and
`UsageMonitor.start(watching:every:)` is already turning over every thirty
seconds with the list of profiles in hand.

In the order they are worth doing:

1. **Bring the profile back.** A profile that was running, is no longer running,
   and has a fresh `stealth-relaunch` marker beside it went down for an update
   and is never coming back on its own. Relaunching it is the whole fix as far
   as the person is concerned, and the eleven-hour row above is what it is
   worth.

2. **Say what happened.** Whatever else is done, "Claude restarted itself to
   update and this profile was reopened" turns a vanished session and a strange
   window into something a person can understand without reading a log.

3. **Deal with the stray instance.** Nobody asked for it and it came through no
   shortcut. Offering to close it is probably right; closing it silently is
   probably not.

On a plain machine the third is more than tidiness, because there the stray is
the **main profile**, and every shortcut grafted from main is then sharing a chat
store with a Claude nobody asked for. That is the loss `ChatConflict`
(`Sources/App/ChatConflict.swift:16`) exists to warn about: it asks when a person
presses Open, and asks nothing when the updater does the same thing.

## Open questions

Whether `CLAUDE_USER_DATA_DIR` names a profile in a way that survives a launch
through the bundle. If it does it changes the shape of every fix above, because
the profile would no longer live only in an argument.

Whether the update path ever takes Claude's own argv-preserving relaunch on some
other version or platform. Four for four here says no, but four is four.

What the stray instance does to a store while it is up. It was closed by hand
within a minute or two each time here, and nothing has yet been checked about
what a longer-lived one writes.

## The fix, 6 September

`UpdateRecoveryMonitor` now watches installed shortcut profiles independently of
usage polling. A blocked usage call must not delay noticing an exit. A complete
process snapshot is read every five seconds; an unsuccessful process read is
discarded rather than interpreted as every profile having quit.

Recovery requires a profile seen running, a new marker timestamp from that run,
and an exit after the timestamp. The marker expires after five minutes, matching
Claude's own reader. Graft gives Claude fifteen seconds to relaunch itself and
waits for ShipIt to finish before reopening the exact profile in the background.
It leaves the marker intact. A launch is claimed once per marker, and a returning
profile process is the only evidence that it succeeded. An unsuccessful launch
is reported rather than retried forever.

Both the window and dropdown explain the restart and recovery. A profile newly
opened on the same chats requires a decision before recovery; one already open
before the update does not. A new default instance can be closed by request,
with its pid and launch date rechecked before the quit. The default Claude that
was already running is never included in that offer. This covers Claude 2 and
Claude 3 sharing chats here while the default instance uses its separate local
provider profile.

Two details were checked directly in the installed bundle. Packaged Claude
clears `CLAUDE_USER_DATA_DIR` outside its developer-approved mode, so setting it
is not a supported way around ShipIt's missing arguments. Also, the same marker
filename carries navigation-only restarts (`navOnly: true`); those are not taken
as evidence of a stealth update. An old marker found at Graft startup does not
reopen a closed profile, either.

Verification: 620 regression checks passed and the app built for both Mac
architectures. A native integration test launched the installed Claude binary
on a disposable profile, wrote the real marker format, and gracefully quit that
instance to simulate the update exit. The monitor reopened exactly one process
on the same directory, Claude consumed the marker, and a subsequent normal quit
remained closed beyond the recovery grace period. This tested the recovery path;
it did not force an actual download or installation by ShipIt. The verified
Graft build was then installed locally, with the previous bundle backed up.

Graft must remain running to observe the profile before its update. The recovery
restores the profile, not the terminal processes Claude killed during shutdown;
those sessions still need to be resumed.

## Preventing the interruption

Recovery was not the primary requirement: an unfinished workflow must not be
killed for an automatic update. The installed bundle was inspected again with
that question in view.

The idle check is per process, not hardwired to Claude 1. The stealth updater
calls that process's Code session manager, Cowork session manager, and active
chat-request tracker. Its Code manager walks its own in-memory sessions. There
is no coordination with the session managers in other Claude instances.

Its definition of active is narrower than an unfinished workflow. In
`sessionActivityKind`, a running attended turn that `isSessionWaitingOnUser`
recognises can be skipped. That includes a permission request waiting ten
seconds with no other pending work. An open terminal process by itself is not
an idle blocker. The ten-minute stealth countdown can therefore finish while
work is waiting for a person; shutdown then kills the open terminal trees.
The logs establish the stealth exits and terminal kills, but do not record
enough session activity to prove which predicate caused each historic event.

Claude documents `disableAutoUpdates` in its
[enterprise configuration reference](https://support.claude.com/en/articles/12622667-enterprise-configuration-for-claude-desktop).
The installed binary also accepts that policy in a local configuration library
without changing providers. A disposable-profile launch confirmed the log
`[updater] Auto-updates disabled by enterprise policy` while its browser data
remained in the original profile, rather than moving into the `-3p` directory.

The user chose manual update mode. Graft now exposes **Update Claude Desktop
manually**, covering the default profile and all installed shortcuts. New
shortcuts and launcher starts apply it too. Disabling the mode restores the
previous update-policy values; it does not restore an old copy of provider
configuration over changes a person made meanwhile. No credentials are copied
into the undo record. Device-managed and remotely supplied configurations are
refused because they may override this local policy.

This is prevention by opting out of automatic Desktop updates, not an assertion
that Claude now coordinates all profiles' active work. The policy must be in
place at startup. Applying it to an existing instance cannot retract an update
already staged in that process, and Graft never restarts working instances to
apply it. To update, finish work, turn the option off, quit Claude instances,
then reopen Claude and check for updates.

Verification: 658 regression checks pass, including preservation and restoration
of provider settings, both policy spellings, later shortcuts, unreadable files,
and redirected filesystem paths. A native test used the installed Claude with
a disposable profile and Graft's actual policy and launch code. Claude reported
updates disabled after enabling the setting, then automatic updates enabled
after disabling it and reopening. Both launches kept the original profile
identity. The test closed only its own instance.

The universal build was installed locally and both existing shortcut launchers
were refreshed. Manual mode was enabled through Graft Settings, then verified
in the selected configurations for Claude, Claude 2, and Claude 3. The existing
login files, Ollama configuration apart from the update flag, configuration
selection, and shortcut relationships were unchanged. All three Claude
instances were closed at verification, so the policy takes effect on their
next launch. The previous Graft and shortcut bundles are backed up under
`/private/tmp/claude-graft-before-manual-updates.noindex`.

## Choosing an update from Graft

Manual mode now includes an independent availability check. The window and
menu bar show Claude Desktop's installed and available versions even while
Claude is running. A pending update changes Graft's menu bar icon to an
exclamation mark. Checking the feed does not start Claude or change its policy.

**Update Claude…** opens a warning naming how many instances are running and
that their workflows will stop. Cancel does nothing to those instances.
Confirming rechecks the current release before any quits, then waits for every
approved process to exit. A later instance or reused pid stops the operation;
an instance that refuses to quit is never forced closed.

The download runs in an empty profile under Graft's own support directory,
with the same device identifier used for availability checks. Working profiles
keep their manual policy throughout. Graft launches the installed, unmodified
Claude, waits for its prepared update, and gracefully quits that updater
instance so Squirrel installs it. The mechanism is described in
[Squirrel's installation documentation](https://github.com/Squirrel/Squirrel.Mac#installing-updates).
Its [termination listener](https://github.com/Squirrel/Squirrel.Mac/blob/main/Squirrel/SQRLTerminationListener.m)
waits for instances of the target bundle to exit rather than killing them.

A shared file lock keeps shortcut launches and automatic profile recovery out
of that sequence. A saved update run can be followed after Graft relaunches;
relaunching Graft never repeats the user's earlier permission to close working
instances. Completion requires the installed bundle version and the installer
process state, not just an updater window disappearing. Graft never writes
Claude's application bundle or copies credentials into the updater profile.

Verification: 703 checks pass. They cover numeric release comparison, bad feed
responses, a withdrawn release or network failure before shutdown, all three
instances exiting, reused pids, new instances, download and install timeouts,
and the cross-process launch lock. A native UI preview verified both available
release layouts and the warning for three running instances; Cancel returned
to the available release without invoking the updater.

The universal build and both shortcut launchers were installed locally. The
installed app read the live feed and displayed Claude 1.46388.4 as up to date.
No real release installation was forced. Existing profile, provider, login,
policy, and shortcut checksums were unchanged, and manual mode remained enabled
for all three profiles. The prior bundles are backed up under
`/private/tmp/claude-graft-before-update-controls.noindex`.
