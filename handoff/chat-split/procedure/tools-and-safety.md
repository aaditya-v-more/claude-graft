# Tools and safety

[Back to HANDOFF.md](../HANDOFF.md)

## Tools

- **Records:** `handoff/chat-split/records.py`, read-only. It reads only the two
  mirrored chat folders named in `ClaudeGraft/mirrored-chats.json`, Claude's
  and Claude 2's, and writes only the snapshot file it is given.

      python3 -I handoff/chat-split/records.py snapshot dist/chat-split/step-0-snapshot.json
      python3 -I handoff/chat-split/records.py diff dist/chat-split/step-0-snapshot.json

  `diff` prints how many records were added, changed or gone since the
  snapshot, then each added or changed record's `sessionId`, `title`,
  `titleSource`, `isArchived`, `lastAssistantUuid`, `cliSessionId` and
  `lastActivityAt`, and which profile it is in.
- **Graft's log:** `~/Library/Application Support/ClaudeGraft/diagnostics.log`.
  Every line is JSON with an `event`. This run looks for `mirror.diverged` and
  `projects.sync`:

      grep -E '"mirror.diverged"|"projects.sync' ~/Library/Application\ Support/ClaudeGraft/diagnostics.log | tail -5

- **Graft and the relay:** `./handoff/shared-usage/check.sh`, as in earlier
  runs.

Save every output in `dist/chat-split/` as `step-…`, with the time each was
taken. Make the folder first with `mkdir -p dist/chat-split`; `dist/` is
ignored, so the checkout stays clean.

## Do not touch

- **Chats:** only the test chat. Find it in each Claude through search by its
  exact title, never by clicking at coordinates in the list.
- **Graft's dropdown:** do not open it between step 3 and step 6. Opening it
  runs a sync, and a sync that lands between the two replies carries one
  across before the other exists. That is the case this change does not split,
  so it would test the wrong thing.
- **Settings:** leave Open at Login, **Update Claude…**, any Graft update, the
  hold setting and Shared usage alone.
- **Repository:** nothing is committed, pushed or released. Create or switch no
  branch.
- **Credentials:** a keychain prompt for "Claude Safe Storage" is Aaditya's to
  answer. For the development build, signed with the Apple Development
  certificate (`codesign -dvv` shows a TeamIdentifier), he chooses **Always
  Allow**. For the installed 2.0.1, ad-hoc signed, he chooses **Allow**. Record
  the step at which each prompt came, and which Graft asked. Aaditya types any
  claude.ai sign-in himself.
