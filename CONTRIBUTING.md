# Contributing to Claude Graft

Bug reports, fixes, tests and documentation are all welcome. This file is what
you need before the first one. `CLAUDE.md` is what you need before changing
anything that touches a profile, the keychain or the menu bar.

## What makes this repository unusual

Graft moves real chat histories between real profiles, and most of what can go
wrong here is not a crash. It is a folder read as empty when it could not be
read, or a stash folded back when it held the only copy — and the symptom
arrives hours later as chats missing from a sidebar, naming none of the code
that took them.

So the code is written around telling two indistinguishable situations apart,
and every rule that does so is written down in `CLAUDE.md` along with the
incident that taught it. Read the part covering whatever you are about to
change. A patch that looks obviously right and contradicts one of those
paragraphs is usually the same mistake being made a second time.

## Getting set up

A Swift toolchain from Xcode, macOS 13 or later, and Claude Desktop installed if
you want to run what you build. There is no project file and nothing to install
first: Sparkle is fetched into `vendor/` on the first build, pinned to a version
and a checksum.

    ./build.sh                    app and launcher into build.noindex/, this architecture
    GRAFT_UNIVERSAL=1 ./build.sh  both architectures, joined with lipo
    ./test.sh                     574 checks, all in a throwaway directory

    Tools/render-diagrams.sh      the README's diagrams, into docs/assets

Quit the installed Graft before running the one you built. Two instances poll
the same profiles, race for the same keychain prompt, and Sparkle will happily
update a bundle nobody installed. The build directory is called `build.noindex`
for the suffix — Spotlight skips it, so your build does not turn up in search
beside the installed copy with nothing to tell them apart.

Expect a keychain prompt on every rebuild. The permission to decrypt Claude's
token is tied to one exact code hash, and an ad-hoc signature earns a fresh one
each time you compile.

Shortcuts carry their own copy of the launcher, so a change to launcher code
reaches an existing shortcut only when the version in `VERSION` differs from the
one stamped in its bundle. Testing launcher changes against a shortcut you made
earlier means bumping that number locally, or deleting the shortcut and making
it again.

## Tests

`Tests/main.swift` is the whole suite and it is a plain program rather than a
framework. Everything runs against a throwaway Application Support and
Applications directory set up in the first few lines, so nothing in it can reach
a real profile or a real app.

A check is a condition and a sentence saying what is true:

    check(profile.token == nil, "a profile with no stored login has no token")

Write the sentence for whoever reads it as a failure, months from now, knowing
none of this. `section("…")` groups them; put a new check beside the ones asking
the same kind of question rather than at the end.

Anything that changes what happens to somebody's chats wants a check, and the
useful ones are almost always about the situation rather than the function: a
store that could not be read, a stash sitting beside a folder, a second pass
over the same profile. The suite also reads the source of `Sources/App` for a
few rules that cannot be expressed any other way — how many callers a function
has, whether a call appears on a line with a timer — so a refactor can fail it
without any behaviour changing. Those checks say what they are protecting.

## Style

Comments say why, never what. A comment that restates the line under it should
be deleted:

    // WRONG — increments the counter
    failures += 1

    // RIGHT — a refused call retried on the next tick becomes 120 attempts an
    // hour, which is how a client earns a rate limit rather than avoids one.
    failures += 1

Documentation is prose. The README explains things in paragraphs rather than
bullet lists, and reads as something a person wrote by hand; keep new sections
in that voice. Nothing published — the README, a commit message, a release
note — names a tool or a pipeline that produced it.

## Commits and pull requests

One line, present tense, saying what the change does for somebody using the app
rather than which file moved. The log is the model:

    Show the Claude that is already open instead of starting a second one
    Leave a chat store the sweep could not read exactly as it was

No trailers and no attribution lines. If a change needs more explanation than
the subject line holds, put it in the body as prose.

Open a pull request against `main`. Say what you changed and how you know it
works — a check in the suite, or what you did by hand to a real profile if the
change is one the suite cannot reach. Continuous integration runs `./test.sh`
and a universal build on every push and pull request, and both have to pass.

If a change would alter what happens to an existing profile's chats, say so
outright in the description. That is the part worth reviewing slowly.

## Versions and releases

`VERSION` at the repo root is the only place the number is written down, and
bumping it is a release, not a change. Leave it alone in a pull request:
`release.sh` refuses a version whose tag already exists, and two builds
answering to one number is something no update feed can tell apart.

The appcast in `docs/` and the release notes beside it are generated by
`release.sh` and signed against archives that are never built again. Nothing in
a pull request should edit them by hand.

## Reporting a bug

Every pass Graft runs writes down what it saw, which is usually faster than
reproducing it. Both files live in `~/Library/Application Support/ClaudeGraft/`:

    diagnostics.log     one JSON line per decision, appended by the app and by
                        every shortcut's launcher
    state-report.txt    what is true right now — the profiles, their accounts,
                        which folders are linked, stashed or missing

Attach them if you can. They carry profile names, folder paths and the account
and organization identifiers Claude uses; they do not carry chat titles,
messages or tokens. Redact whatever you would rather not post.

Say which version of Graft and which version of macOS, and whether the profiles
involved are on one Claude account or two — most of the behaviour here differs
between those two cases.

## Things worth picking up

**Start Session has never been seen to work.** It posts one short message to
mint a five-hour window without switching accounts, and whether the API accepts
it that way is unproven. The endpoint's own error surfaces in the interface
rather than being swallowed, so the first step is reading it.

**macOS and Claude Desktop both move.** A new macOS release breaks something
every year, and Claude Desktop rearranges its own storage every few weeks. A
report that names precisely what moved is worth as much as a patch.

**The diagrams and the README** are as much of this project as the Swift is. If
something took you three reads to understand, that is a bug in the prose.

## Licence

MIT. By contributing you agree your changes ship under it.
