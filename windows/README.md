# Claude Graft for Windows

Run Claude Desktop profiles side by side, each with its own login, and share
Claude Code history between them. The window follows the macOS app: profiles
in the sidebar, the shortcut and chat settings on the right, and Support and
Source at the bottom. The window uses the macOS layout and visual treatment:
traffic-light window controls, blue sidebar selection, rounded grouped rows,
compact action buttons and a two-row grid of six icon presets per row. Its
default size is 820 by 560, with a 720 by 460 minimum, matching the Swift app.
Support still goes to
[the project's Ko-fi page](https://ko-fi.com/aadityavmore).

This is the Windows development port. Windows 10 version 2004 or later is
required. Claude Desktop must already be installed; both the Microsoft Store
package and the older standalone installation are detected. Store profiles
live in the package's LocalCache directory, while standalone profiles live
under Roaming AppData. Graft discovers the matching location instead of mixing
the two. Graft's own state lives in LocalAppData/ClaudeGraft.

## Install

Run the windows-x64-setup.exe download on an Intel or AMD PC, or the
arm64 setup on Windows on ARM. The setup wizard installs Graft for your Windows
account and adds it to the Start menu. A desktop shortcut and Open at Login are
optional. Opening Graft from the Start menu shows the manager immediately.

The installer includes the .NET and Windows App SDK runtimes, and checks for
the Microsoft Visual C++ runtime. If that prerequisite is missing or too old,
Microsoft's bundled installer runs first and may request administrator approval.
The development setup executable is not code-signed.

Remove Graft through Windows Installed apps. Uninstall removes the application
files and shortcuts created by setup; it preserves your Claude profiles, chats,
Graft settings, and generated profile launchers.

## Build and run

Install the .NET 10 SDK on Windows, then run this from the repository root:

```powershell
./windows/build.ps1
```

The script runs the isolated tests, publishes the app and its launcher with
their runtimes, verifies the required files, and creates a ZIP and SHA-256
checksum in windows/dist. Use -Architecture arm64 for Windows on ARM. It does
not publish a release, install an application or change a system registration.

To create the setup executable after publishing, run
./windows/build-installer.ps1 -SkipBuild. It uses Inno Setup and verifies the
signature on the Microsoft runtime before including it. Pass -Compiler with
the full path to ISCC.exe to select a compiler explicitly. If none is installed,
the script prepares a signature-verified portable compiler under windows/dist.
The installer and its checksum are written to windows/dist.

For an isolated install/uninstall check, build with -TestMode and pass that
setup-test.exe to ./windows/test-installer.ps1 -Installer. This variant creates
no installed-app entry or shell shortcuts, verifies the installed app in a
disposable directory, and checks that uninstall preserves unrelated files.

Extract the entire ZIP to a folder you intend to keep, then open ClaudeGraft.exe.
The notification-area icon opens the account list; its menu opens the manager.
Run ClaudeGraft.exe --show to open the manager immediately. The complete folder
is required; the executable alone is not a distribution. The Visual C++ v14
runtime is required by the Windows App SDK if it is not already on the machine.

For a quicker development cycle:

```powershell
cd windows
dotnet test ClaudeGraft.Tests -c Release -p:RestoreLockedMode=true
dotnet build ClaudeGraft -p:Platform=x64
```

Creating shortcuts requires the published app, because that folder includes
the standalone launcher. All tests use disposable profiles, including their
transcript directory. They never need a Claude login or contact the usage API.

After publishing, run ./windows/test-ui.ps1 from the repository root for the
native UI smoke test. It creates an isolated manager instance, checks the
window controls, drag region, Support link and twelve-icon grid, then creates
and renames a shortcut and verifies its saved icon and bundled runtime. Use
-Theme Light -Preset Research to exercise the light appearance and a badged
icon. Screenshots and fixtures remain in windows/dist for inspection.

## Profiles and sign-in

Choose New Shortcut, give it a name, select one of the twelve icon variations,
and choose where its chats come from. The presets follow the Mac's order, hue
angles, grayscale option and Work, Personal, Code and Research badges. Desktop
icons include seven sizes from 16 through 256 pixels; cached icons from earlier
renderers do not override the new colors. Create Shortcut writes a desktop shortcut.
Its launcher has a stable copy of the runtime and a separate graft.json
description, so it keeps working when the manager is closed. The main Claude
profile cannot be renamed, repointed or removed.

Windows sends claude:// browser callbacks to Claude's registered main instance.
If a second account's browser sign-in opens the wrong window, copy the link
behind the browser's Open Claude button and use Complete browser sign-in in
that profile's details. This forwards the link to the already-open profile;
Claude validates its own login state. Graft does not replace Claude's protocol
registration, store callback links, or refresh account tokens.

Live usage borrows only the access token through Windows DPAPI. The five-hour,
weekly and optional Fable limits come from Anthropic. Refresh honors the service's
retry delay, and Start Session sends a message only when its button is pressed.

## Shared chats and settings

Chat records are copied and reconciled in both directions. Merging histories
cannot be undone in the source: copies already made there stay there. Going
back to its own chats restores the borrowing profile's original history while
preserving changes to those records. Transcripts remain in the user's shared
.claude/projects directory.

Directory settings use junctions that need neither administrator rights nor
Developer Mode. Individual settings files use journaled copies, because Windows
junctions cannot represent files. Edits are reconciled at launch; a conflict
keeps the losing file beside the destination as a .graft-conflict file. Original
settings are retained in .graft-own stashes for restoration.

Opening a profile already running brings its window forward. Opening another
profile that shares its chats asks first. Quit the affected Claudes before
moving a profile or changing chat sharing. Removing a shortcut keeps its data
unless deletion is explicitly selected. Corrupt state, unreadable records and
an unavailable process list never count as permission to remove surviving data.

## Platform boundaries

The Swift app, macOS launcher and their tests stay in Sources, Tests and the
existing shell scripts. Everything used by the Windows app is under windows.
The C# core is shared only by the Windows manager, launcher and tests. The root
VERSION and MIT license are the common project metadata.

The macOS Sparkle updater, managed Claude update flow and update-restart
recovery remain macOS integrations. This Windows build is updated by replacing
its extracted folder while Graft is closed; Claude Desktop updates through its
own installation channel. It does not claim to prevent or recover Claude's
updater from interrupting active work. Automatic discovery and import of
same-account chats left in another profile, and Russian interface localization,
are not yet included in the Windows interface.

The code and fixture tests cover sign-in forwarding and usage parsing. Actual
two-account browser authentication, paid Start Session requests and live
conversation sharing still need acceptance testing with disposable signed-in
accounts. They are not exercised against a developer's existing profiles.

## Diagnostics

Graft writes diagnostics.log in its own data directory. These entries describe
filesystem decisions without including tokens or message text. app-error.txt
records an unexpected UI failure. Profile names and paths can identify a person,
so review a diagnostic file before sharing it.

A UI test can use --test-data-root followed by an absolute directory carrying
a .graft-test-root marker. Profiles, transcripts, icons and generated shortcuts
then stay in that directory. This mode supplies an empty process list and is
only for disposable UI fixtures, never for operating real Claude profiles.
