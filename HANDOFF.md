# Claude Graft: where things stand

Rewritten in place; it describes the current state, not a history.

## The Windows port is merged

Both community Windows ports are merged into `main` and GitHub shows both PRs as merged.
- **#16 (Tyler, `snowtyler`)** is the base. It is WinUI 3 on .NET 10 with Velopack installs and updates, everything under `windows/`.
- **#9 (James, `jamesql`)** is merged on top. `windows/` keeps #16's tree, which already carried James's sign-in routing, Store Claude discovery, the state-lock skip and the project-order rollback, each credited to him as co-author.
  - From #9's own changes, only these landed: the Mac half of the rollback (`Sources/Shared/SidebarStorage.swift` and its tests) and a README link.
  - #9's Windows CI workflow was left out, because it calls build scripts that the merged tree doesn't have. A Windows CI workflow written for the current layout is still wanted.
- Both PRs have a thank-you comment saying what was merged and how.
- The owner made both authors code owners of `windows/`. The owner has said `windows/` changes go straight to `main`, without PRs.

## One version line, one release per version

- `VERSION` numbers both systems. `windows/VERSION` is gone.
- A `v*` tag runs `.github/workflows/windows-release.yml`. It tests, publishes, packs with Velopack (`--noPortable`: an installer only, since only an installed Graft can update itself) and uploads the setup, the nupkg and `releases.win.json` to that version's release. If the Mac hasn't made the release yet, the workflow makes it.
- Each version needs `windows/release-notes/<v>.md`. The Mac suite also needs `docs/release-notes/<VERSION>.html`.
- Electron is pinned to 44.9.3, the version tested here.
- Installed Windows copies look for updates in this repository (`Updater.Repo`).
- `GRAFT_UPDATE_FEED` (a folder or URL) points an installed copy at a local feed. It exists for update tests only.

**Plan agreed with the owner.**
- **2.1.0 (now):** Windows only, installer only, tag `v2.1.0` on the commit it was built from.
- **Mac in the meantime:** it stays on 2.0.1 in its Sparkle feed and Homebrew cask until it has passed in the macOS VM.
- **2.1.1 (after the VM pass):** Mac and Windows together, and Windows gets 2.1.1 as an ordinary update.

**Not released yet.** Three things hold the tag:
- the Settings clipping below, which a maximised window on an ordinary 1080p screen would hit;
- the owner's approval of the before/after PDF;
- the owner's sign-in pass on the second PC.

**Existing installs of Tyler's fork.** Installs of Tyler's 2.0.0 still check his fork for updates. They won't get ours until he publishes one bridging update that points them here; he was asked on #16. Until then, they move across by running our setup over their install, which was tested (see below).

## What was checked

**Mac.** On the Mac only builds and suites run; nothing is launched, because extra Claude Desktop instances there can take down the sessions running on that Mac.
- `./test.sh` passes 1456 of 1457 checks.
- The one failure, "the moving text lies exactly where AppKit draws a picture of its size…", also fails on the old `main` with the macOS 26.5 SDK, so it is not caused by the merge.
- `node Tests/sidebar-storage.cjs` passes 25/25, and `./build.sh` builds.

**Windows laptop.**
- `dotnet test windows/ClaudeGraft.Tests` passes 547/547, and the Release publish builds with no warnings.
- The update path, scripted with silent setups and the app started as the desktop user:
  1. Tyler's 2.0.0 installs and runs.
  2. 2.0.9's setup runs over the running 2.0.0. The setup stops 2.0.0; a silent setup doesn't restart Graft, an interactive one does.
  3. With a local feed holding 2.1.0, the installed 2.0.9 found it, downloaded it and installed it on its own. It was running 2.1.0 72 s after start.
  4. With no override, 2.1.0's check of this repository's releases logged no error.

**Second PC (100% scale).**
- 2.1.0 with Donate installs, and its version, settings, Check for Updates and state report pass.
- New Shortcut stays hidden until a Claude is signed in (the first-run gate), so Test A and Test B can only be made after the owner signs in the main Claude.

**Mac, the Windows suite.** `dotnet test windows/ClaudeGraft.Tests` runs on the Mac (SDK in `~/.dotnet`): 509 of 561 pass. The other 52 call Windows APIs or assume Windows paths, so the full suite still runs on a Windows machine.

