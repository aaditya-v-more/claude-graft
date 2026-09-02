<div align="center">

<img src="docs/assets/icon.png" width="120" alt="Claude Graft">

<h1>Claude Graft</h1>

<p>
  <b>1× is less but 5× is too much.</b> <i>(Or 20× is less, lol.)</i><br>
  One Claude login is never quite the right amount.
</p>

<p>
  <a href="https://github.com/aaditya-v-more/claude-graft/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/aaditya-v-more/claude-graft?style=for-the-badge&logo=github&logoColor=white&labelColor=1C1A17&color=C2410C"></a>
  <a href="https://github.com/aaditya-v-more/homebrew-claude-graft"><img alt="Homebrew cask" src="https://img.shields.io/badge/Homebrew-cask-C2410C?style=for-the-badge&logo=homebrew&logoColor=white&labelColor=1C1A17"></a>
  <img alt="macOS 13+" src="https://img.shields.io/badge/macOS-13%2B-C2410C?style=for-the-badge&logo=apple&logoColor=white&labelColor=1C1A17">
  <a href="https://github.com/aaditya-v-more/claude-graft/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/aaditya-v-more/claude-graft?style=for-the-badge&logo=github&logoColor=white&labelColor=1C1A17&color=C2410C"></a>
  <a href="LICENSE"><img alt="MIT licence" src="https://img.shields.io/github/license/aaditya-v-more/claude-graft?style=for-the-badge&labelColor=1C1A17&color=C2410C"></a>
  <a href="https://ko-fi.com/aadityavmore"><img alt="Support this on Ko-fi" src="https://img.shields.io/badge/Ko--fi-support-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white&labelColor=1C1A17"></a>
</p>

<p>
  <a href="https://aaditya-v-more.github.io/claude-graft/"><b>Website</b></a>
  &nbsp;·&nbsp;
  <a href="#install"><b>Install</b></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/aaditya-v-more/claude-graft/releases"><b>Releases</b></a>
</p>

<br>

<img src="docs/assets/menu-bar.png" width="460" alt="Live usage for both accounts in the menu bar">

</div>

Claude Desktop signs into one account at a time. Graft gives you as many Claudes
as you want — each with its own name, icon and login — and lets any of them read
another one's Claude Code history.

