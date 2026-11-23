# The strip survives switching, and Quit leaves no crash report

Platforms: Windows
Needs: Claude Desktop running; no new chat or sign-in
Setup: The build under test already installed. Record the strip, icon, name,
backdrop and manager visibility as found. Start the time window **after** the
install: the old process that quit during installation is not this build.

1. In PowerShell in the signed-in session, set `$from = Get-Date` and
   `$exe = "$env:LOCALAPPDATA\ClaudeGraftApp\current\ClaudeGraft.exe"`.
   Open Settings › Taskbar and sound. Turn the strip on, off and on twice,
   without restarting Graft. Leave it on with the icon on and name off.
   Expect: the strip disappears every time it is off. Each time it returns,
   its click opens the flyout. Click outside to dismiss. Do not use the
   notification area, Quick Settings or any network control.
2. Turn the name on while leaving the icon alone. Bring Claude forward and
   then return to Settings. Switch just the name off again.
   Expect: the name changes without a hover or a click on the strip. After
   a minute the same Graft process is still running and the strip still
   opens and dismisses the flyout.
3. Choose Mica in Settings › Appearance. Quit Graft with the flyout open;
   use the following in the signed-in session. Hold the *running app's*
   process handle, not the exit code of the second exe that signals it.
   Start the whole block together, then click the strip during the delay
   and leave the flyout open. Focusing PowerShell again would dismiss it.

       $app = Get-Process ClaudeGraft
       $null = $app.Handle
       Start-Sleep -Seconds 8
       & $exe --quit
       if (!$app.WaitForExit(10000)) { throw 'Graft did not quit' }
       if ($app.ExitCode -ne 0) { throw "Graft exited with $($app.ExitCode)" }

   Expect: no Graft left, the strip gone, Explorer responsive and Claude
   untouched. A missing process alone does not mean Quit worked.
4. Start Graft from Start, open and dismiss the flyout, close the manager
   and repeat the Quit check with both windows hidden. Repeat from Start
   with Acrylic and then Solid, quitting once in each mode. Restore the
   original material on the next start. Do not change Windows' own appearance.
   Expect: each process exits with code 0; starting again works on one try.
5. Give Windows a few seconds to finish reporting, then inspect the Application log from `$from`:

       Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$from} |
         Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' -and
                        $_.Message -match '\bClaudeGraft\.exe\b' } |
         Select-Object TimeCreated, Id, ProviderName, Message

   Expect: no new Graft termination, application error or APPCRASH. An
   empty result is expected; an unreadable log is not a pass. Save any fault and the matching time and
   process ID. In Graft's diagnostics, each requested exit has an `app.quit`
   with that process ID; no `flyout.shutdown` error follows it.

Restore the original backdrop, switches and window state from Start, including
on failure. Do not kill Graft or restart Explorer.

Pass when: switching the strip never strands it, and each requested quit is
a clean exit rather than a crash that happens to remove the process.
