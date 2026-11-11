# Windows port review

The reference implementation is
[snowtyler/claude-graft at 341870143c6a6c0abcf53e8fde2576a2169457ad](https://github.com/snowtyler/claude-graft/tree/341870143c6a6c0abcf53e8fde2576a2169457ad/windows).
Its Windows C# sources, tests and icon assets supplied the initial implementation
under the project's MIT license. This branch selectively adapts that work;
it does not merge the fork's history, release workflow or macOS files.

The separate C# core, WinUI tray integration, DPAPI access-token reader,
chat-mirror decision table and transcript parser are useful foundations.
A native Windows interface provides the manager and tray. A separate launcher
opens profile shortcuts and can run the offline Chromium helper for sidebar
storage on supported Claude versions. Windows runtime
deployment follows [Microsoft's self-contained deployment guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps).
Installed Store packages are located with
[GetPackagesByPackageFamily](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagesbypackagefamily).

Several details did not survive review unchanged. Parent junctions were not
resolved by the original path boundary checks. Settings files were passed to
a directory-junction API after their originals were stashed. Unreadable records
could be interpreted as deletions. WMI failures were treated as an empty
process list. Windows reserved names, case aliases and quoting needed stronger
validation. Shortcut renaming removed both the old and newly created link.
The bundled launcher depended on a separately installed .NET runtime, and
the shared shortcut list was its only configuration.

This port resolves full paths through Windows file handles, uses the native
reparse-point API without a command shell, journals settings-file copies,
preserves unreadable records and corrupt state, and serializes profile work
between processes. It validates profile boundaries and Windows names, parses
command lines with Windows' own parser, reports launch failures, bundles a
self-contained launcher, and saves independent shortcut manifests. API
requests do not follow redirects, and recovered session records do not invent
a browser-permission override.

The reference only discovers Squirrel installations and sorts versions as
text. This port also detects the Store package and its virtualized data
directory, sorts standalone versions numerically, and sets the explicit
profile environment override supported by the installed Claude build. Users
can select Graft for Claude links in Windows Default apps. A browser sign-in
opens a chooser of running profiles. Other links open the main Claude profile.

The reference's card-list manager has been replaced with Graft 2.0's sidebar
and detail layout: Shared usage, Artifacts, Projects, Search Chats and individual
Claudes. Help retains the original Support and Source links. Profile settings
keep the twelve icon choices. The Windows app content reproduces the macOS blue selection, grouped
forms, compact buttons, two-row icon picker, role badges, usage bars and
light/dark palettes. Its title bar, window buttons, system menu, frame, Snap,
movement and resizing are supplied by Windows. Closing the manager destroys
that window; the tray can create a new one. The native title bar and the app
follow the Windows app color mode by default. The sidebar remains visible at
the minimum window size, where the icon picker moves below its label to keep
the form usable when snapped or resized. Shortcut icons carry separate
image sizes for Explorer and the desktop. Platform-specific omissions and live acceptance checks are listed in
README.md so this branch can be reviewed honestly before merging.

Shared usage uses a separate per-user relay and only reassigns eligible Code
message requests; ordinary Chat remains with its own account. Artifacts,
Projects and Search Chats use Graft-owned, isolated WebView2 account stores.
Their claude.ai sign-in cookies remain in the browser, separate from Claude
Desktop profiles and the access tokens borrowed for usage. Rendered artifact
exports run in a separate browser with external network access blocked. Setup
supplies Microsoft's Evergreen WebView2 bootstrapper when the runtime is absent.

The Windows tests use disposable profiles. They cover routing, record recovery,
usage, shared settings, browser account boundaries and release verification.
The relay tests use a stand-in service, and artifact exports use local fixtures.
The native UI test creates
and renames real .lnk files, and the sidebar integration test opens real
Chromium storage. See [the Windows instructions](README.md) for the build,
installer checks, and the checks that need signed-in accounts.
