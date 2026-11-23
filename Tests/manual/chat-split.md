# A chat two Claudes both carried on becomes two

Platforms: macOS, Windows
Needs: two signed-in accounts (Claude and Test A)
Setup: Graft running. Test A set to read chats from Claude (Reads chats from:
Claude). Both Claudes closed.

A running Claude never reads a record again and would write its half back over
a split, so the split waits until both have quit.

1. Open Claude. In its Code tab start a new chat and send
   `Reply with only the word one.` Wait for the reply and note the chat's
   title.
   Expect: the chat is in Claude's sidebar.
2. Open Test A from Graft's window, choosing Open Anyway when Graft asks. In
   Test A, search for the chat by its exact title and open it.
   Expect: it opens on the reply "one".
3. Leave Graft's dropdown or flyout shut until step 5: opening it syncs, and a
   sync between the two replies carries one across before the other exists.
   In Test A, in that chat, send `Reply with only the word test-a.` and wait
   for the reply. Then in Claude, in that chat, send
   `Reply with only the word claude.` and wait.
   Expect: each Claude shows its own last reply under the same title.
4. Quit Test A from its own menu and wait 15 seconds.
   Expect: the diagnostics log has a `mirror.diverged` line with
   `"status":"waiting"` and none with `"split"`.
5. Quit Claude from its own menu and wait 15 seconds.
   Expect: the log has a `mirror.diverged` line with `"split"`, and a
   `projects.sync` line, not `projects.sync-skipped`.
6. Open Claude. Search for `<title> (Claude)`, then for `<title> (Test A)`.
   Expect: both are listed. The first opens on the reply "claude", the second
   on "test-a", and neither shows the other's last reply.
7. Open Test A from Graft's window, choosing Open Anyway. Search for both
   titles. In `<title> (Test A)` send `Reply with only the word three.`, wait
   for the reply, quit Test A from its own menu and wait 15 seconds.
   Expect: both titles are listed in Test A. After the reply the chat is still
   titled `<title> (Test A)`, and the log has no new `mirror.diverged` line.
8. In Claude, archive both halves. Quit Claude from its own menu, wait 15
   seconds and open Test A from Graft. Quit Test A afterwards.
   Expect: both halves are archived in Test A too, with their titles
   unchanged.

Pass when: a chat both Claudes carried on becomes two once both have quit, each
half named after the Claude that carried it on and opening on that Claude's
reply in both, and a reply on one side splits nothing.
