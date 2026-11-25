# Account switches and unfiled chats

[Back to chat-sharing.md](../chat-sharing.md)

**Chats an account switch left behind are copied, never moved.** Signing out of
one account and into another inside a single Claude leaves the first one's
history exactly where it was written — a store is keyed `<account>/<org>` and an
instance reads only the folder for the account it holds — so both histories sit
in the profile that did the switching, and the shortcut made for the second
account comes up with an empty sidebar while its own chats sit next door.
Nothing is broken and nothing is lost, which is why it needs saying out loud: an
empty sidebar is what a history that has gone looks like too, and the first
thing anybody reaches for is the chat source picker, which merges two
*accounts'* chats rather than recovering one account's and is the one thing here
that cannot be undone. `chatsElsewhere` names the profile holding them, the
count and the newest few titles, since none of the three says on its own whether
this is the history being missed; `adoptChats` brings them in.

Copies, and that is the whole of the rule. The profile they come from is one
this app did not make — `Claude` itself, in the case this exists for — and
nothing here takes a record out of a profile it does not own, so a wrong guess
costs some small JSON instead of a history and that profile still has them the
day the account is signed back into over there. Additive on this side too: a
name already present is left alone rather than written over, so a second run
copies nothing and a chat archived here since keeps the shape this profile gave
it.

Offered to a profile with a history of its own as much as to an empty one, and
the count is what this profile has not got rather than what the other one holds.
Copying takes nothing away, so counting everything over there would leave an
offer standing for ever afterwards saying the chats are elsewhere when they are
also right here — and the number it showed would never match the number that
arrived. Which of the two cases it is still has to be said, since recovering an
account's history and merging a second set in are not the same thing, so
`merging` carries it into the wording rather than into a decision about whether
to ask at all. A grafted profile is offered nothing, its emptiness being this
app's own doing and its chats already on their way in from its source. Refusing
a folder that is stashed away or resolves outside the profile is per store,
since a graft can have taken one of the two and left the other alone.

**And nothing moves while any Claude is up** — not the two profiles either side
of the copy, every one of them. An instance builds its sidebar as it starts and
rewrites records in its own shape as it runs, so records landing underneath one
are invisible until it restarts at best and written over at worst, and any
instance may be sharing these very folders through a graft. `adoptChats` asks
`runningClaudes` before it touches anything and answers with the profiles to
quit rather than with a count, so the window can name them; the wait costs one
restart and is the only version of this that cannot go wrong. Read through an
override, because nothing in a temporary directory can be made to run and a
suite that asked for real would pass or fail on whether somebody had Claude open
while it ran.

**The question is silenced at the door, never in the window.** Not now, and
never, both stop the dialog `open` puts up; the section in the shortcut's own
view stays for as long as anything is missing, because it is the only place left
to change your mind from and an offer outlives the moment it was first made. It
goes when the count reaches zero, which is what having taken them means.

## Unfiled chats across profiles

Claude files a chat under "This computer" only when the chat's `originCwd` lies
inside the `scratch-workspaces` folder of the profile that is open. The test is
lexical: three path components under that profile's own folder, nothing more.
A record copied byte for byte from another profile therefore names the other
profile's folder, and the sidebar shows it as a project called
`scratch-2026-…` instead. Fixing the record on one side only moved the problem,
because the next pass copied the fix across and broke the other side, and two
profiles took turns doing that to each other.

So the mirror gives each profile its own alias. When a scratch record lands in a
profile, its `originCwd` is rewritten to the same three components under that
profile's `scratch-workspaces`, with a symbolic link there to the real working
folder when nothing exists at that path. `cwd` is never touched, so resuming
still finds the transcript where the command line wrote it. When the two sides
are compared, `originCwd` is set back to `cwd` before hashing, so the local
alias is not an edit and nothing is copied for it. Records that are not scratch
records, and any whose `cwd` and `originCwd` disagree about which scratch folder
they mean, are left exactly as they are.

**Asking costs a parse, so most records are never asked.** Every pass puts the
question to every record on both sides of every pair, several times over, and
2.2.0 answered it by parsing the whole record each time with nothing draining
the results until the pass ended. A machine with 8 profiles, 18 pairs and about
66,000 records (16 GB of JSON, 2 % of it unfiled chats) grew Graft to 40–84 GB
within minutes of launch, and macOS killed it each time; 2.0.1, which only
hashed the bytes, had stayed small. Now a record is parsed only when its bytes
hold both `"originCwd"` and `scratch-workspaces`. Neither Claude's
`JSON.stringify` nor `JSONSerialization` escapes a letter or `-`, and an escaped
slash leaves `scratch-workspaces` whole, so no unfiled chat is passed over. Each
name is mirrored in a pool of its own, and so is every pass, as are the
sweep's, the menu bar's and sidebar sync's reads of a record: the reporter's
copy of that change stayed near 0.3 GB through a whole first pass.

What it leaves alone is the reading. Every pass still reads and hashes every
record on both sides while holding the state lock, so at that size a pass costs
minutes of disk and CPU. Remembering each file's inode, size and modification
time against its digest would make a quiet pass a series of `lstat` calls.
