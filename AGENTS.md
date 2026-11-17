# Website publishing

Follow [CLAUDE.md](CLAUDE.md) for repository guidance.

Keep the public website usable at phone widths, in landscape and on desktop.
Preserve existing source, download, license and attribution links, including the
approved creator links to:

- https://aadityamore.com/
- https://github.com/aaditya-v-more
- https://www.linkedin.com/in/aadityavmore/

Keep creator navigation accessible and wrapping, with `data-creator-links` on
that navigation. Verify links and mobile layout before publishing website
changes. Commit only the intended files; preserve unrelated local work.

## Permission for independent project changes

This project is maintained independently of the portfolio. Any modification to
this repository or its deployed website requires the user's explicit approval
for this project and the affected change. This includes content, creator links,
appearance, layout, behavior, source configuration, deployment and repository
guidance.

Permission to change the main portfolio, Play, Learn or another site originating
in the resume repository does not authorize changes to this independent project.
A direct request for a specific change supplies the necessary approval; do not
ask again while completing work already within that approved scope.

## Scope of project synchronization

Synchronizing this project with the portfolio means adding or updating Website,
GitHub, LinkedIn and project source links only with the user's explicit
permission. Linking or listing a project does not itself grant that permission.
Preserve the creator links already approved for this website.

A link request does not authorize new color palettes, theme controls, changes to
the project's default appearance or appearance synchronization between sites.
Keep this website's original appearance behavior unless the user separately asks
to change it.

# Windows development builds

After any change to the Windows build, install it on the development machine
before asking for it to be tested, so what is tested is what was changed. Run
`windows/dev-install.ps1` from PowerShell. It publishes a Release build, packs
it as a Velopack package numbered `<windows/VERSION>-dev.<timestamp>`, asks the
running Graft to quit with `ClaudeGraft.exe --quit`, and has the installed
`Update.exe` apply the package, which waits for Graft to go, swaps the install
folder and starts the new build. A development number sorts below the release
it leads to, so the real release still installs over it.

Never stop Graft by killing it: its taskbar strip is a window inside Explorer's
taskbar, and Explorer stalls until Windows notices its owner is gone.
`--quit` runs Graft's own Quit, which takes the strip out first.

Applying a package runs neither of Velopack's install or uninstall steps, so a
change to `Program.Installed`, `Program.Uninstalling` or `LegacyInstall` is
tested with the full setup instead: pack without `-dev`, as
`windows-release.yml` does, and run `ClaudeGraftApp-win-Setup.exe`. The
machine must already have Graft installed by that setup once for
`dev-install.ps1` to have an `Update.exe` to apply with.

`vpk` is the .NET tool at `%USERPROFILE%\.dotnet\tools\vpk.exe`, on the same
version as the Velopack package the app references; a different `vpk` earlier
on PATH is an unrelated program, which is why the script names it by path.

Graft turns off Velopack's apply-on-startup. With it on, any launch of
`ClaudeGraft.exe`, `--quit` included, applied whatever package lay newest in
`%LOCALAPPDATA%\ClaudeGraftApp\packages`, which after a full setup is the
release, ranked above every development build.
