# How chats are shared between profiles

Grafting links or mirrors one profile's chat store into another, and every way that has gone wrong is written up here. Read this before changing `Graft.swift`'s graft, stash, mirror or ungraft paths, or anything that decides where a profile's chats live.

## How a chat store is laid out

Chat history is `<store>/<accountUuid>/<orgUuid>/`, in both
`claude-code-sessions` and `local-agent-mode-sessions`. An instance reads only
the account it is signed into, so linking a whole store does nothing across two
accounts — the link is made one level deeper, mapping this profile's
`<account>/<org>` onto the source's active one.

**Except that a store does not always spell those two in full.** A profile in
local mode was measured naming both halves by their first eight characters —
`local-agent-mode-sessions/ed417e0f/00000000` against
`claude-code-sessions/ed417e0f-5edd-…/00000000-0000-…`, the same account and the
same organization in the same profile, one store each way. Such a profile
carries `<org>.profile-origin.json` saying `{"mode":"local"}`, which is the only
thing that marks it out, and asking a store like that for the full uuid finds
nothing at all. `counterpartDirectory` is the rule: an exact name, or the one
directory there whose name is a prefix of what was asked for — at least eight
characters of it, and only where there is exactly one, since nothing may put one
account's chats into another's folder. Anything ambiguous comes back nil and the
caller falls back to the name it was given.

**`<org>.profile-origin.json` is a plain file sitting beside the organization
folders,** written as the organization is created, which makes it for a moment
the newest thing in the account directory. `newestChild` returned it as an
organization, and the graft that followed stashed the profile's own copy of the
file and built a directory where it had been. Every walk of an account
directory asks `isDirectory`.

**`skills-plugin` sits beside the account directories without being one.** It
is keyed by organization, so a walk that took it for an account reported its
organization folders as chat stores. `nonAccountStoreItems` names it, and both
the sweep's walk and the state report skip it. Grafting links it, on either
path — it used to be linked only across accounts, having been written after the
same-account branch's early exit — and the link is made after the store is
opened, since a same-account graft puts the whole store away and builds it back
and would take the link to the stash with everything else.

## Grafting, stashing and mirroring

**Never destroy what a profile owns.** Anything a link replaces is moved to a
hidden `.<name>.graft-own` sibling first, and restored when the shortcut goes
back to its own chats. A profile must never be grafted from itself — guarded in
`graft`, `relink` and `install`, because self-grafting stashes away every file
the profile has and leaves links pointing at their own names.

**A stash already sitting there is not proof of a duplicate.** Claude writes
`config.json`, and recreates chat directories, by renaming a temporary over
them, and a rename leaves a real file where the symlink was. The profile then
goes back to writing its own copy while the stash still holds the pre-graft
state. `stash` used to read that as drift and delete it, which threw away every
chat written since the graft; it now folds the two together and keeps the copy
the profile is actually using. `unstash` used to bail whenever something already
sat at the link, which is what left the stash there to arm the next graft.

**An unreadable `config.json` is not an empty one.** A profile that has never
been signed in has no file at all and can share the whole store safely. A file
that will not parse is one caught mid-rename, and treating that as "no account,
so the same account as the source" linked an entire store away. `readableConfigJSON`
tells the two apart; nothing may write that file, or decide where a profile's
chats go, without asking it.

**A mirrored folder holds both histories, and the stash says which was whose.**
Copying is the way round the rule in [session-records.md](session-records.md) —
a real folder is one a profile will write a record into, a link is not — and it works by giving the profile its own
copy of the source's chats and carrying changes both ways. Sharing a history
merges the two rather than standing one in for the other, so the profile's own
chats go into that folder as well and both sidebars end up holding the union.

What keeps that reversible is the stash. On the first pass the profile's own
chats still go to the hidden `.<name>.graft-own` sibling a link would have moved
them to, and `seedOwnChats` copies them straight back into the shared set — the
stash keeping the copy that says which of the merged records this profile
brought, the folder getting the copy the person actually reads. Where its own
copy went depends on how the two are signed in, so `stashedCounterpart` looks
for both shapes: a cross-account graft stashes the organization folder itself,
two profiles on one account stash the whole store above it, and the same records
are then one level further down.

