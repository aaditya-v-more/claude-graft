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
counts as holding the lines and keeps the relay up. A launcher that can neither
reach nor start the relay puts the plain value back before opening, because a
Claude pointed at a proxy that is not there has no Claude Code at all, and
losing shared usage for one launch is the cheaper mistake. Removing the last
account takes the lines out at the next close, and the relay stays up until no
profile holds them. `PoolEnvironment.swift` is the only source that names the
file, and the suite fails if another does.

## The relay

**The relay is a process of its own.** Every request a connected Claude Code
makes goes through it, so it cannot stop when Graft does: an update relaunches
Graft, and a relay inside it would cut every streaming answer on the way past.
`graft-relay` is started detached with a bare environment, keeps one instance by
`flock` on `relay.lock`, and is told everything over a socket only this user can
open — `0600`, and `getpeereid` asked as well. It holds the logins it is lent in
memory alone, so a relay that restarts knows nothing until Graft configures it
again and passes every request through untouched meanwhile: unshared, never
broken. Graft replaces a relay built from a different binary by asking it to
step aside, and the relay stops listening before it answers, so the replacement
can bind the port at once, then leaves when its last exchange ends, fifteen
minutes at most. What does break a connected Claude is no relay at all, since
the proxy it was told to use refuses the connection. That is why the pane asks
for Open at Login, why a launcher that finds no relay opens without the lines,
and why the lines come out before the relay goes.

**A relay is replaced only by a Graft that can tell its successor
everything.** The logins it lends are sealed with Claude's safe-storage key,
and every update of an ad-hoc signed Graft arrives under a code hash the
keychain item has never seen, so the first silent read after it fails. 2.0.0
handled that by replacing the old relay with its own binary all the same and
configuring the new one with what it had: members with no token and nothing to
recognise them by. Confirmed live on the update to 2.0.1: the new relay
recorded no conversations and lent nothing until Refresh Usage was pressed and
the keychain allowed. So while the key is shut, `deliver` sends no
configuration at all — a bare status request instead — and keeps whatever relay
is there, older binary or not, with the logins it was last given; with no relay
running it still starts one, because no relay breaks a connected Claude, and
leaves it unconfigured, passing everything through, until a pass can tell it
more. `relayStep` is the rule. The dropdown says "Lending paused · open the
keychain" meanwhile, and that button is a person asking, so it is allowed to
raise the dialog a pass never may. A key that is missing rather than shut is a
Mac with no logins, and is delivered as before.

A relay whose status will not decode is a relay all the same. A later release
that adds a required field or drops one would answer in a shape this one cannot
read, and 2.0.0 asked a relay to quit only when its status decoded, so it left
the old one serving, started another every twenty seconds that exited on the
lock, and with the relay counted as not ready took the lines out of every
borrowing Claude found closed. Now an unreadable answer is replaced like any
other build, by a bare `{"quit":true}`, which every relay since 2.0.0 reads.

**The authority it presents can vouch for one name.** A `CONNECT` for
`api.anthropic.com:443` is taken apart by an inner TLS listener; every other is
passed through as bytes. The certificate comes from an authority Graft makes
with a critical `nameConstraints` permitting `api.anthropic.com` alone, and the
authority's key is deleted the moment the certificate is signed, so what Node
is told to trust cannot be used for any other site, by anybody. The identity is
imported with `kSecImportToMemoryOnly`, because without it the import writes the
private key into the login keychain, which is somebody else's to fill — and that
flag is macOS 15's, which is why sharing needs 15 while the app needs 13. Node
takes a name-constrained authority through `NODE_EXTRA_CA_CERTS` alone: measured
with Desktop's Claude Code 2.1.317 and no `NODE_USE_SYSTEM_CA`. A new authority
is only ever made as a relay starts, since a session already running trusts the
one it read when it started.

