# Builds Graft and puts it over the installed copy the way a real update does:
# packed as a Velopack package, then applied by the installed Update.exe, which
# waits for Graft to quit, swaps the folder and starts the new build.
#
# It quits and restarts Graft, so run it from a terminal outside any Claude that
# Graft manages. A change to what Graft does on install or uninstall still needs
# the full setup: applying an update runs neither.

param(
    # The Electron the sidebar helper runs on; the version staged beside the installed copy by default.
    [string]$ElectronVersion
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Join-Path $env:LOCALAPPDATA 'ClaudeGraftApp'
$update = Join-Path $root 'Update.exe'
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'

if (-not (Test-Path $update)) { throw "Graft is not installed with its setup. Run dist\velopack\ClaudeGraftApp-win-Setup.exe once first." }
# A Python vpk on PATH is a different program, so the .NET tool is named by its path.
if (-not (Test-Path $vpk)) { throw "vpk not found. Install it with: dotnet tool install -g vpk --version 1.2.161" }

if (-not $ElectronVersion) {
    $staged = Join-Path $root 'current\electron\version'
    $ElectronVersion = if (Test-Path $staged) { (Get-Content $staged -Raw).Trim().TrimStart('v') } else { '44.4.4' }
}

Write-Host "Publishing..."
dotnet publish (Join-Path $here 'ClaudeGraft\ClaudeGraft.csproj') -c Release -p:Platform=x64 -o (Join-Path $here 'dist\ClaudeGraft') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
& (Join-Path $here 'fetch-electron.ps1') -Version $ElectronVersion | Out-Null
if (-not (Test-Path (Join-Path $here 'dist\ClaudeGraft\electron\resources\default_app.asar'))) { throw "Electron was not staged" }

# Below the release it leads to, so the real one still installs over a development build.
$base = (Get-Content (Join-Path $here 'VERSION') -Raw).Trim()
$version = "$base-dev.$(Get-Date -Format 'yyyyMMddHHmmss')"
# A fresh folder each time: a virus scan of the last package can hold it for a moment.
$out = Join-Path $here "dist\dev-packages\$version"
Write-Host "Packing $version..."
& $vpk pack --packId ClaudeGraftApp --packVersion $version --packDir (Join-Path $here 'dist\ClaudeGraft') `
    --mainExe ClaudeGraft.exe --packTitle 'Claude Graft' --packAuthors 'Claude Graft' `
    --icon (Join-Path $here 'ClaudeGraft\Assets\AppIcon.ico') --shortcuts StartMenuRoot --delta None -o $out | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
$package = Get-ChildItem $out -Filter '*-full.nupkg' | Select-Object -First 1
Get-ChildItem (Join-Path $here 'dist\dev-packages') -Directory | Where-Object FullName -ne $out |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

$running = Get-Process ClaudeGraft -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" } | Select-Object -First 1
$apply = @('apply', '--package', $package.FullName, '--silent')
if ($running) {
    Write-Host "Asking Graft to quit..."
    & (Join-Path $root 'current\ClaudeGraft.exe') --quit
    # Waited for here: Update.exe's own --waitPid is refused access to Graft's process and carries on regardless.
    if (-not $running.WaitForExit(60000)) { throw "Graft did not quit within a minute; nothing was applied" }
}
Write-Host "Applying..."
& $update @apply
if ($LASTEXITCODE -ne 0) { throw "Update.exe apply failed ($LASTEXITCODE); its log is in $root" }
Write-Host "Installed $version."
