# Claude Graft handoff

**Live:** Mac 2.0.1 and Windows 2.1.1. The next release is **2.2.0** on both platforms: `VERSION`, [`docs/release-notes/2.2.0.html`](docs/release-notes/2.2.0.html) and [`windows/release-notes/2.2.0.md`](windows/release-notes/2.2.0.md) are on `main`. Only the owner tags it.

**Build:** `main` holds everything for it. #21 (Windows in Russian) went into #20, then #20 (Windows parity, `ec9d5e4`) and #19 (Donate on the Mac) into `main`. The Windows build adds the chat question before opening a Claude another shares, Help and feedback links, the recovered line, separate strip switches, Russian, and Start hidden kept to the launch at sign-in, with the manager brought to the front for a launch from Start. #17 and #12 are not part of it.

**Open QA**, on the Mac mini and wintest2, from one commit of `main` (cases in [`Tests/manual/`](Tests/manual/README.md)):

- **Windows:** `windows-start-hidden.md` steps 3–7 (steps 1, 2 and 8 passed on `d1c6541`, before the bring-to-front fix), `windows-russian.md`, `windows-settings-and-strip.md` (the strip's click never reached Graft under automation), `windows-update.md`.
- **Mac:** `mac-donate.md`, `chat-split.md`, `menu-bar-conversation.md` (against 2.0.1).
- **Both:** `test-profiles.md` first, then the signed-in cases, `delete-open-claude.md`, `new-shortcut-folder.md`, `shared-usage-artifact.md`, the eight `everyday-*.md`, and last `release-install.md` from the exact files people download. Do not infer signed-in coverage from a signed-out pass.

**Decided for 2.2.0:** #22 (follow moved folders) stays out; #17 and #12 are not part of it. What only Windows has (Beta projects, the chime and its dots, the Fable bar in the dropdown, the first-run prompt, the debug window) comes to the Mac in a later update; keep-warm stays Windows-only. The [last parity audit](https://github.com/aaditya-v-more/claude-graft/blob/88ecf4d7c6114bc162d2db622cb1da9002b9f347/handoff/windows-parity/HANDOFF.md) predates #19 and #20.

**Next:** run the QA above. Then the owner tags `v2.2.0`; `release.sh` stages the Mac prerelease and the tag the Windows one; `release-install.md` runs on clean machines against them; publish; set the site's fallback version and disk image in `docs/index.html` to 2.2.0 and recheck live routes. Legal text: no app text changed since 2.0.1 or 2.1.1; the site's privacy wording was reworded, same in substance.

- [Working rules](CLAUDE.md) · [release-install checklist](Tests/manual/release-install.md) · [release and update details](notes/updates-and-releases.md).

## Earlier checkpoint and reference

The links below open the last full checkpoint, not the current task list. For an offline copy: `git show beb7039da5:HANDOFF.md`.

- <a id="next"></a>[Next](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#next)
- <a id="the-windows-port-is-merged"></a>[The Windows port is merged](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#the-windows-port-is-merged)
- <a id="one-version-line-one-release-per-version"></a>[One version line, one release per version](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#one-version-line-one-release-per-version)
- <a id="what-was-checked"></a>[What was checked](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#what-was-checked)
- <a id="settings-in-a-wide-window-fixed"></a>[Settings in a wide window (fixed)](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#settings-in-a-wide-window-fixed)
- <a id="visual-changes-waiting-for-approval"></a>[Visual changes waiting for approval](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#visual-changes-waiting-for-approval)
- <a id="parity-whats-left"></a>[Parity: what's left](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#parity-whats-left)
- <a id="signing-in-a-test-claude-on-windows"></a>[Signing in a test Claude on Windows](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#signing-in-a-test-claude-on-windows)
- <a id="known-small-problems"></a>[Known small problems](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#known-small-problems)
