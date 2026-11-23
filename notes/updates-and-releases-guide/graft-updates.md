# Graft updates

[Back to updates-and-releases.md](../updates-and-releases.md)

## Graft's updates

**An update installs itself and says nothing.** Checked hourly and at launch
once that much has passed, downloaded, installed and restarted with nobody
asked. The gentle-reminder route was tried first and was wrong for this app:
there is usually no window to put a question in front of, so an update that
waits to be noticed is one that never gets installed. What it costs is the
status item blinking out and back.

That goes for a press as much as for the schedule. `checkForUpdatesInBackground`
is what the dropdown calls, never `checkForUpdates` — Sparkle's interactive
check puts up its own panel and waits on Install and Relaunch, so the scheduled
check installed silently while the button asked, which is one app behaving two
ways. Pressing a line that already reads "Version 1.0.6 is available" is not a
request to be asked whether you want it. The suite fails if the interactive call
reappears.

That only works because `applicationShouldTerminate` lets it through, and
Sparkle's own header warns that `updaterWillRelaunchApplication` may not be
called. So `isRelaunchingForUpdate` is set at every hook before an install —
`willInstallUpdate`, `willInstallUpdateOnQuit` and the relaunch — and the suite
counts them, because one missed hook is an update that installs and never
starts, which happened twice before it was understood. `willInstallUpdateOnQuit`
also calls the immediate handler: Sparkle would otherwise wait for a quit that a
menu bar app may not see for weeks.

**A hold is the one way to stop that, and it is off unless somebody turns it
on.** `holdUpdates` is read with a plain `bool(forKey:)`, so every machine that
arrives by updating — none of which has the key — goes on installing silently
as before. On, Sparkle's own schedule is turned off, because it downloads
whatever it finds, and an hourly `checkForUpdateInformation` probe stands in for
it: the feed is read, the version shown in the dropdown and on the status item,
and nothing fetched. A press on that line runs the same background check the
schedule would have, so there is still exactly one way a version arrives and it
still asks nothing. The controller is built with `startingUpdater: false` and
started only after the hold is applied, since Sparkle schedules its launch check
as it starts and a hold arriving a moment late would let that check install the
very thing being held. `UpdatePolicy` carries the rule.

That press needs `SUAllowsAutomaticUpdates` in `Info.plist`. Without the key
Sparkle allows background downloading only while its own schedule is on, so
under a hold `automaticallyDownloadsUpdates` read false whatever had been set.
The press then took the ask-first path and showed nothing: tested for real, it
read the feed and stayed on the old version twice. With the schedule on, the
key changes nothing, which is why the default path never showed the problem.

**One update asks, and the asking lives in the feed rather than the code.**
Every release installs itself without a word, which is the rule above and still
the rule. 1.1.0 is the exception: it merges two chat histories into one and that
cannot be undone, so it is not a thing to do to somebody who was not consulted.
`sparkle:minimumAutoupdateVersion` is what says so — Sparkle prompts anybody
below that version and installs silently for anybody at or above it, and its own
header is plain about the first half: "if the requirement is not met, the user is
always prompted to install the update."

Which makes it a floor and not a mark on one release. Set it to 1.1.0 on every
entry from 1.1.0 onwards and the behaviour is exactly "ask once, quiet
afterwards": a machine already on 1.1.0 meets the requirement and updates
silently for ever, while one still on 1.0.x is asked until it crosses. Leave it
off a later 1.1.x and that release installs itself on a 1.0.x machine and skips
the question 1.1.0 exists to ask — same person, same merge, no dialog.
`release.sh` counts the entries carrying it the way it counts the items, one
more each run, which catches both the new entry missing it and
`generate_appcast` dropping it from an old one on a later pass. It is written in
after generation because there is no option for it, which is safe only because
nothing signs the appcast itself: the signatures in it are over the archives.

No 1.0.x release was needed to arm any of this. The floor is read by the Sparkle
already inside every build that has ever shipped, so a 1.0.0 machine that has
never been opened since honours it too. What a 1.0.x release could have bought is
the dialog arriving in front rather than behind — for a menu bar app Sparkle
shows a scheduled update alert immediately but, in its own words, "behind other
running applications" — and that is a delay, not a silent merge, which is the
half that matters.

