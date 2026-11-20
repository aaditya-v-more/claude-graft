# Working on Claude Graft

## Style, first

Comments say **why**, never what. Delete a comment that restates the line under it.

    // WRONG — increments the counter
    failures += 1

    // RIGHT — a refused call retried on the next tick becomes 120 attempts an
    // hour, which is how a client earns a rate limit rather than avoids one.
    failures += 1

A comment is for the non-obvious: a workaround, an edge-case quirk, a knotty
piece of regex or arithmetic, an external constraint. Never a narration of the
line beside it — no `// loop through items`, `// initialize variable`, `// check
if user exists`. Reach for a clearer name or shape before reaching for a
comment. Never leave a mark that the code was generated or changed — no
`// Added`, no `/* Updated logic */`, no changelog in the source. And leave the
docs and docstrings already here alone unless the task is to rework them.

And keep it short. A why-comment is a line, two at the outside; when it wants a
paragraph the reason has become an invariant, and invariants live in this file
or the README, not in a block beside the code. Porting from the mac build is
where this slips — that source is commented at length on purpose, so carry the
reason across, not the prose that surrounds it there.

Prose in the README, not bullet lists. Tests read as sentences — `check(…, "a
profile with no stored login has no token")`, not `"testTokenNil"`.

Nothing that leaves this machine may name a tool, a model or a pipeline. That
includes commit messages, the README and anything pushed. Write what a person
would have written by hand.

## Where working knowledge lives

In the repository, and nowhere else. A fact kept in a private notes store beside
it goes stale where nobody sees it: the private record of this machine's profile
layout went on saying the opposite of the truth for two months after the
profiles had changed.

This file holds the rules. The reasoning behind each one, the incident that
taught it and the measurements that settled it are in `notes/`, one file per
area, linked from the heading each rule sits under. **Read the note before
changing code in its area** — nearly every rule here exists because two
situations look identical from inside the code, and the note is what says how
to tell them apart. A new fact goes into the note it belongs to; a new rule gets
one line here as well.

## Where builds and tests run

Not on the development machine. There code is edited, committed and read, and
nothing else: no build, no suite, no layout or opening check, no launch of the
app and no release check. Mac builds and every Mac check run on the Mac mini or
the macOS VMs, Windows builds and checks on a Windows test PC, each from a
commit. The commands below are what those machines run.

For prototyping, debugging and live development a developer may work on a test
machine directly, over ssh or by driving it, holding that machine's lock for
the length of the work and leaving it as found: nothing device-wide changed, no
accounts, network or other products touched. What that shows is a development
check; a release and the everyday-use cases are still judged by the testers.

Steps that need a person on a real machine are cases in `Tests/manual/`. A
case says what must not be touched and how to put the machine back, and never
releases, commits or touches a chat. A keychain prompt, permission or
setting it meets is answered by whoever runs the case: Allow, never Always
Allow.

## Commands

    ./build.sh                     app + launcher into build.noindex/, this arch only
    GRAFT_UNIVERSAL=1 ./build.sh   both arches, joined with lipo
    ./test.sh                      the suite, all in a throwaway directory
    ./test-layout.sh               window layout from outside, both languages; needs Accessibility
    ./test-opening.sh              launch paths against disposable, signed-out profiles
    ./test-sidebar.sh              sidebar sync against real Chromium storage; needs Claude and Node
    ./test-relay.sh                the shared-usage relay over loopback, against a stand-in service
    ./release.sh [--install]       tests, builds universal, draws the icon, signs, packages

With only the Command Line Tools installed, the macOS 27 SDK they default to
cannot compile the app's SwiftUI; put
`SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk` in front of
any of these. The why is in [notes/updates-and-releases.md](notes/updates-and-releases.md).

## Topic guides

- <a id="rules"></a><a id="opening-a-profile--notesopening-profilesmd"></a><a id="sharing-chats--noteschat-sharingmd"></a><a id="session-records--notessession-recordsmd"></a><a id="usage-and-credentials--notesusage-and-credentialsmd"></a><a id="shared-usage--notesshared-usagemd"></a>[Profile and sharing rules](notes/claude-guide/profile-and-sharing-rules.md)
- <a id="artifacts--notesartifactsmd"></a><a id="projects-and-search--notesprojects-and-searchmd"></a><a id="interface--notesinterfacemd"></a><a id="updates-and-releases--notesupdates-and-releasesmd-notesupdate-relaunchmd"></a><a id="sidebar-and-desktop-settings--notessidebar-and-settingsmd"></a><a id="website-and-search--noteswebsite-and-searchmd"></a><a id="diagnostics-and-verification--notesdiagnostics-and-verificationmd"></a>[Artifacts, interface and release rules](notes/claude-guide/artifacts-interface-and-release-rules.md)
