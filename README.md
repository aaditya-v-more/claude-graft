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

<img src="docs/images/00-shared-usage.png" width="820" alt="A Claude lending Claude Code usage from Claude 2 and another account">

</div>

Claude Graft runs every Claude account you have side by side, each signed in
on its own profile, and lets them share their usage. It lives in the menu bar.

## Install

```sh
brew install --cask aaditya-v-more/claude-graft/claude-graft
```

Or take the **.dmg** from
[Releases](https://github.com/aaditya-v-more/claude-graft/releases). Graft needs
macOS 13 and Claude Desktop; sharing usage needs macOS 15. It is ad-hoc signed,
not notarised, so the first open needs **System Settings › Privacy & Security**
(the cask does this for you).

## Shared usage

When one account runs out, Claude Code carries on with another's allowance,
without leaving the Claude you are in. Under **Shared usage**, add another of
your Claudes or sign in to an account. Only the model's answers come from the
other account; your chats, files and connectors stay where they are.

## A Claude for every account

**New Shortcut** makes a small app that opens Claude on a profile of its own.
Sign in once and it stays signed in. A new Claude has its own chats; reading
another Claude's chats is a choice you make.

<img src="docs/images/01-claudes.png" width="820" alt="Graft's window with Claude 2 selected">

<img src="docs/images/02-new-claude.png" width="820" alt="A new Claude, reading its own chats">

## The menu bar

The menu bar says which Claude is in front, with its icon and its account's
usage, and its name too if you like. While a Claude shares usage, the bar shows
every account it draws on and lights the one answering right now; a
conversation that has just begun runs across it for two minutes, so you see
which account took it. The dropdown lists every account's five-hour and weekly
usage, kept current while the Claudes are closed, and lets you pick which
account answers each conversation.

<img src="docs/images/03-dropdown.png" width="346" alt="Graft's menu bar dropdown with each account's usage">

## Artifacts, projects and chats

Sign in with an emailed code and see every account's artifacts, projects and
chats in one place. Share an artifact with a group, each person as a viewer,
commenter or editor; rename, duplicate, delete, or export it as Markdown, PDF,
Word or page source.

<img src="docs/images/05-artifacts.png" width="820" alt="Artifacts from two accounts">

<img src="docs/images/06-group-roles.png" width="820" alt="A group with a role beside each person">

<img src="docs/images/07-share-menu.png" width="820" alt="Sharing an artifact with a group">

<img src="docs/images/08-artifact-actions.png" width="263" alt="An artifact's Rename, Duplicate, Export and Delete">

<img src="docs/images/09-projects.png" width="820" alt="Projects from two accounts">

<img src="docs/images/10-search.png" width="820" alt="Searching chats across accounts">

## Safe by design

Graft borrows each Claude's login rather than copying it, and its tokens go to
Anthropic alone. It never touches an app it did not make. Updating keeps every
Claude, setting and chat exactly as it was.

<img src="docs/images/11-settings.png" width="420" alt="Graft's Settings">

## Help

The Help menu has the guide, what's new, a way to report a problem and to
reach me.

<img src="docs/images/12-help-menu.png" width="450" alt="Graft's Help menu">

Made by Aaditya More: [aadityavmore@gmail.com](mailto:aadityavmore@gmail.com),
[LinkedIn](https://www.linkedin.com/in/aadityavmore/),
[issues](https://github.com/aaditya-v-more/claude-graft/issues/new). Graft is
free; the [tip jar](https://ko-fi.com/aadityavmore) keeps it going. Build it
with `./build.sh`. [MIT](LICENSE).
