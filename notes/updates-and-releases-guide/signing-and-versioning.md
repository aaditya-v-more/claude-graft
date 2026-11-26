# Signing and versioning

[Back to updates-and-releases.md](../updates-and-releases.md)

## Signing

Sparkle ships its framework already ad-hoc signed, and its validator accepts an
EdDSA signature alone — the header comment on `validateUpdateForHost` says it
"allows change of Code Signing identity", which is exactly what an ad-hoc build
does on every compile. Elsewhere it says outright that "if no Apple Code Signing
certificate is available, adhoc signing can be used at minimum". `SUFileManager`
strips `com.apple.quarantine` from what it installs, so only the first install
meets Gatekeeper.

A plain `./build.sh` is not a release and is signed with the Mac's Apple
Development certificate when there is one, so the keychain stops asking at every
rebuild ([usage-and-credentials.md](../usage-and-credentials.md)). `release.sh`
always passes its own identity, a Developer ID or ad-hoc, so that certificate
never reaches a download. The XPC services are removed from the embedded copy: they need
entitlements this app does not carry, and launchd refuses them for a
non-sandboxed app. The signing key lives in the login keychain under
`https://sparkle-project.org`; `vendor/bin/generate_keys -x` exports it if CI
ever needs it.

## One version line for the Mac and Windows

Until the Windows port was merged, Windows had its own `windows/VERSION` and
released from another repository under `win-v*` tags, so the same number named
two different apps and a person could not tell from a version which Mac and
which Windows build went together. Now the repository's `VERSION` stamps both,
and each version has one GitHub release. The Mac's `release.sh` makes the tag
and the release with the Mac's zip and disk image; the tag starts
`windows-release.yml`, which tests, packs with Velopack and uploads the setup,
the update packages and `releases.win.json` beside them, leaving the Mac's
title and notes alone. A version can go out on Windows first, as 2.1.1 did:
the workflow then makes the release itself with the Windows notes. It does not mark that release
"latest". The website takes the version it shows from GitHub's latest release, but
its DMG link only from a release carrying one. A Windows-only latest would label
the Mac download with a number whose disk image does not exist, and the README
badge would point at a release with nothing for the Mac. Velopack's update check
lists the releases itself, so Windows finds the update either way. Since
`release.sh` refuses a tag that exists, the Mac then takes the next number
rather than adding a build from a later commit to an older tag.

**A deleted release keeps its tag name.** Releases here are immutable, and
GitHub will not let the tag of a deleted immutable release be made again: the
push is refused as a restricted creation, even for the owner. The 2.1.0 release
was withdrawn on 18 Nov 2026 and its number can never carry a build, so Windows
went out as 2.1.1. A release is for keeps; withdraw one only when a new number is
acceptable.

**A prerelease is frozen the moment it is made.** Immutability covers
prereleases too, so a release made with `--prerelease` takes no more files:
2.2.1 was staged that way with the Mac's files, and the Windows workflow's
upload was refused with `Cannot upload assets to an immutable release`. Stage
a release as a draft, let the Windows workflow add its files to the draft, and
only then publish it as a prerelease. `appcast-staged.xml` cannot be taken off
afterwards either, which is why 2.2.0 still carries it.

**2.2.1 went out for the Mac alone,** since Windows was not affected and the
release could no longer take its files. It was still made latest, so the Mac
download on the site and the README's badge name it, and every Windows link
on the site and in both READMEs names v2.2.0's setup instead of
`releases/latest/download/`, which would have answered 404. Put them back to
`latest` with the next release that carries Windows. Windows Checks asks for
`windows/release-notes/<VERSION>.md` only while that version is untagged, since
it failed every pull request after 2.2.1 for want of notes Windows never needed.

**A version the Mac ships alone is listed in `windows/mac-only.txt`.** 2.2.1
became one by accident and 2.3.0 is one on purpose: accounts, their order and
automatic sessions are not on Windows yet. Without the list there was no way to
say so. Windows Checks failed every pull request for want of notes, and with
notes the tag would have built and attached a Windows setup. A listed version
needs no `windows/release-notes/<VERSION>.md` and must not have one,
`windows-release.yml` ends before its build, and `release.sh` says to wait for
no Windows files. The Windows links stay pinned to the last release with a
setup, as above, and what Windows has gained on main since goes out with the
next version that is not on the list.

The Mac's update feed and cask name their version explicitly, so a release
that carries only Windows packages offers the Mac nothing, and Velopack passes
over a release without `releases.win.json` in the same way.

Windows ships an installer and no portable copy. A portable Graft cannot update
itself, and an out-of-date Graft is what the hourly check exists to prevent.
`GRAFT_UPDATE_FEED` points an installed copy at a folder or web feed instead of
the releases, so an older build can be watched updating to a new one before the
new one is published.
