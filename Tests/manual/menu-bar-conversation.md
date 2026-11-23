# The conversation on the menu bar moves as in the release, for less

Platforms: macOS (15 or later)
Needs: two signed-in accounts (Claude and Test A)
Setup: The last release installed in `/Applications` and running. The build
under test built with `./build.sh`, not opened. Test A drawing on Claude
(`shared-usage.md` step 1) and open. Graft's window and dropdown closed.

Two tools, run from the repository folder:

- **What it costs:** `Tools/menu-bar-cpu.sh 120 "<label>"` prints the share of
  one core Graft, its relay, ControlCenter and the WindowServer used over two
  minutes, and adds it to `Tests/results/menu-bar/cpu.txt`. Start it, bring
  forward what the step names within its 15 seconds, then touch nothing until
  it prints.
- **Ten seconds of the bar:** find Graft's item with
  `osascript -e 'tell application "System Events" to tell (first process whose unix id is <PID>) to get {position, size, description} of every menu bar item of menu bar 2'`,
  using `pgrep -x ClaudeGraft` for the PID. Then run
  `sleep 5; screencapture -x -V 10 -R <x>,0,<width>,40 Tests/results/menu-bar/<name>.mov`,
  from 40 points left of the item to 40 points past it, and bring Test A
  forward in the five seconds.

1. In Test A's Code tab start a session in an empty scratch folder and send
   `Work out which of 391, 397 and 403 are prime, and give the reason for each in one line.`
   When it has answered, start a second session there and send `Say hi.` With
   Test A in front, select each in turn and watch Graft's item.
   Expect: Test A's and Claude's icons side by side, the answering account's
   full size in a red ring and the other small and faint. The first session's
   title, a dot and the answering account move left inside a fixed width,
   holding at the start for about a second and a half each time round. The
   second session's text stands still; if it moves, its title is too long, so
   note it and skip the "still" figures.
2. Measure the release: label `release, moving` with the first session in
   front, `release, still` with the second, and `release, no text` with Claude
   in front. Record the bar as `release.mov` with the first session in front.
   Expect: three lines, each naming `/Applications` for Graft and its relay.
   With Claude in front the item shows Claude's icon and figure, and no text.
3. Quit the release from Quit in its dropdown. Run
   `open "build.noindex/Claude Graft.app"`.
   Expect: within about a minute `pgrep -fl ClaudeGraft` and
   `pgrep -fl graft-relay` name only `build.noindex`, and Test A still draws on
   Claude.
4. Bring Test A forward with the first session selected. Watch the item for a
   pass or two, then record it as `build.mov` with the same rectangle.
   Expect, against `release.mov`: the same speed, hold and gap; the whole text
   moves together, with nothing smeared, lagging or left behind; the icons and
   figure to its left stay put; the text is the figure's colour. The item's
   description includes "Conversation on screen" and the session's title.
5. Left-click the item and close the dropdown. Right-click it with a real
   right-button click and press Escape. Select the second session, then the
   first again.
   Expect: the dropdown opens under the item, the session first under Claude
   Code with the answering account's icon, and the text goes on moving. The
   right-click menu starts with the session's title, then Automatic, Test A
   and Claude, the current choice ticked. The second session's text shows
   within about two seconds, standing still; the first comes back from its
   start, holds, then moves.
6. Measure the build as in step 2, with labels starting `build`.
   Expect: each line names `build.noindex`. With text moving, Graft's figure
   is no higher than the release's, and about half or less of 2.0.1's. Still
   and no text are within half a point of each other and no higher than the
   release's. Graft's memory is within 10 MB of the release's in the same
   condition. ControlCenter and the WindowServer are context, not a verdict.
7. Quit the build from its dropdown and run
   `open "/Applications/Claude Graft.app"`. Quit Test A from its own menu and
   remove Claude from its Shared usage.
   Expect: within about 30 seconds one Graft runs, from `/Applications`, and
   no relay.

Pass when: the text moves exactly as in the release, clicks and switching work
while it moves, and the build costs no more than the release in any condition,
and about half or less of 2.0.1's with text moving.
