# Projects and Search Chats, tested on a second Mac

This folder is temporary. It is deleted once its results are read.

`main` adds two panes to Graft's window, both reading claude.ai through the
same sign-in the Artifacts pane uses:

- **Projects** lists the projects of every signed-in account, starred first,
  then the most recently changed. It only lists. Opening a project goes to
  claude.ai in the browser.
- **Search Chats** searches every signed-in account's chats with claude.ai's
  own search, and their Code sessions by title and folder. One filter icon
  beside the Search button narrows the results by kind, status, where a Code
  session ran, age, account, project and star.
- **One sign-in for all three panes.** An account signed in or out from any
  pane is signed in or out in the other two at once, without a refresh.

The suites check the request shapes, the filters and the layout against sample
rows. This plan checks them against real accounts. It builds `main`, swaps the
installed Graft for the development build, and puts it back at the end. Run it
from a terminal outside any Claude, in the repository folder.

## What the Mac needs

- macOS 14 or later, with Xcode or the Command Line Tools.
- Claude installed, its own profile signed in to one account. Claude 2, if
  this Mac has one, is signed in to a **different** account.
- **Aaditya at the keyboard** for the claude.ai sign-ins in steps 3 and 4 and
  the sign-in again in step 10.
- Code sessions on both accounts, for the filters in step 8: at least one run
  on this Mac by Remote Control, and at least one run on the development Mac.
  A cloud session or an archived one is useful if either account has one, but
  is not needed.
- A word that appears in chats of both accounts. Note it in the run notes.
- Graft installed in `/Applications` is optional. Without it, skip the
  put-back step that reopens it.

## Tools

- **Diagnostics:**
  `grep -E '"(projects\.|search|artifacts\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`
- **Graft's files, read-only:**
  `cat ~/Library/Application\ Support/ClaudeGraft/artifact-accounts.json`
