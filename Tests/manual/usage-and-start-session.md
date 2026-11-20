# Each account's usage is shown, and Start Session opens a five-hour window

Platforms: macOS, Windows
Needs: two signed-in accounts (Claude and Test A)
Setup: Graft running. Claude and Test A signed in to different accounts, both
closed. On a Mac, the first read of a login may raise a keychain prompt for
"Claude Safe Storage"; whoever runs the case answers it with Allow, never
Always Allow.

1. Open Graft's window and select Claude, then Test A.
   Expect: each shows five-hour and weekly bars with a reset time, or "No
   5-hour limit" in place of the first for an account without one. The line
   under the bars says how old the figure is. The two accounts' figures
   differ, and each matches claude.ai's usage page for that account.
2. Open the dropdown (Mac) or flyout (Windows).
   Expect: the same figures as the window, one row per account.
3. Press the refresh arrow beside Test A's figure.
   Expect: Test A's "figure from" time moves to now; Claude's does not.
4. Pick an account whose five-hour window is not running (no five-hour reset
   time shown). Press Start Session for it, once.
   Expect: within about a minute that account shows a five-hour reset about
   five hours away, and its five-hour figure is above 0%. Graft did not open
   any Claude window to do it. The other account's figures are unchanged.
5. Leave Graft open for ten minutes without touching anything.
   Expect: no new session starts for any account (no five-hour window begins
   on its own), except on Windows for an account with "Keep the 5-hour window
   open" switched on.

Pass when: both accounts show their own figures in both places, a press of
Start Session starts that account's window and only that one, and nothing
starts one unasked.
