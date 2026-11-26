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
own, never one of Claude's. claude.ai is the third, for [artifacts](artifacts.md) alone: it sees a
sign-in made in Graft's own window, which stays in that window's web store and
is never read out of it.

**A borrowed token is never written down on the way out.** Up to 2.0.0 the
usage request and Start Session went through `URLSession.shared`, whose
`URLCache` stores the request it answered beside the answer, `Authorization`
header included, in `~/Library/Caches/graft.claude-graft/Cache.db`. The review
before 2.0.1 found one `api.anthropic.com` exchange there with a bearer token in
it, readable by any process running as the person without the keychain being
asked. Both now go through `UsageAPI.session`, ephemeral and with no cache at
all, the way `AccountSignIn` and `ClaudeDesktopUpdater` already did; every
launch empties `URLCache.shared` so what an older version left there goes, and
the suite fails if anything under `Sources/` names `URLSession.shared` again.

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

**A session starts on a press, or for an account somebody switched on.**
`SessionStarter.start` defaults to non-interactive, so a call from anywhere
unaudited fails quietly instead of raising a keychain prompt, and it claims the
profile for the length of the call so the window and the dropdown cannot both
start the same account. The suite reads `Sources/App/*.swift` and fails if a
caller appears outside the three buttons and the pool, or if `startSession`
turns up on a line with `onAppear`, `onReceive` or a timer — a view refreshes
every thirty seconds, and a session wired into one would open a five-hour
window on every account, over and over.

The one start nobody presses is `AutoSessions`, asked for in issue 4: an
account with Auto-start on is sent one message each time its five-hour window
has closed, so the next window is already running down when somebody sits down
to work. What keeps that from being the loop above is one caller and one clock.
`SessionStarter.startUnasked` is named on a single line, in
`AutoSessions.consider`, which only that object's own minute timer and a switch
being turned on can reach; no view calls it. It raises no keychain dialog, and
the refresh after it asks nobody, so a locked keychain is a failure said beside
the switch and not a prompt at three in the morning.

Whether to send is `due`: nothing before the time the last attempt set, nothing
to an account with no five-hour limit or with its week used up, and otherwise
only once the window's reset has passed. An account nothing is known of is
asked, since its first answer says what it has. What an attempt sets is `next`:
the new window's reset after a success, or five hours on when the answer named
none; a quarter of an hour after a failure; the reset a 429 named, and never
sooner than that quarter hour; an hour after a 401 or 403, since a login the
service turned away stays turned away until a person renews it. A signed-in
account's refusal reaches `next` as `.refused`, not as the status, because
`start(account:)` has already put it into words. One account is
sent one message however many names it has here, and the attempt is recorded
against each. The switches and the last attempts are kept by the pool's keys in
`ClaudeGraft/automatic-sessions.json`, and a Claude deleted or an account
removed takes its switch with it. The suite holds the one caller, the absence
of `interactive: true` from that file, and `due` and `next` case by case.

An account signed in through Graft has no profile, so its button goes through
`UsagePoolController.startSession(account:)`, the one caller of
`SessionStarter.start(account:)`, and the suite holds that to the accounts page
and the dropdown. The answer's `anthropic-ratelimit-unified-*` headers are kept
(`heard`), because for a login the usage endpoint will not answer they are the
only figure there is.

**A signed-in account's figure has four sources, and the newest wins**
(`UsagePoolController.entry(forAccount:)`): the usage endpoint, asked by the
same pass and under the same cache and backoff as a profile, keyed by the
account's id, and only for a login carrying `user:profile`; a Claude signed in
to the same account; the account's claude.ai sign-in, described next; and what
the service said on that account's own answers, through the relay or on its
Start Session. A figure taken from an answer is
marked as such under the bars, never as a profile's own record, and keeps the
Fable figure from the last time the endpoint was asked, which no answer carries.

**A login the endpoint will not answer is asked about through claude.ai.** A
login made with `user:inference` alone, which is what a sign-in on 25 November
came back with, never reaches the usage endpoint, and the limit headers on its
answers name the five hours and the week but no model's own week, so its Fable
bar was simply missing. claude.ai's usage page reads
`/api/organizations/<org>/usage`, and the answer has the same windows and the
same `limits` list, so `UsageAPI.reading` reads it unchanged. Where the same
account is signed in under Artifacts, `WebUsageReader` asks that path from the
sign-in's own page (`ClaudeWebSession.send`), about the organization the login
is in or, for a login that names none, the one the sign-in is on
(`WebUsage.route`). It loads a page of claude.ai, so it is asked when the
dropdown or the Accounts page opens and when Refresh Usage is pressed, at most once in five minutes
or half a minute for a press, and never on a clock. An account with no sign-in
under Artifacts, or one that is also a Claude here, is not asked this way.

**A Claude's address is learned, not stored.** A profile's `config.json` names
its account by id alone. The address shown beside a Claude comes from an
account signed in here or under Artifacts with the same id, and otherwise from
`/api/oauth/profile`, asked once for each account since launch with the login
the usage pass was already holding (`UsageMonitor.learnWhose`). Nothing is
written down, and nothing is read from the keychain for it.

`config.json` holds `oauth:tokenCacheV2` in Chromium safe storage: keychain item
service `Claude Safe Storage`, account `Claude Key`, PBKDF2-HMAC-SHA1 with salt
`saltysalt` over 1003 iterations, AES-128-CBC, IV of sixteen spaces, `v10`
prefix.

That item's decrypt ACL names each trusted build by code hash, so an ad-hoc
signature earns a fresh entry every time the app is rebuilt — nine entries for
the same `build.noindex/Claude Graft.app` path were counted on the development machine,
alongside ten `cdhash:` entries in the item's partition list. A Developer ID
would be one `teamid:` entry that survives every version. Ad-hoc, the dialog
comes back at every build and every update, and at every launch for as long as
it is answered with the one-time Allow, which adds nothing to the list.

Releases stay as `release.sh` signs them. A local build is signed with the
Mac's Apple Development certificate when there is one
(`Tools/signing-identity.sh`, team `H4RJ5YGKZA` on the Mac Mini), whose
designated requirement names the certificate rather than the code, so "Always
Allow" once lets every later development build in. Seen on the Mac Mini on 11
November 2026: the first build signed this way asked once and was let in with
Always Allow, and a later build with a different code hash opened, read the key
and lent without a dialog. A rebuild of unchanged sources for one architecture
came out with the same hash, so it proves nothing either way; the universal
build is what changed it. The installed release goes on asking as before. Both a suppressed read
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

## Topic guides

- <a id="polling-budget"></a>[Polling budget](usage-and-credentials-guide/polling-budget.md)
