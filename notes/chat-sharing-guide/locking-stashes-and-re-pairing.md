# Locking, stashes and re-pairing

[Back to chat-sharing.md](../chat-sharing.md)

**One pass at a time, across processes.** `mirrored-chats.json` and
`session-records.json` are each read, worked on and written back, by Graft's
sweep and by every launcher, and those are separate processes. An atomic write
keeps the file whole but not the other pass's work: two shortcuts clicked
together, or one clicked while the dropdown's sweep ran, each saved the state
it had read, and the second to finish put back a baseline from before the
first's copies, which the next pass then read as deletions. So
`Graft.withStateLock` takes a flock on `ClaudeGraft/state.lock` around
`squareUp`, `apply`, `mirrorChatFolders`, `fileMissingSessionRecords` and
dropping pairs. It is reentrant within a process, because those call one
another, and only the outermost hold opens the file: flock belongs to the open
file, so a second descriptor would wait on the first for good. A launcher holds
it across `apply` and `squareUp` together and lets it go before `launch`, which
takes `SidebarSync.withLaunchLock`; nothing takes the state lock on the way into
the launch lock's work, so the two are never held in opposite orders. The usual
case costs one non-blocking `flock` and no wait. A pass that waits longer than
`stateLockPatience` (twenty seconds) goes ahead without it and notes
`state-lock.gave-up`, because a launcher that waited for good on a hung pass
would never open its Claude, and going ahead is what every pass did before. The
App's own callers on the main thread (saving a shortcut, deleting one) can now
wait for a sweep in progress; that is the price of not losing its work.

On Windows a pass that waits out `StateLock.Patience` runs nothing instead:
`StateLock.Run` throws `StateBusyException`, and `Launcher` notes
`launch.state-busy` or `sweep.state-busy` and opens the window anyway with
whatever is already shared. Going ahead was only safe against a pass that had
hung; one that was alive and slow still had its copies read back as deletions
by whichever pass saved second. A profile first opened while busy keeps the
chats Claude makes in its own folders, and the next pass stashes them as it
would on any first pass. What a person asked for (a move, a delete, adopting
chats, the manual-updates switch) is refused with a message rather than half
done; a delete holds the lock across the whole of itself so the folder never
goes without its pairs. `StateBusyAuditTests` fails on a caller in the app or
a launcher with no catch for it.

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

**A source that moved is a re-pair, not a first pass.** A borrower on another
account is paired with whichever of the lender's organization folders is
newest, so the source half moves whenever the lender signs into a second
account or simply starts a chat in its other organization. The borrower's
folder stays exactly where it was, and the pass after the move used to read the
dropped pair as a first one. `dropBorrowedCopies` could take out only what the
*new* source held, which was none of it; `stash` folded the old stash back in
and put the lot away, so the stash named the first organization's chats as the
borrower's own; seeding copied them into the second, and the lender's other
organization held the first one's history. Going back and forth left both
holding the union, and going back to its own chats kept four of the lender's
chats in the borrower for good, since nothing knew any more that they had been
borrowed. Reproduced three ways in a sandbox: an organization change, a lender
signing into a second account, and two profiles on one account turning into two
on different accounts, where a second, organization-level stash appeared beside
the store's.

So `firstPass` is asked of the borrowing folder before any pair is dropped, and
a pair borrowed by the same folder from somewhere else is settled by `repair`
first: the handover `unmirrorChatStores` does, one last pass and then out with
every copy the old source holds byte for byte that the stash does not name.
The new pairing then starts from what the borrower really has. That includes
handing back a chat the borrower started while it borrowed the old source, for
the same reason going back does — after the handover nothing tells it from a
borrowed one — and it stays in the folder it was started against. An old source
that is no longer there gets nothing handed back, and its copies are kept,
being the only ones left.
