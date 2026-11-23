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

Windows writes both files into `%APPDATA%\ClaudeGraft`, the same folder whichever
Claude is installed. The report is rewritten at the end of every pass, the one
an open runs and the one after a Claude quits, under the state lock that pass
holds, so it describes what that pass left. It has the Mac's sections and findings, read from the same
places: the command line login in `%USERPROFILE%\.claude.json`, each profile's
`config.json` and `ant-did`, every organization folder in both chat stores with
its stash, and the pairs in `mirrored-chats.json`. Whether a Claude is running is
asked of the processes once for all the profiles, since each question is a WMI
query and the pass stands in front of a window opening. The report is written
even when the pass stopped at an unreadable `session-records.json` or
`mirrored-chats.json`, and then says which file stopped it, because that is when
somebody reads the report. Like the Mac's, it never holds a title, a chat's
contents or the login's email address; account, organization and device ids
stay, and so do the folder paths in the mirrored pairs. The suite runs its
notes into a temporary folder of its own, so no test reaches the real one.
So far only the suite has read a Windows report; none has yet been seen from a
Graft running on a real machine.

## Verification state


Working and checked end to end: shortcut creation, adopting an existing profile,
cross-account chat mapping, live usage for both accounts, memory steady around
106MB at zero idle CPU. Quitting too, from outside: with the item in the bar a
quit is cancelled, the window closes and `mainWindowOpen` goes to 0 with the
process still alive; with `showInMenuBar` off the same quit ends it, so the app
cannot become unquittable. Quit in the dropdown takes the second of those two
paths.

That idle figure is from before 2.0. On 9 November the installed 2.0.1 sat at
about 9% of a core and 123 MB, with 18 MB more in `graft-relay`, while a
conversation was on the bar and nothing else was asked of it; a profile put
most of that in redrawing the bar's text and in the look's walk over every
record. Both are cheaper now, as [shared-usage.md](shared-usage.md) says, and
the suite proves that nothing in Graft wakes while the text moves.

Seen on the Mac Mini on 11 November, macOS 27.0.1, against the installed 2.0.1
under the same conditions and over two minutes each: the text moves on a
redraw alone, at the same speed, hold and gap, whole and leaving nothing behind.
With nothing on the bar Graft fell from 3.32% of a core to 1.26%, and with text
standing still from 3.40% to 1.31%, so still text now costs nothing more than
none. Moving text fell from 8.11% to 4.91% and 5.04%, a cut of about 38%
against the half that was expected: the moving cost over the idle figure went
only from 4.8 points to 3.7, so most of what a tick costs is the redraw itself.
The WindowServer fell with it, from 15.1% to between 5.4% and 10.7%. Graft's
memory while moving read 126 MB and then 67 MB in the two samples, against 83
MB installed, which says the figure moves more than the change does.

A third run there the same day put the text on a layer that Core Animation
steps, with nothing redrawn. Moving text took 1.51% and 1.40% of a core against
1.34% still and 1.31% with none, so a moving title now costs about a tenth of a
point. The WindowServer took 4.68% and 3.91% while it moved. The text moved at
the same speed, hold and gap, came round whole, and left nothing behind. Graft's
memory read 150 MB moving and about 116 MB otherwise; no leak was looked for or
seen. The one fault was still text standing a pixel above the same text moving,
which [shared-usage.md](shared-usage.md) explains.

A fourth run, with the strip laid where a marked drawing of the button shows the
blank, compared frames captured at the display's own scale rather than cut down
from recordings. In 2.0.1 and in the development build alike, moving text in
its hold, still text, moving text chosen again and moving text after another
Claude came forward all began on rows 10–18, 90 pixels into the item, with the
figure on rows 10–19. The first step after the hold moved left only. Movement,
clicks and the Claude in front behaved as in the third run, and moving text took
1.35% of a core against 1.33% still.

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

## Topic guides

- [Live verification runs](diagnostics-and-verification-guide/live-verification-runs.md)
