# A release installs and updates from exactly what people download

Platforms: macOS, Windows
Needs: a staged release (a GitHub prerelease `vX.Y.Z` with the cask on the
tap's `release-X.Y.Z` branch; release.sh prints how), its Distribution Checks
run passed, and per system two machines:
- **clean**: never had Claude Graft or the `aaditya-v-more/claude-graft` tap,
  Claude Desktop installed (a freshly restored VM is best);
- **previous**: the release before this one installed from that release's own
  download (Mac: its disk image; Windows: its setup), and nothing newer.
Setup: Write down `X.Y.Z` and the prerelease page,
`https://github.com/aaditya-v-more/claude-graft/releases/tag/vX.Y.Z`. Use only
what that page and the README offer; copy nothing from a build folder.

## Before publishing

Mac, clean machine:
1. Download `ClaudeGraft-X.Y.Z.dmg` from the prerelease page in Safari. Run
   `shasum -a 256` on it.
   Expect: the same sha256 as `Casks/claude-graft.rb` on the tap's
   `release-X.Y.Z` branch.
2. Open the disk image, drag Claude Graft to Applications, open it, and allow
   it under System Settings › Privacy & Security as the README says.
   Expect: Graft opens, its menu-bar item appears, About says X.Y.Z, and the
   main Claude shows in its window.
3. Quit Graft and move it to the Bin. Then point the tap at the staged cask
   and run the README's command, copied from the README:
   `brew tap aaditya-v-more/claude-graft && git -C "$(brew --repo aaditya-v-more/claude-graft)" checkout release-X.Y.Z`
   then `brew install --cask aaditya-v-more/claude-graft/claude-graft`.
   Expect: brew downloads the disk image without a checksum error and installs
   Claude Graft X.Y.Z, which opens with no Privacy & Security step.
4. `brew uninstall --cask claude-graft`, then
   `git -C "$(brew --repo aaditya-v-more/claude-graft)" checkout main`.
   Expect: Graft quits and is gone from Applications.

Mac, previous machine:
5. Quit Graft. Point it at the staged feed:
   `defaults write graft.claude-graft SUFeedURL "https://github.com/aaditya-v-more/claude-graft/releases/download/vX.Y.Z/appcast-staged.xml"`.
   Open Graft and choose Check for Updates….
   Expect: X.Y.Z is offered; installing it relaunches Graft on X.Y.Z with the
   same shortcuts and settings as before.
6. `defaults delete graft.claude-graft SUFeedURL`.

Windows, clean machine:
7. Download `ClaudeGraftApp-win-Setup.exe` from the prerelease page in a
   browser and run it.
   Expect: Graft installs and starts, its notification-area icon appears, and
   Settings › About says X.Y.Z.

Windows, previous machine:
8. Quit Graft with `ClaudeGraft.exe --quit`. From PowerShell start it on the
   staged release:
   `$env:GRAFT_UPDATE_FEED='https://github.com/aaditya-v-more/claude-graft/releases/download/vX.Y.Z/'; & "$env:LOCALAPPDATA\ClaudeGraftApp\current\ClaudeGraft.exe"`.
   In Settings press Check for Updates and let it install.
   Expect: X.Y.Z downloads and installs, Graft restarts on X.Y.Z with the same
   profiles, and nothing asks to reinstall.
9. Quit Graft with `--quit` and start it plainly, without the variable.

## After publishing

10. On a Mac still on the previous release (or reinstalled to it), with no
    SUFeedURL override, choose Check for Updates….
    Expect: X.Y.Z is offered from the live feed.
11. `brew update && brew info --cask aaditya-v-more/claude-graft/claude-graft`.
    Expect: X.Y.Z.
12. On a Windows machine still on the previous release, with no
    GRAFT_UPDATE_FEED, Settings › Check for Updates.
    Expect: X.Y.Z is offered.
13. Open graft.aadityamore.com and the README on GitHub and press each
    download button and link.
    Expect: each opens or downloads a file of this release or the release page.

Pass when: every route a person takes (disk image, brew command, Sparkle
update, Windows setup, Windows update) gives a working X.Y.Z from the files
people download, before and after publishing.