That last part is the point. Every other way of running two accounts gives you
an empty second profile. Graft lets your work login open your personal chats,
connectors and extensions while the accounts stay completely separate. There are
screenshots and the short version on
[the site](https://aaditya-v-more.github.io/claude-graft/).

## Before you install

Graft works on a copy of Claude Desktop, so Claude Desktop has to be there
first — in `/Applications`, or in `~/Applications` if that is where you keep it.
Graft installs it for you no more than it signs you in, and without it there is
nothing to copy.

macOS 13 or later. Universal, for Apple Silicon and Intel.

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

## Making a Claude

Name it, choose where its chats come from, press **Create Shortcut**. You get a
real app in `/Applications` that you can launch from Spotlight or pin to the
Dock like any other.

<img src="docs/assets/shortcuts.png" width="580" alt="Claude and Claude 2 side by side in Spotlight">

Launch it, sign in with the other account, and both run side by side.

<img src="docs/assets/window.png" width="660" alt="The shortcut's settings">

Each shortcut is self-contained — it carries its own launcher and a description
of its profile — so it keeps working whether or not Graft is installed or
running. Every launch re-establishes its links first, which matters because
Claude replaces some config files wholesale, and that quietly detaches a
symlink.

Point the profile folder at one that already exists and the shortcut adopts it,
which saves signing in again if you set something up by hand.

## Usage in the menu bar

The bar shows whichever account is currently open, the rest behind the tooltip.
Figures come from Anthropic's own endpoint using the login each profile already
holds, so they're live with real reset times even for an account that isn't
running.

Reading that token needs one keychain prompt: choose **Always Allow** and later
reads are silent. macOS ties the permission to one exact build, so a new version
asks again — once, rather than quietly showing you worse numbers.

Only the access token is ever decrypted, never the refresh token, which
Anthropic rotates and which would sign Claude Desktop out. Nothing is written
back to Claude's config or keychain, and the token goes to `api.anthropic.com`
and nowhere else.

**Start Session** opens a five-hour window on an account without switching to
it — one short message, `max_tokens` as low as the API allows, reply discarded.
A handful of tokens, and only because you pressed the button.

## Updating

Graft updates itself: hourly, and at launch once an hour has passed, it
downloads, installs and restarts on its own, the only sign being the menu bar
item blinking out and back. Every download is verified against a signing key
that ships inside the app. **Check for Updates…** does it immediately.

## Worth knowing

Two Claudes sharing a chat store write to the same files, so opening the *same*
conversation in both at once can lose messages. Opening one from Graft — window
or menu bar, a shortcut or Claude itself — warns you when something else is
already on those chats; from the Dock it doesn't check.

Opening a Claude that is already open brings that one forward rather than
starting a second, which Claude Desktop won't refuse on its own — two copies of
one profile being the worst version of that problem: not two accounts sharing
chats, but the very same files written twice over. With the window closed and
the item left in the menu bar, opening it builds the window back, the way a Dock
icon does.

A profile that borrows chats gets its own copy rather than a link. Claude
Desktop will not write a conversation's record into a folder that resolves
outside its own profile, and a link to another profile's chat store is exactly
that — so through a link a borrowed conversation can be read and never changed:
archiving one takes it out of that window's sidebar, saves nothing anywhere, and
it is back on the next launch. Renaming and starring go through the same write.

A copy is a folder Claude will write into, so all of that works, and Graft
carries the changes both ways each time either Claude is opened. Sharing merges
the two histories rather than swapping one for the other — the profile keeps the
chats it had and gains the source's, and both sidebars show the combined set.
Archive the same conversation differently in both between two openings and the
one touched last wins. Only the small record files are copied; the messages live
in `~/.claude` and are shared either way.

Because it goes both ways, the borrowing profile's own chats are copied into the
source as well. Switching a shortcut back hands everything over and leaves that
profile with exactly what it had, down to anything it archived along the way —
but the copies already in the source stay there. Merging two histories is the
one thing here that cannot be undone.

`~/.claude` — settings, `CLAUDE.md`, skills, plugins, MCP servers, transcripts —
is already shared by every instance, coming from `$HOME` rather than the profile.

Claude Code prunes transcripts after 30 days while the desktop's records are
permanent, so old chats can open to "Session not found on disk". That happens in
a normal single-account setup too; raise `cleanupPeriodDays` in
`~/.claude/settings.json` to keep them longer.

Deleting a shortcut asks whether to take the profile folder with it. Keeping it
is the default, and the destructive option is guarded: never Claude's own
profile, never anything outside `~/Library/Application Support`, never a profile
that's running or that another shortcut points at. Graft also refuses to touch
any app it didn't create, identifying its own by the description file they carry
rather than by name.

## How it works

Every chat is stored twice. The transcript holds the messages, lives in
`~/.claude/projects`, has no account anywhere in its path, and is shared by
every Claude on the machine. The record is the sidebar row — title, times,
archived flag — and its account is not a field inside it but the folder it sits
in.

So sharing maps one profile's `account/org` onto the other's, one level below
the store, because a Claude only ever reads the account folder matching its own
login. Settings are shared by symlink; records are shared by copying.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/layout-dark.svg">
  <img alt="Claude 2 and Claude 1 side by side. Claude 2's settings are a symlink to Claude 1's, their record folders hold copies carried both ways, and a hidden .graft-own folder seeds Claude 2's own chats into the shared set once. Both profiles read one set of transcripts in ~/.claude/projects, which has no account in its path." src="docs/assets/layout-light.svg" width="760">
</picture>

Records are copied rather than linked because Claude Desktop will not write one
into a folder that resolves outside its own profile. Through a link a borrowed
chat can be read and never archived, renamed or deleted.

Pressing a shortcut runs its own launcher, which squares the storage up before
any window exists.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/launch-dark.svg">
  <img alt="Pressing a shortcut: if a Claude is already on that profile it is brought forward, otherwise the settings links are pointed at the source, chat changes are carried both ways, records are filed for sessions that closed without one, and then Claude launches." src="docs/assets/launch-light.svg" width="760">
</picture>

The first launch stashes the profile's own chats and copies them straight back
into the shared set, so both sidebars end up holding both histories. The stash
is what makes going back exact.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/merge-dark.svg">
  <img alt="A profile's own chats go to .graft-own on the first launch and are copied straight back into a shared folder alongside the source's chats. Going back to its own chats keeps what the stash names and hands the rest over." src="docs/assets/merge-light.svg" width="760">
</picture>

Merging is the one thing here that cannot be undone: what a profile brings stays
in the profile it was merged into.

## Building it

```
./build.sh     the app, into build.noindex/
./test.sh      542 checks, all in a throwaway directory
./release.sh   tests, builds universal, signs, packages

Tools/render-diagrams.sh   the README's diagrams, into docs/assets
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
