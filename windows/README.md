# Claude Graft for Windows

Run Claude Desktop profiles side by side, each with its own login, and share
Claude Code history and usage between them. The sidebar follows Graft 2.0:
Shared usage, Artifacts, Projects, Search Chats, and the individual Claudes.
Help keeps the documentation, source and support links together. Profile
settings retain the twelve icon presets, shared-chat controls and usage bars.
Windows supplies the standard title bar, window buttons, borders, system menu,
resizing and Snap. The initial content area is 1000 by 680, with a minimum
window size of 820 by 560 device-independent pixels. At the minimum width the
icon picker moves below its label. The app, title bar and tray follow the Windows
app color mode, including changes while Graft is open. Settings can pin them
to Light or Dark. Support still goes to
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

Graft installs without administrator rights. The installer and portable ZIP
include the .NET, Windows App SDK and Visual C++ runtimes. Each profile's
standalone launcher also carries the runtime files it needs.
The shared account panes and rendered artifact exports use Microsoft Edge
WebView2. Setup reuses an installed Evergreen Runtime or installs it for your
Windows account using Microsoft's signed bootstrapper. That first runtime
installation needs an internet connection. On an offline computer, install
Microsoft's [Evergreen Standalone Installer](https://developer.microsoft.com/microsoft-edge/webview2/)
first. Portable ZIP users also need that runtime installed. Graft does not
remove this shared Microsoft runtime when uninstalled.
Development builds are unsigned. Windows Smart App Control can block their
setup or the installed manager. If Windows blocks a development build, keep
the protection enabled and use a publisher-signed release when available.

Remove Graft through Windows Installed apps. Uninstall removes the application
files and shortcuts created by setup; it preserves your Claude profiles, chats,
Graft settings, and generated profile launchers.

## Build and run

Install the .NET 10 SDK and Visual Studio C++ Build Tools on Windows. Include
the target architecture's MSVC release redistributable (14.50.35719 or later).
For a licensed offline Visual Studio layout, set `VCToolsRedistDir` to its
`VC\Redist\MSVC\<version>` directory before running the build.
Then run this from the repository root:

```powershell
./windows/build.ps1
```

The script runs the isolated tests, publishes the app, its launcher and the
shared-usage relay with their runtimes, verifies the required files, and creates a ZIP and SHA-256
checksum in windows/dist. Use -Architecture arm64 for Windows on ARM. It does
not publish a release, install an application or change a system registration.

To create the setup executable after publishing, run
./windows/build-installer.ps1 -SkipBuild. The build copies unmodified,
signature-verified Microsoft DLLs from Visual Studio's redistributable CRT
folder and records each DLL's SHA-256 in vc-runtime.json. Distributors must
have the applicable Visual Studio redistribution rights.
[Microsoft's redistribution terms](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)
apply to those files. The installer build verifies each recorded hash before
compiling setup. Pass -Compiler with
the full path to ISCC.exe to select a compiler explicitly. If none is installed,
the script prepares a signature-verified portable compiler under windows/dist.
The installer and its checksum are written to windows/dist.
The setup build also downloads the small Evergreen WebView2 bootstrapper from
Microsoft, verifies its embedded Authenticode signature, and records its SHA-256
and signer in the build manifest. To package without a network download, pass
`-WebView2Bootstrapper C:\path\MicrosoftEdgeWebview2Setup.exe`; the same signature
check applies. This bundles the downloader, not the full offline runtime.

### Publisher-signed setup

Release builds require the maintainer’s publicly trusted code-signing certificate
in `Cert:\CurrentUser\My` with access to its private key, Windows SDK SignTool and
the CA’s RFC 3161 timestamp service. Set `GRAFT_WINDOWS_PUBLISHER_THUMBPRINT`
to the public certificate thumbprint and `GRAFT_TIMESTAMP_URL` to that service.
Use a fresh staging directory for each architecture. From a Windows SDK
developer shell, after publishing x64, run

~~~powershell
$stage = Join-Path $env:TEMP ('graft-signed-x64-' + [guid]::NewGuid())
Copy-Item -Recurse -LiteralPath ./windows/dist/ClaudeGraft-x64 -Destination $stage
$tool = (Get-Command signtool.exe).Source
$publisher = @{ PublisherThumbprint = $env:GRAFT_WINDOWS_PUBLISHER_THUMBPRINT
    TimestampUrl = $env:GRAFT_TIMESTAMP_URL; SignTool = $tool }
./windows/build-installer.ps1 -Architecture x64 -SkipBuild -PublishRoot $stage @publisher
~~~

The script reports dependency paths, signature status and SHA-256 hashes.
Keep existing valid vendor signatures. For each unsigned file, obtain a
vendor-signed build, or review its exact bytes against the locked dependency
and confirm that its license permits redistribution under your signature.
Electron and H.NotifyIcon include unsigned files. After that review, sign
each approved **staged** file individually. For H.NotifyIcon.WinUI.dll, run

~~~powershell
$file = Join-Path $stage 'H.NotifyIcon.WinUI.dll'
if ((Get-FileHash -LiteralPath $file).Hash -ne $approvedUpstreamSha256) { throw 'Unexpected dependency bytes.' }
if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'NotSigned') { throw 'Review the existing signature.' }
& $tool sign /s My /sha1 $publisher.PublisherThumbprint /fd SHA256 /tr $publisher.TimestampUrl /td SHA256 $file
if ($LASTEXITCODE -ne 0) { throw 'Dependency signing failed.' }
~~~

