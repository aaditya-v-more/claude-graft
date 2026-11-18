# An installed Graft updates itself to the build under test

Platforms: Windows
Needs: nothing special
Setup: The previous release installed by its own setup and running. The build
under test packed as a release (setup, nupkg and `releases.win.json`) in a
local folder. `%LOCALAPPDATA%\ClaudeGraftApp\packages` emptied first, since a
package already there is applied without being downloaded or checked.

1. Point the installed Graft at the local folder: set `GRAFT_UPDATE_FEED` to
   it for the desktop user, then quit Graft from the flyout and start it from
   the Start menu.
   Expect: it starts on the previous version (Settings › About).
2. Wait up to three minutes with Graft's window closed.
   Expect: Graft restarts on its own, and Settings › About shows the new
   version. The diagnostics log records the update with no error.
3. Remove `GRAFT_UPDATE_FEED`, restart Graft, and press Check for Updates in
   Settings › About.
   Expect: it reports no newer version, and the log has no error from the
   check against GitHub.
4. Run the build under test's setup over the installed copy.
   Expect: it finishes, Graft runs, and the shortcuts, settings and sign-ins
   are as they were.

Pass when: the previous release finds, installs and runs the new build on its
own, and the setup runs cleanly over it.
