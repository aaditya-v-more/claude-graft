# Run the split test

[Back to HANDOFF.md](../HANDOFF.md)

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
