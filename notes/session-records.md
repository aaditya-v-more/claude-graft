# Session records and the sweep that files them

Why a grafted profile writes no records, and how `fileMissingSessionRecords` files them instead without refiling what somebody deleted or archived. Read this before changing the sweep, `StoreContents`, `resumedCopies` or anything in `session-records.json`.

## Why records go missing

**A profile will not write a record into a folder that resolves outside
itself, and the graft link is exactly such a folder.** This is the rule the
whole session sweep was built to work around, and it was misread for months as
being about accounts. A grafted profile shows a conversation in its window and
writes nothing at all: on close the session is gone from both profiles, leaving
a transcript nobody filed a record for. It reads exactly like the other profile
deleting it, which is how it was first reported.

The account has nothing to do with it. Measured directly: a profile signed into
one account, given a real organization folder of its own, created a session,
wrote its record, and archived it — and that session was stamped
`ownerAccountUuid` of the *other* account on the `bridge-session` line of
`~/.claude/projects/<encoded cwd>/<cliSessionId>.jsonl`. Same profile, same
account, same machine login, with the folder linked instead of real: nothing
written, ever. The one variable that moves is whether the directory the record
would land in resolves inside the profile.

Every earlier reading follows from that. Rewriting the owner stamp to name the
grafted profile's account changed nothing, because the stamp is not consulted —
it is the command line's note of who it was, not a permission. Sessions a
profile owned before it was grafted have records; sessions it started after do
not. A profile outside every shortcut, with a real store, writes its records
without trouble. And the reason the source profile always worked is that its
own folder is the real one.

Measured five ways before the behaviour was believed, and every one of those
five had a link in place, which is how the cause stayed hidden behind the
account for so long. Linking the owner's account path into the grafted profile
so the record had somewhere to land changed nothing — it was another link;
neither did closing the other Claude for the whole test, nor leaving the app
open two minutes rather than thirty seconds. The record is never written. The
absent directory was the rule showing through, not the cause, and the link that
looked like the fix was reverted rather than shipped.

**Ownership is written in one place, and the command line is what puts it
there.** A record carries no account field at all — its account is the
directory it sits in — so the only thing on disk saying who a conversation
belongs to is the `ownerAccountUuid` on its `bridge-session` line. Nor is that
line written once at the start: one session here had twelve of them, the last
at the very end of the file, each with a fresh `bridgeSessionId` and every one
naming the same owner.

Which owner is not the profile's, however much it looks like it should be.
Claude Desktop keeps a copy of Claude Code inside every profile —
`<profile>/claude-code/<version>/claude.app`, which is what it spawns for a
bridged session — and hands it that profile's own token in
`CLAUDE_CODE_OAUTH_TOKEN`, alongside `CLAUDE_CODE_ENTRYPOINT=claude-desktop`.
Read that and you would expect each profile to own what it starts. It does not:
a session started in a grafted profile, with no other profile running, came
back stamped with the source account. Identity comes from the machine login —
`oauthAccount` in `~/.claude.json`, behind one keychain item under
`Claude Code-credentials` — and every copy of Claude Code shares it, the ones
inside profiles and the standalone one on `PATH` alike. The token pays for the
inference; it does not say whose the conversation is.

So which account a chat is stamped with is settled by whoever `claude` was
logged in as when it started, and by nothing about the window it was typed
into. What the stamp does not do is decide who may write the record: one
session was rewritten to name the grafted profile's account, with an untouched
session beside it as a control, and the grafted profile still would not archive
it — while the same profile, given a real folder, archived a session stamped
with the other account without complaint. The stamp is the command line's note
of who it was. Nothing reads it back.

**The same rule governs every write to a record, not only the first one.** Archiving
a borrowed conversation from the grafted side takes it out of that window's
sidebar and puts nothing on disk, so the record still reads
`isArchived: false` and the chat is back the next time the profile starts. It
was reported as archived conversations unarchiving themselves, which is what it
looks like from the sidebar and is a different thing entirely from the sweep
refiling them — the two were only told apart by watching the store while it
happened. Every write to both profiles' stores was logged across the window: two
conversations archived from the owner's profile were on disk within a second,
`isArchived: true`; two archived from the grafted profile produced no write
anywhere at all, their records untouched since the night before. The grafted
profile was then given its organization folder back as a real directory,
started a chat, and archived it, and that one stuck — the record written by the
profile itself, in its own shape, for a session stamped with the other
account's uuid. One variable moved between the two halves of that test, and it
was not the account.

Nothing here can repair it. What a sweep files is a record that was never
written; an archive that was never written leaves nothing behind to find, so
there is no counterpart to `fileMissingSessionRecords` to be had. Chat state on
the grafted side — archiving above all — belongs to whichever profile holds the
account, and that is worth saying in the README rather than working around.

