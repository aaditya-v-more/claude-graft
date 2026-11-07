# The window, the menu bar, colours and Artifacts, tested on a second Mac

This folder is temporary. It is deleted once its results are read.

`main` adds four things that the suites can't fully check:

- **The window fits.** Graft's window fits its smallest size without
  scrolling, with one-line notes and the long text behind ⓘ buttons. The
  Claude Desktop update line now sits in the sidebar.
- **The menu bar.**
  - Graft's own half-filled circle leaves the menu bar while a Claude in front
    is shown there.
  - A Claude that borrows from other accounts shows every account's icon, with
    the one answering its newest conversation lit.
  - The right-click menu shows its title.
  - The dropdown leads with the conversation in front.
- **Colours.** A new Claude takes a colour nothing else wears. An account lent
  in shared usage gets a colour too, or its Claude's colour if it is one.
- **Artifacts.** A new pane lists and shares the artifacts of every claude.ai
  account signed in inside Graft, including sharing with groups Graft keeps.

The plan builds `main`, swaps the installed Graft for the development build,
checks all four live, and puts everything back. Run it from a terminal
outside any Claude, in the repository folder.

## What the Mac needs

- macOS 15 or later, with Xcode or the Command Line Tools, and `python3`.
- **Claude**: Claude installed, its own profile signed in to one account.
- **Claude 2**: a second Claude made by Graft and signed in to a **different**
  account. Its folder is usually `Claude-2`. If the folders differ, pass both to
  `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- **Aaditya at the keyboard** for the two claude.ai sign-ins in Part B, and for
  any keychain prompt.
- Graft installed in `/Applications` is optional. Without it, skip the
  put-back steps that reopen it.

## Tools

- `./handoff/shared-usage/check.sh` (read-only): Grafts running, the relay, each
  profile's Code environment and the lenders.
- `python3 handoff/second-mac/status.py [file.json]` (read-only): what the relay
  carried and which account answered. Prints no token.
- **Diagnostics:**
  `grep -E '"(pool\.|relay|menu\.|artifacts\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`
- **Menu-bar picture:** `screencapture -x -R 0,0,<screen width>,40 handoff/second-mac/<name>.png`.
  If it captures only wallpaper, the terminal lacks Screen Recording; say so and
  use a full-screen `screencapture -x`.
- **Item frame and title, read-only.** Use the development Graft's PID from
  `check.sh`:

      osascript -e 'tell application "System Events" to tell (first process whose unix id is <PID>) to get {position, size, description, title} of every menu bar item of menu bar 2'

- **The notch, if this Mac has one.** Run this once and record the x range it
  prints:

      swift -e 'import AppKit; for s in NSScreen.screens { if let l = s.auxiliaryTopLeftArea, let r = s.auxiliaryTopRightArea { print(l.maxX, r.minX) } }'

- **Graft's files, read-only:** `ls ~/Library/Application\ Support/ClaudeGraft/`,
  with `shortcuts.json`, `artifact-accounts.json` and `artifact-groups.json`
  printed with `cat`.

Save every output and picture in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them: the last step
deletes them with the folder.

## Do not touch

- **Chats:** only the one scratch conversation in A6 is created. No other chat
  is opened or written to.
- **Claudes:** Claude's main profile is never quit. Only Claude 2 is opened and
  quit, and it ends closed.
- **Settings:** leave Open at Login, **Update Claude…**, any Graft update and
  the hold setting alone. Don't move, hide or rearrange menu-bar items.
- **Repository:** nothing is committed, pushed or released. Create or switch no
  branch.
- **Credentials:** claude.ai sign-ins, passwords and email codes are typed by
  Aaditya alone, in Graft's sign-in window. A keychain prompt for "Claude Safe
  Storage" is also for Aaditya, with **Allow** (the one-time option). Record
  which step it came at.
- **Sharing:** only the scratch artifact chosen in B0 has its sharing changed,
  and it ends exactly as it started.

