# Claude Graft for Windows

This is the Windows port of Claude Graft. It does what the macOS app does from a
notification-area icon rather than the menu bar: it runs extra Claude Desktop
profiles side by side, each with its own login, and shares Code history, pins,
usage and accounts between them.

Claude Desktop lays a profile out the same way on both systems, so the reasoning
the macOS app is built on carries across. Where the Mac makes a symlink, Windows
makes a directory junction, which needs no administrator rights. Where the Mac
reads the usage token out of the keychain, Windows unwraps the same key from the
profile's `Local State` with DPAPI, which raises no dialog. A packaged Claude
keeps its data somewhere other than `%APPDATA%\Claude`, and a `claude://` link
reaches the main Claude whichever profile asked for it; `notes/opening-profiles.md`
says how Graft finds the one and routes the other.

The port is the work of Tyler ([snowtyler](https://github.com/snowtyler)).
James ([jamesql](https://github.com/jamesql)) built a second Windows port from
an earlier copy of this one, and several of its pieces were carried over here,
among them the sign-in routing and the rollback of a project order write that
fails partway. That port is
[aaditya-v-more/claude-graft#9](https://github.com/aaditya-v-more/claude-graft/pull/9).

## Install

Install Claude Desktop on your Windows PC. From [Claude Graft releases](https://github.com/aaditya-v-more/claude-graft/releases), download `ClaudeGraftApp-win-Setup.exe` (x64), not an `.nupkg` update package. Run the installer, then open **Claude Graft** from Start. Choose **New Shortcut** to add another Claude profile.

## Layout

- `ClaudeGraft.Core` — the graft itself, with no UI: profiles, the stash, the
  record sweep, the chat-history mirror, credentials and usage.
- `ClaudeGraft` — the WinUI 3 app: the tray icon and the manager window.
- `GraftLaunch` — the launcher a desktop shortcut points at. It squares a
  profile up and opens Claude on it without the app running.
- `GraftRelay` — the Shared Usage relay, which outlives the app.
- `ClaudeGraft.Tests` — the suite, written as sentences the way the macOS one is.

## Building and running

The core and the tests build and run anywhere `net10.0` does:

    dotnet test windows/ClaudeGraft.Tests

The app needs the .NET 10 SDK, Developer Mode and the WinApp CLI:

    winapp run windows/ClaudeGraft --arch x64

To try a build the way an update arrives, over an installed copy, run
`windows/dev-install.ps1` from a terminal outside any Claude Graft manages,
since it quits and restarts Graft.

The icons under `ClaudeGraft/Assets` are scaled from the Mac's
`Resources/AppIcon.icns`. When that changes, regenerate them with:

    python windows/Tools/make-icons.py

## Releases

The Mac and Windows builds share one version, written in the repository's
`VERSION`, and one GitHub release per version. Pushing a `v*` tag runs
`.github/workflows/windows-release.yml`, which tests, publishes and packs the
Windows build with Velopack and adds it to that version's release, making the
release first if the Mac has not. Each version needs its notes in
`windows/release-notes/<version>.md`; the workflow stops without them.

The release carries an installer and the update packages, not a portable copy:
only an installed Graft can update itself. It installs per user under
`%LOCALAPPDATA%\ClaudeGraftApp`, kept apart from the `ClaudeGraft` folder that
holds Graft's data because Velopack's uninstall deletes the folder it installed
into. Graft checks this repository's releases hourly and installs an update
quietly, waiting while a Graft window is open, unless Hold Graft's updates is on
in Settings. A 1.x install is not offered 2.x; running the new setup over it
moves it across.

To watch an installed build update before a release is published, pack the new
version into a folder and set `GRAFT_UPDATE_FEED` to that folder (or to a web
address serving it) for the installed Graft; it then reads that feed instead of
the releases.

## Still to do

Nobody has checked whether Windows Claude, like the Mac's, refuses to write a
session record into a folder that resolves outside the profile; the record
sweep is ported in full either way, and does no harm if it is not needed. The
state report is written after every pass, but so far only the suite has read
one; it has not yet been seen from a Graft running on a real machine.