Set `$approvedUpstreamSha256` from the independently verified original.
Repeat the same build command with `-SkipBuild` and that staging directory.
The script signs the manager, launcher, shared-usage relay and their copies of
the core assembly.
Inno signs setup and its uninstaller. SignTool verifies the finished setup
before the build writes its checksum and publisher record under `windows/dist/signed`.
Repeat for ARM64 using the ARM64 publish folder. Test both signed setups on
protected Windows installations. [Microsoft’s Smart App Control signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)

On the release Mac, put both reviewed setups for the current VERSION and
their `.build.json` files together. Install Python 3 and `osslsigncode`,
and set `GRAFT_WINDOWS_PUBLISHER_THUMBPRINT` from the trusted publisher’s
certificate. If the tool’s trust store needs additional issuing roots,
set `GRAFT_AUTHENTICODE_CA_FILE` to a trusted PEM bundle for both the publisher
and timestamp authorities. After building the Mac app to obtain Sparkle’s
release tools, run `bash windows/sign-release.sh /path/to/artifacts`.
It verifies both Windows signatures and hashes before signing their checksums
and filenames with Graft’s update key. Attach the setup EXEs, .sha256 and
.sha256.sig files to the upstream release tagged vVERSION.

### Testing the build

For an isolated install/uninstall check, build with -TestMode and pass that
setup-test.exe to ./windows/test-installer.ps1 -Installer. This variant creates
no installed-app entry or shell shortcuts, verifies the installed app in a
disposable directory, and checks that uninstall preserves unrelated files.
TestMode neither downloads nor runs the WebView2 bootstrapper. Run
`./windows/test-webview2-installer.ps1 -Compiler C:\path\ISCC.exe` to verify
the runtime detection, integrity and failure handling against an inert fixture,
and compile both installer architectures. An optional `-Bootstrapper` checks a
downloaded Microsoft bootstrapper's signature without running it.

Extract the entire ZIP to a folder you intend to keep, then open ClaudeGraft.exe.
Click or right-click the notification-area icon to open the account list.
If the icon is under **Show hidden icons**, Graft places its popup beside or
above that panel when space allows. Choose **Open Claude Graft** to open the
manager.
Run ClaudeGraft.exe --show to open the manager immediately. The complete folder
is required because the executable loads its runtime from that folder.

The standard Close button and Alt+F4 close the manager window normally. The
tray app remains available, and opening the manager again creates a fresh
window. Quit Claude Graft in the tray menu exits the background app too.

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
native window frame, matching title-bar and content themes, minimum/wide layout,
the new panes without a login, system commands, close/reopen behavior, Help links and
twelve-icon grid, then creates
and renames a shortcut and verifies its saved icon and bundled runtime. Use
-Theme Light -Preset Research to exercise the light appearance and a badged
icon, or -CheckAppearance to switch Light, Dark and System through Settings
while the app is running. The default is System; the checks read the Windows
app mode without changing it. Screenshots and fixtures remain in windows/dist
for inspection.

