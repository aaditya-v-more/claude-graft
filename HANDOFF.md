# Claude Graft handoff

**Live:** Mac 2.0.1 and Windows 2.1.1. The next release uses one version above 2.1.1 for both platforms. #14 and #15 are merged; their new-shortcut and shared-usage cases still need release-candidate coverage. #17 is the contributor’s draft, not a release task.

**Next:** finish signed-in tests on both platforms and the Windows parity and everyday-use checks. Include the approved Windows startup behavior: automatic sign-in launch may stay hidden, but opening Graft manually must show the window. Keep the Mac Donate/Help and profile-deletion regression cases. Do not infer signed-in coverage from a signed-out pass.

Merge the passing Windows Russian work (#21) into Windows parity (#20), then the approved Mac (#19) and Windows work into `main`; check current PR state before retrying. Stage the exact release files, test clean installs and updates, then publish and recheck live routes. Do not bump or publish for this documentation change.

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
- <a id="rig"></a>[Rig](https://github.com/aaditya-v-more/claude-graft/blob/beb7039da541f67d1c8260b740eee069fb27b877/HANDOFF.md#rig)
