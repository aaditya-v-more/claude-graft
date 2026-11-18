# A Claude that reads another's chats sees a new chat there

Platforms: macOS, Windows
Needs: two signed-in accounts (Claude and Test A)
Setup: Graft running. Test A set to read chats from Claude (Reads chats from:
Claude). Both Claudes closed.

1. Open Claude. In its Code tab start a new chat and send
   `Reply with only the word one.` Wait for the reply. Note the chat's title.
   Quit Claude from its own menu.
   Expect: the chat is in Claude's sidebar before it quits.
2. Open Test A from Graft.
   Expect: the chat noted in step 1 is in Test A's sidebar, and opens on the
   reply "one".
3. In Test A, archive that chat. Quit Test A from its own menu. Open Claude.
   Expect: the chat is archived in Claude too.
4. macOS only (Windows does not ask yet): open Test A from Graft while Claude
   is still open.
   Expect: Graft first asks whether to open it while another Claude is open on
   the same chats, with Open Anyway and Cancel. Choose Cancel; Test A does not
   open.
5. Look at the diagnostics log.
   Expect: no line with `"refused"` or `"unreadable"` from this run.

Pass when: a chat made in Claude reaches Test A, an archive made in Test A
reaches Claude, and on a Mac opening a second Claude on the same chats asks
first.
