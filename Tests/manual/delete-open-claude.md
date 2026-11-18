# Removing a Claude that is open never strands its folder

Platforms: macOS, Windows
Needs: nothing special
Setup: Graft running. Make a scratch shortcut called `Scratch` with Its own
chats and create it (no sign-in needed). Its folder is `Claude-Scratch`.

1. Open Scratch from Graft and leave its window up (it may sit on its sign-in
   screen). Then ask Graft to delete Scratch (Mac: Delete Shortcut…;
   Windows: Delete Shortcut…).
   Expect: Mac: the question offers "Delete Shortcut Only" and Cancel, with no
   "Delete Shortcut and Profile", and says Scratch is open so its profile
   cannot be deleted now. Windows: "Also delete its chats and login" is greyed
   out, with a line saying Scratch is open and to quit it first.
2. Cancel. Quit Scratch from its own menu and wait for it to go. Open the
   delete question again.
   Expect: the option to delete the profile (Mac) or the checkbox (Windows)
   is back.
3. While that question is up, open Scratch again from its own shortcut (Mac:
   its app in ~/Applications or the Dock; Windows: its Start menu or desktop
   shortcut). Then choose deleting the profile too (Mac: Delete Shortcut and
   Profile; Windows: tick the box, then Remove).
   Expect: "Nothing was deleted" (Mac) or "Nothing was removed" (Windows),
   saying Scratch is still open. Scratch is still in Graft's list and its
   folder still exists.
4. Quit Scratch, then delete it with its profile.
   Expect: Scratch leaves the list and `Claude-Scratch` is gone from
   ~/Library/Application Support (Mac) or %APPDATA% (Windows).

Pass when: an open Claude's profile is never offered for deletion, a delete
that would strand its folder removes nothing, and a closed one goes cleanly.
