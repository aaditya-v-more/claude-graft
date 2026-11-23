# Run the search checks

[Back to HANDOFF.md](../HANDOFF.md)

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