What is Graft's to repair is the record, and the repair is to write it.
`Graft.fileMissingSessionRecords` sweeps the transcripts for bridge lines,
asks which profile the owner's account lives on now, and writes
`local_<cliSessionId>.json` into that profile's `<ownerAccount>/<ownerOrg>` —
where the session's own window and every profile grafted onto it lists it,
a linked organization included. Every field comes out of the transcript, so
nothing is invented: the id from the filename, the owner and organization
from the bridge line, the title from the title line, the times from the first
and last stamps. The bridge id is renamed on the way — `cse_…` in a
transcript is `session_…` in a record, measured against a session that had
both written down — and the name is fixed by the session, so a later pass
can only ever find a record where an earlier one left it, and a delete in a
sidebar writes a marker naming it the same way.

**A filed record never grants more than its transcript says.** Every record
the sweep wrote up to 2.0.0 carried `chromePermissionMode:
skip_all_permission_checks`, whatever the person had chosen, and a transcript
naming no permission mode was filed as `auto`. Neither value came out of a
transcript and no note gave a reason for either. The browser setting is a
choice Claude makes per session — on this machine three records carry
`follow_a_plan` and the twelve Claude rewrote carry no such key at all — so a
recovered session continued in Claude got the most permissive setting there
is, which nobody picked. The key is left out now, which is how Claude's own
rewrites leave it and lets the person's setting hold, and a transcript with no
mode files as `default`. A record still holding the bytes the older shape gave
it is still this app's, so `olderSessionRecordData` recognises it and the next
pass rewrites it whether or not its transcript moved; waiting for the session
to move again would have left the setting there for good. The transcript cache
is stamped with `transcriptReadingVersion` for the same reason, since an entry
read the old way goes on saying `auto` for as long as its file sits still.

Four things hold the sweep in. A transcript written moments ago waits out a
quiet window first, a minute of it — but only while a Claude signed into the
owner's account is actually running, because that is the only thing the wait
is for. Such a window writes the record itself within moments of a session
opening, measured at about a second, so a minute is far more than that wait
needs; with no such Claude up there is nobody to wait for, and waiting anyway
made closing a chat and reaching straight for the other profile the one move
guaranteed to miss it. A sidebar is built once, at launch, and that launch is
the very press being waited out. A record already on disk is never written
over by a pass either way. What a pass files it keeps current: the
sweep remembers the facts behind every record it wrote, in
`ClaudeGraft/session-records.json`, and while the record on disk still holds
exactly those bytes it is known to be one of ours to refresh as the
transcript moves — a title landing late, a turn finishing. The moment the
bytes stop matching, Claude has rewritten the record in its own shape, and
the sweep retires its version for good, because a parsed transcript flattened
over the host's record would lose everything the transcript cannot recover,
its MCP server config for one. A session still being written grows past the
quiet window and files later. And a transcript with no prompt in it is never
filed at all — an open-and-close with nothing said never had a conversation
to lose.

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

## Resumed copies and forks

**A conversation carried on is not a conversation lost.** Claude gives a
conversation a new command line session as it goes — a compaction, a resume —
and the record names the ones it grew out of in `priorCliSessionIds`. Those
earlier transcripts stay on disk, whole, with no record naming them, which is
precisely what a session that closed without one looks like. Filing them put
the same conversation in the sidebar twice, same title and same folder, the
second copy carrying none of what had been done to the first — so archiving the
conversation left its earlier half sitting there in plain sight. Five such
records were on this machine, against three conversations. `StoreContents.superseded`
is what a record says it has taken over, and nothing in it is ever filed.

**And a conversation asked again is not a second conversation.** Editing a
prompt or resuming a chat in the desktop starts a new command line session.
Sometimes it copies the history and asks the edited prompt again; sometimes it
is a fresh session where the same opening prompt was typed and then left. If
the profile was grafted at the time, no record ever named the earlier one in
`priorCliSessionIds`, so `superseded` could not see it and the sweep filed every
attempt: one conversation turned up six times under one title, and the copies
were only told apart by reading them.

`resumedCopies` is the rule, and it asks three things of a session before
calling it a copy of another with the same account, organization and folder.
They open with the same prompt, and it says something: the first version matched
on prompts alone and put three unrelated chats that only said "hi" out of sight.
Every prompt typed in it was typed in the other, which holds more, or the same
and was active later. And it did nothing the other did not — the first version
also put away a retry holding the only finished draft of a PR comment, its
counterpart overloaded at that turn, and the session holding an original
implementation its counterpart only found and built on. A resumed copy keeps
the line id of everything it copied, so a session's own work is exactly the
answers the other has no id for; a file edited there rules it out, and a few
sentences of its own — what a stopped attempt leaves behind — are allowed for.

Prompts are what a person typed: a tool's output, an `isMeta` note and a stopped
turn's `[Request interrupted by user]` line all arrive as `user` lines with a
prompt id, and counting the last one kept the stopped stub and dropped the real
conversation in four pairs out of four. Fingerprints are taken over the raw
escaped bytes, because decoding every prompt made a cold pass two thirds slower.

Checked against this machine's 302 sessions by reading every one the rule
caught: the 28 that were copies did at most 173 bytes of answering of their own
and edited nothing, the two real branches did 2,573 bytes with four edits and
9,097 bytes, and the allowance sits between them. The rule now catches exactly
those 28 and files the five it got wrong. It only decides filing; records
already on disk stay where they are.

