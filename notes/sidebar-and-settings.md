# Sidebar pins and desktop settings

What `SidebarSync` may write into Claude's browser storage, and why `claude_desktop_config.json` stays with each profile. Read this before changing either.

## Sidebar pins

**Sidebar pins have an authority outside the session records.** In Claude
2.110.1, `isStarred` is a derivative. The pin set is in IndexedDB's `keyval-store`,
under `store:pin-state:dframe-starred-code`; order and Code sorting are in the
`dframe-store` local-storage record. The local-storage and desktop-preference
fallbacks must agree too, or an empty pin set is repopulated at startup.
Code sorting also participates in Claude's account preferences. A sort change
must set `ccd-sync-pending:ccd/dframe-store` to that profile's own account/org
scope, after checking `ccd-sync-owner` and the quarantine marker. Claude then
reconciles the local edit itself; otherwise startup restores the old server
value. Pin/order changes alone must never enqueue a server preference upload.

`SidebarSync` runs only before opening a closed profile, and only when every
profile connected to its shared history is closed. Its launch lock covers the
app and generated shortcuts. It projects onto session ids present on both
sides of an active account/organization pair; inactive accounts, unrelated pins,
custom groups and other modes' settings are outside that projection. The last
successful agreement distinguishes an unpin from a missing pin. Failed writes
never advance it, and removing a chat-mirror pair removes its sidebar agreement.

Never write Chromium database files directly or share browser-storage folders
between profiles. The storage helper uses a private copy of the installed
runtime and browser storage APIs, with no Claude application scripts. APFS
clones keep the original app intact; framework symlinks fail because the runtime
locates its resources relative to the resolved framework. An empty offline
document has access to one profile's storage at a time, while its cookies and
other browser files live in disposable storage. No debugging port is opened.
All participants are read and validated before writing. Only the named sidebar
records and two fallback preferences may change, with a local backup made first.
Unknown versions, mismatched account scopes and unreadable settings refuse the
sync. Claude still opens when sidebar synchronization is unavailable.

**A profile with nothing pinned has no pin record, and that is not an
uninitialized profile.** Claude persists its pin store only once it differs from
the default, so a profile that has never pinned a Code session, or has unpinned
the last one, has no `store:pin-state:dframe-starred-code` at all, while
`dframe-store` and the rest of its sidebar are there as usual. The helper read
the missing record as `not-initialized` at both origins, and then refused the
pass because neither origin qualified. Every sync between two profiles without
pins failed that way from 2 October onwards, logged as `unsupported-storage`,
and this machine's profiles have no pins. A missing record is now an empty pin
set; `dframe-store` is what says whether an origin is in use, and restoring a
profile that had no record deletes the record rather than writing a null.

**The runtime checks its integrity dictionary against a digest baked into the
framework.** From Electron 44, which Claude 4.16336.0 ships, the framework
carries a `__DATA_CONST,__asar_integrity` slot: a sentinel,
`AGbevlPCksUGKNL8TSn7wGmJEuJsXb2A`, then a used flag, a version and a SHA-256
over every `ElectronAsarIntegrity` entry in key order (path, algorithm, hash,
concatenated). A dictionary that does not hash to it is refused before any
script runs: "Failed to get integrity for validatable asar archive", fatal, so
the helper exited and every pass read as `storage-unavailable`. Claude's own
dictionary was checked against the slot and matches. `sealIntegrity` writes the
helper's digest into the helper's own clone of the framework, in place so the
clone shares every other block with the original, and signs the framework
again. The helper apps beside it keep Anthropic's signature and library
validation, which refuses a framework signed by anyone else, so they are signed
again too. The installed Claude's framework hashes the same before and after.
`helperFormat` is part of the cache key, because the helper cached before this
still verified as signed and still could not start.

`./test-sidebar.sh` checks the selective updates and then uses actual Chromium
databases in disposable profiles, including a fresh process after each change.
It needs a local Claude installation and Node for the JavaScript checks; neither
is added as a dependency of the distributed app. The regular suite drives the
merge rules, launch guard, failed writes and account changes without real profiles.

## Desktop settings

**Desktop settings belong to each profile.** `claude_desktop_config.json`
contains account-specific permission opt-ins alongside local MCP definitions.
It must not be in `sharedItems`: relinking it on launch replaces permission
choices Claude saved by renaming over the old link. `copyDesktopServers` adds
missing MCP definitions to a real local file, keeping existing definitions and
every local preference. A legacy link migrates from the saved original; without
one, it imports only MCP definitions, never another profile's permission grants.
An unreadable settings file or backup blocks the copy. The source is never
written, and the backup remains until the ordinary ungraft cleanup.
