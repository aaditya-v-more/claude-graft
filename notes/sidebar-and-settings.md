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

**The cache keeps one helper, and a bad one is replaced rather than obeyed.**
Every Claude update and every change to the script makes a new key, and each
key a new clone of the whole runtime under `sidebar-runtime.noindex`. A clone
is free on disk only while it shares its blocks with the installed Claude; the
next Claude update unshares them, and a backup copies every clone in full.
Nothing removed an old one — this machine was keeping two, two days apart — nor
a `.build-` folder left by a pass that was killed, nor the backups in
`sidebar-backups`, one per writing pass. `pruneHelperCache` now runs after
every `prepareHelper` that succeeds, under the launch lock: it removes every
other key's folder, `.build-` folders more than an hour old, and copies set
aside as damaged, touches nothing whose name it did not make, and marks the
cache as excluded from backups. `pruneBackups` keeps the newest twenty.

A copy at the destination that lacked its marker or failed verification used to
stop the pass with `runtime-cache-damaged`, and since the key does not change
until Claude does, every later pass stopped the same way until someone deleted
the folder by hand. `installHelper` moves it aside and puts the fresh copy in.

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

**The sidebar helper leaves the settings file as a person would find it.** A
sync writes two fallback preferences into `claude_desktop_config.json`, and
it used to write the whole file back as one compact line with mode 0600, on the
way forward and on a rollback alike. The values were right and the file was
not: a file people open and edit by hand turned unreadable, a mode widened on
purpose was narrowed, and a rollback put back the object the text parsed to
rather than the text, which is not the same file once a key repeats or the
layout mattered to somebody. A rollback now writes `prefsText` back byte for
byte; a forward write keeps the indent the file had (two spaces for a file
written for the first time), its final newline and its mode, chmodded past the
umask.

## Project order

The order of the sidebar's project headings is kept per account, so two
accounts sharing one history drift apart as soon as either is reordered. Claude
stores it twice. `preferences.epitaxyPrefs["code-projects-order.<account>"]` in
`claude_desktop_config.json` is the list the sidebar is built from at startup.
`LSS-persisted.code-projects-order.<account>` in local storage holds the same
list with the time it was saved. Writing only the local-storage copy changes
nothing on screen: a profile showed an order from a month earlier while its
local storage held the new one, because its preferences file still had the old
list.

`synchronizeProjectOrder` reads the local-storage copies from clones of each
profile's storage, so a profile about to open is never locked for a read, and
takes the list from the preferences file where there is one. The newest order
wins and keeps its time, so the next pass finds nothing to do, and two orders
saved at the same moment are left alone rather than overwriting each other in
turn. Folders and repositories are named the same in every profile. A saved
environment is not: `preset::<id>` is matched by name and target, then by folder,
and dropped where the receiving account has no equivalent. Entries only the
receiving account lists stay, after the copied ones. Only that one preference and
that one local-storage entry are written, and only while no connected Claude is
running.

It runs before a profile is opened through Graft and two seconds after the last
Claude quits. The second is the only moment that covers a profile opened from the
Dock, which runs no launcher, and it goes over every group of profiles sharing a
history: a quit says nothing about which group it belonged to, and a group that
leaves Claude's own profile out is as likely to have been reordered.

Both run under the launch lock, so a shortcut clicked while either is under way
waits for it and opens on the order it left. Claude's own app opened from the
Dock waits for nothing: it is not Graft's to hold, and one that starts while
the helper has its Local Storage open cannot open its own. So the pass after a
quit gives Claude's own profile the preferences entry alone, a file replaced
whole that a starting Claude reads either before or after, which is all it
displays from; the timed copy is left for a pass made before Graft opens
something, with the door shut. Until then that profile shows the new order
under its old time, which no pass carries anywhere.

A saved environment that matches by neither name nor target falls back to its
folder alone, so two environments on one folder that differ only in kind can
stand in for each other. Claude matches them no better itself; it is left as a
known limit.

The preferences entry is written by the helper, with `preferencesText` and
`atomicWrite`, like the pin pass's fallbacks. It was first written from Swift,
and `JSONSerialization` gave the file Apple's indent, a key order of its own and
the default mode, which is the hand-edited file turned unrecognisable that the
section above describes. It goes in before the timed Local Storage copy: a pass
stopped between the two then leaves an order shown with an old time, which the
next pass carries nowhere, rather than an old order under a new time, which it
would carry everywhere.

A failed project pass restores earlier participants as well as the one that
failed. A locked preferences file on the second Windows fixture exposed that
the first participant had previously kept its new order after the helper
reported failure. The helper now remembers the exact original settings text,
mode and presence of each Local Storage key. It restores the timed value
before the displayed preference, and only restores a value still equal to
the one it wrote; an external change made while the pass runs is left alone.

The Windows port uses these same helper operations, with directory junctions
instead of privileged directory symlinks for the short-lived write profile.
Project order has its own pass, so an unsupported pin database cannot prevent
it from being read. The manager's last-quit pass spares Claude's default Local
Storage, just as the Dock case above does; a stock Windows shortcut does not
wait for Graft's launch lock either. Saved environments with a folder fallback
compare that Windows folder without case.

The Windows native fixture changes both accounts' real disposable Chromium
stores, checks the displayed preferences, and holds the second preferences
file open to force rollback after the first account has committed. The first
account's original preferences bytes and file attributes must come back. It
also compares the default-profile stand-in's database file names, sizes and
timestamps around a preferences-only pass. The shared helper's Unix mode
check uses the mode the platform actually supports: Windows writable/read-only
attributes do not establish preservation of an independently customized NTFS
access-control list.

The Windows compatibility allowlist also includes Claude 4.23118.1. Its
installed package was inspected without launching it or opening profile
databases: the version marker and executable report Electron 44.9.3 and
Chromium 152.0.7977.167, matching the pinned helper. Static renderer code
still persists `dframe-store` at version 1 with `pinnedOrder` and
`sortByByMode`, the `pin-state` store holds `starredIds`, and the legacy
starred/local-slice keys remain. The helper still validates the actual store
shape before any write. Uninspected Claude versions and mismatched engines
remain disabled.

