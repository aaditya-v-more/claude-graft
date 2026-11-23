# Recovery decisions and the fix

[Back to update-relaunch.md](../update-relaunch.md)

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

Every line of a snapshot is asked whether it is Claude's installer, and that
test used to find Claude's app on disk and the home folder again for each line:
5.5 ms a snapshot for the seven hundred lines on the development machine, more
than the listing was read for. `Graft.installerProcessCheck()` works both paths
out once and hands back the test, and this monitor's snapshot and
`ClaudeDesktopUpdater`'s both take it.

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
