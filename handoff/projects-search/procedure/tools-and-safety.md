# Tools and safety

[Back to HANDOFF.md](../HANDOFF.md)

## Tools

- **Diagnostics:**
  `grep -E '"(projects\.|search|artifacts\.)' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail`
- **Graft's files, read-only:**
  `cat ~/Library/Application\ Support/ClaudeGraft/artifact-accounts.json`
- **Code sessions on this Mac, read-only.** Counts the transcripts that carry a
  Remote Control id. Prints no content:

      grep -l '"bridge-session"' ~/.claude/projects/*/*.jsonl | wc -l

- **A picture of the window:** `screencapture -x -l <window id>` or a
  full-screen `screencapture -x handoff/projects-search/step-<n>.png`.

Save every output and picture in this folder as `step-…`, with the time each
was taken. Git does not ignore them, so commit none of them. The report step
deletes them once the results are sent.

## Do not touch

- **Chats and projects:** nothing is created, renamed, archived, starred,
  moved or deleted, on claude.ai or in any Claude. Searching and opening are the
  only things done to them. Don't delete a Code session to make one "gone from
  claude.ai". Step 8 checks that filter only if the Mac already has such a
  session.
- **Claudes:** no Claude is quit or opened. Bringing an open one forward is
  fine.
- **Settings:** leave Open at Login, the update settings and the hold setting
  alone.
- **Repository:** nothing is committed, pushed or released, and no branch is
  created or switched.
- **Credentials:** claude.ai sign-ins, passwords and email codes are typed by
  Aaditya alone, in Graft's sign-in window. A keychain prompt is also for
  Aaditya to answer, with **Allow** (the one-time option).
