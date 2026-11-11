# What the menu bar costs, measured on the Mac Mini

This folder is temporary. It is deleted once its results are read.

Since 2.0, the conversation on screen runs across Graft's item in the menu bar
whenever there is one. On the development Mac, the installed 2.0.1 then used
about 9% of a core with nothing else asked of it. `main` makes three things
cheaper:

- **The moving text.** Each step used to give the status item a whole new
  title, twelve and a half times a second, and AppKit measured, sized and read
  the item out again every time. The item now keeps one picture of the text and
  is only asked to draw it again, a little further along. Text short enough to
  stand still runs no timer at all.
- **The look at Claude's session records**, every two seconds, dates each
  record with one file-system call instead of reading all its attributes. That
  is eight times cheaper.
- **The process check**, every five seconds, works out where Claude's installer
  lives once per listing instead of once per line.

The suite proves that the picture moves within itself and that a step asks only
for a redraw. It also proves the new date and process checks. It cannot show
that the menu bar puts a redrawn picture on screen, or what Graft spends live.

This plan measures the installed Graft first. Then it builds `main`, swaps the
build in, and checks that the text moves exactly as before. It measures again
under the same conditions and puts the installed Graft back. Run it from a
terminal outside any Claude, in the repository folder.

## What the Mac needs

- macOS 15 or later, with Xcode or the Command Line Tools, and `python3`.
- **Graft 2.0.1** in `/Applications`. Everything is measured against it.
- **Claude**: its own profile, signed in to one account. It stays open
  throughout.
- **Claude 2**: a second Claude made by Graft, signed in to a **different**
  account. Its folder is usually `Claude-2`. If the folders differ, pass both
  to `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- **Aaditya at the keyboard** for any keychain prompt.
- **Screen Recording** for the terminal, for `screencapture`, and
  **Accessibility**, for the frame command. If either is missing, say so in the
  run notes and carry on with what works.

## Tools

- `./handoff/shared-usage/check.sh` (read-only): the Grafts running, the relay,
  each profile's Code environment and the lenders.
- `./handoff/menu-bar-cpu/cpu.sh <seconds> <label>` (read-only).
  - It first gives you 15 seconds to bring forward what the step names and let
    the bar catch up.
  - It then prints the share of one core that Graft, its relay, ControlCenter
    and the WindowServer used over the stretch, with each one's memory. For
    Graft and the relay it also prints which build is running.
  - Each result is also added to `cpu.txt` beside it.
  - Start it, bring forward what the step says, then touch nothing until it
    prints.
- **The item's frame and spoken title**, read-only, using Graft's PID from
  `check.sh`:

      osascript -e 'tell application "System Events" to tell (first process whose unix id is <PID>) to get {position, size, description} of every menu bar item of menu bar 2'

- **Ten seconds of the bar:**

      sleep 5; screencapture -x -V 10 -R <x>,0,<width>,40 handoff/menu-bar-cpu/<name>.mov

  - `<x>` is 40 points left of Graft's leftmost item in the frame command's
    output. `<width>` reaches 40 points past its right edge.
  - The five seconds are for bringing a Claude forward after pressing Return.
  - If no file appears, drop `-R …` to record the whole screen, and note it.
  - If the recording shows only wallpaper, the terminal lacks Screen
    Recording.
- **Diagnostics:**
  `grep -E '"(pool\.|relay|menu\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`

Save every output and recording in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them.

## Do not touch

- **Chats:** only the two scratch sessions in step 3 are created. No other
  chat is opened or written to.
- **Claudes:** Claude's main profile is never quit. Only Claude 2 is opened
  and quit, and it ends open or closed as step 0 found it.
- **Settings:** leave Open at Login, **Update Claude…**, any Graft update and
  the hold setting alone.
- **The menu bar:** don't move, hide or rearrange its items. Change neither the
  appearance nor the wallpaper, since either changes what the bar draws.
- **Repository:** nothing is committed, pushed or released. Create or switch
  no branch.
- **Credentials:** a keychain prompt for "Claude Safe Storage" is Aaditya's to
  answer, with **Allow** (the one-time option). Record which step it came at.
- **Sharing:** the only change allowed is Claude 2 borrowing Claude. Step 2
  sets that up if it isn't already, and the put-back undoes it if step 2 set it
  up.

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

## Putting everything back

1. Quit the development build from **Quit** in its dropdown.
2. Run `open "/Applications/Claude Graft.app"`.
3. **If step 2 set up the borrowing:** quit Claude 2 from its own menu and wait
   for its process to go. Then, in Graft, select **Claude 2** and remove Claude
   under **Shared usage** with the minus button.
4. Leave Claude 2 open or closed, as step 0 found it.
5. Run `rm -rf /tmp/menu-bar-cpu`. The two scratch sessions stay in Claude 2's
   list.

Shows, within about a minute:

- **Graft:** `check.sh` shows one Graft, under `/Applications`.
- **Lenders:** as step 0 found them.
- **The relay:**
  - If one is listed, `pgrep -fl graft-relay` names `/Applications`.
  - If step 0 had no relay, there is none now.
  - If one is left while nothing holds the lines, run `pkill -f graft-relay`.
- **Claude:** running under step 0's PID.

## Report, then clean up

1. Fill in both tables below and the run notes.
2. Print this file and `cpu.txt` in the terminal, in full, so Aaditya can carry
   them back to the development Mac.
3. Copy the two recordings to `~/Desktop/menu-bar-cpu/` for him.
4. Delete this folder alone, with every `step-…` output, recording and
   `cpu.txt` in it:

       rm -rf handoff/menu-bar-cpu
       git status --short

   `git status --short` lists only `D handoff/menu-bar-cpu/…` lines. Leave the
   deletion uncommitted. The development Mac removes the folder in a commit of
   its own, and the next pull here then matches.
5. Commit and push nothing.

## Results

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| 0 | Clean checkout; installed Graft 2.0.1; state, macOS and Claude's PID recorded | | |
| 1 | Suite passes; build names 2.0.1 | | |
| 2 | Claude 2 borrows Claude; relay on 47823 (or it already did) | | |
| 3 | First title moves and holds at its start; second stands still | | |
| 4 | Three installed measurements; ten seconds of the bar | | |
| 5 | Development Graft alone, with its own relay; lines still held; when the keychain asked | | |
| 6 | Same speed, hold and gap; text moves whole, leaving nothing behind; item unchanged; colour; spoken title | | |
| 7 | Dropdown and right-click menu while it moves; switching conversations | | |
| 8 | Development measurements: moving about half or less; still close to no text; memory | | |
| Back | Installed Graft alone; sharing and Claudes as step 0 found them | | |

## Measurements

Share of one core over two minutes, from `cpu.txt`.

| Condition | Graft, installed | Graft, development | Relay, installed → development | ControlCenter, installed → development | WindowServer, installed → development |
| --------- | ---------------- | ------------------ | ------------------------------ | -------------------------------------- | ------------------------------------- |
| No text | | | | | |
| Moving | | | | | |
| Still | | | | | |
| Graft's memory while moving | | | | | |

## Run notes
