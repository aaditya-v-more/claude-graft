# The update restart, and the Claude it brings back

Notes from the investigation of 5–6 September 2026. Everything here was measured
on this machine unless it says otherwise, and the measurements are listed so the
next person can check them rather than take them on trust.

## Where this came from

A user wrote in:

> Unfortunately I have experienced two crashes on the second instance in the
> past 12 hours; causing my sessions to get interrupted. When it crashes, it
> opens the first instance by default (only the second was running). Having
> happened twice in the middle of a lot of work, this solution wont work for me.

It happens here too — four times over two days, all four caught in the logs, one
of them within two minutes of it happening. Their account is accurate in every
particular.

## What we are trying to settle

Whether Graft causes it, what actually happens, and what Graft should do. As
things stand Graft notices none of it: nothing anywhere looks at Claude going
down, or at the instance that turns up afterwards.

Graft does not cause it. The initial investigation treated recovery as the only
option; the manual update mode described below supersedes that conclusion.

## What actually happens

Claude Desktop installs updates by quitting and restarting itself once it has
been idle a while. It calls this a stealth update and says so:

    15:13:55 [stealth-update] Triggering stealth update after idle timeout
    15:13:55 [CCD] Killing 4 PTY process tree(s) on quit
    15:13:55 beforeQuitForUpdate handler fired, going down for update

Nothing crashed. The "crash" in the report is the app quitting itself
mid-session, and the interrupted sessions are literal — that middle line is
Claude killing the command line processes behind every open session on the way
down. No dialog, no warning, no crash report.

The restart is then done by Squirrel's ShipIt, which launches the bundle:

    15:14:02 On main thread and launching: file:///Applications/Claude.app/
    15:14:02 Attempting to launch app on 11.0 or higher
    15:14:07 Successfully launched application at file:///Applications/Claude.app/

There is nowhere in a ShipIt request to put a command line.
`~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/ShipItState.plist`
carries `bundleIdentifier`, `launchAfterInstallation`, `targetBundleURL`,
`updateBundleURL` and `useUpdateBundleName`, and nothing else. So what comes
back has no arguments at all.

**And a profile is nothing but an argument.** `Graft.launchArguments(for:)`
gives a shortcut `--user-data-dir=<profile>` and gives the main profile nothing,
and `Graft.isDefaultInstance` recognises main by that absence. A Claude restarted
by its own updater is therefore, by construction, the first instance. The
grafted profile does not come back at all.

Four events, and the pattern does not vary:

    stealth update      went down    argument-free instance    profile back
    4 Sep 14:13:13      Claude-2     14:28:30                  14:48:31
    4 Sep 17:42:18      Claude-3     17:42:31                  17:47:32
    5 Sep 03:10:31      Claude-2     03:10:43                  14:00:15
    5 Sep 15:13:55      Claude-2     15:14:09                  15:14:22

Every one of those last times is a `launcher.run` in
`ClaudeGraft/diagnostics.log` — a person clicking the shortcut, every single
time. The profile never returns by itself. The third row is the one to look at:
the update landed at 03:10 and the profile stayed down for close to eleven hours,
until somebody sat down and opened it.

## The relaunch that would have worked, and is not the one used

Claude has a relaunch of its own that rebuilds the command line it is running
with:

    function pKt(e = []) {
      let t = process.defaultApp ? 2 : 1
      let n = [...process.argv.slice(1, t),
               ...process.argv.slice(t).filter(e => !fKt(e)),
               ...e]
      ...
      o.app.relaunch({ args: n })
    }

`fKt` drops deep links, paths ending `.dxt` or `.mcpb`, anything starting
`--os-entry=`, absolute paths, `--relaunched-after-gpu-crash-loop` and
`--claude-hybrid-switch-org=`. It does not drop `--user-data-dir`. (One of its
two predicates, `Wc`, was not pinned down — the minifier reuses the name across
chunks — and it looks like a URL test.)

