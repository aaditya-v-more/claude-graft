# Shared usage

How one Claude's Claude Code draws on other accounts' allowance: who lends to whom, how Claude Code is reached, the relay that carries the requests, which account answers and what the dropdown lists. The code calls it the usage pool. Read this before changing `UsagePool`, `PoolEnvironment`, `ClaudeCodeEnvironment`, `AccountSignIn`, `UsagePoolController`, `SharedUsageSection` or anything under `Sources/Relay`.

## What moves and what stays

One Claude stays the identity and the accounts added to it answer its Claude
Code requests. Everything a conversation owns — the chat, its artifacts,
connectors, Claude in Chrome, Remote Control — is reached through calls that
keep the sender's own token, and only `POST /v1/messages`, the request the model
answers, goes out under another account's login. That split is the whole
design, and it is why nothing about the identity changes. Measured before a line
was written: Claude's Code listed and read its own artifacts while Claude 2's
organization answered its messages, the artifact calls carrying Claude's token
untouched. The Chat tab cannot be shared at all; its messages go to claude.ai
and the conversation lives with the account that sent it.

Claude in Chrome and Remote Control were checked against that split rather than
assumed into it. Chrome's tools never reach the relay at all: Chrome starts
Claude's own native host, which listens on a socket under
`/tmp/claude-mcp-browser-bridge-<user>/`, and Claude Code talks to that socket
directly. What does go through is the model turns that decide to click and
read, and those are ordinary messages, shared like any other — screenshots ride
in them inline as base64, never as an uploaded `file_id` (none in 103
transcripts on this machine), so nothing pins them home. The extension's own
side panel is a chat of its own and is never shared. Remote Control's bridge,
its event stream and any socket it opens are not model requests, so they leave
under the sender's token exactly as written; the model requests of a bridged
session are, and they move, which matters because nearly every Code-tab
session is bridged and leaving them out would leave nothing shared. Nothing
about Remote Control needs syncing: a session belongs to the account named by
`ownerAccountUuid` on its own record, which no answering account changes and
the relay never writes.

**Lending belongs to the Claude that borrows.** There is no pool to join and no
switch to turn it on: each Claude has its own list of accounts it may draw on,
in `ClaudeGraft/usage-sharing.json`, and an account lends only to the Claudes
that name it. A lender is another Claude Graft knows, or an account signed in
from the pane, which makes no shortcut and no profile. The first version needed
a shortcut per account and a pool they all joined, which is a second Claude to
keep signed in for nothing when what was wanted is one Claude with more
allowance behind it. An account that is also one of the Claudes is kept and
named beside it, since the two are one allowance, and the relay counts one
account once whichever way it arrived: under the first of its names that can
lend, because a Claude's own login lapses while that Claude stays closed and one
signed in from the pane lasts a year. A conversation moved to the other name
goes to the one counted, and the dropdown offers only that one. The borrowing
Claude's own account is refused at sign-in, and marked as adding nothing if it
arrives as a Claude.

An account signed in from the pane still belongs to the Claudes that draw on
it, and its login still goes when the last of them lets go. What it gained is a
place of its own to be seen: the Accounts page lists each with its name and
colour, its figures, who draws on it and when its login runs out, and offers
Start Session, Sign In Again and Remove, which takes it out of every Claude at
once. The page is always in the sidebar now, since it also holds the order the
accounts are used in and each one's Auto-start switch, Claudes included. The dropdown and the bar's tooltip
show the ones no Claude here is signed in to, under the Claudes; one that is
also a Claude is that Claude's row already.

**The plan terms are said once, behind a symbol.** Anthropic's terms for Claude
plans may not allow one person to combine subscriptions this way. That sentence
closed the heading's explanation until 25 November 2026, was taken out, and came
back three days later at the owner's asking as a triangle beside the ⓘ, opening
the sentence alone: what it is, with nothing added about consequences. It is
left out of What's New and the release notes.

## Reaching Claude Code

**Claude Code is reached through a proxy because nothing else gets in.** Desktop
starts its Code process from its own environment and then overwrites
`ANTHROPIC_BASE_URL`, `CLAUDE_CODE_OAUTH_TOKEN` and `CLAUDE_CODE_ENTRYPOINT`;
its environment editor refuses all three outright, and an `env` block in
`settings.json` loses to Desktop as well. `HTTPS_PROXY` and
`NODE_EXTRA_CA_CERTS` survive — Desktop adds a proxy of its own only where none
is set. An address would have been the worse route anyway: Claude Code switches
off tool search, Remote Control and its prompt cache features the moment its
address is not `api.anthropic.com`, and through a proxy the address never
changes. Managed settings can replace the proxy variables, which leaves that
Claude unshared rather than broken. The port is fixed at 47823 because a session
reads it once, as it starts, and a port that moved would leave every session
already running pointing at nothing.

