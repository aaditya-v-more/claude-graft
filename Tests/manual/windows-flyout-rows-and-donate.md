# The flyout fits three Claudes and Donate works before the manager opens

Platforms: Windows
Needs: Claude running, three profile rows and a fourth disposable shortcut.
For the full-height check, the first three must show their usage bars.
Setup: record Start hidden and the strip settings. Enable Start hidden and the
strip, quit with `ClaudeGraft.exe --quit`, then run the Startup shortcut with
`--autostart`. Note the new PID. Do not open the manager yet.

1. Click the taskbar strip. Look at the actions below the profiles.
   Expect: Donate comes first, with an outline heart in the colour of its text.
2. Press Donate. Check that both tip destinations are offered. Dismiss
   without paying, then open Donate from the manager's sidebar.
   Expect: the manager and dialog open on the first try, with the original
   PID. The sidebar uses the same heart. Dismissing the dialog returns to a
   working manager.
3. With exactly three fully populated profiles, reopen the flyout.
   Expect: when the work area can fit them, all three rows are visible and
   the list has no vertical scroll range. The footer fits on screen. Save a
   picture; record the work area, scale and which rows had usage bars.
4. Add a disposable fourth profile and reopen the flyout. Scroll the list.
   Expect: the fourth row is reachable, at most three rows are shown in full
   at once, and the actions do not scroll away. Capture the top and bottom.
   On a shorter work area the list may scroll sooner to keep Quit reachable.
   Record any screen sizes or populated row counts not exercised.
5. Remove only the disposable fixtures, restore the strip and Start hidden.
   Check Windows' Application log over the run as in
   [the strip lifetime case](windows-strip-lifetime.md).
   Expect: no new Graft crash. Empty or signed-out rows do not establish the
   fit of three full rows.

Pass when: Donate works from a fresh hidden start and the list scrolls only
when its rows need more space, leaving all the flyout actions reachable.
