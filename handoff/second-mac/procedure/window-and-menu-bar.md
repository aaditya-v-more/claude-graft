# Window and menu bar

[Back to HANDOFF.md](../HANDOFF.md)

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
