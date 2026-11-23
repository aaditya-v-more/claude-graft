# Forks and sweep scheduling

[Back to session-records.md](../session-records.md)

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
dropdown opens, which is where the line about what was recovered is shown, and one a few seconds after any Claude quits, so a profile opened next, even from the Dock, already has what that Claude wrote ([chat-sharing.md](../chat-sharing.md)). The thirty-second timer this replaced filed records
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
