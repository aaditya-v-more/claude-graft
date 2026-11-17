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

**Windows 2.1.1 is released** (tag `v2.1.1`, 18 Nov). It is the first Windows release.
- The 2.1.0 release was withdrawn on 18 Nov, and GitHub won't let a deleted immutable release's tag be made again (`notes/updates-and-releases.md`), so Windows went out as 2.1.1.
- The release is not marked "latest", so the website and README still offer the Mac 2.0.1. Installed Windows copies find it anyway.
- **Mac:** stays on 2.0.1 in its Sparkle feed and Homebrew cask. It passed in the macOS VMs except for the signed-in parts, and takes the next number after 2.1.1.

**How 2.1.1 was checked (second PC).**
- Built from the release tree: `dotnet test` passes 561/561.
- New Shortcut drafts with "Its own chats"; typing the name "Test C" made the folder `Claude-Test-C`; the state line went from Running to Not running live when that Claude quit; Donate opens the tip dialog with both Ko-fi links.
- The published release, with the packages folder cleared first: an installed 2.1.0 found 2.1.1 on GitHub, downloaded it and was running it 81 s after start, with the published DLL. The published setup then ran over it cleanly (`dist/tests/scripts/release-check-211.ps1`).
- Delete Shortcut with "Also delete its chats and login" ticked, while that Claude was open: the shortcut left the list and Graft said the folder was kept because Claude was still running. That is by design.
- **A catch for update tests:** Velopack applies a package of the right name already sitting in `ClaudeGraftApp\packages` without downloading or checking it. Clear that folder before testing a real download.

**Existing installs of Tyler's fork.** Installs of Tyler's 2.0.0 still check his fork for updates. They won't get ours until he publishes one bridging update that points them here; he was asked on #16. Until then, they move across by running our setup over their install, which was tested (see below).

## What was checked

**macOS VMs (18 Nov, `main` at 5ceb0d2, macOS 27.2 guests).** No Mac app bug found.
- `./test.sh` 1457/1457 (the text-position check that fails on the Mac mini passes there); `./test-layout.sh` 1836/1836 in English and in Russian; `./test-sidebar.sh` 25/25 and 23/23 against Claude Desktop 6.6395.0.
- `./test-opening.sh` had gone stale: it no longer compiled once `SessionStarter` used `UsageAPI`, and one check still expected the old failed bundle lookup. Fixed in `2e75655`; 31/31 in the VM.
- A smoke run of the built app: the window opens and fits, the menu-bar item and its dropdown work, Quit leaves nothing running.
- Nothing signed in ran there: usage, Start Session, sharing, Artifacts and Projects need the owner to sign in to Claude in a VM.
- Notes, logs and screenshots are in the session notes folder, not the repo. mac2's disk is nearly full (about 5 GB free).

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
- 2.1.0 installed, and its version, settings, Check for Updates and state report passed.
- New Shortcut stays hidden until a Claude is signed in (the first-run gate), so Test A and Test B can only be made after the owner signs in the main Claude.

**Mac, the Windows suite.** `dotnet test windows/ClaudeGraft.Tests` runs on the Mac (SDK in `~/.dotnet`): 509 of 561 pass. The other 52 call Windows APIs or assume Windows paths, so the full suite still runs on a Windows machine.

## Settings in a wide window (fixed)