**Graft writes those two lines, and only into a closed Claude.** They live in
`<profile>/ccd-environment-config.json`, whose `envVars` is the base64 of
Electron's safe storage over a JSON object of names and values — the key and
scheme `config.json`'s token cache uses — and is what the Code tab's own
environment editor shows and saves. A running Claude decrypts the field once,
keeps the result, and saves the whole of it back from that editor, so a write
landing while it is open is never read and is then written over. Every write is
made with the profile closed, under `SidebarSync.withLaunchLock`, the lock every
route that opens a Claude takes, by renaming a complete file over the old one
the way Claude's own store does. A `.json.journal` beside the file is that store
part way through a write, and nothing is decided on it.

Encrypting needs Claude's keychain item, and a launcher asking for it would put
a keychain dialog in front of every shortcut. So Graft reads and encrypts, and
leaves two finished values per profile in `ClaudeGraft/pool/environment.json`:
the field as it stands without the lines and the same with them. A launcher
puts one in place of the other, and only while the file holds exactly the one
expected; anything else is an edit Graft has not read yet, and is left for
Graft to read. Claude's own profile has no launcher when it is opened from the
Dock, so Graft settles it whenever it finds that Claude closed, the moment it
quits included.

The lines go in only where they take nothing away. A proxy or a certificate a
person set themselves is reported and left alone, in `envVars` or in
`scopedEnvVars`, whose values are laid over the plain ones as a session starts
and would win without a word; names are matched without regard to case, since a
lower-case proxy variable is read as well. Claude also keeps, under
`:machine:<scope>` in that same field, what a folder's own environment inherited
from the profile-wide one, and lays that over `envVars` too — so a copy of the
lines made there would outlive their removal and keep Claude Code pointed at a
relay that may have stopped, out of reach of the one field a launcher swaps.
Graft takes exact copies of the lines out of the scoped values whenever it finds
the profile closed, leaving `envVars` to decide alone, and until then a copy
counts as holding the lines and keeps the relay up.

A line stays Graft's after the person sets something beside it. Any name
holding exactly the relay's proxy or Graft's certificate is Graft's, whatever
else the environment says, and the profile counts as holding the lines while
one of them is left. Until 2.0.1 a value of the person's own under either name
made the whole reading a conflict before anything else was looked at: point
`NODE_EXTRA_CA_CERTS` at a bundle of your own after the lines went in, and
Graft's `HTTPS_PROXY` stayed for good, Claude Code dialling the relay without
trusting it while the pane said only "Not shared"; and with lenders removed the
profile read as holding nothing, its journal entry went, and the relay could be
told to quit while that Claude still pointed at it. Now the lines never go in
beside a value of the person's own, the one of Graft's that is left comes out at
the next close, and the pane keeps naming the conflict. A field holding only
part of the lines — a proxy whose certificate line was deleted by hand — matches
neither finished value, so no launcher can swap it; Graft, which has just read
that very value, writes the field without Graft's lines first, and then swaps
as usual. A launcher that can neither
reach nor start the relay puts the plain value back before opening, because a
Claude pointed at a proxy that is not there has no Claude Code at all, and
losing shared usage for one launch is the cheaper mistake. Removing the last
account takes the lines out at the next close, and the relay stays up until no
profile holds them. `PoolEnvironment.swift` is the only source that names the
file, and the suite fails if another does.

## Topic guides

- <a id="the-relay"></a><a id="who-is-asking-and-what-is-lent"></a>[Relay lifecycle and identity](shared-usage-guide/relay-lifecycle-and-identity.md)
- <a id="which-account-answers"></a>[Account selection](shared-usage-guide/account-selection.md)
- <a id="the-dropdown"></a>[Dropdown and foreground tracking](shared-usage-guide/dropdown-and-foreground-tracking.md)
- <a id="testing-it"></a><a id="artifact-tool-arguments-are-not-inference-containers"></a>[Archived chats, menus and testing](shared-usage-guide/archived-chats-menus-and-testing.md)