- **Code sessions on this Mac, read-only.** Counts the transcripts that carry a
  Remote Control id. Prints no content:

      grep -l '"bridge-session"' ~/.claude/projects/*/*.jsonl | wc -l

- **A picture of the window:** `screencapture -x -l <window id>` or a
  full-screen `screencapture -x handoff/projects-search/step-<n>.png`.

Save every output and picture in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them. The report step
deletes them once the results are sent.

## Do not touch

- **Chats and projects:** nothing is created, renamed, archived, starred,
  moved or deleted, on claude.ai or in any Claude. Searching and opening are the
  only things done to them. Don't delete a Code session to make one "gone from
  claude.ai". Step 8 checks that filter only if the Mac already has such a
  session.
- **Claudes:** no Claude is quit or opened. Bringing an open one forward is
  fine.
- **Settings:** leave Open at Login, the update settings and the hold setting
  alone.
- **Repository:** nothing is committed, pushed or released, and no branch is
  created or switched.
- **Credentials:** claude.ai sign-ins, passwords and email codes are typed by
  Aaditya alone, in Graft's sign-in window. A keychain prompt is also for
  Aaditya to answer, with **Allow** (the one-time option).

## Steps

1. **Build and swap.**
   - Run `git pull` on `main` and record `git log -1 --oneline`.
   - Run `unset SDKROOT`, then `./build.sh`. Its last line names the version
     built.
   - Quit the installed Graft from **Quit** at the bottom of its dropdown.
   - Run `open "build.noindex/Claude Graft.app"`, then open its window from the
     dropdown.

   Shows: the sidebar's first section reads Claude, Artifacts, **Projects**,
   **Search Chats**, each of the last three with "Every account" under it.

2. **No account yet.** Run this step only if `artifact-accounts.json` is
   missing or empty. Otherwise, write down the accounts it lists and go to
   step 3.
   - Select **Projects**, then **Search Chats**.

   Shows: each pane shows one line saying to sign in, an ⓘ, and **Sign In…**.
   Nothing else. No request is made.

3. **Sign in from Projects.**
   - In **Projects**, press **Sign In…** (or **Add Account…**). Aaditya signs in
     to Claude's account in the window that opens.

   Shows:
   - The window closes by itself. A chip with the account's address, or
     "Claude" if it is this Mac's Claude account, appears in the Projects
     header.
   - Within a few seconds the list fills.
   - Compare the list with `claude.ai/projects` for that account in the browser:
     - the same projects, with none of the archived ones;
     - starred ones first, marked with a yellow star;
     - "How to use Claude", if present, says "Example";
     - a project shared with the organization shows the two-people icon.
   - Select **Artifacts**, then **Search Chats**. The same chip is in both
     headers already, with nothing pressed.

4. **Sign in from Search Chats.**
   - In **Search Chats**, press **Add Account…**. Aaditya signs in to the second
     account (Claude 2's, if this Mac has one).

   Shows:
   - Both chips appear in the Search Chats header.
   - Select **Projects**. The second account's projects join the list without
     pressing refresh. Their subtitle names that account.
   - Select **Artifacts**. Both chips are there, and that list holds both
     accounts' artifacts.

5. **Projects, refresh and filter.**
   - Type part of one project's name into **Filter**.
   - Clear it, then press the refresh arrow.
   - Double-click a row, then press another row's open arrow.

   Shows:
   - The filter narrows the list and the count beside it, with no spinner.
   - Refresh shows a spinner briefly and the same list comes back.
   - Each project opens at `claude.ai/project/…` in the browser. If the browser
     is signed in to the other account, claude.ai says so. That is expected;
     note it.

6. **Search across both accounts.**
   - In **Search Chats**, type the word noted earlier and press Return.

   Shows:
   - A spinner on the button. Then chats from **both** accounts, with the
     first result from each account before any account's second.
   - The matched word is bold in titles and in the grey snippet under each
     chat.
   - The subtitle names the account, and the project when the chat is in one.
   - Code sessions follow the chats. Each shows its folder in monospace and
     "Code · On this Mac", "On another Mac" or "In the cloud".
   - The foot says "N results".
   - Compare against claude.ai's own search box, signed in to each account, for
     the same word. The chats it lists should be among these.
   - Typing without pressing Return asks nothing. Check the diagnostics log
     shows no new search line while typing.

7. **Open a result.**
   - Press the open arrow on a chat, then on a Code session.

   Shows: a chat opens at `claude.ai/chat/…`, a Code session at
   `claude.ai/code/session_…`.

8. **Filters.** The filter icon beside Search is an outline while nothing
   narrows. Open it and check each filter, then **Reset Filters** after each:
   - **Kind → Code Sessions:** only Code sessions. **Chats:** only chats.
   - **Status → Archived:** only archived ones, marked "Archived". **Active
     and Archived:** both. Compare with claude.ai's search box with "Show
     archived" on.
   - **Where → On This Mac:** only Code sessions run here. Compare the folder
     each shows with this Mac's projects. **On Another Mac:** a session run on
     the development Mac is here. **In the Cloud:** only cloud sessions, or
     nothing if neither account has any.
   - **Where → Gone from claude.ai:** if anything shows, each one opens its
     transcript in Finder rather than a browser. Check that one of them really
     is missing from claude.ai's Code list for its account. If nothing shows,
     write "none on this Mac".
   - **Changed → Past Day:** only results changed in the last day.
   - **Account:** one account's results only.
   - **Project → a project:** only that project's chats. **No Project:** only
     chats outside every project, and no Code sessions.
   - **Starred Only:** only starred chats.

   Shows, for each: the icon fills while anything narrows. Hovering it says
   what is narrowing. The foot reads "N of M results". Changing a filter makes
   no new request and shows no spinner.

9. **Search again with the second account changed.**
   - With the results on screen, select **Artifacts** and sign the second
     account out from its chip (**Sign Out**).
   - Select **Search Chats**, then **Projects**.

   Shows:
   - That account's chip is gone from both headers.
   - Its results and its projects are gone, without a refresh.
   - If the account filter named it, the filter is back to every account.

10. **Sign back in from Artifacts.**
    - In **Artifacts**, press **Add Account…**. Aaditya signs in to the second
      account again.

    Shows: in **Search Chats**, the search on screen runs again by itself and
    the account's results return. In **Projects**, its projects return.

11. **The window fits.**
    - Drag the window to its smallest size.
    - Take a picture of Projects and of Search Chats.

    Shows: nothing is cut off at either edge, and the sidebar is whole. The
    account chips scroll sideways if they don't fit. Nothing pushes the
    window wider.

12. **Diagnostics.** Save the diagnostics line above.

    Shows: no `projects.list failed` and no `search failed`. A
    `search.code-sessions refused` line means the session list answered with
    something other than 200. Record its status.

## Putting everything back

1. Quit the development Graft from **Quit** at the bottom of its dropdown.
2. Run `open "/Applications/Claude Graft.app"` if it was installed.
3. The claude.ai sign-ins made in steps 3, 4 and 10 stay, as they would after
   using the panes. Sign out from a chip only if Aaditya asks.

## Report, then clean up

Fill in the results table and the run notes, then send them back as one
message. Delete the `step-…` files, but leave the folder itself: the
development Mac removes it once it has read the results.

## Results

| Step | What was checked | Pass / Fail | Notes |
| ---- | ---------------- | ----------- | ----- |
| 1 | Build, swap, sidebar entries | | |
| 2 | Signed-out panes ask nothing | | |
| 3 | Sign-in from Projects reaches all three panes | | |
| 4 | Sign-in from Search reaches all three panes | | |
| 5 | Projects match claude.ai; refresh, filter, open | | |
| 6 | Search across both accounts; order, bold, Code places | | |
| 7 | Chat and Code links open the right pages | | |
| 8 | Each filter, the icon, "N of M" | | |
| 9 | Sign-out empties every pane at once | | |
| 10 | Sign-in again refills every pane | | |
| 11 | Smallest window fits | | |
| 12 | Diagnostics clean | | |

## Run notes

- Commit tested:
- Word searched:
- Accounts (by chip name):
- Transcripts with a Remote Control id on this Mac:
- Anything unexpected:
