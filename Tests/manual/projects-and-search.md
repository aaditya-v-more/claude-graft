# Projects and Search Chats read every signed-in account

Platforms: macOS, Windows
Needs: the owner, for claude.ai sign-ins by emailed code to two accounts
Setup: Graft running. A word that appears in chats of both Claude's and Test
A's accounts, named by the owner before the run. Nothing on claude.ai is
created, renamed, archived, starred or deleted in this case.

1. Select Projects. If no account is signed in, press Sign In…; **Owner:**
   signs in with Claude's account.
   Expect: the list fills. It matches claude.ai/projects for that account:
   the same projects, none archived, starred ones first with a star.
2. Select Search Chats and press Add Account…. **Owner:** signs in with Test
   A's account.
   Expect: both chips in the Search Chats header. Projects and Artifacts show
   both chips too, and Projects lists the second account's projects without a
   refresh.
3. In Search Chats type the word, without pressing Return.
   Expect: nothing is searched yet (no spinner, no results, no new `search`
   line in the diagnostics log).
4. Press Return.
   Expect: chats from both accounts, the word in bold in titles and snippets,
   each naming its account; Code sessions after the chats with their folder
   and where they ran; "N results" at the foot. The chats claude.ai's own
   search finds for that word, per account, are among them.
5. Open one chat result and one Code session result.
   Expect: claude.ai/chat/… and claude.ai/code/… open in the browser.
6. Open the filter menu, choose Kind → Code Sessions, then Reset Filters.
   Expect: only Code sessions, "N of M results", the filter icon filled while
   it narrows, and no spinner or new request.
7. Sign Test A's account out from its chip.
   Expect: its chip, results and projects leave all three panes at once.
8. Drag the window to its smallest size and look at Projects and Search Chats.
   Expect: nothing cut off; the chips scroll sideways if they do not fit.

Pass when: both panes show both accounts' data matching claude.ai, a search
runs only on Return, and a sign-out empties every pane at once.
