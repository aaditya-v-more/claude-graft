# Claude Graft: Mac vs Windows feature parity

Status key:
- **Yes**: the feature is there.
- **No**: a real gap.
- **Partial**: some of it is there.
- **By design**: deliberately left out.
- **Platform**: only meaningful on one OS.
- **Unverified**: I could not confirm it from the source.

## 1. Feature table

| Feature | Mac | Windows | Notes |
|---|---|---|---|
| Create / edit / rename / delete a profile | Yes: `App/ShortcutDetail.swift`, `ContentView.swift` ("New Shortcut", "Delete Shortcut Only" / "…and Profile") | Yes: `ProfileEditor.xaml(.cs)`, `MainPage.xaml.cs` `ConfirmDelete` (one checkbox to delete the data too) | Mac asks with two buttons; Windows asks with a checkbox. Both have "Update Shortcut", a folder field, Show in Finder / Explorer, and "Reads chats from". |
| Shortcuts / launchers | Yes: `.app` bundles via `App/Installer.swift`, `Sources/Launcher/main.swift` | Yes: `.lnk` + `GraftLaunch/`, `ClaudeGraft/Installer.cs` | Same model on both. |
| Icons and colours (12 presets, each colour used once) | Yes: `Model.swift` `IconPreset`, icon popover in `ShortcutDetail.swift:289` | Yes: `ShortcutIcons.cs`, `ProfileEditor.xaml` `IconGrid` | — |
| Each Claude gets its own taskbar/Dock button | By design: a running Claude keeps Claude's Dock icon (CLAUDE.md, Interface) | Yes: `TaskbarButtons.cs`, `WindowIdentity.cs` | The OS allows it on Windows only. |
| Open a profile (already open → bring it forward) | Yes: `ProfileOpener.swift` | Yes: `ProfileRows.OpenAsync` | — |
| **Chat-conflict prompt** ("Another Claude is open on these chats", Open Anyway) | Yes: `App/ChatConflict.swift`, asked from all three open routes | **No**: `ProfileRows.cs:54` launches directly; `Core/Launcher.cs:70,111` checks only the profile's own process, not other Claudes on the same chats | Windows grafts with junctions (`windows/README.md`), so two Claudes still write the same files. Gap, **S**. |
| Offer to bring an account's chats over after an account switch | Yes: `ShortcutDetail.swift:583` `adopt`, plus "Do Not Show Again" | Yes: `MainPage.xaml.cs` `AskAboutChats` / `Adopt`, plus a "Don't ask again" checkbox | Parity. |
| Chat / Code history sharing (mirror, field-level merge, split) | Yes: `Shared/GraftCore.swift` | Yes: `Core/Graft.Mirror*.cs`, `Graft.MirrorFields.cs` | Parity, per the 2.0.0 Windows notes. |
| Record sweep (file missing session records) | Yes: `GraftCore.swift`, `App/SessionRecords.swift` | Yes: `Core/Graft.Orchestrator.cs` `FileMissingSessionRecords`, `TranscriptParser.cs` | Windows hasn't checked whether it is even needed there (`windows/README.md`, "Still to do"). |
| "Recovered N sessions" notice | Yes: `MenuBarContent.swift:78-95` | **No**: no UI string found | Gap, **S**. |
| Pins + Code sort order sync | Yes: `Shared/SidebarSync.swift`, `SidebarStorage.swift` | Yes: `Core/SidebarSync.cs`, `SidebarStorage.cs` | — |
| Project order sync | Yes: `SidebarSync.swift` `synchronizeProjectOrder` | Yes: `Core/SidebarProjects.cs` (with rollback taken from PR #9) | — |
| MCP server sharing (copy missing definitions) | Yes: `copyDesktopServers` in `GraftCore.swift` | Yes: `Core/Graft.DesktopServers.cs` | — |
| Usage: 5h + weekly, freshness, back-off, "No 5-hour limit" | Yes: `UsageMonitor.swift`, `UsageAPI.swift`, `MenuBarContent.swift` | Yes: `Core/UsageMonitor.cs`, `UsageApi.cs`, `FlyoutView.xaml` | — |
| Fable (model-specific weekly) bar | **Partial**: shown in the window only (`ShortcutDetail.swift:675`); the dropdown `MenuBarContent.swift` doesn't draw it | Yes: window (`MainPage.xaml:203`) and flyout (`FlyoutView.xaml:167`) | Mac gap, **S**. |
| Start Session (one press) | Yes: `SessionStarter.swift`, dropdown and window | Yes: `Core/SessionStarter.cs`, flyout and window | Mac's own notes say a Start Session has never been seen to succeed (`notes/diagnostics-and-verification.md`). |
| Keep the 5h window open (auto-start) | By design: no; CLAUDE.md "Only a person starts a session" | Yes: `Core/AutoStarter.cs`, `MainPage.xaml:230`, `App.xaml.cs:255` | A deliberate divergence. Porting it to the Mac would break a Mac rule, so this is a policy decision, not a build task. |
| Shared Usage relay | Yes: `Sources/Relay/*`, `UsagePoolController.swift` (needs macOS 15) | Yes: `GraftRelay/*`, `Core/PoolPolicy.cs`, `PoolState.cs` | Parity. |
| Lending choices (add a Claude / sign in to an account) | Yes: `SharedUsageSection.swift`, `AccountSignIn.swift` | Yes: `MainPage.xaml` "Shared usage", `MainPage.xaml.cs` `SignInToLend`, `Core/AccountSignIn.cs` | — |
| Per-conversation account choice | Yes: `SharedUsageSection.swift:336` `SharedChats`, used only in the dropdown (`MenuBarContent.swift:50`) | Yes: flyout (`FlyoutView.xaml.cs:168-206`) | Parity: both offer it only in the menu-bar/tray popup. |
| "Lending paused" notice | Yes: `LendingPausedNotice` (keychain-shut case) | Unverified: the text is only in `DebugWindow.cs:198` staging | The Mac trigger is about the keychain. Windows' equivalent is only "relay can't be configured". |
| Artifacts pane (list, share to groups with roles, rename, duplicate, delete, export MD/PDF/Word/source) | Yes: `ArtifactsView.swift`, `ArtifactCenter.swift`, `ArtifactExport.swift` | Yes: `ArtifactsPage.xaml(.cs)`, `Artifacts/*.cs` (also exports a zip of the source) | Parity. |
| Projects pane | Yes, partial: `Projects.swift:106` asks `projects_v2` only | Yes: `Core/Projects.cs` asks `/projects` **and** Beta projects `/v1/code/channels`, and can open "signed in as its account" (`ClaudePageWindow`) | Mac gap, **M**. `notes/projects-and-search.md:43-62` says `projects_v2` came back empty for one account. |
| Search Chats pane (filters: kind, status, where, changed, account, project, starred) | Yes: `ProjectsView.swift:135` `ChatSearchView` | Yes: `SearchPage.xaml(.cs)` | Parity. |
| Menu-bar dropdown vs tray flyout: contents | Dropdown (`MenuBarContent.swift`): update notices, per-account usage + Start/Open/Refresh, shared chats, "Turn on live usage", recovered notice, **switches** (Open at Login, Show in Menu Bar, front icon/name, manual Claude updates, Hold updates), Refresh Usage, Check for Graft Updates, Open, Support…, Quit | Flyout (`FlyoutView.xaml`): Claude Desktop update line (`ClaudeUpdateLine`, l.44), setup prompt (l.77), Graft update line + Check/Update, per-account usage + Start/Open/Refresh, Claude Code chats, Refresh Usage, Open Claude Graft, Support Claude Graft…, Quit | Windows has **no switches** in the flyout; they are on the Settings page only. That may be the intended platform style. |
| Front-Claude indicator / conversation ticker | Yes: the status item (`FrontClaude.swift`, `MenuBarController.swift`), icon/name toggles | Yes: optional taskbar strip (`TaskbarStrip.cs`, `ChatOnScreen.cs`, `FrontClaude.cs`) plus a tray tooltip | Windows adds green "done" and yellow "needs you" dots, which the Mac lacks. Known limit: the hover border is tuned for a dark taskbar. |
| Chimes / "Claude is ready" | **No** | Yes: `Chime.cs`, Claude Code hook via GraftLaunch (Settings "Chime when a Claude is ready") | Mac gap, **M**: needs a hook entry in `~/.claude/settings.json` and sounds. The done/needs-you dots come from the same hook. |
| Claude Desktop update from Graft + "update manually" | Yes: `ClaudeDesktopUpdater.swift`, `ClaudeUpdateFlow.swift`, `ManualUpdateSettings.swift` | Yes: `ClaudeUpdateCenter.cs`, `ClaudeUpdateLine.xaml`, `Core/ManualUpdates.cs`, `ClaudeDesktopUpdates.cs` | Labels differ (section 3). |
| Recovery after Claude restarts itself to update | Yes: `UpdateRecovery.swift`, `UpdateRecoveryNotice.swift` | Yes: `UpdateRecovery.cs` | — |
| Graft self-update | Sparkle; installs silently, a press included (`Updater.swift`) | Velopack, hourly; installs quietly but waits while a Graft window is open (`GraftUpdates.cs`, `UpdateToast.cs`) | Both have a Hold switch. |
| Hold Graft updates | Yes: `HoldUpdatesControl`, plus a status-item badge while held | Yes: Settings "Hold Graft's updates until I install them" | — |
| Launch at login | Yes: "Open at Login" (`SMAppService`, `Settings.swift`) | Yes: "Start with Windows" (`AutoStart.cs`) | — |
| Start hidden | Implicit: reopens hidden if the window was closed at the last quit (`Shared.startHidden`) | Yes: explicit "Start hidden" switch | — |
| Themes / appearance | **No**: follows macOS | Yes: Theme palette, Mode (system/light/dark), Backdrop (Mica/Mica Alt/Acrylic/Solid) (`SettingsPage.xaml:18-45`, `Theming.cs`, `Appearance.cs`) | Mac gap. Probably **By design**; mode-follow only. |
| Donate | "Support Claude Graft…" (dropdown, Help menu) and footer "Support" → `ko-fi.com/aadityavmore` (`App/Links.swift`) | Footer and flyout "Donate" with a red heart → a dialog with the same Ko-fi and Tyler's (`Core/Links.cs`) | The Mac still says Support; renaming it waits for screenshots in the macOS VM. |
| Source link | Footer "Source" + Help "View Source on GitHub" → `github.com/aaditya-v-more/claude-graft` | Footer "Source" → `"https://github.com/" + Updater.Repo`, this repository | — |
| Help / Report a problem / Email / What's New / LinkedIn | Yes: Help menu, `Links.swift:87-99` (site → `aaditya-v-more.github.io/claude-graft/`, releases, `issues/new`, `mailto` pre-filled with version + macOS, LinkedIn) | **No** | Windows gap, **S**: add About-card links or a flyout item, with the mailto carrying the Windows version. |
| Diagnostics log | Yes: `Shared/Diagnostics.swift` (`diagnostics.log`, rotated) | Yes: `Core/Diagnostics.cs` | — |
| State report (`state-report.txt` after each pass) | Yes: `Diagnostics.swift:186-301`, `GraftCore.swift:4019` | **No**: listed under "Still to do" in `windows/README.md` | Windows gap, **M**. |
| First-run guidance (Claude not installed / not signed in) | Partial: an empty-list hint (`ContentView.swift:300`), "No Claude profiles yet", "Not signed in" | Yes: `SetupPrompt.cs` (Install Claude Desktop → claude.ai/download, Check Again); Open becomes "Sign In"; email sign-in advice | Mac gap, **S–M**. |
| `claude://` link routing / sign-in chooser | Platform: not needed | Yes: `ClaudeLinkWindow.cs`, `Core/Launcher.cs`, "Choose Graft for Claude links" (`MainPage.xaml:218`) | Windows only. |
| Packaged (MSIX / Store) Claude support | Platform | Yes: `ClaudeInstallation` (`notes/opening-profiles.md:189`) | — |
| Keychain "Turn on live usage" | Platform: `MenuBarContent.swift:64` | Platform: DPAPI raises no dialog | — |
| Show in Menu Bar / ⌘Q / Dock reachability policies | Platform: `Settings.swift` `QuitPolicy`, `WindowPolicy`, `MenuBarPlacement` | Platform: the tray is always shown; `SessionEnd.cs` handles logoff and installer close | — |
| Localisation | Yes: en + **ru** (`Resources/{en,ru}.lproj/Localizable.strings`, 509 keys each; `L10n.text`) | **No**: hard-coded English, no `.resw` | Windows gap, **L**: every string in XAML and C# would need resource extraction. |
| Accessibility identifiers | Yes: `.identified(...)` (`App/Accessibility.swift`), plus `test-layout.sh` | Yes: `AutomationProperties.AutomationId` / `Name` throughout, `Spoken.cs` | Parity. The Mac also has an external layout suite. |
| Debug window (stage states) | **No** | Yes: `DebugWindow.cs`, `Core/Staging.cs`; seven taps on the About icon (`SettingsPage.xaml.cs:95`) | Mac gap, **M**. Useful for screenshot reviews. |
| Settings: where they live | A Settings window (⌘,), `ClaudeGraftApp.swift:66` `SettingsView`, mirrored in the dropdown | A Settings page in the sidebar (`SettingsPage.xaml`) | The lists are in section 1a. |
| Uninstall puts Claudes back | Yes: cask runs `graft-launch --stop-sharing`; the relay cleans up | Yes per the 2.0.0 release note ("Uninstalling Graft puts every Claude back"); probably `Core/Graft.Teardown.cs` (not read) | — |

### 1a. Settings, each side

**Mac** (`ClaudeGraftApp.swift:66-105`; the same switches appear in the dropdown, `MenuBarContent.swift:113-124`):
1. Open at Login
2. Show in Menu Bar
3. Show the icon of the Claude in front
4. Show the name of the Claude in front
5. Update Claude Desktop manually
6. Hold Claude Graft updates

The footer note reads "menu bar item keeps reporting usage…". There is no Check button in Settings; that lives in the dropdown.

**Windows** (`SettingsPage.xaml`), one scrolling page under five headings. The 2.0.0 notes say "three pages".
- **Appearance:** Theme (palette grid), Mode, Backdrop.
- **Starting up:** Start with Windows, Start hidden.
- **Taskbar and sound:** Show usage on the taskbar, Chime when a Claude is ready.
- **Updates:** Update Claude only when I ask, Hold Graft's updates until I install them.
- **About:** a "Claude Graft" card with Check for Updates (seven taps → debug window).

Per profile, Windows also has "Keep the 5-hour window open" (`MainPage.xaml:230`).

Missing on Windows: separate front-icon / front-name switches (the strip is all or nothing).
Missing on Mac: theme, backdrop, start hidden, taskbar/strip, chime.

## 2. Gaps, ranked for "build next"

| # | Gap | Side lacking | Reference implementation | Size |
|---|---|---|---|---|
| 1 | Chat-conflict prompt before opening a profile another Claude shares | Win | `Sources/App/ChatConflict.swift`, call sites in `ContentView`, `MenuBarContent`, `ShortcutDetail` | S |
| 2 | Help / Report a problem / Email / What's New links | Win | `Sources/App/Links.swift` (`GraftHelpCommands`, `Links.mail`) | S |
| 3 | State report after each pass | Win | `Sources/Shared/Diagnostics.swift:186-301` | M |
| 4 | Beta projects (Code channels) + open in the account's own window | Mac | `windows/ClaudeGraft.Core/Projects.cs:40-83`, `windows/ClaudeGraft/Artifacts/ProjectCenter.cs` | M |
| 5 | Chime + done / needs-you signal | Mac | `windows/ClaudeGraft/Chime.cs`, hook handling in `GraftLaunch` | M |
| 6 | Fable bar in the dropdown | Mac | `windows/ClaudeGraft/FlyoutView.xaml:167`; Mac data already in `UsageAPI.swift` | S |
| 7 | First-run setup guidance | Mac | `windows/ClaudeGraft/SetupPrompt.cs` | S–M |
| 8 | "Recovered sessions" notice | Win | `Sources/App/MenuBarContent.swift:78-95` | S |
| 9 | Debug/staging window | Mac | `windows/ClaudeGraft/DebugWindow.cs`, `windows/ClaudeGraft.Core/Staging.cs` | M |
| 10 | Localisation (Russian, at least) | Win | `Resources/ru.lproj/Localizable.strings` | L |
| 11 | Theme / backdrop picker | Mac | `windows/ClaudeGraft/Theming.cs`, `Appearance.cs` | M (likely By design) |
| 12 | Front icon/name switches, separate | Win | `Sources/App/MenuBarContent.swift:533-549` | S |
| 13 | Keep-warm | Mac | `windows/ClaudeGraft.Core/AutoStarter.cs` | Policy, not size: Mac rule forbids it |

## 3. Same feature, noticeably different behaviour

- **Donate wording.** Windows says "Donate" with a red heart; the Mac still says "Support Claude Graft…" and "Support". The Mac goes straight to the author's Ko-fi; Windows offers it and Tyler's.
- **Source and update repo.**
  - Windows' Source link and update feed both derive from `Updater.Repo`, this repository.
- **Help site URL.** Mac Help opens `aaditya-v-more.github.io/claude-graft/`, which CLAUDE.md says only redirects; the canonical site is `graft.aadityamore.com`. Windows has no help link at all.
- **Graft self-update.**
  - Mac installs silently and immediately, even on a press of Check.
  - Windows checks hourly and installs quietly but waits while a Graft window is open. In 1.1.x it asked Install/Dismiss.
  - Check for Updates is in the Mac dropdown, and in Windows Settings › About plus the flyout.
- **Hold label.** Mac: "Hold Claude Graft updates". Windows: "Hold Graft's updates until I install them".
- **Manual Claude updates label.** Mac: "Update Claude Desktop manually". Windows: "Update Claude only when I ask".
- **Launch at login.** "Open at Login" vs "Start with Windows". Windows adds a separate "Start hidden"; the Mac infers it from how it last quit.
- **Front-Claude display.** Mac is always in the menu bar, with icon and name switches. Windows is an opt-in taskbar strip, all or nothing, and has dots the Mac lacks.
- **Switches in the drop-down.** The Mac dropdown carries all six switches. The Windows flyout has none.
- **Delete confirmation.** Mac uses two buttons (Shortcut Only / Shortcut and Profile). Windows uses one button plus a "delete data" checkbox.
- **Keep-warm.** It exists only on Windows, while the Mac forbids any unprompted session. A user running both will see one app start sessions on its own.
- **Projects.** Windows lists Beta projects that the Mac doesn't show, so the same account can show different project lists.
- **Fable.** It appears in the Windows flyout but only in the Mac window, not the dropdown.
- **Opening a shared profile.** The Mac warns about two Claudes on the same chat files. Windows opens without a word.
- **Language.** A Russian-locale Mac gets Russian. Windows is always English.