**What cannot change accounts goes out as it came.** Only a model request from
a member is ever taken apart and written again; everything else is sent byte
for byte, head included. The first version rebuilt every request, and
rebuilding drops `Connection` and `Upgrade` as one hop's fields — which is
correct for the request it rewrites and fatal to a WebSocket, whose upgrade
then never reaches the service. So an upgrade gets a connection of its own and,
once the `101` is back, the two are spliced and the relay stops reading HTTP on
them. A chunked body is not held either: a stream may send its next piece only
after reading the answer to the last, and holding the whole body before
sending any would deadlock it, so it is forwarded as it arrives while the
answer comes back. A body with a length is still read first, because that is
what lets a kept connection the service had dropped be tried again. An
upgraded connection counts as in flight, so a relay stepping aside holds such a
socket open until it ends or the quarter hour is up, and then the client
reconnects to the new relay as it would after any dropped network.

## Who is asking, and what is lent

**A sender is recognised by its token, and lends the one Desktop gave Claude
Code.** Desktop hands each Code process one of the entries in its profile's
token cache, so a request is traced to its profile by the SHA-256 of its bearer
against every entry that profile holds; a request no member owns passes through
as it came. What a member lends is its current `user:sessions:claude_code`
entry, else a current `user:inference` one, and never the refresh token, for the
reason in [usage-and-credentials.md](usage-and-credentials.md). Measured on this
machine: the Code entries had ten and thirty days left, and the inference-only
entry a lender falls back to after that had nearly a year. Whether the service
takes Claude Code's requests on that last one has not been seen; a refusal is a
refused login and the request goes back to its own account, so being wrong
costs a retry.

An account signed in from the pane lends the one login it has: a
`user:inference` token asked for with a year's lifetime, through the same
authorization Claude Code's own long-lived token comes from, and sealed with
Claude's safe-storage key into `ClaudeGraft/pool/accounts.json`. The answer
carries a refresh token as well, and it is never read: renewing would be a
second kind of login to look after, a year is long enough to sign in again, and
the suite fails if `AccountSignIn` so much as names it. The browser hands its code to
a listener on `127.0.0.1` alone, checked against a `state` of its own and a PKCE
verifier.

## Which account answers

**Only the sender and the accounts it names are asked,** one account counted
once. An account has room while its login has not been refused, the service has
not said no, its five-hour window is under 98% and its week under 99%, a window
whose reset has passed counting as empty. Those with room go in order of the
soonest weekly reset, an unknown reset after every known one, and a tie to the
sender, since staying costs nothing. The sender's own account takes its place in
that order while it has room and goes last when it has none; the request then
goes out as Claude sent it and the service decides.

**A conversation stays while its cache lives,** unless it is held longer for its
reasoning, below. Keyed by
`X-Claude-Code-Session-Id`, it stays with the account that last answered it for
as long as its prompt cache lives there — five minutes from its last turn, or an
hour when the body asks for `"ttl":"1h"` — because moving it inside that time
pays for the whole context again, and moving it after costs nothing. It used to
stay an hour regardless, which held a conversation on an account long after
there was anything left to lose by moving it. A choice made in the dropdown goes
in front of both while that account has room, and is dropped once the
conversation has been quiet for a day. The relay learns limits from the
`anthropic-ratelimit-unified-*` headers on its own answers — fractions, resets
in Unix seconds — and Graft pushes what the usage endpoint said with every
configuration, so an account not yet asked is not a blank.

**A conversation carrying reasoning stays where the reasoning was written.**
Every request in a conversation carries back the reasoning the model wrote on
earlier turns, and for the models whose reasoning is bound to the account that
wrote it, the service honours those blocks only from that account. From any
other, in both directions, they are dropped without a word: the answer is still
a 200, and nothing reaches Claude Code. Measured by replaying unedited
conversations from both accounts on this machine and counting what the service
put back — input and cache tokens of the real request against `count_tokens` of
the same body with the reasoning taken out: kept on the writing account every
time, dropped on the other twelve times in twelve, and dropped again through
Desktop's own Claude Code resuming the transcript under the other profile. The
binding follows the token, not the body — rewriting `metadata.user_id` to the
borrower's own, or sending none, still drops it — and it follows the block, not
the model reading it, so nothing the relay could edit would save it. A model
measured keeping its reasoning across the move is listed in
`PoolRequest.reasoningTravels`; any other is taken to be bound, since holding one
needlessly costs a little sharing and moving one costs its reasoning.