Run ./windows/test-web-accounts.ps1 against the published executable to exercise
real WebView2 stores with intercepted fixture responses. It verifies separate
cookies, sign-out cleanup, permission checks, cancellation after account removal,
and rendered Markdown, PDF and Word exports. All browser requests are intercepted;
these checks do not sign into a live account. ./windows/test-relay.ps1 verifies
the separately published relay's process, control and restart behavior. CI runs these
native checks on both x64 and ARM64.

## Profiles and sign-in

Choose **New Claude**, give it a name, select one of the twelve icon variations,
and choose where its chats come from. New profiles start with their own chats;
sharing requires selecting another profile. The presets follow the Mac's order, hue
angles, grayscale option and Work, Personal, Code and Research badges. Desktop
icons include seven sizes from 16 through 256 pixels; cached icons from earlier
renderers do not override the new colors. Create Shortcut writes a desktop shortcut.
Its launcher has a stable copy of the runtime and a separate graft.json
description, so it keeps working when the manager is closed. The main Claude
profile cannot be renamed, repointed or removed.

Pin a generated shortcut to the taskbar to keep its profile icon there. Each pin
has a stable Windows identity and opens or brings forward the matching profile.
Running Claude windows still appear under Claude Desktop’s taskbar group.

Install with the setup wizard, select a saved profile, and choose **Sign in to
this profile**. Graft checks which app Windows uses for `claude:` links. If
setup is needed, choose **Open Default apps**, select **Claude Graft** for the
**CLAUDE** link type, then return to Graft and choose **Retry sign-in**. Once
Graft is selected, the sign-in action opens or brings forward the chosen Claude
Desktop profile. You can also open the profile using its desktop shortcut.
Setup registers the link handler for your Windows account. You can switch back
to Claude Desktop in Default apps.

Start email, Google, or company SSO sign-in in the intended Claude window and
finish in your browser. When the browser opens Claude Graft, select the profile
where you started and press **Continue sign-in**. Windows provides one callback
for all profiles. The chooser lists running profiles, leaves the choice empty,
and rechecks the process before handing the link to Claude. If the profile
closed, reopen it and start sign-in again. Other Claude links open in the main
Claude. Sign-in links are kept in memory for this handoff and omitted from
Graft diagnostics.

Live usage borrows only the access token through Windows DPAPI. The five-hour,
weekly and optional Fable limits come from Anthropic. Refresh honors the service's
retry delay, and Start Session sends a message only when its button is pressed.
Plans with no five-hour limit show "No 5-hour limit" and their weekly usage;
Start Session is hidden because there is no five-hour window to open. A zero
five-hour figure still represents a limited, idle plan. The Desktop history
fallback follows the same conservative rule as macOS: a stray weekly-only sample
keeps the last complete reading and its original timestamp, while consecutive
weekly-only samples with no recent five-hour figure for that organization can
establish that the plan has no five-hour limit.

## Shared Usage

The Shared Usage pane selects lending accounts separately for each Claude.
An account can come from another saved Claude profile or an independent
browser OAuth sign-in. The independent sign-in requests inference access and
stores only its access token, sealed with Windows DPAPI for the current user.
It never renews or replaces a Claude profile's login. Accounts representing
the same account UUID count as one allowance.

The standalone `relay/GraftRelay.exe` listens only on `127.0.0.1:47823`.
Graft writes `HTTPS_PROXY` and `NODE_EXTRA_CA_CERTS` into a closed profile's
encrypted Code environment. Existing custom proxy/certificate values are
preserved and reported as a conflict. A journal records both sealed versions
before changing the profile; launchers restore the original if the relay is
unavailable. Enable **Open Graft at login** in the pane to keep the relay ready
after restarting Windows, including when opening Main Claude directly.

Only identified `POST /v1/messages` requests to `api.anthropic.com` can borrow
another account's access token. Artifacts, settings, Remote Control, WebSocket
traffic and other destinations retain the sender's traffic. Streamed replies
and duplex requests stay streaming. Selection, prompt-cache affinity, bound
reasoning, continued threads, rate-limit retries and sender fallback follow
the macOS policy. Recent conversations can be assigned to a specific account
from the pane or tray. A moved conversation's model request carries its
contents to that lending account; the pane explains this before adding one.

