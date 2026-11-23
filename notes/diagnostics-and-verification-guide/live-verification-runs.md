# Live verification runs

[Back to diagnostics-and-verification.md](../diagnostics-and-verification.md)

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
stand-in had not shown; [shared-usage.md](../shared-usage.md) says what. Still
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

A third run that evening took the merged 2.0.1 through all of it at once. The
keychain was asked once, at launch, by the usage pass that may ask once while it
is shut, and the new relay took over as soon as it was allowed, with the old
one kept lending until then; the "Lending paused" notice that a refusal leaves
was therefore not seen. Switches reached the bar in 1.05 to 1.17 seconds, the
right click's menu, Show in Menu Bar, archiving and deleting all held, and
nothing a person had set changed. Artifacts, Projects and Search Chats each
crashed Graft on opening, which [artifacts.md](../artifacts.md) explains.

The fix was then seen running. All three panes opened twice and again after a
relaunch with no crash, and the launch sweep removed the eight stores no
account owned, one `artifacts.store stray` line each, with no removal failing.
One account was then signed in with an emailed code: its six artifacts, its
project and a search all loaded, and after Graft was quit and reopened it was
still signed in, its one store kept. That search called two transcripts gone
that were halves of a conversation still open, which
[projects-and-search.md](../projects-and-search.md) explains.
The run after the fix searched the same words: the same hits came back less
those two halves, nothing was marked gone, the gone filter listed nothing, and
the account and its store had come through the swap untouched.

Unproven: **Start Session** has never successfully opened a window. It POSTs one
Haiku message with `max_tokens: 4` and Claude Code's system prompt, which these
tokens are minted for. If that turns out to be wrong the API's own error message
surfaces in the UI rather than being swallowed — read it rather than guessing.

`~/Library/Application Support/ClaudeGraft/usage-status.json` records what each
poll found, including whether the figures came from the API. Read it to check
the state of live usage instead of reading numbers off the screen.

On Windows, seven taps on the About card's icon in Settings, each within two
seconds of the last, open a debug window that stages Claude's and Graft's update
states, setup, the link handler, a busy or unreadable state pass, and each
profile's sign-in, open dot, usage, Fable limit, usage refresh, sidebar
sync line, shared-usage lenders and chats-elsewhere offer, the unreadable
shortcut list, a Graft update mid-download, and the sign-in chooser with
made-up Claudes. It is held in `Staging`, in memory only,
so quitting Graft or Reset puts everything back, and a stage only ever makes Graft
show more or do less: a Claude can be staged open but never closed, and a staged
update is never fetched, installed or restarted onto. A staged offer copies
nothing, a staged lender is never saved, and the staged chooser sends nothing. Sign In and Open still
act for real on a staged card.
