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
   Read the full freshness line without hovering. At minimum height, Claude,
   the two test shortcuts, New Shortcut and Settings stay in view together
   with the sidebar expanded. Take a picture at each size. If there is no
   real Update button, in Settings › About press the Graft version card's
   icon seven times to open the debug window. Set Claude Desktop to
   “Update available”, inspect the sidebar, then put it back to “As it is”
   and close that window. Label that picture staged; do not install anything.
2. Select Test A, then open its editor (name, folder, reads chats from), then
   press Delete without confirming.
   Expect: every label, the source list and the delete question are Russian.
   Cancel.
3. In Settings visit each section from the picker, including О программе.
   Expect: every card, switch label ("Вкл."/"Выкл." or the like), combo box,
   the Help and feedback card's four links and About are Russian and fit.
   At minimum width, the Help and feedback heading and description must be
   readable along with all four links, not a mostly empty card. Take pictures
   of Appearance and About. Use the fit check in [smoke.md](smoke.md).
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
