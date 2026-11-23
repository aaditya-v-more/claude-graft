# Gaps and behavior differences

[Back to HANDOFF.md](../HANDOFF.md)

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
