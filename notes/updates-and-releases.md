# Updates and releases

Graft's own updates through Sparkle, Claude Desktop's updates and the recovery after them, and what `release.sh` checks. Read this before changing `Updater`, `UpdatePolicy`, `UpdateRecovery`, `ManualUpdates`, `ClaudeDesktopUpdater`, the appcast or the release script.

## Versions and builds

**The version is written down once.** `VERSION` at the repo root, stamped into
the bundle by `build.sh`. `Resources/Info.plist` carries `0.0.0` and nothing
else, so a bundle still holding that number was never stamped; the suite fails
if the real number reappears there. `release.sh` refuses to build a version
whose tag already exists — two binaries answering to one number is a thing no
update feed can tell apart, and it is only noticed after the upload.

The same is true of a version built twice without a bump, which is a thing only
a development loop does. `Installer.refreshLauncher` replaces a shortcut's
launcher when the version stamped in its bundle differs from Graft's own, so
rebuilding at the same number leaves every shortcut running the previous binary
while claiming to be current. A change to launcher code needs the bump or it
reaches nobody, and the symptom is a fix that works in the app and not from the
Dock.

**The version reaches a second repository, and that one has no build to fail.**
`VERSION` drives the bundle and the appcast, both of them here and both checked
before the upload. The Homebrew cask is in `homebrew-claude-graft` and was
edited by hand, so it sat at 1.0.6 while the feed served 1.0.10 — four releases
where `brew install` fetched a build four versions old and then had Sparkle
replace it within the hour, which is the long way round to the current version
and reads from outside like the project stopped shipping. `release.sh` now
clones the tap into `dist/`, rewrites the cask against the checksum of the disk
image that run actually built, commits it, and names the push beside the other
two. Nothing here pushes, as before; what changed is that no number is carried
across by hand and the printed instructions are no longer half a publish.
Every run deletes that clone and takes a fresh one, so a change to the rest of
the cask — 2.0.1's uninstall step, which takes the sharing lines out of each
closed Claude — is committed on top after the run and pushed with it, or the
next run quietly drops it.

**A release carries both architectures.** `build.sh` compiles one slice per
architecture and `lipo`s them; a plain `./build.sh` stays single-slice because a
development build only has to run here. `release.sh` sets `GRAFT_UNIVERSAL=1`
and then asks the bundle what it actually got, because an Intel Mac handed an
arm64-only app reports nothing more useful than a bounce in the Dock.

**The build directory is called `build.noindex` for the suffix.** Spotlight
skips any directory whose name ends in it, and a development build needs that:
what comes out is a complete, launchable `Claude Graft.app`, so an ordinary
`build/` puts a second Graft in Spotlight beside the installed one with nothing
to tell them apart. Opening the wrong one gives two instances polling the same
profiles, racing for the same keychain prompt, and a Sparkle that updates a
bundle nobody installed. Seen for real, which is why the directory was renamed.

**The Command Line Tools' default SDK cannot build the app.** With only the
Command Line Tools installed, the macOS 27 SDK they default to cannot expand
SwiftUI's `@State` — the compiler reports that the plugin for `SwiftUIMacros`
was not found — so anything that compiles the app fails in `SettingsView`
before it reaches a line of this project. The 26.5 SDK beside it works:
`SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk` in front of
any of the commands.

## Topic guides

- <a id="claude-desktops-own-updates"></a>[Claude Desktop updates](updates-and-releases-guide/claude-desktop-updates.md)
- <a id="grafts-updates"></a>[Graft updates](updates-and-releases-guide/graft-updates.md)
- <a id="signing"></a><a id="one-version-line-for-the-mac-and-windows"></a>[Signing and versioning](updates-and-releases-guide/signing-and-versioning.md)