So restarting without losing the profile is possible, and Claude does it for
config changes and preview loads. The update path is not one of them: it ends at
`o.autoUpdater.quitAndInstall()`, which hands the job to ShipIt and the bundle.
That is the whole of the bug.

## Why the stray instance is a different Claude on every machine

A Claude with no `--user-data-dir` takes its profile from its own configuration.
With a third-party provider configured it uses a separate identity:

    var aW = "-3p", oW = `Claude${aW}`      // "Claude-3p"

which gives it `~/Library/Application Support/Claude-3p` and
`~/Library/Logs/Claude-3p`. Here that is the profile pointed at a local model, so
the stray instance comes up talking to `127.0.0.1:11436`:

    15:14:09 [custom-3p] Credentials loaded from managed config { provider: 'gateway' }
    15:14:09 [custom-3p] 3P mode active { provider: 'gateway' }
    15:14:11 [custom-3p] inference apiHost=http://127.0.0.1:11436

With no such configuration it is the plain main profile, which is what the
reporter saw and called the first instance. Same mechanism, different landing
spot — and that difference cost most of a day, because the main profile here has
had no writes since 29 August and looked like proof the argument-free launch
never happened. It had happened, four times, one directory over.

Seen in the same neighbourhood and not followed up: `CLAUDE_USER_DATA_DIR` is
read somewhere near the `-3p` naming. Worth understanding before designing any
fix that turns on the command line.

## The marker, which is the part worth keeping

Before going down, Claude writes a marker into the user data directory of the
profile that is going down — `<profile>/stealth-relaunch`, holding a timestamp,
whether the window was visible, whether another app was full screen, and the
saved navigation history. It is consumed on the next start of that profile, so
you will not find one sitting there unless you catch it mid-flight; its fate
shows up in the next `[startup-perf]` block as
`update_relaunch_marker: 'none' | 'stale'`.

That marker is the only thing on disk telling "the updater took this profile
down and meant to bring it back" apart from "somebody quit it". Any recovery
Graft attempts has to turn on that distinction, because bringing back an app
somebody deliberately closed is worse than the problem being solved.

## What was ruled out, so nobody redoes it

It is not a crash. `~/Library/Logs/DiagnosticReports` holds nothing for Claude
and the Crashpad directories in the profiles are empty for the relevant days.

It is not Graft. Graft launched nothing at any of the four moments; the only
launcher runs are the recoveries afterwards.

It is not something Graft could avoid by launching differently. The stray
instance is started by LaunchServices against the bundle, from a process Graft
has no part in.

It is not the main profile on this machine. That is the `-3p` redirect above, and
checking `~/Library/Application Support/Claude` for signs of it will keep
answering no.

## Where to look next time

Claude's logs are shared by every profile launched from `/Applications/Claude.app`
and live in `~/Library/Logs/Claude/main.log`; a profile in third-party mode logs
to `~/Library/Logs/Claude-3p/main.log` instead. Which profile a given startup
belongs to is settled by its `[oauth] token cache location:` line, which names
the `config.json` it loaded. `Starting app` marks each launch.

`~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/ShipIt_stderr.log` is the
updater's own account of what it installed and what it launched.

`ClaudeGraft/diagnostics.log` says whether Graft launched anything, and
`ClaudeGraft/state-report.txt` says what was true at the last pass.

**Watch the clocks.** Graft's diagnostics are UTC with a `Z`; Claude's logs are
local time. They were 5½ hours apart here, which is wide enough to make one event
look like two, and it did — a grep narrowed to the wrong half hour is what
produced the first, wrong version of this document, in which the profile appeared
to come back on its own.

## Topic guides

- <a id="what-graft-could-do"></a><a id="open-questions"></a><a id="the-fix-6-september"></a><a id="preventing-the-interruption"></a>[Recovery decisions and the fix](update-relaunch-guide/recovery-decisions-and-the-fix.md)
- <a id="choosing-an-update-from-graft"></a>[Choosing an update from Graft](update-relaunch-guide/choosing-an-update-from-graft.md)
