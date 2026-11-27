# The keep-window-open switch holds what it was set to

Platforms: Windows
Needs: two Claudes besides the main one; nothing need be signed in
Setup: Graft installed and freshly started. Note each Claude's "Keep the
5-hour window open" setting before starting, and put each back afterwards.
Nothing here sends a message: with nobody signed in, no window can be opened.

1. Pick a Claude whose switch is off and click the switch once.
   Expect: it reads On.
2. Pick another Claude in the sidebar, then the first one again.
   Expect: still On. Before the fix this was the first click on a new card,
   which was dropped, so the switch came back Off.
3. Quit Graft with `ClaudeGraft.exe --quit` and start it again. Pick the same
   Claude.
   Expect: still On.
4. Move to the switch with Tab and press Space to turn it Off, then repeat
   step 2.
   Expect: Off.
5. Turn on Narrator (Ctrl+Win+Enter), move to the switch, and flip it with
   Narrator. Turn Narrator off and repeat step 2.
   Expect: the switch holds what Narrator set.
6. Pick the other Claudes in turn, some with the setting on and some off,
   without touching a switch. Then compare `keepWarm` for each in
   `%APPDATA%\ClaudeGraft\shortcuts.json` with what was noted in Setup.
   Expect: only the Claude changed in steps 1 to 5 differs.

Pass when: every change made by click, key or Narrator is still there after
switching away and back and after a restart, and no Claude's setting changes
without one.