`unmirrorChatStores` is where that record is spent. A record goes only when the
other side holds the same bytes, which after the handover pass describes the
profile's own chats exactly as well as the borrowed ones — so the ones the stash
names are kept rather than removed, or going back would hand this profile's
whole history to the one it borrowed from and keep nothing back. Kept rather
than removed and fetched out of the stash afterwards, too, so that a chat of its
own the profile archived while the graft was up stays archived: restoring the
stashed copy over it would put the conversation back unarchived, which is the
symptom this app gets reported for most.

Seeding runs on the first pass alone, and it is the same clause that stops the
second launch stashing away everything the first one mirrored in. On every
launch it would fetch back every merged chat the person had since deleted,
which is that endless loop pointed the other way.

What is merged into the source stays there. Nothing here takes a chat out of the
profile holding it, so going back leaves those copies where they are, and the
merge is the one thing this app does that it cannot undo. That belongs in the
window rather than in a comment, and `ShortcutDetail.mergeNote` is where it is
said.

Which pass is the first one is asked of `mirrored-chats.json` and never of the
stash. `apply` runs on every launch, so a profile that had nothing to put away
— and therefore left no stash — would look new for ever, and looking new for
ever means the second launch stashes away everything the first one mirrored in
and fetches it all back. `openForMirror` is the rule, `firstPass` the clause.

**Going back is a handover, not a delete.** A link can simply be dropped; a
folder full of copies cannot, so `unmirrorChatStores` runs one last pass to
carry everything the profile did while it was mirroring — a chat started, one
archived, one deleted — over to the profile it borrowed from, and only then
takes the copies out. Going back to its own chats is the one way out that has
to do this, and `ungraft` is the only caller for that reason.

Which copies go is not a guess and does not trust that pass: a record is
removed only when the other side is holding the same bytes at that moment. A
source that has been deleted, a folder that could not be read, a disk with no
room — each leaves every copy where it is rather than taking a chat away on the
strength of a handover that did not happen.

**A folder that lost everything at once was moved, not cleared.** This is the
worst thing that has happened to this app, and it happened to a real machine
with real chats on it. Signing in again moves where a profile's chats live —
`<account>/<org>`, both halves — so the pair an earlier pass wrote down went on
naming a folder nobody wrote to any more, while `mirrorChatStores` computed a
new one, found no pair under it, and read that as a first pass. `openForMirror`
duly stashed the live mirrored set as though it were the profile's own chats.
The next `mirrorChatFolders` then saw one side holding none of the 150 names its
baseline described and carried all 150 across as deletions — into the profile
the chats had been borrowed from, which is where the only copy lived. One
sign-in, both sidebars, no warning.

So a side holding none of the names the baseline describes, while the other
still holds them, is a folder that was stashed, replaced or emptied wholesale,
and the baseline is forgotten rather than acted on: the survivors then look new
and the empty side is filled again, which is the opposite mistake and the
harmless one. A chat put back is a nuisance; a chat taken away for good is not.
`forgetStalePairs` is the other half, dropping the pair that names where a
profile's chats used to live before any launcher can square it up. It takes the
set of folders still being mirrored rather than one of them, because two
profiles on one account mirror every organization the account has and no single
one is the pair worth keeping — which is why that branch went without it.

**The baseline says what the filesystem did, never what a pass meant to do.**
Copying has always been written down only when the write landed; removing was
written down either way, so a removal the filesystem refused left a name on one
side with no baseline behind it — which is a new chat, and the next pass copied
the deleted conversation straight back to the side it had been deleted from. A
name gone from both sides is dropped too: the loop only visits what one of them
holds, so such an entry sat there for good, growing the file and standing ready
to read that name coming back on one side alone as a deletion to carry across.

