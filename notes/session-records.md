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

## Topic guides

- <a id="deleted-unreadable-and-out-of-sight"></a>[Deleted and unreadable records](session-records-guide/deleted-and-unreadable-records.md)
- <a id="resumed-copies-and-forks"></a><a id="sessions-from-another-mac"></a><a id="which-profiles-are-written-to"></a><a id="when-the-sweep-runs-and-what-it-costs"></a>[Forks and sweep scheduling](session-records-guide/forks-and-sweep-scheduling.md)