Settings ran off the right edge once the window was wider than about 1300 DIPs. The cause is in `notes/interface.md` (WinUI centres a scroller's content by its asked-for width). The fix is `eee8fe2`.

The 2.1.0 package on the second PC was rebuilt from that commit. `dist/tests/scripts/probe.ps1` gives `RESULT PASS` at the default size, at 1360 and maximised. The probe build's patch stays uncommitted in `dist/tests/scripts/`.

## Visual changes waiting for approval

These are committed; they ship in 2.1.1.
- **Windows Donate entry.** The sidebar's "Support" link and the tray's "Support Claude Graft…" now read "Donate", with a heart in Tinkerfold's red (#E0492B light, #EE5A3A dark). They still open the support dialog, with the author's Ko-fi and Tyler's.
- **Windows credit.** A "Windows port" card in Settings › About credits Tyler and links to his GitHub profile. Both READMEs credit him and James.
- **Settings in a wide window** now stays centred and inside the window (`eee8fe2`).

The review PDF is in the owner's Drive, under "Tinkerfold UI review" › Claude Graft, as `claude-graft-windows-2.1.1-review.pdf`. It has four pages: sidebar foot, tray flyout, Settings › About, and wide Settings.

The flyout shots come from `dist/tests/scripts/flyout.ps1`, without touching the tray:
- It posts a mouse-up to Graft's own taskbar strip, a child window of the taskbar, which opens the flyout.
- "Show usage on the taskbar" has to be on for the shot, and is turned off again afterwards.

The PDF is built by `dist/tests/scripts/mkpdf.py` from `dist/tests/review-before/` and `dist/tests/review-after/`, into `/Volumes/SSD_enclosure_2/Developer_2/review/`.
- The before shots are mostly the default window on the laptop at 125%, about 1087 DIPs wide; the wide-Settings before shots are from the second PC at 1360.
- The after shots are the default window on the second PC at 100%, which is the same layout, plus the wide pair.

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

## Signing in a test Claude on Windows

Google and company sign-ins come back as a `claude://` link, and each Claude takes only a code for a sign-in it started itself. On the second PC Windows hands those links to Graft, which asks "Which Claude did you start this sign-in from?" and passes the link to that Claude. Checked on 18 Nov with a dummy link (no real code): the chooser comes to the front, lists every running Claude, and the one picked receives it.
- The owner's first attempts for Test A and Test B left no trace: no link reached Graft, Test A or the main Claude. 2.1.1 logs what the chooser did (`link.signInAsked`, `link.signInSent` with the profile folder, `link.dismissed`, …), never the link.
- To retry: start Continue with Google in that Claude, click Open when Chrome asks to open Claude Graft, choose that Claude in the box, Continue Sign-In. Email sign-in needs no link.
- Claude Desktop logs per profile in `%LOCALAPPDATA%\<profile folder>\logs\main.log`; a delivered link shows as `second-instance` there.

## Known small problems

- Velopack's stub is called `Claude Graft.exe` (from `--packTitle`); the app itself is `current\ClaudeGraft.exe`.

## Rig

- **Laptop working folder:** `%USERPROFILE%\graft-dev`.
  - `pkg\<version>\` holds the test packages: 2.0.0 (Tyler's head), 2.0.9 and 2.1.0 (the release candidate before Donate), and `final-2.1.0` (with Donate).
  - The scripts beside them are `pack.ps1`, `upgrade.ps1` and `fresh.ps1`.
  - Scheduled tasks are named `GR…`.
- **Second PC (lock `win2`, PowerShell over ssh):** Graft's own test machine.
  - Graft 2.1.1 from the published release is installed, and the main Claude is signed in (the owner did it, 18 Nov). Test C is deleted.
  - Test A and Test B were made through Graft (folders `Claude-Test-A`, `Claude-Test-B`) and opened once for the owner to sign in with `<account 2>`. Made before 2.1.1, they read the main Claude's chats; switch them to their own in their details if the owner wants them apart.
  - The layout and tray helpers in `dist/tests/scripts/` run in the desktop session through `run-desktop.ps1`. `ui.ps1` drives Graft's own controls by automation id; pass text with spaces through `-ValueFile`.
  - Nothing touches the tray, Quick Settings, or network or Tailscale controls: no clicks, no keys, no UI Automation presses. The two drop-offs that led to this rule turned out to be power cuts, but the rule stays.
- **Testing:** Claude Desktop is installed on both machines for testing. Test profiles are `Test A` and `Test B`. Signing in is the owner's job. Testers never type emails or codes.
