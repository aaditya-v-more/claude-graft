# Graft starts, shows its window and its menu-bar item, and quits cleanly

Platforms: macOS, Windows
Needs: nothing special
Setup: The build under test installed (Windows: through its setup; Mac: in
/Applications, or the development build opened from `build.noindex`). Claude
Desktop installed and signed in. No other Graft running.

1. Start Graft.
   Expect: within a few seconds its item is in the menu bar (Mac) or its icon in
   the tray (Windows). The window opens on first start or from the item.
2. Look at the window at its default size, then drag it to its smallest.
   Expect: the sidebar lists Claude, any shortcuts, Artifacts, Projects and
   Search Chats. Nothing is cut off, nothing overlaps, and no page needs
   scrolling at the smallest size. Every note is one line, its longer text
   behind an ⓘ.
3. Select Claude, then Test A, then Settings (Mac: ⌘, ; Windows: the Settings
   page).
   Expect: each shows its account's plan usage or a one-line reason it has
   none, and Settings fits inside the window, including with the window
   maximised.
4. Open the menu-bar dropdown (Mac) or the tray flyout (Windows).
   Expect: one row of usage per account, with the five-hour and weekly
   figures, and Quit at the bottom, inside the screen.
5. Press ⌘Q in Graft's window (Mac only).
   Expect: the window closes and the menu-bar item stays.
6. Choose Quit from the dropdown or flyout.
   Expect: within ten seconds no Graft process is left (`pgrep -fl ClaudeGraft`
   on a Mac, `Get-Process ClaudeGraft` on Windows). A running Claude is not
   closed by it.

Pass when: Graft starts, every page fits, the dropdown or flyout shows usage,
and Quit leaves no Graft process and every Claude as it was.
