# Shared usage, tested on a second Mac

Temporary. This folder is deleted once its results are read; nothing else in the
repository refers to it.

Shared usage is new on `main` and has so far been checked only by the suites on
the development machine. This plan builds `main` on another Mac and checks it
there: the suites first, then the feature live — Graft writing the proxy lines
into a real Claude, a sign-in from the pane, a conversation answered by the
other account, the dropdown moving it, a conversation that carries reasoning
staying where it was, a socket opened through the relay by Remote Control, and
Claude in Chrome working while shared.

It swaps the installed Graft for a development build and puts it back at the
end. Run it from a terminal outside any Claude, since step 6 opens and closes
one.

## What the Mac needs

- macOS 15 or later (sharing is off below that), Xcode or the Command Line
  Tools, and `python3` and `openssl`, which macOS has.
- Claude installed, its own profile (`~/Library/Application Support/Claude`)
  signed in to one account. Call it **Claude**.
- A second Claude made by Graft, signed in to a **different** account. Call it
  **Claude 2**; its folder is usually `Claude-2`. If the folders differ, pass
  both to `check.sh`: `./handoff/shared-usage/check.sh Claude <folder>`.
- Graft installed in `/Applications` is optional. Without it, skip "Putting the
  installed Graft back" except for its last paragraph.
- Both Claudes closed when you start.

`handoff/shared-usage/check.sh` is read-only and prints what every step is
checked against: which Graft is running, the relay, both Code environments
against what Graft left for a launcher, the lenders, any account signed in from
the pane without its token, and the last pool lines in the diagnostics log. Run
it after every step and paste anything surprising into the notes column.

## Do not touch

- Any chat other than the one scratch conversation step 7 starts in
  `/tmp/pool-check`.
- Open at Login and its **Turn On** button in the development build: it would
  register the development bundle as the login item.
- **Update Claude…**, any Graft update, and the hold setting.
- Nothing is released, and nothing is pushed to `main`.

A keychain prompt for "Claude Safe Storage" is the development build asking for
the first time. It is for the Mac's owner to answer, with **Allow** — the
one-time option, not Always Allow — so this build's signature is not added to
the item for good.

## Steps

0. **The suites.** From a fresh clone or `git pull` of `main`:

   ```sh
   ./test.sh
   ./test-relay.sh
   ./test-layout.sh      # needs Accessibility for the terminal
   ```

   If the build fails with SwiftUI errors and only the Command Line Tools are
   installed, put `SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk`
   (or whichever 26.x SDK is in that folder) in front of each command.

   Shows: each ends with every check passed. Copy the last line of each into the
   table, and any `FAIL` lines into the notes.

1. **Build and swap in the development build.**

   ```sh
   ./build.sh
   ```

   Quit the installed Graft from **Quit** at the bottom of its dropdown (⌘Q only
   closes the window), then `open "build.noindex/Claude Graft.app"`.

   Shows: `check.sh` lists one Graft, under `build.noindex`. Both environments
   `file absent` or `no journal entry`, no relay, no lenders.

2. **Claude 2 borrows Claude.** In the window select **Claude 2**. Under
   **Shared usage**, **Add an Account** lists **Claude**, a divider and **Sign In
   to Another Account…**. Choose **Claude**.

   Shows: a row for Claude with its five-hour and weekly bars (or **No 5-hour
   limit on this account** in place of the first) and **Ready**. `check.sh`: a
   relay listening on 47823, `Claude-2: file present, envVars set … wanted=True,
   HOLDS THE LINES`, and a `pool.environment written` line with
   `"lines":true,"ok":true`. Claude's own line unchanged.

3. **Signing in the account Claude 2 already uses is refused.** In Claude 2's
   **Shared usage**, **Sign In to Another Account…**. The browser opens
   claude.com and the pane shows **Finish signing in in your browser.** Sign in
   with Claude 2's own account and approve.

   Shows: the browser page says that is the account this Claude already uses;
   the pane says the same in orange with **OK**. `check.sh`: no account listed.

4. **Signing in Claude's account is accepted and named.** **Sign In to Another
   Account…** again, as Claude's account. Switching accounts on claude.com may
   need a sign-out there first.

   Shows: the browser says the account is signed in. The pane gains a row named
   by its email, captioned **Same account as Claude**, then **Ready**.
   `check.sh`: one account with that email, `scopes` holding `user:inference`,
   `expires` about a year out, `sealed` present, `refresh token stored: no`. If
   it fails, the orange line in the pane is the service's own wording; copy it.

5. **Claude waits while open.** Open **Claude** (its own Dock icon), then in
   Graft select it and add **Claude 2** from its **Shared usage** menu.

   Shows: **Starts the next time this Claude opens.** `check.sh`: `Claude: …
   wanted=True, without the lines`. Remove it with the minus button: the line
   goes and `Claude: … no journal entry`. Quit Claude again.

6. **Open Claude 2.** From its Dock icon or Graft's dropdown.

   Shows: `check.sh` still `HOLDS THE LINES` and the relay unchanged.

7. **One conversation through the relay.** In Claude 2's Code tab start a
   session in a scratch folder (`mkdir -p /tmp/pool-check`) and send a one-line
   prompt. Open Graft's dropdown.

   Shows: a **Claude Code** heading with that conversation's title and, on the
   right, the account that answered: Claude 2 or Claude, whichever has the
   sooner weekly reset. Claude's row in the window reads **Ready · answered N
   requests** once it has answered one.

8. **The dropdown moves it, and reasoning keeps it there.** Ask something that
   makes the model think before answering, such as `Work out which of 391, 397
   and 403 are prime, then answer with just the primes.` Then press the account
   name in the dropdown, choose the **other** account, and send `And the next
   prime after the largest?`. Then choose **Automatic**.

   Shows: the right side changes to the account you chose and the conversation
   keeps working. Wait six minutes — past the prompt cache — and send `Thanks,
   one word reply.` The answer still comes from the account you chose, not the
   one the ranking picked in step 7: a conversation carrying reasoning stays
   with the account that wrote it.

   This holds only for models whose reasoning is bound to its account, which is
   any model not named in `PoolRequest.reasoningTravels`. Note the model the
   session used. If the session's transcript under `~/.claude/projects/` has no
   `"type":"thinking"` in it, the check does not apply; say so.

9. **Claude in Chrome.** Only if Chrome is running with the Claude extension
   connected. In the same conversation: `Use Claude in Chrome to open
   example.com in a new tab and take a screenshot of it.` Close that tab
   afterwards, and no other.

   Shows: the tab opens and the screenshot comes back into the conversation,
   while the dropdown still names the account answering it. The browser talks
   to Claude over a local socket, never through the relay, so a failure that
   also happens with sharing off belongs in the notes, not as a fail.

10. **Remote Control.** Only if this conversation shows as available on another
    device. Open it from claude.ai/code or the mobile app, signed in as Claude
    2's account, and send `Reply with the word relay.` from there.

    Shows: the message appears in the Code tab on this Mac, is answered, and the
    answer reaches the other device. `check.sh`'s pool lines show no `relay
    passed over` and no 502 for it. This is the socket the relay now passes
    through untouched.

11. **Take it all out.** Quit Claude 2. In its pane remove both rows with the
    minus buttons.

    Shows: `Claude-2: file present, envVars none … no journal entry`, no
    account, and the relay gone within about 30 seconds.

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
