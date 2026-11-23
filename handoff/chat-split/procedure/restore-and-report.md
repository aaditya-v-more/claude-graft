# Restore and report

[Back to HANDOFF.md](../HANDOFF.md)

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
