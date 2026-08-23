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

    ./build.sh                 app + launcher into build/
    ./test.sh                  169 checks, all in a throwaway directory
    ./release.sh [--install]   tests, builds, draws the icon, signs, packages

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

**Credentials are borrowed, never taken.** Only the access token is decrypted,
never the refresh token: Anthropic rotates those and using one signs Claude
Desktop out. Nothing is written back to Claude's config, cookies or keychain.
The token goes to `api.anthropic.com` and nowhere else. Background polls read
the keychain non-interactively and fall back quietly; only an action the user
took may raise the macOS prompt.

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

The thirty-second timer reads local files and checks what is running. The API is
asked once per profile per five minutes. Failures back off 60/120/300/900/1800
seconds and a success clears the count; a `Retry-After` from the service wins
and is the one wait a person pressing refresh cannot skip.

## Verification state

Working and checked end to end: shortcut creation, adopting an existing profile,
cross-account chat mapping, live usage for both accounts, memory steady around
106MB at zero idle CPU.

Unproven: **Start Session** has never successfully opened a window. It POSTs one
Haiku message with `max_tokens: 4` and Claude Code's system prompt, which these
tokens are minted for. If that turns out to be wrong the API's own error message
surfaces in the UI rather than being swallowed — read it rather than guessing.

`~/Library/Application Support/ClaudeGraft/usage-status.json` records what each
poll found, including whether the figures came from the API. Read it to check
the state of live usage instead of reading numbers off the screen.