Every release has to bring a note, and that note has to say what upgrading from
1.0.x does; `release.sh` refuses to build one without both. This used to be
asked of 1.1.0 alone, on the reasoning that it was the one release putting a
dialog in front of somebody. It is not. Sparkle offers a 1.0.x machine the
newest entry in the feed, not the one the floor names, and the dialog it puts
up carries that entry's notes — so from a 1.0.11 Mac every release since 1.1.0
has been the one that stops to ask, and 1.1.7, 1.1.8 and 2.0.0 asked with notes
that never mentioned the merge the question exists for. A note with nothing to
say about 1.0.x is as good as no note in that dialog. The check looks for
"from 1.0.x", which both headings used so far carry: "Upgrading from 1.0.x"
and "Coming from 1.0.x?". Notes live in
`docs/release-notes/<version>.html` and are copied beside the archive, which is
where `generate_appcast` looks for them; carrying no DOCTYPE or body tags is what
makes them embed rather than become a link to somewhere that has to stay up.

**The feed keeps every release it has ever carried.** `generate_appcast` prunes
to a handful of entries per branch point unless told otherwise, so each release
silently dropped the oldest — 1.0.0 and 1.0.1 had already gone before anyone
looked. Nothing breaks when they go, since a client is offered the newest entry
regardless, but the signature in an entry is over an archive that is never built
again, so a dropped entry cannot be regenerated, only recovered out of git.
`release.sh` passes `--maximum-versions 0` and checks the count went up by
exactly one; the old check was "no fewer than before", which adding one and
losing one satisfies perfectly.

**The new entry is checked on its own, and the live feed changes last.** The
signature check used to search the whole file for `sparkle:edSignature`, and
every older entry carries one, so it passed whether or not the new entry was
signed — the exact silent failure it was written for, when the key found does
not match `SUPublicEDKey`. `release.sh` now cuts out the `<item>` naming this
version's zip and asks that one for a signature, a `length` equal to the zip it
built, and the floor; it also counts signatures across the file, one more than
before, for a pass that lost an old one. All of this used to happen in
`docs/appcast.xml` itself, so a run that stopped at a check, or at the cask
after it, left the live file offering a release with no archive uploaded and no
tag, one `git add docs` away from being served. The feed is built and checked in
`dist/sparkle/appcast.xml` and copied into `docs/` as the last step before
`--install`.

**Nothing a person downloads or runs ships before it has been run.** Every
check above asks of the files this machine built, none of what people fetch,
and an install command or a link is only as good as what it fetches.
So a release is staged before it is published. Its GitHub release is a
prerelease, which Sparkle never sees (the feed is not pushed yet), Velopack's
GitHub source skips, and the site's `releases/latest` lookup ignores; the zip,
disk image, Windows setup and packages are on it at the addresses they will
keep, with the new feed beside them as `appcast-staged.xml`; the cask waits on
the tap's `release-<version>` branch. `Tools/check-distribution.py --tag`
fetches all of that the way a person would: every release link on the README
and the site opens, every `brew install` command finds its cask, the cask's
disk image matches its sha256, the feed's new zip is the length it says and
verifies against `SUPublicEDKey`, and the Windows feed's package matches its
size and SHA256. Then `Tests/manual/release-install.md` installs it on clean
Macs and Windows machines by each route, a Mac pointed at the staged feed with
`defaults write graft.claude-graft SUFeedURL` and a Windows Graft with
`GRAFT_UPDATE_FEED`, both of which read the release's own files. Publishing
flips the prerelease, pushes the feed and moves the tap's `main` to the
branch, and the same script then runs daily against what is live.

**The feed URL is unchangeable once shipped.** A copy out in the world polls the
URL it was born with, and GitHub gives Pages no redirect if the account is ever
renamed — repositories redirect, Pages does not. `Resources/Info.plist` and the
`--download-url-prefix` in `release.sh` both name the account, and the suite
checks they agree, because a rename that touched one would leave the feed
advertising downloads nobody can fetch.
