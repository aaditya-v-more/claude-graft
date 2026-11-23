# Choosing an update from Graft

[Back to update-relaunch.md](../update-relaunch.md)

## Choosing an update from Graft

Manual mode now includes an independent availability check. The window and
menu bar show Claude Desktop's installed and available versions even while
Claude is running. A pending update changes Graft's menu bar icon to an
exclamation mark. Checking the feed does not start Claude or change its policy.

**Update Claude…** opens a warning naming how many instances are running and
that their workflows will stop. Cancel does nothing to those instances.
Confirming rechecks the current release before any quits, then waits for every
approved process to exit. A later instance or reused pid stops the operation;
an instance that refuses to quit is never forced closed.

The download runs in an empty profile under Graft's own support directory,
with the same device identifier used for availability checks. Working profiles
keep their manual policy throughout. Graft launches the installed, unmodified
Claude, waits for its prepared update, and gracefully quits that updater
instance so Squirrel installs it. The mechanism is described in
[Squirrel's installation documentation](https://github.com/Squirrel/Squirrel.Mac#installing-updates).
Its [termination listener](https://github.com/Squirrel/Squirrel.Mac/blob/main/Squirrel/SQRLTerminationListener.m)
waits for instances of the target bundle to exit rather than killing them.

A shared file lock keeps shortcut launches and automatic profile recovery out
of that sequence. A saved update run can be followed after Graft relaunches;
relaunching Graft never repeats the user's earlier permission to close working
instances. Completion requires the installed bundle version and the installer
process state, not just an updater window disappearing. Graft never writes
Claude's application bundle or copies credentials into the updater profile.

Verification: 703 checks pass. They cover numeric release comparison, bad feed
responses, a withdrawn release or network failure before shutdown, all three
instances exiting, reused pids, new instances, download and install timeouts,
and the cross-process launch lock. A native UI preview verified both available
release layouts and the warning for three running instances; Cancel returned
to the available release without invoking the updater.

The universal build and both shortcut launchers were installed locally. The
installed app read the live feed and displayed Claude 1.46388.4 as up to date.
No real release installation was forced. Existing profile, provider, login,
policy, and shortcut checksums were unchanged, and manual mode remained enabled
for all three profiles. The prior bundles are backed up under
`/private/tmp/claude-graft-before-update-controls.noindex`.
