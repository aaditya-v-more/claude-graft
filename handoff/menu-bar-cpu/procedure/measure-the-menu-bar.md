# Measure the menu bar

[Back to HANDOFF.md](../HANDOFF.md)

## Steps

0. **Where things stand.**
   - Run `git status --short`. It lists nothing.
     - If it lists only `D handoff/…` lines left by an earlier run, run
       `git checkout -- handoff`.
     - If it lists anything else, stop and record it.
   - Run `git pull`. Record `git rev-parse HEAD` and `sw_vers -productVersion`.
   - Run
     `defaults read "/Applications/Claude Graft.app/Contents/Info" CFBundleShortVersionString`.
     It prints `2.0.1`. If it prints anything else, stop and record it.
   - Record `./handoff/shared-usage/check.sh` and which Claudes are open.
   - Record Claude's PID:

         ps -Ao pid=,command= | grep '/Claude.app/Contents/MacOS/Claude' | grep -v -e user-data-dir -e grep

1. **Suite and build.** The installed Graft keeps running through this step.
   - Run `./test.sh`. Its last line reads "N/N checks passed" with both numbers
     the same, and no line above it says FAIL.
   - Run `./build.sh`. Its last line names `build.noindex/Claude Graft.app` and
     2.0.1.
   - If the build fails with "plugin for module 'SwiftUIMacros' not found", run
     `unset SDKROOT` and build again.
   - If the default SDK cannot compile SwiftUI, put
     `SDKROOT=$(xcrun --show-sdk-path)` in front of the command.

2. **Claude 2 borrows Claude.** If step 0's `lenders` already shows Claude 2
   borrowing Claude, note that and go to step 3. Otherwise:
   - Close Claude 2 if it is open: quit it from its own menu and wait for its
     process to go.
   - In the installed Graft's window, select **Claude 2**. Under **Shared
     usage**, choose **Add an Account**, then **Claude**.
   - Open Claude 2 from Graft (**Open**, then **Open Anyway** if it warns), and
     wait for its window.

   Shows, in `check.sh`:
   - Claude 2 `HOLDS THE LINES`.
   - A relay listening on 47823.
   - Claude's own environment untouched.

3. **Two scratch conversations.**
   - Run `mkdir -p /tmp/menu-bar-cpu`.
   - In Claude 2's Code tab, start a new session in that folder and send
     `Work out which of 391, 397 and 403 are prime, and give the reason for each in one line.`
   - When it has answered, start a second new session in the same folder and
     send `Say hi.`
   - Wait until both have titles in Claude 2's sidebar, and record both.
   - With Claude 2 in front, select each conversation in turn and watch Graft's
     item.

   Shows:
   - **The first:** its title, a dot and the account answering it, moving left
     inside a fixed width. Each time round it holds still at its start for
     about a second and a half.
   - **The second:** its text stands still. If it moves too, its title is too
     long to stand still. Record the title, and skip every "still" measurement
     below.

4. **The installed Graft, measured.** Close Graft's window and its dropdown
   first. Each measurement takes two and a quarter minutes. If one figure looks
   out of line with the rest, as when something else woke up, run it again and
   keep both lines.
   - **No text:**
     - Run `./handoff/menu-bar-cpu/cpu.sh 120 "installed, no text"`, then bring
       Claude forward.
     - Graft's item should show Claude's icon and figure, with no conversation
       text.
   - **Moving:**
     - Run `./handoff/menu-bar-cpu/cpu.sh 120 "installed, moving"`, then bring
       Claude 2 forward with the first conversation selected.
     - When it has printed, bring Claude 2 forward again and run the frame
       command.
     - Then record ten seconds of the bar as `step-4-installed.mov`.
   - **Still:**
     - Run `./handoff/menu-bar-cpu/cpu.sh 120 "installed, still"`, then bring
       Claude 2 forward with the second conversation selected.

   Shows:
   - Three lines in `cpu.txt`, each naming `/Applications/Claude Graft.app` for
     Graft and its relay.
   - Copy the figures into the measurements table.

5. **Swap in the development build.**
   - Quit the installed Graft from **Quit** at the bottom of its dropdown.
   - Run `open "build.noindex/Claude Graft.app"`.
   - A keychain prompt for "Claude Safe Storage" may come now or within a
     minute. Aaditya answers it.

   Shows, within about a minute:
   - **Graft:** `check.sh` shows one Graft, under `build.noindex`.
   - **The relay:** the relay on 47823 is now the development build's, and
     `pgrep -fl graft-relay` names `build.noindex`. If the keychain prompt was
     refused or never came, the old relay may stay and the dropdown may say
     lending is paused. Record which.
   - **Claude 2:** it still `HOLDS THE LINES`.
   - **Claude:** still running under step 0's PID.

6. **The text moves as it did.**
   - Bring Claude 2 forward with the first conversation selected.
   - Run the frame command.
   - Record ten seconds of the bar as `step-6-development.mov`, with the same
     rectangle as in step 4.
   - Watch the item yourself for a pass or two as well.

   Shows, against `step-4-installed.mov`:
   - **Movement:** the same speed and the same hold of about a second and a
     half at the start. The same gap comes before the start comes round again.
   - **Drawing:** the whole text moves together. No part of it stands still or
     lags while the rest moves. Nothing is smeared or drawn twice, and nothing
     is left behind where the text was.
   - **The item:** the icons and figure to its left stay put. The frame
     command's x and width match step 4's, unless the figure itself has
     changed in between.
   - **Colour:** the text is the same colour as the figure beside it.
   - **Spoken title:** the frame command's description includes "Conversation
     on screen" and the first conversation's title.

7. **Clicks, and a change of conversation.** Start with the text moving.
   - **Left click:** left-click Graft's item. The dropdown opens under it as
     before, and the text goes on moving while it is open. Close it.
   - **Right click:** right-click the item with a real right-button click. The
     menu opens under it with the conversation's title as its first line.
     Press Escape, choosing nothing. The text is still moving.
   - **To the second conversation:** select it in Claude 2. Within about two
     seconds the bar shows its text, standing still.
   - **Back to the first:** select it again. Its text comes back from its
     start, holds there, then moves.

8. **The development build, measured.** Repeat step 4's three measurements
   with the labels `development, no text`, `development, moving` and
   `development, still`. Skip its recording.

   Shows:
   - **Build:** each line names `build.noindex` for Graft and its relay.
   - **Moving:** Graft's figure is well below the installed build's, about
     half of it or less.
   - **Still and no text:** each is below the installed build's figure, and
     the two are within half a point of each other, since text that stands
     still now costs nothing.
   - **Memory:** Graft's memory is within 10 MB of the installed build's in the
     same condition.
   - **Context:** record ControlCenter and the WindowServer too. They are
     context, not a verdict.