So a request whose messages hold `thinking` or `redacted_thinking` blocks stays
with the account that last answered that conversation, past its cache and
whatever the ranking says, and moves only when that account has no room — then
stays where it went, so the reasoning is lost once rather than on every turn.
Losing it once costs a few percent more output at low effort and a fifth more
at the highest; losing it every turn costs up to two thirds. A conversation the
relay has not seen answered, from before it last started or with no session id,
stays on the sender's account, where it began. A request with no reasoning in it
has nothing to lose — a new conversation, or one just compacted — and is routed
like any other, which is where sharing still spreads the load. A choice from the
dropdown still moves a held conversation: a person asking is the one move, made
on purpose, and the conversation then stays on that account. The relay does not
add the header that would make the service report a drop, since nothing that
cannot change accounts is altered on the way.

**A conversation kept as a thread stays where the thread is.** Claude Code
2.1.317 keeps each conversation as a thread on the service: the first request
says `"thread":{"type":"create"}` and carries everything, and each one after
says `"thread":{"type":"continue","previous_message_id":…}` and carries only
the turns since. The rest is stored with the account that answered, and any
other account answers 404 `thread_not_found`; Claude Code takes that as the
thread having lapsed and sends the whole conversation again as a new one. The
first live run on two accounts found every moved turn refused this way — the
relay took the 404 for a plan that lacked something and sent the request back
where it came from, so a move from the dropdown never stuck, while a lapsed
thread on the sender's own account was recovered as designed. So a continuation
is held like reasoning, whatever the model, and when the dropdown or a full
account does move one, a `thread_not_found` from where it went is handed to
Claude Code rather than sent home: the new thread that follows is created on
the account the conversation moved to, and stays there. Telling that refusal
from any other 404 means reading it, and the service compresses even its
errors for a client that accepts it — a plain 401 comes back as brotli — so the
second live run's moved turns were sent home all the same. A continued thread
going to a borrowed account now asks for `Accept-Encoding: identity`; nothing
else about the answer changes, and a refusal still compressed is treated as
before. A refusal read and found to be something else leaves its error type on
the `relay passed over` line.

Only the conversation's own turns carry a thread. Claude Code asks for a title
and the like under the same session id with none, often of a smaller model,
and those were overwriting which account the relay remembered for the
conversation — the dropdown named the account that answered the title, and
the next turn was steered there to be refused. Once a session has been seen
with a thread, only an answer to a request with one says where it is.

What comes back decides what happens next, from the status line alone. A 429
marks the account full until the reset it names and the request goes on to the
next; a 401 on a borrowed login marks that login refused until Graft hands over
another; a 400, 403 or 404 on a borrowed login sends the request home, since
something in it belongs to the sender or the plan lacks it — except a 404 that
says `thread_not_found` to a continued thread, above, which is read and
delivered; anything else is delivered. Measured: with Claude 2's login swapped for a bogus one, the first
request was refused, retried on Claude's own account, and Claude Code saw only
the answer.

**Some requests never move.** A body naming `"file_id":`, `"container":` or
`"mcp_servers":` refers to an uploaded file, a code execution container or a
connector Anthropic runs, and each belongs to one organization; another account
asked for it answers that it does not exist. A body whose `Content-Encoding`
is not identity cannot be read to find out, so it stays too. Keys are matched
with their closing quote and colon, which JSON never escapes, so the same word
in a prompt does not pin anything — and with the start of a value that makes
them a reference (`"file_id":"`, `"container":"` or `"container":{"id":"`,
`"mcp_servers":[{`), because a connector's tool schema can have a property of
the same name. One does on this machine, a `"container":{"type":"object",…}`,
and every request listing the tools carried it: the third live run found that
pinned each full request to its sender, so the new thread Claude Code sends
after `thread_not_found` went home and a move from the dropdown never landed,
while the short continuations that leave the tools to the thread moved freely.

## The dropdown

**The dropdown lists what the relay carried.** The relay keeps the
conversations it has seen by `X-Claude-Code-Session-Id`, which is the
`cliSessionId` a record names, so a title comes from that record or, before one
exists, from the title line in the transcript. Listed are conversations from a
Claude with something to borrow, active in the last half hour and not put away,
the Claude in front first and five at most.

