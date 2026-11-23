# Polling budget

[Back to usage-and-credentials.md](../usage-and-credentials.md)

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

An account in use moves while nobody looks, so the tick's five minutes is
two for a profile whose Claude is open, and bringing a Claude to the front asks
for its account at the strength opening the dropdown does — matched to the
profile through its command line, the main one by having no `--user-data-dir`.
A press on one account's refresh button is `focus`ed: that profile gets the
press and the rest are read as a tick reads them, keychain prompt included.

Every figure carries when it was really taken. A reused reading used to be
stamped with the moment the pass ran, and a failed request returned the last
live reading of any age marked the same way, so a bar an hour behind read as
current. `refreshed` is the service's answer time or the profile's own sample
time, the row says which of the two it is, and `note` says why when it is not a
fresh live answer. Opening the dropdown mid-pass used to be dropped like a tick;
`arrival` queues anything stronger than a tick, and a queued request keeps its
own strength rather than being replayed as a press. A sample the profile wrote
after the last live answer is the better figure and wins.

Anything on screen that is worked out from the clock runs on a `TimelineView`
of its own. SwiftUI redraws a view only when its inputs change, and a figure
that has not moved is the same input every pass, so a reset countdown drawn in
a plain body stood still for ten minutes while the weekly one beside it counted
down.

Starting a session is the one thing here that changes the number itself, so it
calls `invalidate` for that profile before refreshing rather than trusting any
cache window. That is ordered against the refresh by the serial queue both go
through. Without it, a five-hour window would open and the bar would go on
showing the figure from before it.

Opening the dropdown asks for a current figure but does not skip the backoff,
because a failing endpoint would then be asked again on every open. Failures
back off 60/120/300/900/1800 seconds and a success clears the count; a
`Retry-After` from the service wins and is the one wait a person pressing
refresh cannot skip. It holds only as long as the service is the one asking: a
later failure that carries none starts a wait of Graft's own, and the service's
mark comes off with it, or a press would sit out an ordinary backoff as though
the service had asked for it.

Only a failure that came back from the service, or no answer at all, backs
off. One on this Mac — the keychain shut, no login, an expired one, a config
caught mid-write — happens before anything is sent, is put right on this Mac,
and is tried again on the next pass. All of them used to read "Anthropic didn't
answer" and wait up to half an hour, which pointed the person at the network
and kept the account stale for thirty minutes after they had fixed the cause.
`UsageMonitor.cameFromService` is the line between the two, and each local one
says what it is in the row.

A config that is there and will not parse is Claude renaming a new one over it,
not a profile with no login, so `ClaudeCredentials.tokens` reads it through
`readableConfigJSON` and throws `configUnreadable`: retried, never taken for
signed out. Read as empty, it left a lender with nothing to be recognised by
for a pass.

**A pass that has been told no asks nobody after it.** The key is one item for
every profile, so once a read in a pass comes back shut, every profile after it
in that pass is read silently. Before, `mayRaiseDialog(.yes)` answered yes for
each profile whatever had just happened, and Start Session's follow-up refresh
went out allowed to ask and with no focus, so a Deny on a Mac with three
Claudes was answered with three more dialogs. That refresh now asks nobody and
only about the account the session was started on: the press already asked the
keychain once, in starting the session.
