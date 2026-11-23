# A chat two Claudes both carried on becomes two

Commit `31e549d` ("Split a chat two Claudes both carried on into two") changes
what a sync does with a chat that got a reply in both Claude and Claude 2
between two syncs. Each Claude carried the conversation on into a transcript of
its own, and one record can point at only one of them, so the old merge kept one
half and left the other on disk with nothing in any sidebar naming it. Now the
chat becomes two, in both profiles:

- **Claude's half** keeps the chat, titled `<title> (Claude)`.
- **Claude 2's half** becomes a new chat, titled `<title> (Claude 2)`.
- **Both titles** are marked as set by hand (`titleSource: "user"`), so Claude's
  automatic retitling does not drop the mark.
- **Nothing else splits a chat:** an archive, an unarchive, a rename, a look,
  or a reply on one side only.
- **The split waits while either Claude is open**, because a running Claude
  never re-reads a record and would write its own half back over the split.
  It happens at the sync a few seconds after the second of the two quits.

The suite covers all of this with records written by hand. This run checks it
with real Claudes: real replies, a real quit, and Claude showing and resuming
both halves afterwards.

Run it from a terminal outside any Claude, in the repository folder.

## This run breaks two standing rules

Aaditya agrees to both before it starts.

- **Claude's main profile is quit once**, in step 6, and opened again in step 7.
  The split cannot happen while it is open. Whatever is running in it stops
  until then.
- **One new chat is created and written to:** the test chat in step 2. No other
  chat is opened or written to.

## Procedure (read in order)

- <a id="tools"></a><a id="do-not-touch"></a>[Tools and safety](procedure/tools-and-safety.md)
- <a id="steps"></a>[Run the split test](procedure/run-the-split-test.md)
- <a id="putting-everything-back"></a><a id="report-then-clean-up"></a><a id="results"></a><a id="run-notes"></a>[Restore and report](procedure/restore-and-report.md)