## Settings is clipped in a wide window (blocks the release)

Once the window is wider than about 1300 px (in DIPs), everything on the Settings page below the title moves right and runs past the window's edge:
- the dropdowns are cut off;
- the switches leave the window;
- the About buttons can't be reached.

At 1086 (the default at 100%) and at 1000 it is fine.

What is known so far:
- The cards keep their 1064 px cap, but they are centred as if the scroll area were about 220 px wider than the window.
- The cause is not known.
- The same `MaxWidth` layout was there before the merge, but no wide shot of the old build exists, so whether it is older is unconfirmed.

The suspects:
- `CardSwitch`'s `-200` right margin;
- `PaletteGrid`'s negative margin and its nested scroller;
- the page's `ScrollViewer` lacking the `HorizontalScrollMode="Disabled"` that `MainPage.xaml` sets.

The next step is a dev build with `dist/tests/scripts/layout-probe.patch` (not committed), run with `GRAFT_LAYOUT_PROBE=1` and resized to 1360. It logs each element's width and position to `%TEMP%\graft-layout-probe.txt`. After the fix, `dist/tests/scripts/probe.ps1` must say `RESULT PASS` at the default size, at 1360 and maximised.

## Visual changes waiting for approval

These are committed but not released.
- **Windows Donate entry.** The sidebar's "Support" link and the tray's "Support Claude Graft…" now read "Donate", with a heart in Tinkerfold's red (#E0492B light, #EE5A3A dark). They still open the support dialog, with the author's Ko-fi and Tyler's.
- **Windows credit.** A "Windows port" card in Settings › About credits Tyler and links to his GitHub profile. Both READMEs credit him and James.

The PDF goes to `/Volumes/SSD_enclosure_2/Developer_2/review/`, built by `dist/tests/scripts/mkpdf.py`.
- The before shots are in `dist/tests/win-donate-before/`. They are the default window on the laptop at 125%, about 1087 DIPs wide.
- The after shots are therefore taken at the default size on the second PC at 100%, which is the same layout.
- One wide pair (`settings-wide-*`) shows the clipping fix.

## Parity: what's left

The full table is `handoff/windows-parity/HANDOFF.md`. Gaps:

**Windows lacks:**
- The chat-conflict prompt before opening a profile another Claude shares (S).
- Help, Report a Problem and email links (S). The address should be support@tinkerfold.com, per the owner's rule; the Mac's "Email the Developer" still uses a personal address.
- The "Recovered N sessions" notice (S).
- Separate front icon and name switches (S).
- Russian (L).

**Mac lacks:**
- Beta projects in Projects (M).
- The ready chime and the done/needs-you dots (M).
- The Fable bar in the dropdown (S).
- First-run guidance when Claude isn't installed or signed in (S–M).
- The debug staging window (M).
- Theme and backdrop choices (probably intended).

**Policy, not a gap:** keep-warm auto-start exists only on Windows, and a Mac rule forbids an unprompted session.

**Mac Donate.** The Mac still says "Support Claude Graft…" and "Support". Renaming it to Donate with the red heart is a visual change, so it waits for the VM, where its screenshots can be taken.

## Known small problems

- Velopack's stub is called `Claude Graft.exe` (from `--packTitle`); the app itself is `current\ClaudeGraft.exe`.

## Rig

- **Laptop working folder:** `%USERPROFILE%\graft-dev`.
  - `pkg\<version>\` holds the test packages: 2.0.0 (Tyler's head), 2.0.9 and 2.1.0 (the release candidate before Donate), and `final-2.1.0` (with Donate).
  - The scripts beside them are `pack.ps1`, `upgrade.ps1` and `fresh.ps1`.
  - Scheduled tasks are named `GR…`.
- **Second PC (lock `win2`, PowerShell over ssh):** Graft's own test machine.
  - Graft 2.1.0 is installed, with one Claude window left at its sign-in screen.
  - The layout and tray helpers in `dist/tests/scripts/` run in the desktop session through `run-desktop.ps1`.
  - Testers reach the tray only through UI Automation (`tray.ps1`), never by clicking at a screen position or by blind keys: blind keys once opened Quick Settings and the PC dropped off the network.
- **Testing:** Claude Desktop is installed on both machines for testing. Test profiles are `Test A` and `Test B`. Signing in is the owner's job. Testers never type emails or codes.
