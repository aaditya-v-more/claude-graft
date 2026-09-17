<div align="center">

<img src="docs/assets/icon.png" width="120" alt="Claude Graft">

<h1>Claude Graft</h1>

<p>
  <b>1× is less but 5× is too much.</b> <i>(Or 20× is less, lol.)</i><br>
  One Claude login is never quite the right amount.
</p>

<p>
  <a href="https://github.com/aaditya-v-more/claude-graft/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/aaditya-v-more/claude-graft?style=for-the-badge&logo=github&logoColor=white&labelColor=1C1A17&color=C2410C"></a>
  <a href="https://github.com/aaditya-v-more/claude-graft/releases"><img alt="Total downloads" src="https://img.shields.io/github/downloads/aaditya-v-more/claude-graft/total?style=for-the-badge&logo=github&logoColor=white&label=Total%20downloads&labelColor=1C1A17&color=C2410C"></a>
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

Run multiple Claude Desktop accounts side by side, each with its own login,
shortcut and optional shared Claude Code history — including pins and sidebar
order. More on [the site](https://aaditya-v-more.github.io/claude-graft/).

## Install

Requires macOS 13+ and Claude Desktop in `/Applications` or `~/Applications`.
Works on Apple Silicon and Intel, in English and Russian. Sidebar sharing was
tested with Claude Desktop 2.110.1 on macOS 27.

```sh
brew install --cask aaditya-v-more/claude-graft/claude-graft
```

Homebrew may ask you to trust this third-party cask. Graft is ad-hoc signed,
not notarised; the cask clears its quarantine flag. For a manual install, get
the **.dmg** from [Releases](https://github.com/aaditya-v-more/claude-graft/releases)
and allow it in **System Settings → Privacy & Security**.

## Making a Claude

Choose a name, icon and where its chats come from, then press **Create Shortcut**.
Open it from Spotlight or the Dock and sign into the other account.

<img src="docs/assets/shortcuts.png" width="580" alt="Claude and Claude 2 side by side in Spotlight">

Each shortcut works without Graft running and can adopt an existing profile
folder. Its custom name and icon appear in Finder and on the pinned shortcut;
the running app keeps Claude's name and icon. Graft leaves Claude's app untouched.

<img src="docs/assets/window.png" width="660" alt="The shortcut's settings">

## Bringing over chats from a previous login

An empty sidebar after signing into a new shortcut can mean your chats are
still in the profile you previously used. Graft finds them and offers
**Copy Them Here**. Quit every Claude instance before copying.

<img src="docs/assets/chats-elsewhere.png" width="605" alt="The Chats found elsewhere section, saying that Claude 2 is holding 188 chats for the account this profile is signed into that this one has not got, listing five of them with the dates they were last active, above a Copy Them Here button">

This copies missing chats once, keeping the originals and anything already in
the destination. For ongoing sync, choose chat sharing instead.

## Usage in the menu bar

See live usage and reset times for your accounts, even when they aren't running.
Graft reads each profile's access token and sends it only to `api.anthropic.com`;
it leaves refresh tokens and logins alone. Choose **Always Allow**
at the keychain prompt; a new Graft build may ask again.

**Start Session** sends one short request to open that account's five-hour
window. It uses a few tokens and runs only when you press it.

## Updating

Graft checks hourly and installs signed updates automatically. Use
**Check for Claude Graft Updates** to check now.

To control Claude's updates, enable **Update Claude Desktop manually** in
Graft's menu or Settings; it takes effect when each Claude next starts.
**Update Claude…** asks before closing running instances and stopping their
workflows. Finish your work first. Your logins, settings and shortcuts stay in
place; managed installations may need administrator approval.

If Claude's updater reopens the wrong profile, Graft can restore the affected
shortcut while Graft is running. Interrupted sessions still need resuming.

## Worth knowing

**Avoid using the same conversation in two accounts at once:** shared transcripts
can lose messages. Graft warns when opening shared chats from its window or menu;
opening directly from the Dock skips that warning.

**Sharing merges both histories.** Chat changes sync in both directions at
launch. Switching back to your own chats does not remove copies already merged
into the source. Back up your profiles first if you need an untouched copy. For a
one-time move, use [Copy Them Here](#bringing-over-chats-from-a-previous-login).

**Pins and sidebar order sync after closing the linked Claudes.** Open the Code
sidebar in each profile once, then quit both and launch either shortcut to sync.
Pins, unpins, pinned order and Code sort choice carry across; other histories,
custom groups and Cowork settings stay local. Graft backs up sidebar settings
before changes and shows a retry message if it cannot sync safely.

**Permissions stay local.** Choose the permission mode in each Claude profile;
organization restrictions still apply. Sharing copies missing desktop MCP server
definitions, keeping existing definitions and later edits local. Account-linked
connectors need a separate sign-in. Files under `~/.claude`, including skills,
plugins and transcripts, are already shared by every instance.

Old chats may show “Session not found on disk” after Claude Code prunes their
transcripts. Raise `cleanupPeriodDays` in `~/.claude/settings.json` to keep them
longer. Deleting a shortcut keeps its profile folder by default.

## How it works

Transcripts live in the shared `~/.claude/projects` folder. Sidebar records live
under each profile's account. Graft copies those records both ways so Claude can
save renames and archive changes, while selected settings use links.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/layout-dark.svg">
  <img alt="Claude 2 and Claude 1 side by side. Claude 2's settings are a symlink to Claude 1's, their record folders hold copies carried both ways, and a hidden .graft-own folder seeds Claude 2's own chats into the shared set once. Both profiles read one set of transcripts in ~/.claude/projects, which has no account in its path." src="docs/assets/layout-light.svg" width="760">
</picture>

Each shortcut prepares its profile and syncs changes before opening Claude.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/launch-dark.svg">
  <img alt="Pressing a shortcut: if a Claude is already on that profile it is brought forward, otherwise the settings links are pointed at the source, chat changes are carried both ways, records are filed for sessions that closed without one, and then Claude launches." src="docs/assets/launch-light.svg" width="760">
</picture>

The first shared launch saves the profile's original chats in `.graft-own` and
merges both histories. That saved set lets it return to its own chats later.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/merge-dark.svg">
  <img alt="A profile's own chats go to .graft-own on the first launch and are copied straight back into a shared folder alongside the source's chats. Going back to its own chats keeps what the stash names and hands the rest over." src="docs/assets/merge-light.svg" width="760">
</picture>

## Building it

```sh
./build.sh                  # App in build.noindex/
./test.sh                   # Regression checks in a throwaway directory
./test-sidebar.sh           # Sidebar checks in disposable profiles
./release.sh                # Test, build universal, sign and package
Tools/render-diagrams.sh    # Render these diagrams into docs/assets
```

Needs Xcode's Swift toolchain. The first build fetches a pinned, checksum-verified
Sparkle release; `VERSION` sets the app version. The optional sidebar suite needs
Node and Claude Desktop; the app itself needs no Node installation.

## Contributing

Bug reports and patches welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and
[CLAUDE.md](CLAUDE.md) before changing profile or credential handling. Diagnostics
are in `~/Library/Application Support/ClaudeGraft/`: `diagnostics.log` and
`state-report.txt`. Pushes and pull requests run tests and a universal build.

## Supporting it

Free, with no account or telemetry. If it helps, the
[tip jar](https://ko-fi.com/aadityavmore) keeps it going.

## Licence

[MIT](LICENSE).
