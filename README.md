# Claude Graft

**1× is less but 5× is too much.** *(Or 20× is less, lol.)*

One Claude login is never quite the right amount.

Claude Desktop signs into one account at a time. Graft gives you as many Claudes
as you want — each with its own name, icon and login — and lets any of them read
another one's Claude Code history.

That last part is the point. Every other way of running two accounts gives you
an empty second profile. Graft lets your work login open your personal chats,
connectors and extensions while the accounts stay completely separate.

There are screenshots and the short version at
**[claude-graft](https://aaditya-v-more.github.io/claude-graft/)**.

![Live usage for both accounts in the menu bar](docs/assets/menu-bar.png)

## Install

```
brew install --cask aaditya-v-more/claude-graft/claude-graft
```

Homebrew asks you to trust the cask the first time, because this isn't an
official Homebrew tap. Say yes once and it never asks again.

The app is ad-hoc signed rather than notarised — that needs a paid Apple
developer account — so macOS would normally refuse it. The cask clears the
quarantine flag for you at install time; the command it runs is right there in
the cask file. If you'd rather judge that yourself, take the **.dmg** from the
[releases page](https://github.com/aaditya-v-more/claude-graft/releases) and
allow it in System Settings → Privacy & Security instead.

Requires macOS 13 or later. Universal, for Apple Silicon and Intel.

## Making a Claude

Name it, choose where its chats come from, press **Create Shortcut**. You get a
real app in `/Applications` that you can launch from Spotlight or pin to the
Dock like any other.

![Claude and Claude 2 side by side in Spotlight](docs/assets/shortcuts.png)

Launch it, sign in with the other account, and both run side by side.

![The shortcut's settings](docs/assets/window.png)

Each shortcut is self-contained — it carries its own launcher and a description
of its profile — so it keeps working whether or not Graft is installed or
running. Every launch re-establishes its links first, which matters because
Claude replaces some config files wholesale, and that quietly detaches a
symlink.

Point the profile folder at one that already exists and the shortcut adopts it,
which saves signing in again if you set something up by hand.

## Usage in the menu bar

The percentage in the bar is whichever account is currently open, with the rest
behind the tooltip. Figures come from Anthropic's own endpoint using the login
each profile already holds — so they're live even for an account that isn't
running, with real reset times.

Reaching that needs the profile's token, which macOS gates behind one keychain
prompt. Choose **Always Allow** and later reads are silent. macOS ties that
permission to one exact build, so a new version asks again — once, rather than
quietly showing you worse numbers.

Only the access token is ever decrypted, never the refresh token, because
Anthropic rotates those and using one would sign Claude Desktop out. Nothing is
written back to Claude's config or keychain, and the token goes to
`api.anthropic.com` and nowhere else.

**Start Session** opens a five-hour window on an account without switching to
it: one short message, `max_tokens` as low as the API allows, reply discarded.
It costs a handful of tokens and happens only because you pressed the button.

## Updating

Graft updates itself. It checks hourly, and at launch when an hour has passed,
then downloads, installs and restarts on its own — the only sign is the menu bar
item blinking out and back. Every download is verified against a signing key
that ships inside the app. **Check for Updates…** does it immediately if you'd
rather not wait.

## Worth knowing

Two Claudes sharing a chat store write to the same files, so opening the *same*
conversation in both at once can lose messages. Opening one from Graft — the
window or the menu bar, a shortcut or Claude itself — warns you when something
else is already on those chats. Launching it from the Dock doesn't check.

Opening a Claude that is already open brings that one forward instead of
starting a second. Claude Desktop itself doesn't refuse a second copy on the
same profile, and two copies of one profile is the worst version of the problem
above — not two accounts sharing chats, but the very same files being written
twice over. If you've closed the window and left it in the menu bar, opening it
builds the window back, the way clicking a Dock icon does.

A profile that borrows chats gets its own copy of them rather than a link to
them. Claude Desktop will not write a conversation's record into a folder that
resolves outside its own profile, and a link to another profile's chat store is
exactly such a folder — so through a link a borrowed conversation can be read
and never changed. Archiving one takes it out of that window's sidebar and
saves nothing anywhere, so it is back on the next launch. Renaming and starring
go through the same write.

A copy is a folder Claude will write into, so all of that works, and Graft
carries the changes both ways each time either Claude is opened. Sharing merges
the two histories rather than swapping one for the other: the profile keeps the
chats it already had and gains the source's, and both sidebars end up showing
the combined set. Archive the same conversation differently in both between two
openings and the one touched last wins. Only the small record files are copied
— the messages themselves live in `~/.claude` and are shared either way.

Because it goes both ways, the borrowing profile's existing chats are copied
into the source as well. Switching a shortcut back to its own chats hands
everything over and leaves that profile with exactly what it had before, down
to anything it archived along the way — but the copies already in the source
stay there. Merging two histories is the one thing here that cannot be undone.

`~/.claude` — settings, `CLAUDE.md`, skills, plugins, MCP servers, transcripts —
is already shared by every instance, since it comes from `$HOME` rather than the
profile.

Claude Code prunes transcripts after 30 days while the desktop's records are
permanent, so old chats can open to "Session not found on disk". That happens in
a normal single-account setup too; raise `cleanupPeriodDays` in
`~/.claude/settings.json` to keep them longer.

Deleting a shortcut asks whether to take the profile folder with it. Keeping it
is the default, and the destructive option is guarded: Claude's own profile can
never be removed, nor anything outside `~/Library/Application Support`, nor a
profile that's running or that another shortcut still points at. Graft also
refuses to touch any app it didn't create — it identifies its own by the
description file they carry, never by name.

## How it works

Every chat is stored twice. The transcript holds the messages, lives in
`~/.claude/projects`, has no account anywhere in its path, and is shared by
every Claude on the machine. The record is the sidebar row — title, times,
archived flag — and its account is not a field inside it but the folder it sits
in.

So sharing maps one profile's `account/org` onto the other's, one level below
the store, because a Claude only ever reads the account folder matching its own
login. Settings are shared by symlink; records are shared by copying.

```mermaid
flowchart TB
    subgraph DST["Claude 2 — account B"]
        direction LR
        B1["config.json · extensions<br/>window state"]
        B2["records<br/>B / org"]
        B3[".graft-own<br/>what B brought"]
    end
    subgraph SRC["Claude 1 — account A"]
        direction LR
        A1["config.json · extensions<br/>window state"]
        A2["records<br/>A / org"]
    end
    T["~/.claude/projects — the transcripts<br/>no account in the path, one set for the machine"]

    B3 -.->|seeded once| B2
    B1 -.->|symlink| A1
    B2 <-->|copies, both ways| A2
    B2 -.-> T
    A2 -.-> T

    classDef link stroke:#2563eb,stroke-width:2px
    classDef copy stroke:#16a34a,stroke-width:2px
    classDef shared stroke:#8a929c,stroke-width:2px,stroke-dasharray:4 3
    class A1,B1 link
    class A2,B2,B3 copy
    class T shared
```

Records are copied rather than linked because Claude Desktop will not write one
into a folder that resolves outside its own profile. Through a link a borrowed
chat can be read and never archived, renamed or deleted.

Pressing a shortcut runs its own launcher, which squares the storage up before
any window exists.

```mermaid
flowchart TD
    P([Shortcut pressed]) --> R{"Claude already<br/>on this profile?"}
    R -->|yes| F([Bring that one forward])
    R -->|no| L["Point the settings links at the source"]
    L --> M["Carry chat changes both ways"]
    M --> S["File records for sessions<br/>that closed without one"]
    S --> O([Launch Claude])
```

The first launch stashes the profile's own chats and copies them straight back
into the shared set, so both sidebars end up holding both histories. The stash
is what makes going back exact.

```mermaid
flowchart LR
    O["Its own chats"] -->|first launch| ST[".graft-own"]
    ST -->|copied back in| MG["Shared folder<br/>both histories"]
    SRC["Source's chats"] -->|copied in| MG
    MG -->|back to its own chats| K["Keeps what the stash names,<br/>hands the rest over"]

    classDef keep stroke:#16a34a,stroke-width:2px
    classDef merge stroke:#2563eb,stroke-width:2px
    class ST,K keep
    class MG merge
```

Merging is the one thing here that cannot be undone: what a profile brings stays
in the profile it was merged into.

## Building it

```
./build.sh     the app, into build.noindex/
./test.sh      542 checks, all in a throwaway directory
./release.sh   tests, builds universal, signs, packages
```

Swift toolchain from Xcode, no project file, no dependencies to install —
Sparkle is fetched into `vendor/` on the first build, pinned to a version and a
checksum. The version lives in one file, `VERSION`.

`CLAUDE.md` has the notes that aren't obvious from the code: what Claude Desktop
keeps where, the rules around borrowed credentials, and the several things that
had to go wrong before this worked. Read it before changing anything that
touches profiles, the keychain or the menu bar.

## Supporting it

Free, and staying that way — no licence to buy, no account to make, nothing
measured and sent anywhere. If it saved you the trouble, there's a
[tip jar](https://ko-fi.com/aadityavmore). New macOS releases break things and
Claude Desktop moves its own furniture every few weeks, and that's what the
money is for.

## Licence

MIT.