**But a fork is a second conversation, and only its record says so.** Forking
a chat — a side question, or a session handed off with this one's history —
copies every line of the transcript, still stamped with the original's session
id, and then asks something more. From transcripts alone that is a resumed copy
with the roles reversed: the original holds nothing its fork lacks, so the rule
reads the original as the copy. Run over this machine it held back two of the
five originals that had forks. Nothing was lost, because a recorded session is never filed or touched and
Claude writes a record for every fork and keeps the original's. The rule only
matters on the day the records are gone, though, and they go all at once: a
profile deleted, or a sign-in moving `<account>/<org>`. On that day the sweep
files everything again from transcripts and would have brought back the fork
alone.

What tells the two apart is in the record. A fork gets a record of its own
naming its source in `forkedFromSessionId`, by the source record's name; a
resume keeps its record and moves the old session into `priorCliSessionIds`.
Every pass reads those names and keeps the pairs in `session-records.json` as
`forks`. They have to be learned while both records still stand, since the
pass that needs them is the one where neither does. Neither half of a remembered
pair is ever a copy of the other. A retry of either half still is, and on this
machine the rule went from 29 copies to the 27 it should hold.

## Sessions from another Mac

**A transcript carried over from another Mac is not a session that lost its
record here.** On 5 November fourteen insta-orchestrator conversations came
across with the rest of `~/.claude/projects`, every one of them worked in under
`/Users/aaditya`, a home this Mac has never had. Each had a bridge line, a
stamp naming an account a profile here holds, and no record anywhere, which is
exactly what a grafted profile's lost session looks like, so the first pass
filed all fourteen into Claude. They sat in the sidebar beside this Mac's own
copy of the project under `/Volumes/SSD/Developer`, four of them titled the
same, and when Claude reached for one it failed to start it with
`cwd_not_found`. The repeated titles were four separate conversations, not one
filed four times.

So `sessionFolderIsHere` is asked before anything is filed, and it asks about
the home folder or the drive, never the folder itself. A worktree cleaned up
after its chat closed is gone while the project it sat in is not, and that chat
is this Mac's history: the sidebar still reads it. Only a `/Users/<name>` or a
`/Volumes/<drive>` that is not here at all says the session happened somewhere
else. Nothing is remembered about the refusal — the answer is `folderElsewhere`
and the next pass asks again — so a drive that was not mounted files once it
is. A path outside every home and drive, or none at all, files as it always
did.

The rule only keeps a pass from filing. What was filed before it existed has
long since been rewritten by Claude in its own shape and belongs to Claude, so
those fourteen are deleted from the sidebar by hand, and the deletion markers
keep them from coming back.

## Which profiles are written to

**A Claude this app did not make is read, never written to.** Every profile in
Application Support holding a chat store is worth reading, because the question
a sweep asks is whether any Claude anywhere is already listing the session, and
a record in a profile outside every shortcut still means one is. Writing is a
different question with a different answer, and the two were one list: the
owner's account matching was enough to have records filed into a profile Graft
had never heard of, in somebody else's sidebar, under names of Graft's
choosing. `recordFilingProfiles` is the filing list — what the caller named,
Claude's own profile, and the folders in `ClaudeGraft/shortcuts.json`, which is
the only thing on the machine that says which profiles are this app's doing. A
launcher reads that list too; it knows its own profile and its source and
nothing else, and the widening it needs is to Graft's other profiles, not to
everybody's.

## When the sweep runs, and what it costs

The session-record sweep is on no timer at all. A record is read when a Claude
starts and at no other moment — it builds its sidebar as it comes up — so the
pass that matters is the one a shortcut runs on its way to opening one, in
`Graft.run`, after the links and before the launch. It runs on the way past a
profile that is already open too, which is the one case nothing else covers:
with no timer, that press is not going to start anything, and without a pass
on it, going looking for a chat that had not arrived yet was the surest way to
leave it not arriving. Graft itself runs one when it starts and one when the
dropdown opens, which is where the line about what was recovered is shown. The thirty-second timer this replaced filed records
that nobody would read for hours, and the one thing it was really buying —
being current when a profile opened — is bought better by the profile's own
launch.

That only works because a pass is cheap enough to stand in front of a window
opening. Two hundred megabytes of transcript is read in about a fifth of a
second: each line's bytes are walked once and every `"key":"value"` pair taken
off it as it goes, rather than the line being searched again for each of a
dozen fields, which took twelve seconds and made the sweep something that
could only ever happen in the background. Strings are walked the way JSON
writes them, escapes and all, since a value that ends early takes the rest of
the line with it. What has been parsed is remembered against each file's size
and modification time in `ClaudeGraft/transcript-cache.json`, so the pass a
launcher actually pays is forty milliseconds — in memory alone that cache
would be worth nothing to a launcher, which is a process that exits the moment
it has handed over.

One field is not read the way the rest are. An answer is written
`{"parentUuid":…,"message":{…,"type":"message",…},"type":"assistant"}`, so the
first `type` on the line belongs to the message nested inside it and the line's
own comes last — taking the first, which is right for every other field, quietly
put every recovered session back at the default model and effort.
