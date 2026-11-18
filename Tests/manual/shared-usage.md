# Test A's Code tab is answered by Claude's account, and comes back out cleanly

Platforms: macOS (15 or later), Windows
Needs: two signed-in accounts (Claude and Test A)
Setup: Graft running. Claude and Test A signed in to different accounts. Test A
closed. Nothing lent yet: Test A's "Shared usage" lists no accounts.

1. In Graft's window select Test A. Under Shared usage choose Add an Account,
   then Claude.
   Expect: a row for Claude with its bars and "Ready". The diagnostics log has
   a `pool.environment written` line with `"ok":true`.
2. Select Claude and add Test A under its Shared usage while Claude is open
   (open Claude first if it is closed).
   Expect: "Starts the next time this Claude opens." Remove it again with the
   minus button; the row goes. Claude is not closed or restarted by this.
3. Open Test A from Graft.
   Expect: it opens on its own account, as before.
4. In Test A's Code tab start a new session in an empty scratch folder and
   send `Reply with the word ready.`
   Expect: the answer arrives. The dropdown or flyout lists the conversation
   under Claude Code with the account that answered it. Claude's row in Test
   A's Shared usage reads "Ready · answered N requests" once Claude's account
   has answered one.
5. In the dropdown or flyout, choose the account that did not answer for this
   conversation, then send `Reply with the word moved.` Then choose Automatic.
   Expect: the answer arrives, and the account shown changes to the one
   chosen.
6. Quit Test A from its own menu. In Graft, remove Claude from Test A's Shared
   usage.
   Expect: within about 30 seconds no relay is running (`pgrep -fl graft-relay`
   on a Mac, `Get-Process GraftRelay` on Windows), and nothing listens on port
   47823.
7. Open Test A again and send one message in its Code tab, then quit it.
   Expect: it is answered by its own account, with no error about a proxy or a
   certificate.

Pass when: the lent account answers Test A's Code tab, a choice in the dropdown
or flyout moves the conversation, and taking it out leaves no relay and Test A
working on its own account.
