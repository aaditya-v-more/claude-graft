# Artifacts

[Back to HANDOFF.md](../HANDOFF.md)

## Part B: Artifacts

B0. **Pick the scratch artifact.**
   - In a browser signed in to Claude's account, open
     `https://claude.ai/artifacts`.
   - Choose one artifact of **yours** whose Share panel reads **Only people
     invited**, with nobody but you under "People with access", and which
     nobody else needs. "Pool check B" is one if it is there.
   - If there is none, ask Aaditya to pick one. Don't create one.
   - Record its title and address. Close the panel without changing anything.

   Below, **the scratch artifact** means this one.

1. **Signed out.** In Graft's sidebar, select **Artifacts**.

   Shows:
   - One line, "Sign in to see artifacts", with an ⓘ that opens the full
     explanation.
   - A **Sign In…** button, and **Groups (0)**.
   - Nothing scrolls.
   - `artifact-accounts.json` is absent or `[]`.

2. **Sign in to Claude's account.** Press **Sign In…**. A window titled "Sign In
   to claude.ai" opens. Aaditya signs in there with Claude's account; record
   the method used.

   Shows:
   - The window closes by itself.
   - A chip with a green dot reads **Claude**, or the address if no Claude on
     this Mac matches.
   - The list fills, with "N artifacts" at the right.
   - `artifact-accounts.json` holds one entry with `id`, `email`,
     `accountUuid` and `org`, and **no cookie, token or session**.

3. **The list against claude.ai.** Compare with the browser's
   `https://claude.ai/artifacts`, read-only.

   Shows:
   - Everything in **All** is in Graft's list, newest first.
   - Graft's **Yours** and **Shared with You** filters match the browser's
     tabs.
   - Each row's lock, people or globe icon agrees with claude.ai.
   - Typing part of the scratch artifact's title in Search narrows the list
     to it.

4. **Copy and open.** On the scratch artifact, press the link button and run
   `pbpaste`. Then press the open button.

   Shows:
   - `pbpaste` prints its address.
   - The status line reads "Link copied · only people with access can open
     it".
   - The browser opens on that artifact.

5. **Anyone with the link.** Open the scratch artifact's lock menu and choose
   **Anyone with the Link**.

   Shows:
   - A globe, and "Anyone with the link can view “<title>”".
   - In the browser, reload it and open **Share**. General access reads
     **Anyone with the link**. Close it without changes.

6. **A one-person group.**
   - Press **Groups (0)**, then **+**, and rename the group to `Live check`.
   - Put in one address: the email of Claude 2's account. If it isn't known
     yet, do step 7 first.
   - Press **Done**.

   Shows: **Groups (1)**, and `artifact-groups.json` holds `Live check` with
   that address.

7. **A second account.** Press **Add Account…**. Aaditya signs in with Claude
   2's account.

   Shows:
   - A second chip.
   - That account's artifacts join the list.
   - `artifact-accounts.json` holds two entries.

8. **Share with the group, then put it back.**
   - On the scratch artifact, open the person-with-plus menu and choose
     **Live check (1)**. Then refresh the pane.
   - Then, in the browser's Share panel for it, remove the invited person and
     set General access to **Only people invited**. Refresh Graft's pane again.

   Shows:
   - **Before the put-back:**
     - "Shared “<title>” with 1 of 1 in Live check".
     - An invitation email goes to that address.
     - claude.ai's Share panel lists that person.
     - After the refresh, the artifact is listed once, under Claude.
   - **After the put-back:** the lock again.

9. **Private again from Graft.** On the scratch artifact, choose **Anyone with
   the Link**, then **Only People Invited**.

   Shows:
   - "“<title>” is private again", and the lock.
   - In the browser, the Share panel reads **Only people invited**, with
     nobody but the owner.

10. **Sign-ins survive a restart.**
    - Quit the development Graft from its dropdown.
    - Open `build.noindex/Claude Graft.app` again and select **Artifacts**.

    Shows: both chips come back green, and the list loads with no sign-in
    window.

11. **Sign out of the second account.**
    - In Claude 2's chip menu, choose **Sign Out**.
    - Delete `Live check` in **Groups** with the minus button.

    Shows:
    - That chip and its artifacts leave the list.
    - `artifact-accounts.json` holds one entry, and `artifact-groups.json`
      holds `[]`.

12. **Sign out of the first.** In Claude's chip menu, choose **Sign Out**.

    Shows:
    - "Sign in to see artifacts" again.
    - `artifact-accounts.json` holds `[]`.
    - Diagnostics show no `artifacts.list failed`. Record any that appear.
