# Claude Desktop updates

[Back to updates-and-releases.md](../updates-and-releases.md)

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

**A restarted Graft holds the lock before it reads anything that can fail.**
`resume` used to read the processes first and take the lock after, with one
error path for both that went to `finish`, which deletes the journal and lets
the lock go. The read fails while any Claude has no launch date yet, which is
exactly the updater starting or a Claude closing — and `poll` treats the same
failure as a reason to wait. A Graft that restarted in that window, through its
own update, a crash or a quit, forgot the run: nothing blocked a shortcut, and
when the updater nobody watched any more quit, Claude's installer could swap
`Claude.app` under a Claude opened meanwhile. The lock is now taken as soon as
the journal decodes, the saved phase is shown, and the decision
`ClaudeUpdateResume` makes waits for the first snapshot that reads; until then
each poll asks again and the journal stays.

**Stopping the updater asks again, and stops hiding it.** One quit was sent and
then nothing, for as long as it took. A quit the updater missed held the lock —
every shortcut refused — behind a status line that never changed, with the one
window that could have been closed by hand kept hidden even from its Dock icon.
While stopping, `poll` sends the quit again every ten seconds, and a minute
after the first one stops concealing the updater for the rest of the run
(`ClaudeUpdateStop`). The lock is still held until it has gone; that part was
right.

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