## Part A: the window and the menu bar

1. **Build and swap.**
   - Record `git rev-parse HEAD`, `./handoff/shared-usage/check.sh` and
     `shasum ~/Library/Application\ Support/ClaudeGraft/shortcuts.json`.
   - Run `./test.sh`. It should end "all checks passed" with no FAIL.
   - Run `./build.sh`. Its last line names the version. If it fails with
     "plugin for module 'SwiftUIMacros' not found", run `unset SDKROOT` and
     build again. If the default SDK cannot compile SwiftUI, put
     `SDKROOT=$(xcrun --show-sdk-path)` in front, or use the
     `MacOSX26.5.sdk` path from the README.
   - Quit the installed Graft from **Quit** at the bottom of its dropdown.
   - Run `open "build.noindex/Claude Graft.app"`, then open its window from the
     dropdown.

   Shows:
   - `check.sh`: one Graft, under `build.noindex`; no relay; `lenders: {}`.
   - **Sidebar:** Claude, Shortcuts, **Artifacts**. At the bottom, a "Claude
     Desktop" line with its version, above Support and Source. No update bar
     runs across the top of the window.

2. **The window fits.**
   - Drag the window to its smallest size.
   - Take a full-screen picture with Claude selected, then with Claude 2
     selected.

   Shows, in both:
   - No scroll bar, and nothing hidden under the buttons at the bottom.
   - Each note is one line, with longer explanations behind an ⓘ. Press one ⓘ,
     see it open, then close it.
   - The line under Plan usage reads like "<when> · figure from <time>".
   - Claude 2's top row holds its icon, name and folder on one line. Pressing
     the icon opens a grid of colours. Press Escape, choosing nothing.

3. **Graft's mark gives way.**
   - Bring Claude forward, then take a menu-bar picture and run the frame
     command.
   - Then bring Graft's own window forward and do the same.

   Shows:
   - **With Claude in front:** Graft's item shows Claude's icon and the figure,
     and **no half-filled circle**.
   - **With Graft in front:** record what the item shows. Whenever no Claude
     icon is shown, the half-filled circle is.

4. **A new Claude takes a free colour.** Note Claude 2's icon colour, then press
   **New Shortcut**.

   Shows:
   - The draft's icon is not Claude's orange, and not the colour of any
     existing shortcut. Record which colour it is.
   - Press **Discard**. `shasum` of `shortcuts.json` matches step 1's.

5. **Claude 2 borrows Claude.** Select **Claude 2**. Under **Shared usage**,
   choose **Add an Account**, then **Claude**.

   Shows:
   - The new row starts with Claude's orange icon, then "Claude" and its state,
     all on one line.
   - `check.sh`: Claude 2 `HOLDS THE LINES`, a relay listening on 47823, and
     Claude's own environment untouched.

6. **Every account in the bar, the answering one lit.**
   - Open Claude 2 from Graft (**Open**, then **Open Anyway** if it warns), and
     wait for its window. Close any Graft dropdown or alert.
   - Take a menu-bar picture with Claude 2 in front.
   - Run `mkdir -p /tmp/pool-check-second`. In Claude 2's Code tab, start a new
     session in that folder and send `Reply with the word ready.`
   - About 40 seconds after the reply, take a menu-bar picture, and run the
     frame command and `status.py`.

   Shows:
   - **Before the conversation:** Graft's item shows Claude 2's icon and
     Claude's side by side, both full size, with no half-filled circle.
   - **After:** the icon of the account `status.py` says answered is full size,
     and the other is smaller and faint. The order is unchanged.
   - The description includes "Answering now:" and that account's name.
   - If the Mac has a notch, the item does not overlap its x range.

