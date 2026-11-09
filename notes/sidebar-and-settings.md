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

## Linux

**The helper is a copy of Claude's own Electron binary, run headless with a
script of Graft's.** `graft-sidebar` reads and writes the pins the way the Mac
helper does, through Chromium's own IndexedDB and local storage APIs in the
same Chromium that wrote them, and never writes a LevelDB file itself. The
Claude on the development machine (`claude-desktop-unofficial`
2.9939.4-3.3.4, Electron 44.4.3) has these fuses, read from the fuse wire in
`/usr/lib/claude-desktop-unofficial/claude-desktop`: run-as-Node off,
`NODE_OPTIONS` off, the inspect arguments off, embedded asar integrity
validation on, loading only from `app.asar` on, cookie encryption on. So the
installed binary cannot be made to run a script of anybody else's, as Node or
through a debugger, and it only ever loads the `app.asar` beside it. Electron
finds that archive from the real path of its own executable
(`/proc/self/exe`), so a link to the binary would load Claude's application;
the binary itself has to be copied. Everything else the runtime reads beside
it, the `.pak` files, ICU data, V8 snapshots, locales, `libffmpeg.so` and
`chrome-sandbox`, is opened by path and is linked rather than copied. On this
machine's composefs `/usr` a hard link is refused, so the copy is a real one,
about 220 MB, under `~/.config/ClaudeGraft/sidebar-runtime/<key>/runtime`,
named `sidebar-storage`. The cache keeps one key, as on a Mac, and carries a
`CACHEDIR.TAG`, which is what Linux backup tools honour in place of a Mac's
excluded-from-backup flag. The key is a digest of the launcher script (which
carries the package version, since an image-based system stamps every file in
`/usr` with time zero), the binary's size and time, the runtime's `version`
file, the script and `helperFormat`; a copy is used only when its marker,
written last, matches, its binary has the source's size and its archive is
this script's.

**Asar integrity has nothing to seal on Linux.** The fuse is on, but this
Electron implements the check only on macOS and Windows: the binary holds no
`AGbevlPCksUGKNL8TSn7wGmJEuJsXb2A` sentinel and no "Failed to get integrity"
message, and a copy carrying Graft's archive starts and runs it. The archive
still carries per-file SHA-256 integrity, which Electron does check as it reads
each file. A future Electron that brings the check to Linux would make the
helper exit at start, every pass would read as `storage-unavailable`, and
Claude would still open; the fix would be the Linux form of `sealIntegrity`.

**It runs where nothing can see it.** The helper is started on the host
(through `graft_core::host`, since the Flatpak's `/usr` is not Claude's) as
`timeout -k 3 25 env -i PATH=/usr/bin:/bin HOME=<scratch>/home sidebar-storage
--ozone-platform=headless --disable-gpu --password-store=basic <request>`. The
empty environment is the Linux form of filtering `ELECTRON_`, `NODE_` and
`DYLD_` variables, and it also leaves out the display and the session bus, so
the helper has no window to show and no keyring to reach: with cookie
encryption fused on, a helper on the desktop's password store would ask the
Secret Service for Chromium's key, and a locked keyring would put up a
dialog. The headless platform crashes Electron when a `BrowserWindow` is
created, so the script uses a `WebContentsView`, which needs no window. The
Chromium sandbox stays on. Its process is not a Claude to
`graft_core::claude` (the executable is not `claude-desktop`), and the Claude
launcher's reapers leave it alone, since its path is outside
`/usr/lib/claude-desktop*` and it carries no `--user-data-dir`.

**What to write is decided by Graft, not by the helper's script.** On a Mac
`decode`, `snapshot`, `patch`, `patchPreferences` and `preferencesText` live in
the script and are tested with Node. On Linux they are Rust
(`graft_sidebar::records`), so the suite drives every one of them with no
JavaScript engine, and the script only reads the raw records of each profile
and writes the ones it is handed. A write names the records it expects, the
script reads them again and refuses with `changed-during-sync` if anything
moved, then writes, flushes, reads back and compares; a profile that fails is
put back as it was read, and the reply lists the profiles that were written.
The settings file is Graft's to write, so it is written first, through
`atomic_write_text` with the indent, final newline and mode it had, chmodded
past the umask, and put back byte for byte, or removed if the pass created
it, for every profile the databases did not take. A fingerprint over the raw
records, the settings text and the origin is what the read hands out and the
write is checked against, as on a Mac.

**The launch lock is Graft's one launch lock.** The Mac `SidebarSync` had a
lock file of its own; on Linux the pass runs inside
`graft_core::lock::with_launch_lock`, which every launcher and the window
already take from deciding a profile is closed to starting Claude on it.
`synchronize_before_opening` refuses to run when that lock is free, which is
the call made from outside it. The process list it checks is
`graft_core::claude::running_profiles`, one `ps` on the host.

**Measured here.** `linux/test-sidebar.sh` passed all 25 of its checks against
three disposable profiles under `linux/target/sidebar-tests`: real Chromium
databases seeded by a second copy of the runtime, merged pins agreeing after
the storage engine is reopened, an unpin, reorder and sort change reaching
the other profile, the last pins cleared, a profile with no pin record read
as empty and written into, unrelated records and appearance kept, the settings
file's own servers, permission choice, indent, newline and mode kept, one key
in the cache, no scratch folder or helper process left, and nothing written to
the real home folder. A pass that reads and writes took 1.8 seconds, two
helper starts. Not yet seen: the pass inside the Flatpak, where the copy, the
listing and the helper go out through `flatpak-spawn --host`; a pass over real
profiles, which is a person's to run; and any packaging but the rpm. An
AppImage's runtime sits in a mount that goes when it exits and its launcher
names no `app_exec`, so there the pass stops at `unsupported-runtime` unless
`GRAFT_CLAUDE_RUNTIME` names a binary that stays put.

**Desktop settings.** `copy_desktop_servers` is in `graft_sidebar::settings`
and behaves as on a Mac: missing MCP definitions are added to the profile's
own real file, a legacy link becomes a real file from the stash or from the
source's servers alone, and an unreadable file on either side stops it. The
chat sharing calls it while grafting, and calls `graft_sidebar::forget_pairs`
whenever it saves `mirrored-chats.json`, which this crate reads and never
writes.
