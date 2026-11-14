# A chat two Claudes both carried on becomes two

Commit `31e549d` ("Split a chat two Claudes both carried on into two") changes
what a sync does with a chat that got a reply in both Claude and Claude 2
between two syncs. Each Claude carried the conversation on into a transcript of
its own, and one record can point at only one of them, so the old merge kept one
half and left the other on disk with nothing in any sidebar naming it. Now the
chat becomes two, in both profiles:

- **Claude's half** keeps the chat, titled `<title> (Claude)`.
- **Claude 2's half** becomes a new chat, titled `<title> (Claude 2)`.
- **Both titles** are marked as set by hand (`titleSource: "user"`), so Claude's
  automatic retitling does not drop the mark.
- **Nothing else splits a chat:** an archive, an unarchive, a rename, a look,
  or a reply on one side only.
- **The split waits while either Claude is open**, because a running Claude
  never re-reads a record and would write its own half back over the split.
  It happens at the sync a few seconds after the second of the two quits.

The suite covers all of this with records written by hand. This run checks it
with real Claudes: real replies, a real quit, and Claude showing and resuming
both halves afterwards.

Run it from a terminal outside any Claude, in the repository folder.

## This run breaks two standing rules

Aaditya agrees to both before it starts.

- **Claude's main profile is quit once**, in step 6, and opened again in step 7.
  The split cannot happen while it is open. Whatever is running in it stops
  until then.
- **One new chat is created and written to:** the test chat in step 2. No other
  chat is opened or written to.

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

## Steps

0. **Where things stand.**
   - Run `git status --short`. It lists nothing. If it lists anything, stop and
     record it.
   - Run `git log --oneline -1`. It is the commit adding this plan, or a later
     one. `git merge-base --is-ancestor 31e549d HEAD` succeeds.
   - Run `./handoff/shared-usage/check.sh`. It shows one Graft, under
     `/Applications`.
   - Record which Claudes are open, and main Claude's PID:

         ps -Ao pid=,command= | grep '/Claude.app/Contents/MacOS/Claude' | grep -v -e grep

     Main Claude is open. Claude 2 (a line with `--user-data-dir=…/Claude-2`)
     is closed. If Claude 2 is open, stop and record it.
   - Run
     `codesign -dvvv "build.noindex/Claude Graft.app" 2>&1 | grep -E 'CDHash|TeamIdentifier'`.
     It shows `TeamIdentifier=H4RJ5YGKZA`. Record the CDHash. If the build is
     missing or unsigned, run
     `SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk ./build.sh`
     first.
   - Take the snapshot, as `step-0-snapshot.json`, with the command under
     **Tools**.

1. **Swap in the development build.**
   - Quit the installed Graft from **Quit** at the bottom of its dropdown.
   - Run `open "build.noindex/Claude Graft.app"`.
   - **Aaditya:** a keychain prompt may come now or within a minute. He answers
     it as **Do not touch** says. Record whether one came.

   Shows, within about a minute:
   - **Graft:** `check.sh` shows one Graft, under `build.noindex`.
   - **Claude:** still running under step 0's PID.

2. **The test chat, in Claude.**
   - Bring main Claude forward. In its Code tab start a new chat, in "This
     computer" or whatever the new-chat screen offers first. Send:
     `Reply with only the word one.`
   - Wait for the reply. Record the title Claude gives the chat.
   - Run `diff` as `step-2-diff.txt`.

   Shows: `added 1` in Claude, with `isArchived: false`.

3. **The chat reaches Claude 2.**
   - Open Claude 2 from Graft's window, not its dropdown, and wait for its
     window. Its launcher syncs first, so the chat is there when it opens.
   - In Claude 2, find the chat by its exact title and open it. It shows
     "one".
   - Run `diff` as `step-3-diff.txt`.

   Shows: the chat in both profiles, the same `sessionId`, `title` and
   `lastAssistantUuid` on both sides.

4. **Both Claudes carry it on.** From here until step 6, keep Graft's dropdown
   shut.
   - In Claude 2, in the test chat, send `Reply with only the word claude-two.`
     Wait for the reply.
   - In main Claude, in the test chat, send `Reply with only the word claude.`
     Wait for the reply.
   - Run `diff` as `step-4-diff.txt`.

   Shows: one record on each side, still one chat. The two sides'
   `lastAssistantUuid` now differ, and each side's `lastActivityAt` is later
   than in step 3.

