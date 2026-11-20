# Start hidden keeps only the sign-in launch out of sight

Platforms: Windows
Needs: nothing signed in
Setup: Graft installed and running, "Start with Windows" and "Start hidden"
on in Settings (the defaults). On wintest2, never touch the notification area;
quit Graft only with `ClaudeGraft.exe --quit`.

1. Before installing the build under test, note what the Startup shortcut
   runs: `(New-Object -ComObject WScript.Shell).CreateShortcut("$([Environment]::GetFolderPath('Startup'))\Claude Graft.lnk").Arguments`.
   Expect: empty on 2.1.1 or earlier builds.
2. Install the build under test and let Graft start. Run the same command.
   Expect: `--autostart`. The shortcut still points at
   `%LOCALAPPDATA%\ClaudeGraftApp\current\ClaudeGraft.exe`.
3. Quit Graft with `--quit`. Open it from Start (type "Claude Graft", press
   Enter) once.
   Expect: the manager window appears on that first try.
4. Close the window (Graft keeps running), then open Graft from Start again.
   Expect: the window comes back on the first try.
5. Quit Graft with `--quit`. Start it the way sign-in does: double-click
   `Claude Graft` in the Startup folder (`shell:startup` in Explorer's
   address bar). If this machine may be signed out, sign out of Windows and
   back in instead, and say which you did.
   Expect: Graft runs (its process is there; on a machine where the taskbar
   strip is on, the strip appears) and no window opens.
6. With Graft running hidden from step 5, double-click the Startup shortcut
   again.
   Expect: still no window. Then open Graft from Start: the window appears on
   the first try.
7. In Settings, turn "Start hidden" off; its description reads "At sign-in,
   open in the notification area with no window". Quit with `--quit` and
   repeat step 5.
   Expect: the window opens. Turn "Start hidden" back on.
8. In Settings turn "Start with Windows" off and on again, then run the step 1
   command.
   Expect: `--autostart`.

Pass when: every launch from Start shows the window on the first try, and only
the Startup shortcut (or a real sign-in) with Start hidden on leaves Graft
running with no window.