**Which Claude is in front is `FrontmostClaude`'s answer,** the one the status
item draws. Matching a Claude to its profile costs a `ps`, and the list used to
run its own on every activation beside the one the status item was already
paying for. Its answer is also the right one for a dropdown: Graft coming
forward leaves it where it was, so opening the dropdown keeps the Claude the
person was just using at the top.

**The conversation on screen runs across the menu bar** for as long as it is
there: its title and the account answering it, and a right click on the item
moves it with the same choices as its row. Until
2.0.1 it was only a conversation that had just begun, for its first two
minutes, and a person who switched chats saw the bar go on naming one no longer
in front, or "New session" for a test chat begun minutes earlier.

Which conversation is on screen is Claude's word alone. It writes
`lastFocusedAt`, in milliseconds, into a conversation's record the moment its
window switches to it, so the bar names the conversation the front Claude's
store focused last. One the relay has not carried — an old chat opened to read,
which has sent nothing since sharing began — is named by its record's title
alone, with nothing lit and no account beside it, since none has answered it.
The first 2.0.1 candidate named nothing there, and on a machine whose chats
mostly began before 2.0 the text went whenever one of them was opened. Keeping
the conversation before instead would have named the one thing certain not to
be on screen.
One the relay did carry is left to its row, which an archived or deleted one
has already left; named from its record, it would be back on the bar. A Claude
whose records say nothing of focus falls back on the
conversation typed into last, `promptedAt`: a turn on a conversation's thread
whose last user message is not a tool's answer. A lone message with no thread
does not count, because the title Claude Code asks for beside a conversation is
exactly that, and a first message is marked by `startedAt` anyway, set only
when the first request the relay sees for a session carries no answer — a relay
restarted part way through sees every conversation for the first time. Each
change of conversation is logged as `menu.chat`, with the first eight
characters of its session, whether an account was chosen for it and whether
the relay carried it, so a run can be checked against what was on screen.

A new chat is the one wait the bar cannot shorten. Claude's first record of it
lands about three seconds after its first message and already carries its
title, so there is no "New chat" stage to show, and until it lands the bar goes
on naming the chat before. The relay sees that first message at once, but a
conversation beginning is not always the one on screen: a scheduled task, or a
session another one started, begins the same way, and only the records say
which conversation the window shows. The second live run timed it: each new
chat's name was on the bar within a tenth of a second of Claude's first record.

**A focus belongs to the store it turned up in first.** Claudes that share
chats each keep a mirrored folder, and the mirror copies a changed record
across with its `lastFocusedAt`. Read as the second store's own, the copy made
the conversation on one Claude's screen the newest focus in the other's, and
once old chats were named from their records, the other Claude's bar would
have named it too. So each focus, a session and the moment it was focused, is
held against the stores that had it when it was first seen, and a Claude's
focus is the newest of its own. On the first pass after Graft starts every
store holding a focus owns it, as every store did before; a copy made after
that is told apart. The mirror can also write the other Claude's copy of a
record over this one's, and an older focus with it. The focus this store had
then stays the word, as for a deleted record, since this Claude still shows
that conversation.

The text is drawn into a picture of fixed width and scrolled inside it, because
text scrolled a character at a time changes width with every letter, and a
status item that changes width moves every item to its left with it. Following
the screen costs a look at each record's modification date while anything is
shared, with a record parsed again only when it changed and a missing
transcript looked for once a minute; a full pass reads logins as well, which is
why that one waits half a minute. The look ran every two seconds, and a switch
took up to four to reach the bar: Claude writes the record about a second after
its window changes conversation, and the look could come two seconds after
that. `StoreWatch` now has FSEvents say when a store changes, a tenth of a
second after it does, which leaves Claude's own second as the wait. It leaves
out Graft's own writes, since the sweep files records and the mirror copies
them and neither says what is on screen; the suite wakes it with `touch` for
that reason. A look woken while another is under way runs as soon as that one
ends, and the two-second look stays for the relay's news, which no file
carries. The timer, not a look, decides which stores are watched, because the
watch itself wakes a look and must not be torn down from inside its own call.