5. **Claude 2 quits; the split waits.**
   - Quit Claude 2 from its own **Claude › Quit Claude** menu.
   - Wait 15 seconds. Run `diff` as `step-5-diff.txt` and the log command as
     `step-5-log.txt`.

   Shows:
   - **Records:** exactly as step 4 left them: one record a side, each with its
     own `lastAssistantUuid`, and no new record.
   - **Log:** a `mirror.diverged` line for the chat's record with
     `"status":"waiting"`, since main Claude is still open.

6. **Main Claude quits; the chat splits.** Whatever is running in it stops.
   That is expected.
   - Quit main Claude from its own **Claude › Quit Claude** menu.
   - Wait 15 seconds. Run `diff` as `step-6-diff.txt` and the log command as
     `step-6-log.txt`.

   Shows:
   - **Records:** `added 2` (one new record in each profile) and the chat's own
     record changed in both. Nothing else in either profile changed.
   - **Claude's half:** the chat's original `sessionId`, in both profiles,
     titled `<title> (Claude)`, with `titleSource: "user"` and the
     `lastAssistantUuid` main Claude had in step 4.
   - **Claude 2's half:** a new `local_…` `sessionId`, the same in both
     profiles and matching its file name, titled `<title> (Claude 2)`, with
     `titleSource: "user"` and the `lastAssistantUuid` and `cliSessionId` Claude
     2 had in step 4.
   - **Log:** a `mirror.diverged` line with `"split"` naming the new record's
     file, and a `projects.sync` line that is not `projects.sync-skipped` with
     an error. `unchanged` is fine.

7. **Claude shows and resumes both halves.**
   - Run `open -a /Applications/Claude.app` and wait for main Claude's window.
     Record its new PID.
   - Search for `<title> (Claude)`. It is listed and opens on the reply
     "claude". Take `step-7-claude-half.png`.
   - Search for `<title> (Claude 2)`. It is listed and opens on the reply
     "claude-two". Take `step-7-claude-2-half.png`.
   - Neither shows the other's last reply.

8. **Claude 2 shows them too, and one side carrying on does not split
   again.**
   - Open Claude 2 from Graft's window and wait for it.
   - Search for both titles. Both are listed. Take `step-8-list.png`.
   - In `<title> (Claude 2)`, send `Reply with only the word three.` Wait for
     the reply. The chat's title still ends in `(Claude 2)`.
   - Quit Claude 2 from its own menu. Wait 15 seconds. Run `diff` as
     `step-8-diff.txt`.

   Shows: still exactly two test records a side. `<title> (Claude 2)` carries
   its new `lastAssistantUuid` in both profiles, its title unchanged. No third
   record, and no new `mirror.diverged` line.

9. **Archive both halves.**
   - In main Claude, archive `<title> (Claude)` and `<title> (Claude 2)`.
   - Wait 15 seconds, then open and close Graft's dropdown once, which runs a
     sync. Run `diff` as `step-9-diff.txt`.

   Shows: both test records `isArchived: true` in both profiles, titles
   unchanged, and still exactly two test records a side.

## Putting everything back

- Quit the development build from **Quit** in its dropdown.
- Run `open "/Applications/Claude Graft.app"`.
- Leave Claude 2 closed and main Claude open, as step 0 found them, apart from
  main Claude's new PID.
- Leave the two archived test chats where they are. Aaditya deletes them if he
  wants them gone.

Shows, within about a minute:

- **Graft:** `check.sh` shows one Graft, under `/Applications`. Record any
  keychain prompt.
- **Claude:** main Claude open under step 7's PID; Claude 2 closed.

## Report, then clean up

1. Fill in the table below and the run notes.
2. `git status --short` lists nothing. Everything this run wrote is in
   `dist/chat-split/`, which is ignored.
3. Commit and push nothing.
4. Tell Aaditya the run is finished, once main Claude is running again.

## Results

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| 0 | Clean checkout holding `31e549d`; installed Graft alone; team-signed build; main Claude open, Claude 2 closed; snapshot taken | | |
| 1 | Development Graft alone; main Claude unchanged; keychain prompt recorded | | |
| 2 | Test chat created in Claude with one reply; its title | | |
| 3 | The chat in both profiles, the same on both sides | | |
| 4 | Both replied; one record a side with different `lastAssistantUuid` | | |
| 5 | Claude 2 quit; records unchanged; `mirror.diverged` waiting | | |
| 6 | Main Claude quit; two chats in both profiles, titled `(Claude)` and `(Claude 2)`, `titleSource` user; nothing else changed; order sync not failed | | |
| 7 | Main Claude lists both and opens each on its own last reply | | |
| 8 | Claude 2 lists both; a further reply on one side keeps its title and splits nothing | | |
| 9 | Both halves archived in both profiles, nothing split | | |
| Back | Installed Graft alone; main Claude open, Claude 2 closed | | |

## Run notes