The relay's signing authority is constrained to the API hostname, and its
private key is discarded after signing one leaf. The leaf identity is DPAPI
protected; its temporary Windows user key is removed when the certificate is
disposed. No user or machine certificate trust store is changed. The extra
authority is passed only to Claude Code through its own environment.
Configuration travels through a same-user named pipe, and relay status never
contains access tokens or conversation contents.

The relay runs from a private stable copy so closing or updating Graft does
not interrupt active Code sessions. **Stop all sharing** first disables the
saved assignments, restores closed profiles, and leaves the relay serving
sender traffic until open profiles can be restored. Uninstall invokes the
same cleanup. After removal, the relay also monitors the original manager
path, restores closed profiles and exits when no profile needs it. It does
not reopen Claude. A manually edited unreadable environment is preserved;
resolve the reported conflict or unreadable file before removing its proxy.

`dotnet test` covers the relay over disposable local sockets, real Schannel
TLS with fixture-only trust, encrypted environments, account deduplication,
fallback/retries, duplex chunking, WebSocket upgrade, PKCE/state validation,
and DPAPI account storage. After publishing, run:

```powershell
./windows/test-relay.ps1 -Executable ./windows/dist/ClaudeGraft-x64/relay/GraftRelay.exe
```

This checks the actual native relay, its bundle digest, exclusive lease,
same-user control, credential-free restart and clean exit in an isolated
profile root. CI runs it on both native Windows architectures. Actual plan
acceptance, browser OAuth and usage sharing against real accounts remain
separate manual checks.

## Artifacts

The Artifacts page lists the artifacts from every account added to Graft's
claude.ai sign-in. Use **Continue with email** and the code claude.ai sends;
the same sign-in serves Projects and Search Chats. Filter the list locally by
title or ownership, open or copy a link, and use each row's Actions menu to
rename, duplicate or delete where the account has permission. Deleting asks
first because claude.ai keeps no bin for artifacts.

Sharing can make a link public, restore invited-only access while retaining
invitees, or invite a saved group. Groups keep a role for each address; a role
the artifact does not support is lowered, never raised. Sending a group invite
asks first and emails each person through claude.ai. Export saves Markdown,
PDF, Word or the page source locally; a source with multiple files is zipped.

Each browser account has its own WebView2 store under Graft's data directory.
Signing out removes the account from all three panes and clears that store.
The account list contains account identity and organization metadata; Graft
does not read browser cookies. Exports render downloaded files in a separate
temporary browser with no account sign-in and no network connections.

## Projects and Search Chats

Projects lists the projects across the accounts added through **Add Account**.
Filter by account or title and open a project to see its conversations. Search
Chats searches titles across Chat and Code with account and kind filters and
highlights matching text. Code results are cached briefly; Refresh asks again.
An unavailable page keeps the results already read and shows that the list is
incomplete. Missing remote results alone never label a local Code conversation
as deleted; Graft checks its account, organization and transcript history.

The same **Continue with email** sign-in serves all three panes. A permission
refusal on one artifact or project does not remove a working account. A session
that has expired shows **Sign in again** on its account button. Desktop profile
sign-in remains the separate browser callback flow described above.

## Shared chats and settings

Chat records are copied and reconciled in both directions. Merging histories
cannot be undone in the source: copies already made there stay there. Going
back to its own chats restores the borrowing profile's original history while
preserving changes to those records. Transcripts remain in the user's shared
.claude/projects directory.

When both profiles change a chat, Graft preserves independent field edits, so an
archive survives the other profile merely showing the conversation. If both
profiles receive different new replies, Graft waits until both are closed and
keeps two chats, each titled with its profile's name. Unfiled chats keep a local
scratch-folder alias in each profile while retaining the original working folder
and transcript for resuming. Switching the lender's active account or organization
hands the old borrowed copies back before sharing the new history.

Directory settings use junctions that need neither administrator rights nor
Developer Mode. Individual settings files use journaled copies, because Windows
junctions cannot represent files. Edits are reconciled at launch; a conflict
keeps the losing file beside the destination as a .graft-conflict file. Original
settings are retained in .graft-own stashes for restoration.

Claude Desktop permission choices stay local to each profile. Graft copies
missing MCP server definitions into the local desktop configuration and keeps
existing definitions. Profiles from early Windows builds recover their original
desktop settings from the saved backup when available.