7. **The right-click menu, and the light follows a move.** This must happen
   within two minutes of A6's reply.
   - Right-click the centre of Graft's item with a real right-button click.
   - Straight away, `screencapture -x` the whole screen.

   Shows: a small menu under the item.
   - **Its first line is the conversation's title**, or "New chat", with no
     scroll arrow above it.
   - Then **Automatic**, Claude 2 and Claude, with the current choice ticked.

   Then:
   - Choose the account that did **not** answer, and send
     `Reply with the word moved.`
   - About 40 seconds later, take a menu-bar picture and run `status.py`.

   Shows: the lit, full-size icon is now the other account's, and `status.py`
   agrees.

8. **The dropdown leads with that conversation.** Left-click Graft's item.

   Shows:
   - Under "Claude Code", the A6 conversation comes first, in slightly heavier
     text, with the answering account's icon beside its name.
   - **If there are other recent conversations:** a line "Other conversations ·
     n" with a chevron, which shows them when pressed and hides them when
     pressed again.
   - Usage notes are one line each.
   - Close the dropdown.

9. **Unshare and close Claude 2.**
   - Choose **Automatic** for the conversation in the dropdown.
   - Quit Claude 2 from its own menu, and wait for its process to go.
   - In Graft, select **Claude 2** and remove Claude with the minus button.

   Shows, within about 30 seconds:
   - `check.sh`: `lenders: {}`, no relay, and nothing on 47823.
   - Claude still running, under the same PID as in step 1.
   - With Claude in front, Graft's item shows a single Claude icon again.

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

## Putting everything back

1. Quit the development build from **Quit** in its dropdown.
2. If an installed Graft existed, run `open "/Applications/Claude Graft.app"`.
3. `check.sh`: at most one Graft, under `/Applications`. No relay,
   `lenders: {}`, and no Code environment lines in either profile.
4. Claude is running, and Claude 2 is closed.
5. The scratch artifact's Share panel reads **Only people invited**, with
   nobody else.
6. If a relay is still listed while nothing holds the lines, run
   `pkill -f graft-relay`. Run `rm -rf /tmp/pool-check-second`.

## Report, then clean up

1. Fill in the table below and the run notes.
2. Print both in the terminal, in full, so Aaditya can carry them back to the
   development Mac.
3. Delete the whole testing folder from this checkout, including every
   `step-…` output and picture, and the shared-usage tools it borrowed:

       rm -rf handoff
       git status --short

   `git status --short` should list only `D handoff/…` lines: the deletion is
   left uncommitted. The development Mac removes the folder from the repository
   in a commit of its own, and the next pull here then matches.
4. Commit and push nothing.

## Results

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| A1 | Suite passes; development Graft alone; update line in sidebar; Artifacts listed | | |
| A2 | Smallest window: no scrolling, one-line notes, ⓘ opens, icon grid | | |
| A3 | Claude in front: no half circle; what showed with Graft in front | | |
| A4 | New draft takes an unused colour; Discard leaves shortcuts.json unchanged | | |
| A5 | Claude 2 borrows Claude; lender row shows Claude's icon | | |
| A6 | Both icons shown; answering one lit; clear of any notch | | |
| A7 | Right-click menu shows its title; light follows the move | | |
| A8 | Dropdown leads with the conversation; others behind a count | | |
| A9 | Unshared, no relay; one icon again | | |
| B0 | Scratch artifact chosen, private at the start | | |
| B1 | Signed-out pane: one line, ⓘ, Sign In | | |
| B2 | Sign-in closes itself; chip and list; file holds no secret | | |
| B3 | List, filters, icons and search agree with claude.ai | | |
| B4 | Link copied and opened | | |
| B5 | Anyone with the link, seen on claude.ai | | |
| B6 | Group made and saved | | |
| B7 | Second account listed | | |
| B8 | Shared with the group, listed once, put back | | |
| B9 | Private again from Graft, seen on claude.ai | | |
| B10 | Sign-ins survive a restart | | |
| B11 | Signing out removes the account | | |
| B12 | Last sign-out; no failures logged | | |
| Back | Installed Graft alone; nothing shared; scratch artifact private | | |

## Run notes
