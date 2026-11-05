# Usage figures and borrowed credentials

Where the usage figures come from, how the token is read out of Claude's safe storage, when the keychain may ask, and how often the endpoint is polled. Read this before changing `ClaudeCredentials`, `UsageMonitor`, `SessionStarter` or anything with a timer.

## Credentials and the keychain

**Credentials are borrowed, never taken.** Only the access token is decrypted,
never the refresh token: Anthropic rotates those and using one signs Claude
Desktop out. Nothing is written back to Claude's config, cookies or keychain.
Claude's safe-storage key does seal two things of Graft's making, both for
[shared usage](shared-usage.md): the Code environment it writes into a closed
profile, and the logins signed in from the pane, kept in Graft's own
`pool/accounts.json`. A token goes to `api.anthropic.com` and nowhere else. The
one other address is `platform.claude.com`, which a sign-in from the pane sends
the browser's one-time code to, and what it answers with is a login of Graft's
own, never one of Claude's.

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

## The usage figures

**One account, one figure.** The window read `plan-usage-history.json` for
itself while the menu bar showed what the endpoint had just answered, so one
account was reported from two sources that do not agree. That file is only
written while that Claude runs, so a profile closed for days had the window
showing figures from before a weekly reset — 92% of a week the bar correctly
had at 30%, side by side on one screen. Both detail views take
`UsageMonitor.entry(for:)` now, which is live where live is available and falls
back to the same file underneath; the suite fails if `Graft.usage(` reappears
in either of them. Their Start Session buttons `invalidate` too — that rule was
written for the dropdown alone, and the moment a window showed the monitor's
figure it needed it for the same reason.

Live usage is `GET https://api.anthropic.com/api/oauth/usage` with a bearer
token and `anthropic-beta: oauth-2025-04-20`. It answers with `five_hour` and
`seven_day` objects carrying `utilization` and `resets_at`, plus
`subscription_type`, a `limits` array of `weekly_scoped` per-model windows, and
`extra_usage` credits. The last two are parsed but not shown anywhere yet.

**No five-hour limit is not zero used.** Since November 2026 some accounts have
only a weekly limit, and the endpoint answers them with `five_hour: null` or no
`five_hour` key at all. A limited account always gets the object, idle
included: nothing used reads `{"utilization": 0, "resets_at": null}`. So a null
or absent window is an account with no limit, while an object that is there and
cannot be read is a broken answer and is refused, as it always was. Reading the
missing window as 0% put an account at 94% of its week on the menu bar as 0%,
and, because the bar's Claude was picked by its five-hour figure, made it lose
to any other account in use.

`plan-usage-history.json` is the fallback: samples of `{t, org, u:{fh, sd}}`
where `fh` is the five-hour percentage used and `sd` the seven-day one, written
only while that instance runs. It is ~180KB and grows, so it is parsed once per
change and cached against modification time and size.

A sample without `fh` is what Claude writes for an account with no five-hour
limit, but one alone does not prove it: a limited account goes on writing `fh`,
`fh: 0` when idle, and a single weekly-only sample can sit between complete
ones. `hasNoFiveHourLimit` therefore wants the newest sample without `fh`, the
one before it on the same organization, if there is one, without it as well, and no complete
sample of that organization inside the last five hours. Otherwise the newest
complete sample of that organization is the figure, and gaps neither read as
0% nor move the five-hour reset. Both kinds of account are in use, so neither
reading may become the default for the other. In November 2026 this machine's
own histories held no sample without `fh`.

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
refresh cannot skip.
