# Relay lifecycle and identity

[Back to shared-usage.md](../shared-usage.md)

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

**Graft going takes the lines with it.** Only a Graft pass took the lines out
of Claude's own profile, which has no launcher, so trashing Graft, `brew
uninstall --zap`, or going back to 1.1.8 — whose launchers know nothing of the
journal, and whose bundle carries no relay — left every borrowing Claude
holding them. Nothing broke until the next logout, since the relay outlives
Graft; after it, nothing listened on 47823 and every Claude Code request from
those Claudes failed to connect, with nothing pointing at Graft. Two things now
put them back, neither needing the key, since the journal already holds each
profile's finished value without them. `graft-launch --stop-sharing` swaps it in
for every closed profile under the launch lock and marks every entry unwanted,
so a launcher left behind puts nothing back; it is what the cask's uninstall
step is meant to run, after stopping Graft with a `TERM` (a quit event only
hides it), since a Graft still running sets the lines again on its next pass.
And the relay looks for its own file once a minute. Missing at two looks running
— an update swapping the bundle is back well inside a minute — it stops lending,
does the same swap for each closed profile as it finds them closed, and leaves
once none still holds the lines. It keeps the last journal it read, because a
zap takes the file with it while the Claudes it describes may still be open,
and writes nothing into a folder a zap has removed, the launch lock and the log
included. What neither can reach: lines copied into a folder's scoped values,
and a field changed since Graft last read it, both of which need the key to
read; the relay counts such a profile as still holding and stays up for it
until the logout. A development build cleared by `./build.sh` for over a minute
looks the same to its relay, which costs sharing until Graft's next pass. The
cask lives in its own repository and `release.sh` only changes its version and
checksum, so the uninstall step and the zap list are the tap's to carry.

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
one it read when it started, and only by the relay that has taken `relay.lock`.
It used to be made first and the lock taken after, so a second relay started
while the first was slow to answer could renew both files inside the renewal
window and then leave on the lock, with the running relay still presenting the
old certificate to every session that had since read the new authority. A
renewal still cuts off the sessions already running, which trust only the old
authority, until they start again; it comes about two years after a machine
first shares, at a login or after one of Graft's updates, which install
themselves at any hour. Trusting both authorities across the switch would
avoid that and has not been built.

A `CONNECT` for that name is opened only for Claude and Claude Code. Through
2.2.1 every one was, whoever sent it, and a program Claude Code starts that is
not Node inherits `HTTPS_PROXY` without the authority, so a `curl` or a Python
client calling the service from inside a session failed its certificate check
where it would have passed straight through. Telling such a program apart
inside TLS would mean presenting a certificate first, so the relay looks at
who holds the other end of the loopback connection instead (`PoolPeer`): the
process whose socket has the same two ports the other way round, and the
executable of each of its parents. Claude Code is started by Claude's own
`Contents/Helpers/disclaimer`, so its lineage meets Claude's bundle one step
up. A program it started meets the bundle two or more steps up and is passed
through as bytes, logged as `relay left unopened`, unless it is the same
executable as the one just below the bundle, which is Claude Code starting
itself. A connection nobody can be found holding, or one not traced to Claude
at all, is opened as before. Measured on the Mac Mini on 26 November 2026
against the real bundle path: `curl` run by a Code session reached the service
and got its 404 with no authority, and `curl` started directly below the
helper, where Claude Code sits, was still opened.

The connection to the service carries TCP keepalive, a probe after a minute
idle and four more a quarter minute apart. A Mac that slept part-way through
an answer heard nothing more on that connection, and a wait that never ends
kept an old relay from ever stepping aside for its successor.

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
reason in [usage-and-credentials.md](../usage-and-credentials.md). Measured on this
machine: the Code entries had ten and thirty days left, and the inference-only
entry a lender falls back to after that had nearly a year. Whether the service
takes Claude Code's requests on that last one has not been seen; a refusal is a
refused login and the request goes back to its own account, so being wrong
costs a retry.

**The same allowance is a login in one organisation.** One address can belong
to two organisations, each with limits of its own, and through 2.2.1 the
account uuid alone named an allowance, so two such Claudes were counted as one
and one of them never lent. Now the name is the login and the organisation
(`Allowance`): the bare uuid as before, and `uuid/organisation` only once two
organisations have been seen under one login, so a machine with one keeps the
names its files already hold. An organisation not known matches any. It is read
from three places: the key of Claude's token cache entry,
`acct:<account>|<client>:<organisation>:<host>:<scope>`, the `acct:` part only
in newer caches; the organisation of the profile's newest usage sample, which
says which one it last ran under; and `organization.uuid` in a pane sign-in's
answer. A profile holding logins for two organisations lends the one in use
(`UsagePoolController.lent`). An account signed in from the pane before this
has no organisation stored and matches either until it is signed in again. The
relay takes the name as given and compares nothing else.

A profile's token cache is read out of its `config.json`, which Claude replaces
by renaming a new file over it, and a pass landing in between reads a file that
will not parse. Read as no login, that handed the relay the member with nothing
to recognise it by for a pass; now it is a login that cannot be read, and with
the key in hand `UsagePoolController.steady` lends the last one read whole in
its place, for ten minutes at most, after which an unreadable file is no rename
and the token held may be one Claude has since replaced.

An account signed in from the pane lends the one login it has: a token asked
for with a year's lifetime, through the same authorization Claude Code's own
long-lived token comes from, and sealed with Claude's safe-storage key into
`ClaudeGraft/pool/accounts.json`. It is asked for as `user:inference
user:profile`, the second being what the usage endpoint wants. Whether the
service grants that pair for a year has not been seen, so a sign-in that is
refused at the exchange, answers `invalid_scope`, or is granted for under
thirty days is sent round again in the same tab for `user:inference` alone
(`askNarrowly`), and the account lends without figures of its own until
something answers for it. Signing in again from the row or the accounts page
(`renew`) must come back as the same account, and replaces the login alone: the
id, name, colour and the Claudes drawing on it stay. Graft tells the relay what
it knows of such an account in `PoolMember.limits`, where before it sent nil;
the relay already keeps the newest of what it is told and what it hears. The answer
carries a refresh token as well, and it is never read: renewing would be a
second kind of login to look after, a year is long enough to sign in again, and
the suite fails if `AccountSignIn` so much as names it. The browser hands its code to
a listener on `127.0.0.1` alone, checked against a `state` of its own and a PKCE
verifier.
