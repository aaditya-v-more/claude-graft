# Diagnostics, and what has been checked for real

What each pass writes down for the next person, and which behaviour has been seen working on a real machine rather than only in the suite.

## Diagnosing it afterwards


Nearly every rule in these notes turns on telling two indistinguishable situations apart: a
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

No event names a chat by its title. `sweep.file` used to, on every record it
filed, and the log is what the bug form asks people to paste; the session id
already says which session it was, and the title is only the person's own
words about their work. Folder paths stay, since where a session ran is part of
the diagnosis, and the form says the log holds them.

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

The offer to bring across chats an account switch left behind has been seen
for real: a profile signed into an account another profile was holding chats
for was offered them, and the copy brought them across. Thirty-two checks
carry the rest, over both stores, the shortened local-mode spelling, a stashed
destination, a merge into a profile that has its own chats, a record whose
stamp disagrees with its file, and a second run. The refusal while a Claude is
open is the one clause the suite still stands alone for, since it is driven
through an override — nothing in a temporary directory can be made to run.

Usage freshness has been watched live with both Claudes closed: the label and
its refresh button under every account in both places, the keychain fallback
named in orange, one account's refresh leaving the other alone, Refresh Usage,
a reopened dropdown asking again after a minute, and closed profiles advancing
every five minutes. A second run with one Claude open covered the rest: the
open account every two and a half minutes beside the closed one's five and a
half, bringing that Claude forward asking for its account alone within a
second, a return inside the minute reusing the figure, and switching between
other apps asking for nothing.

The Claude in front has been watched live with main Claude open and Claude 2
closed, a recorder reading the status item's accessibility title and frame and
photographing the bar on every change: the icon by default beside that
account's figure, the plain headline figure with Finder, Chrome or anything else
in front and back again, the dropdown leaving it in place, the name alone, the
two together, both off, and back to the defaults from Settings, with the item
reachable in all 81 readings. Bringing Claude forward with its figure 91 seconds
old refreshed it within the second, so the refresh moved with the lookup. Two
Claudes swapping places in front is the one case not yet seen, since Claude 2
stayed closed.

Shared usage has run against both accounts on this machine. Desktop's Claude
Code 2.1.317, started as Claude's own profile the way Desktop starts it with the
two lines added, went through the built relay on a spare port and had all three
of its model requests answered by Claude 2, whose weekly reset was sooner. The
limits that came back were Claude 2's — 1% and 0%, against Claude's 33% and
19% — which is the account answering rather than the relay's word for it.
Lending per Claude, choices from the dropdown, the hold for as long as a cache
lives and one account counted once are driven by `./test-relay.sh` against a
stand-in service; the environment, the swap a launcher makes and every refusal
are driven by the suite over sealed files in a throwaway directory. A socket
upgraded through the relay and a chunked request answered while it is still
being written are driven there too, against the stand-in.

Four live runs on 7 November 2026 then took it through Desktop 4.23118.0 with
Claude 2 borrowing Claude. Seen working: Graft writing the lines into a closed
Claude 2 and leaving Claude's own profile alone while open, the relay starting
and stepping aside about half a minute after the lines came out, a pane sign-in
refused for the borrower's own account and accepted for Claude's with no refresh
token kept, a Code-tab conversation answered by the ranked account, and — once
the fourth run had e592068 — a Sonnet conversation moved from the dropdown,
answered by the account chosen (one `thread_not_found`, then the new thread
there), staying on it through the next turn and after **Automatic**, with titles
answered elsewhere leaving the dropdown alone. Remote Control's socket went
through untouched in every run, and a message sent from another device was
answered on the moved account. Each run before that found one thing the
stand-in had not shown; [shared-usage.md](shared-usage.md) says what. Still
not seen: Claude in Chrome on a shared conversation (the extension was not
connected to Claude 2), a held conversation past its hour of cache, and the
dropdown with two Claudes sharing at once.

A run on 8 November 2026 watched the conversation on screen with the first
2.0.1 candidate installed, Claude open and Claude 2 closed, a recorder matching
each `menu.chat` against the record Claude had just focused. Switches between
two test chats, between two that shared a name, and to an idle one, which that
candidate named nothing for, were followed every time within the four seconds
the recorder allowed. A chat moved to Claude 2 from the dropdown was answered
there and the bar said so, and archiving one chat and deleting the other took
both off the bar and the dropdown. The first messages after the candidate went
in reached the relay only once the keychain had been asked again and allowed.
Not seen: a new chat's "New chat" giving way to its name, and the right
click's menu, which the recorder could not open. Old chats named by their
titles, the watch that wakes on Claude's write and the red ring came after
that run.

A second run on 9 November, with the second candidate, saw them. Older chats
were named by their titles alone with nothing lit, and switches reached the bar
1.06 to 1.42 seconds after Claude's focus, against Claude's own write at 1.0.
The account answering was ringed, and the ring moved to Claude 2's icon when
the right click's menu, which opens below the bar, moved a chat there and back.
The name came back at its width after Show in Menu Bar was turned off and on,
and archived and deleted chats left the bar and the dropdown. Each new chat was
named within a tenth of a second of Claude's first record of it, which already
carried its title, so the "New chat" the first run looked for does not exist.
The keychain was asked once, after the swap. Still not seen: the text giving
way for room, and a focus the mirror copied, since Claude 2 stayed closed.

Unproven: **Start Session** has never successfully opened a window. It POSTs one
Haiku message with `max_tokens: 4` and Claude Code's system prompt, which these
tokens are minted for. If that turns out to be wrong the API's own error message
surfaces in the UI rather than being swallowed — read it rather than guessing.

`~/Library/Application Support/ClaudeGraft/usage-status.json` records what each
poll found, including whether the figures came from the API. Read it to check
the state of live usage instead of reading numbers off the screen.
