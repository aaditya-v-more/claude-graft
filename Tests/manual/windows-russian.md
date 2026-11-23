# The Windows app reads in Russian

Platforms: Windows
Needs: Test A signed in (see test-profiles.md)
Setup: Graft installed from the build under test. Quit it with
`ClaudeGraft.exe --quit`, then start it in Russian:
`$env:GRAFT_LANGUAGE='ru'; & "$env:LOCALAPPDATA\ClaudeGraftApp\current\ClaudeGraft.exe"`.

1. Open Graft's window at its default size, then at its smallest.
   Expect: the sidebar, Claude's page with its plan usage card and
   countdowns, and the bottom buttons are in Russian. Nothing is cut off
   mid-word or overlaps; Update/Обновить stays visible at the smallest size.
   Take a picture at each size.
2. Select Test A, then open its editor (name, folder, reads chats from), then
   press Delete without confirming.
   Expect: every label, the source list and the delete question are Russian.
   Cancel.
3. Open Settings and scroll through it.
   Expect: every card, switch label ("Вкл."/"Выкл." or the like), combo box,
   the Help and feedback card's four links and About are Russian and fit.
   Take a picture of the top and of About.
4. Open the flyout from the taskbar strip, not the notification area.
   Expect: the account rows, Запустить сессию and Открыть, the 5-hour, week
   and Fable bars, the status lines and the bottom menu are Russian, and the
   flyout's width holds them. Take a picture.
5. Open Artifacts, Projects and Search Chats.
   Expect: headers, chips, filter menus and counts are Russian. A count of 1,
   3 and 5 items, if any list shows them, uses the right Russian form
   (e.g. 1 артефакт, 3 артефакта, 5 артефактов).
6. Quit Graft and start it plainly, without `GRAFT_LANGUAGE`.
   Expect: English again on an English Windows.

Pass when: every screen listed reads in Russian with nothing clipped, and the
app returns to English without the override.