**Nothing goes to the stash before the source has been read.** The
same-account branch opened the destination first and consulted the source
second, so a source that turned out to hold nothing under this account — spelled
shorter, holding only `skills-plugin`, signed in as somebody else — put the
borrowing profile's whole history away and left an empty folder standing in its
place. No pair was written down either, so `firstPass` was true again and every
launch after did it again. What the source has is worked out first, and a source
with nothing to share leaves the destination exactly as it was.

**And the same emptiness must not be read as a deletion by the sweep.** The
stash leaves a real, readable, empty folder behind, so `contents.stores` holds
it, every record remembered there is missing, and two passes make that
permanent — 150 sessions across two profiles withdrawn in one afternoon, which
is a thing no later pass undoes. `mayFileRecords` had asked whether a stash sat
beside a folder on the way *in* since the day it was written; the withdrawal
pass never asked it on the way out. Both directions ask it now. Withdrawing is
forever, and forever deserves the cheaper mistake.

**The pairs are the keys of the state file, and every way out drops them.** A
shortcut being opened squares up its own pair, which covers the profile doing
the borrowing and nobody else — so archive a chat in the borrowing profile,
open the source, and the source is not the one running `apply`. `mirrorKnownPairs`
reads the pairs back out of the file instead, which is also all a launcher
needs to know. That file is read by every launcher, though, so a pair left in
it outlives the graft that made it: deleting the profile and deleting the
shortcut both call `forgetMirrors`, and undoing a graft drops the pairs it
settled, or two folders go on being squared up for a graft that no longer
exists.

**A pair has two halves and only one of them is undoing anything.** Every
question a graft asks of that file — is this a first pass, what should stop
being mirrored, which copies come back out — is about the borrowing half, and
asking `mirrorPairs(under:)` answers for both. `apply` calls `ungraft` on any
profile with no source of its own, which is every launch of the profile others
borrow from, and undoing a graft took every pair with a half underneath it. The
lender read its own folder as the borrowed one; a lender keeps no stash, so the
clause that holds back what a profile brought had nothing to name; and a merge
leaves both sides holding the same bytes, so the test for which copies to take
out matched all of them. Opening the source emptied the shared history out of
the sidebar it was about to build, forgot the pairs on the way past, and left
the borrower's next launch reading as a first pass — which stashes. Both
sidebars, on every launch, for as long as the shortcut existed, and the only
thing on screen to notice was a sidebar with one chat in it.

`mirrorPairs(borrowedBy:)` is the rule, and the roles come from the order the
key was written in: `mirrorChatFolders` is only ever called with the profile's
own folder ahead of the one it is borrowing from, and after a merge nothing on
disk tells a lender from a borrower. `forgetStalePairs` asks the same way, and
takes the pairs this pass is mirroring rather than a list of folders to find a
half in, since a source that moves leaves a pair as dead as a destination that
moves and only the destination half was ever checked.

**A first pass that finds a stash already there is a second one.** `stash` folds
a stash it finds back into the live folder before moving the lot aside — right
for a link the profile has written a real file over, wrong for a mirror, where
the live folder holds the merge and the stash is the only thing that says which
of those records the profile brought. Fold them together and that is gone for
good: this machine's stash went 155, 158, 159 while the count was the only
symptom. `dropBorrowedCopies` takes out what the source is holding byte for byte
that the stash does not already name, so the stash goes on naming exactly what
was brought and the borrowed copies come back on the pass that follows.

It runs only on a folder that resolves inside the profile. A shortcut left on
the shape an older version made has its own chats in the stash and a link where
they used to be, so the folder's contents are the source's own files and every
one of them matches the source byte for byte because it *is* the source —
deleting the lender's whole history and calling it tidying up. The link is not
always the folder either: a released version sharing one account linked the
entire store, so the organization folder under it is a real directory reached
through a symlinked parent and `isSymlink` answers no. Both shapes are the rule
the rest of this app is built on, which is whether the folder resolves inside
the profile at all.

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

