# The Mac says Donate with a red heart

Platforms: macOS
Needs: nothing special
Setup: The build under test running; macOS in Light appearance first.

1. Open Graft's window and look at the foot of the sidebar.
   Expect: "Donate" with a filled red heart on the left, "Source" on the
   right. The word is grey like Source; only the heart is red. Take a
   picture of the sidebar foot.
2. Hover Donate, then press it.
   Expect: the word underlines under the pointer; the browser opens
   ko-fi.com/aadityavmore.
3. Open the menu-bar dropdown.
   Expect: near the bottom, "Donate…" with the same red heart before it, in
   place of "Support Claude Graft…". Nothing in the dropdown moves or
   overflows. Take a picture of the dropdown's lower half.
4. With Graft's window in front, open the Help menu.
   Expect: "Donate…" in place of "Support Claude Graft…"; it opens the same
   page.
5. Switch macOS to Dark appearance and repeat the pictures of steps 1 and 3.
   Expect: the heart is a lighter red that still reads clearly on the dark
   background.
6. Set macOS to Russian (or run Graft with `-AppleLanguages '(ru)'`) and look
   at the sidebar foot and the dropdown.
   Expect: "Пожертвовать" and "Пожертвовать…", each word whole on one line,
   never broken or cut off. If the sidebar is too narrow for Donate and Source
   side by side, Source sits under Donate. Widen the sidebar to its widest
   and back: they move side by side once both fit.

Pass when: every place that said Support now says Donate with the red heart, in
both appearances and both languages, and each opens the Ko-fi page.
