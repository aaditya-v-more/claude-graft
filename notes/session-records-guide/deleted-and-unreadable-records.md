# Deleted and unreadable records

[Back to session-records.md](../session-records.md)

## Deleted, unreadable and out of sight

**A record you cannot see is not a record somebody deleted.** A session taken
out of a sidebar stays out, and there are two ways of knowing which one went.
Graft names its records for their session, so the marker Claude leaves behind
carries the session's own id — a deletion read rather than inferred, needing
no memory of earlier passes at all. For a record Claude named itself, by an id
no transcript carries, the sweep goes on its own memory of which store each
record sat in: one gone from a store the pass actually read is one somebody
deleted — and two things have to hold before a pass will say so.

The store has to have been read. Every directory in the walk is taken with a
`try?`, so a profile that has been deleted, or stashed behind a graft, or is
waiting on a permission, comes back holding nothing, and reading that as a
sidebar cleared by hand withdrew every session it held, for good, which is a
thing no later pass undoes. Graft stashes organization folders itself, so its
own graft was the surest way to trigger it. `StoreContents.stores` is the set
of directories a walk really got into; a record in a store missing from it
stays remembered exactly where it was.

And the record has to be missing twice over. Claude re-files a session under a
new record name as it runs — a record named for one session was found on this
machine holding a session filed under another name an hour earlier — so there
is a moment when the old name has gone and the new one has not landed, and a
pass falling into it reads a deletion that never happened. `vanished` carries
the sessions one pass found missing so the next can agree, and nothing missing
is filed while that question is open: filing it would put the record back
under this app's own name, the pass after would find it there, and a chat
taken out of a sidebar would be quietly restored by the very thing meant to
notice it going.

The one guess left is a marker written before the first pass ever ran, which
names a record no longer there and can only be read by timing — the session
that had just gone quiet in the minute before the press.

**And a record you cannot see is not a record nobody filed.** That reading was
applied on the way out and not on the way in, so a store the walk could not get
into withdrew nothing — the rule above — and was then filed into as though it
were empty, which is the same mistake pointing the other way and the more
expensive of the two. Everything in an unreadable store looks like a session
that closed without a record, and a record written fresh says
`isArchived: false`, so a conversation archived by hand comes back into the
sidebar. Graft stashes organization folders itself, so its own graft was again
the surest way to trigger it. Reproduced with one folder stashed and a single
pass run over it: the whole history refiled, the organization folder rebuilt
over the top, and the stash left with nowhere to go — a second copy of every
chat, none of them archived, and the profile's real store orphaned beside it. `outOfSight` is the clause for
a session Graft has a record remembered for, `mayFileRecords` for the store
itself, and its two halves are the whole distinction: a folder that is there
must be one this pass actually read, and a folder that is not there must not
have a stash sitting beside it. A folder that is simply absent is still filed
into, since that is how a profile gets its first record.

**A stash is not always a sibling, and asking only for the sibling caught half
of them.** There are two shapes and `stashedCounterpart` has known both since
the day merging was written: a cross-account graft puts the organization folder
away and leaves `.<org>.graft-own` beside it, while two profiles on one account
put the whole store away and the same records end up one level further down.
`mayFileRecords` only ever asked about the sibling — and after a same-account
graft there is no folder left for a sibling to stand next to, so the absence
read as a profile that had never filed anything and the sweep rebuilt
`<account>/<org>` inside the emptied store and refiled the lot, unarchived,
with the real store orphaned in the stash beside it. Which is the same incident
as the paragraph above, reached by the half of the case its fix did not cover.
`isStashedAway` asks about every folder up to the store, and everything that
reasons about an absent folder goes through it.

**A stash hides what is in it, not what stands beside it.** Every mirrored
folder has a stash beside it, and `mayFileRecords` lets the sweep file into one,
since it is there and was read. From the next pass on, though, the withdrawal
loop put every session remembered in a folder with a stash beside it out of
sight before asking anything else, so a record the sweep had filed there kept
the title, model and last activity it was filed with for as long as the graft
lasted. The guard is for records remembered before a first pass moved them into
the stash, which the walk cannot see; it could not tell those from a record
filed afterwards and standing in the live folder in plain view. A session the
walk found a record for is in sight now, whatever sits beside its folder, and
goes on to be refreshed like any other.

**A record remembered where nothing exists any more is a pair to drop, not a
fact to act on.** `state.records` was only ever added to, so a folder that
stopped existing — the profile deleted, or a sign-in moving `<account>/<org>` —
went on being remembered for ever, and a session remembered there is out of
sight on every pass from then on. Out of sight blocks filing, so the chat was
in nobody's sidebar with its transcript whole on disk, and no finding named it
either, since a report cannot describe a folder that is not there. Forgetting
is what `forgetStalePairs` does for a mirror and for the same reason: the
session then looks new and lands where the account lives now. Told apart from
the unreadable case by `exists`, and only after `isStashedAway` has said this
app did not put it away itself.

**The timing guess is for the markers that cannot be read any other way.** A
marker naming a record Claude wrote carries an id no transcript has, so the
only trace of which session went is which one had just gone quiet. That guess
used to see every marker on the machine, including the ones named for a session
outright — and markers accumulate and are never pruned, so one deletion went on
suppressing whatever else had happened to go quiet in the minute before it, for
as long as the marker existed. Only markers naming no transcript reach it now.

**A marker that withdrew a session is not news the next time it is read.**
Markers are never pruned, so the one that named a session goes on naming it on
every pass for good, and the sweep read it afresh each time: one session,
deleted from Claude's sidebar on 16 October, was logged as withdrawn on 54
passes in a row across 1.1.8 and 2.0.0 builds, and every `sweep.end` said
`"withdrawn":5`. Nothing was being done to it. Its marker, its transcript and
the copies a mirror had carried into Claude-2 were all untouched since the day
they were written, no record for it existed anywhere, and `session-records.json`
had it in `withdrawn` and nowhere else; the count was the running total of all
five sessions ever withdrawn, under a name that reads as this pass's. The four
withdrawn by a store read twice were each logged once, because a withdrawal
drops them from `records` and nothing brings them back to be read again. Only a
session not yet withdrawn is withdrawn by its marker now, `withdrawn` on
`sweep.end` is the pass's own count, and the total is `withdrawnInAll`.
