# Windows port review

The reference implementation is
[snowtyler/claude-graft at 341870143c6a6c0abcf53e8fde2576a2169457ad](https://github.com/snowtyler/claude-graft/tree/341870143c6a6c0abcf53e8fde2576a2169457ad/windows).
Its Windows C# sources, tests and icon assets supplied the initial implementation
under the project's MIT license. This branch selectively adapts that work;
it does not merge the fork's history, release workflow or macOS files.

The separate C# core, WinUI tray integration, DPAPI access-token reader,
chat-mirror decision table and transcript parser are useful foundations.
A native Windows interface avoids carrying a second browser runtime, and a
separate launcher preserves the project's shortcut behavior. Windows runtime
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
profile environment override supported by the installed Claude build. Sign-in
callbacks can be forwarded manually to the selected profile without taking
over the system's protocol handler.

The reference's card-list manager has been replaced with the project's sidebar
and detail layout, its twelve icon choices, and the original Support and Source
links. Platform-specific omissions and live acceptance checks are listed in
README.md so this branch can be reviewed honestly before merging.

Local validation on Windows passed 186 core tests and the native UI smoke
test, including creation and renaming of a real .lnk in a disposable desktop
folder. The portable x64 build was opened and visually inspected. The installed
Store version of Claude was discovered, launched with an empty workspace
profile, and observed creating its files there before that test process was
closed. No existing signed-in profile was used for these checks.
