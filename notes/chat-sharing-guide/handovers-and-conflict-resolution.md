# Handovers and conflict resolution

[Back to chat-sharing.md](../chat-sharing.md)

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

**Two sides that both moved were nearly always an archive and a glance.**
Claude writes a chat's whole record back from memory every time it shows the
chat, and archiving sets `isArchived` without touching `lastActivityAt` — both
read out of Claude Desktop 4.23118.1, and both visible in the records of a
machine with three profiles borrowing from one. So between two passes, an
archive on one side and nothing more than a look at the same chat on the other
was enough for both sides to have moved, and the pass kept one whole record:
the one with the later `lastActivityAt`, which the archive had not changed, and
on a tie the borrower's. Its `isArchived: false` went back over the archive on
both sides. A newer file time picks no better: copies made by a pass move it
too, and a glance made later still carries the old flag.

So every record has its fields written down beside its baseline — each
top-level key with a digest of its value, as both sides held it — and two sides
that both moved are merged field by field. A field one side changed and the
other did not takes the change, whichever side made it: an archive survives a
glance, and so does an unarchive, which no rule about archived records winning
could keep. Only a field both sides changed to different values is a contest.
A time takes the later of the two. An archive state takes the unarchived one:
a chat archived on one side and brought back on the other stays in both
sidebars, since an archive lost costs a click to make again and a chat somebody
was still using gone from view costs the chat, and before this the two settled
on whichever side had a borrower's tie, which kept the archive in one order and
the unarchive in the other. Anything else is settled the way the whole record
used to be, by the later `lastActivityAt` and on a tie the borrower's. The fields
live in `ClaudeGraft/mirror-fields/`, one file a pair, rather than in
`mirrored-chats.json`: a history of a couple of thousand chats carries a couple
of megabytes of them, and the state file is read and written by every pass over
every pair. An entry belongs to the baseline digest it was taken from, so one
left by a pass that kept no fields — a shortcut still on an older launcher — is
not trusted, and that record is settled the old way until the two sides next
agree.

Two things are read past the fields themselves. `isArchived` counts as decided
on a side whose `resumedAt` moved, since Claude sets it when it unarchives a
chat or brings a stopped one back to work in, which unarchives it too: an
unarchive made in a Claude still showing the chat archived, after a pass had
already written the unarchive underneath it, comes back with the same `false`
and would otherwise look like nothing at all. And a side whose
`lastActivityAt` comes back earlier than both sides agreed on was written from
an out-of-date copy, Claude only ever moving it forward; every field it differs
in is then as likely its old memory as its own doing, so the record is settled
whole the way it always was, and the later activity wins.

**A chat two Claudes both carried on becomes two.** A record points at one
transcript, and two Claudes that each had a reply land in the same chat since
the last pass wrote two: each resumed it into a command line session of its own
from the point the two last agreed. A merge keeps only one, and the other half
of the conversation stayed on disk with nothing in any sidebar naming it. So
when both sides' `lastAssistantUuid` moved off the baseline to different values
and both `lastActivityAt` moved past it, the source's half keeps the chat and
the borrower's goes under a new `local_` name, each titled with the Claude that
carried it on — Claude's own profile as Claude, a shortcut by the name it was
given — and marked as titled by hand, since an automatic title gives way to the
next one Claude thinks of. Nothing else splits a chat: an archive, an unarchive,
a rename or a look moves no reply, and a side whose activity went back is a
running Claude's old copy, not a second conversation. The split waits while
either Claude is open, because one never reads a record again once it holds it
and would write its own half back over the split; until both are closed each
side keeps what it has. Where two borrowers of one source both carry a chat on,
the first pair passes one borrower's half to the source and the second splits
it there, so that half is titled with the source's name rather than the
borrower that wrote it.

**A readable record beats one that is not.** Claude skips a record it cannot
read, so an empty or torn file carried over a whole one took the chat out of
both sidebars. Whether the empty file arrives as a change of its own or in a
conflict, the readable copy is kept and handed back to the side that lost it.

**A pass goes round until nothing moves.** Pairs that share a source pass a
change along one pair at a time, in the order of their keys. A chat archived in
the borrower whose pair comes last reached the source in the first round and the
borrowers listed before it only in the next, so a launcher that ran one round
opened its Claude a change behind. `mirrorKnownPairs` goes round again until a
round moves nothing; a change never needs more rounds than there are pairs. The
sweep also runs a few seconds after any Claude quits: a Claude reads its records
once, as it starts, so an archive made in one profile otherwise waited for
somebody to open the dropdown or a shortcut before another profile opened from
the Dock could see it.

**A running Claude can still write an archive back over.** A Claude never
re-reads a record it already holds — nothing watches the folder, and loading
again skips every chat in memory — and every save writes the whole record from
memory. So a record a pass writes under a running Claude is never seen by it,
and the next time that Claude shows the chat it writes its old `isArchived`
back; the pass after reads that as somebody unarchiving the chat on purpose and
carries it to every profile. That is the side this errs on by choice: a Claude
showing the chat is somebody using it, and the cost is an archive to make again,
never a conversation out of sight. Holding the write back until that Claude has quit
stops it, but a write held back from a source is held back from every other
borrower of that source too, and a pass that reads one pair at a time then
merges each borrower against a source that is out of date. Doing it properly
means merging every folder that shares a source in one go, with what each one
held when last read, rather than pair by pair. Until then, what is on screen in
a Claude that is already open is what it read when it started.