**A conversation put away leaves the bar and the dropdown.** Archived means
every record its own Claude keeps for it says `isArchived`. A copy in a Claude
that shares the chats is not asked: the mirror carries a change across only on
its next pass, and read first, a copy kept a deleted conversation on the bar.
Deleted is a `deleted_` marker naming the session, which Claude writes only for
a record Graft filed, since those are named for their session; or a record seen
before and missing on two passes running, since one pass can land while Claude
rewrites the file. A store that will not open hides nothing, and the walk skips
stashes as the sweep does, or a deleted conversation's old record there would
keep it listed. Deleting the conversation on screen takes the latest focus with
its record, so the focus it had stays the word until Claude writes a newer one;
before that, the bar named the conversation focused before it. Once that record
has been missing on two passes running, the conversation is taken as deleted
and the bar names nothing, or one from before sharing would stay there by its
title. One pass is not enough, because the watch can wake a pass before
Claude's write has put the new file in place. A conversation
deleted while Graft was not running was never seen with a record, and the
dropdown goes on listing it for the rest of its half hour.

**The account menu marks the choice in force.** It is an inline `Picker`
inside the row's `Menu`, which draws macOS's own checkmark; a `Label` with a
`checkmark` image in a plain `Menu` loses the image. Automatic names the account
the ranking settled on, "Automatic · now Claude 2", and the row reads
"Auto · Claude 2" against a chosen account's bare name, because a conversation
answered by Claude 2 looked the same whether someone had chosen Claude 2 or the
ranking had. The right click's menu says the same.

The text gives way when the bar has no room for it. The wider item is not
refused by macOS but put past the edge of the screen, still reporting itself
visible, and on the development machine's MacBook Air, with a full bar beside
the notch, Graft's item vanished for the two minutes the text ran, refused a
right click as offscreen, and came back when the text went. Half a second after
the text goes in or grows, `keepReachable` asks `isShowing`; if the item was
reachable before and is not now, the text comes out for that conversation and
`menu.ticker withdrawn` is logged. The words stay in the item's spoken title
and the right click still moves the conversation, because those need no room.
The next run showed where the item had gone. It wasn't past the edge but
behind the notch: with Claude 2's icon in the bar too, the text widened Graft's
item to x 866–1054, and this MacBook Air's notch covers 751–959. Every
edge-of-screen test said yes, so `MenuBarPlacement` now treats any overlap with
a notch (the gap between `auxiliaryTopLeftArea` and `auxiliaryTopRightArea`) as
out of reach. That run's screenshots show no Graft item at all, even before the
text. The capture tool filters out apps it wasn't granted, so a check of the bar
needs `screencapture` instead.

The run after the notch fix saw the text come out about half a second after it
went in, and the item stay at x 1025, clear of the notch. A real right click
opened the account menu and moved the conversation. But the menu had been popped
up at a point inside the status button, which is not flipped, so the point lay
above the bar. The menu opened with its top above the screen and the
conversation's title behind a scroll arrow. It is now set as the item's menu for
that one click, so macOS places it as it places any menu-bar menu.

The relay is asked every three seconds while the dropdown is open and every
thirty otherwise, over its socket and never the service. `MenuBarController`
starts that watch as the popover shows and ends it in `popoverDidClose`,
because a view inside a popover is not reliably told it has gone, and a watch
nobody ends keeps the fast timer running for good.

## Testing it

`./test-relay.sh` builds the relay and drives it over loopback against a
stand-in for the service: which account each request reached, lending per
Claude, choices from the dropdown, the hold for as long as a cache lives, one
account counted once, and one relay handing over to the next. The suite drives
the environment, the swap a launcher makes and every refusal over sealed files
in a throwaway directory. The layout suite draws the pane and the dropdown's
conversations from fixtures through `UsagePoolController.attach`, which hands
over the store and the usage monitor and starts nothing — no relay, no timer, no
file. Rows are identified by what stays put under them, a lender by its profile
folder or account id and a conversation by its session id, since a shortcut can
be renamed and a conversation retitled while a script is still asking for it.

What has run against real accounts, and what is still only a plan, is in
[diagnostics-and-verification.md](diagnostics-and-verification.md).
