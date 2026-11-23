# Restore and report

[Back to HANDOFF.md](../HANDOFF.md)

## Putting the installed Graft back

Only after step 11 shows no relay and no journal entry for either Claude.

1. Quit the development build from **Quit** in its dropdown.
2. `open "/Applications/Claude Graft.app"`. It puts its own launcher back into
   Claude 2's bundle as it starts, since launchers are replaced by version.
3. `check.sh`: one Graft, under `/Applications`. No relay.

If a relay is still listed with every environment `without the lines` or `no
journal entry`, nothing needs it: `pkill -f graft-relay`. With either Claude
still `HOLDS THE LINES`, do not stop it; go back to step 11 with the development
build.

`Claude-2/ccd-environment-config.json` stays behind holding `{}`, which Claude
reads as an empty environment. `ClaudeGraft/pool/` and `usage-sharing.json` are
ignored by a Graft without sharing.

## Sending the results back

Fill in the table, add anything worth knowing under it, then commit this file on
a branch of its own and push that branch, not `main`:

```sh
git switch -c results/shared-usage
git add handoff/shared-usage/HANDOFF.md
git commit -m "Shared usage results from the second Mac"
git push -u origin results/shared-usage
```

## Results

Mac, macOS version, Claude version, and the model step 8 used:

| Step | What it should show | Seen | Notes |
| ---- | ------------------- | ---- | ----- |
| 0 suite | every check passed | | |
| 0 relay | every check passed | | |
| 0 layout | every check passed | | |
| 1 | One Graft, from `build.noindex`; nothing shared yet | | |
| 2 | Claude 2 written straight away, relay up, Claude untouched | | |
| 3 | Claude 2's own account refused, in the browser and the pane | | |
| 4 | Claude's account added, named as Claude, no refresh token kept | | |
| 5 | Claude waits while open; removing leaves nothing | | |
| 6 | Claude 2 opens holding the lines | | |
| 7 | Conversation listed and answered by the ranked account | | |
| 8 | Moved by the dropdown, and still there after six minutes on Automatic | | |
| 9 | Chrome tab opened and screenshot returned (optional) | | |
| 10 | Remote Control message answered on both ends, no relay error (optional) | | |
| 11 | Lines out, account gone, relay stopped | | |
| Back | Installed Graft running alone, no relay | | |