If you used two accounts in the original Claude profile, sign into the account
you want in a shortcut set to **Its own chats**. In the manager, select the
shortcut and choose **Review chats…** when Graft finds that account's missing
history. Recent titles and dates help you check the offer. Quit all Claude
Desktop instances, then choose **Copy them here** or **Merge them here**. Graft
copies missing records and deletion markers once. Existing chats, their local
edits, and the original history stay where they are.

Opening a profile already running brings its window forward. Opening another
profile that shares its chats asks first. Quit the affected Claudes before
moving a profile or changing chat sharing. Removing a shortcut keeps its data
unless deletion is explicitly selected. Corrupt state, unreadable records and
an unavailable process list never count as permission to remove surviving data.

### Pins and sidebar order

Close every Claude window that shares the history, then open one of its
shortcuts. Graft synchronizes pins, unpins, pinned order and the Code sort
choice for shared conversations. Each profile keeps its other sidebar entries
and account settings. The shortcut's chat settings show whether synchronization
finished or needs another launch.

Sidebar synchronization is enabled for Claude Desktop 4.16336.0 and 4.23118.1
on Windows. Both use Chromium 152.0.7977.167. Graft packages a checksum-verified,
offline copy of Electron 44.9.3 to open the storage with the same engine.
The compressed runtime is about 151 MiB on x64 and 149 MiB on ARM64. A different
Claude version or an unfamiliar database format leaves the sidebar choices
with each profile. The integration check opens two disposable Chromium
databases in fresh processes and verifies that unrelated browser records survive.

## Platform boundaries

The Swift app, macOS launcher and their tests stay in Sources, Tests and the
existing shell scripts. The Windows package uses the same sidebar storage
script as the Mac launcher. Windows-specific sources live under windows.
The C# core is shared only by the Windows manager, launcher, usage relay and tests. The root
VERSION and MIT license are the common project metadata.

Setup installs check GitHub for Graft updates at launch and hourly. Graft
verifies the signature with its embedded release key and the checksum of the
download before it runs per-user setup, then reopens. In Settings, choose
**Wait for me to install Graft updates** to check on schedule and install when
you choose. Portable copies offer to open the setup wizard when an update is
available. To keep using a portable copy, replace its extracted folder while
Graft is closed.

Open **Settings → Claude Desktop updates** from the manager or tray to check the
installed MSIX against Anthropic’s Windows feed. If a release is available,
Graft lists the Claude profiles it needs to close. On confirmation, it downloads
the package, verifies its signature and identity, and asks those processes to
quit. A new process or a profile that stays open stops the update. Windows
installs the signed package for this user and Graft verifies the installed
version. You reopen Claude when you are ready. Older standalone installations
use Claude’s own update command.

**Enable manual updates** sets Claude’s per-user `disableAutoUpdates` policy
where Windows permits it. Restart each Claude when convenient for the new
setting to take effect. **Restore automatic updates** removes the value Graft
set. Choose it before uninstalling Graft if you want Claude to resume updating
itself. An existing user or machine update policy is managed by its administrator.
If Windows makes the per-user policy key read-only, an administrator must
configure the pause. Graft can still check for updates and offer a one-time
installation when no update policy has been set.

The Windows interface currently uses English.

To check browser sign-in, create two test shortcuts with **Its own chats**.
Keep one signed into a recognizable test account. Start sign-in in the other,
bring the first window forward, then finish in the browser. Select the
initiating profile in Graft. Verify the account shown in both windows after
each of the email, Google and company SSO flows. To check chat handoff, use
a disposable shared conversation and continue it in the second account.
The automated tests use fixtures for accounts, usage and paid Start Session
requests.

## Diagnostics

Graft writes diagnostics.log in its own data directory. These entries describe
filesystem decisions without including tokens or message text. app-error.txt
records an unexpected UI failure. Profile names and paths can identify a person,
so review a diagnostic file before sharing it.

A UI test can use --test-data-root followed by an absolute directory carrying
a .graft-test-root marker. Profiles, transcripts, icons and generated shortcuts
then stay in that directory. This mode supplies an empty process list and is
only for disposable UI fixtures, never for operating real Claude profiles.
