# Stages the bundled Electron runtime the sidebar-sync helper runs on. Claude's
# own binary has asar integrity fused on, so Graft ships its own Electron rather
# than reusing Claude's. The runtime lands under dist/, which is gitignored, and
# install.ps1 carries it beside ClaudeGraft.exe, where SidebarStorage looks for
# electron\electron.exe.
#
# The publish -> dist mirror purges anything not in the publish output, so this
# must be re-run after each mirror. The zip is cached under LocalAppData, so a
# re-stage after the first download is a local unzip, not a fresh download.
#
# Run after publishing/mirroring and before packaging. Pass -Version to pin; the
# default takes the latest stable. Pass -Force to re-extract even if present.
param([string]$Version = "", [switch]$Force)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'dist\ClaudeGraft\electron'

if (-not $Version) {
    $release = Invoke-RestMethod 'https://api.github.com/repos/electron/electron/releases/latest' `
        -Headers @{ 'User-Agent' = 'claude-graft' }
    $Version = $release.tag_name.TrimStart('v')
}

if ((Test-Path (Join-Path $dest 'electron.exe')) -and -not $Force) {
    Write-Host "Electron already staged in $dest (use -Force to re-extract)"
    return
}

$cacheDir = Join-Path $env:LOCALAPPDATA 'ClaudeGraft-build'
$zip = Join-Path $cacheDir "electron-v$Version-win32-x64.zip"
New-Item -ItemType Directory -Force $cacheDir | Out-Null
if (-not (Test-Path $zip)) {
    $url = "https://github.com/electron/electron/releases/download/v$Version/electron-v$Version-win32-x64.zip"
    Write-Host "Downloading Electron $Version..."
    Invoke-WebRequest $url -OutFile $zip
} else {
    Write-Host "Using cached Electron $Version zip"
}

Write-Host "Extracting to $dest ..."
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Expand-Archive $zip $dest -Force

if (-not (Test-Path (Join-Path $dest 'electron.exe'))) { throw "electron.exe missing after extract" }

# The helper never shows UI, so other languages and the WebGPU shader compiler
# are dead weight; Chromium falls back to en-US when a locale pak is missing.
# default_app.asar stays: it is what loads the helper folder named on the command
# line, and without it electron.exe exits 1 before running a line.
Get-ChildItem (Join-Path $dest 'locales') -Exclude 'en-US.pak' | Remove-Item -Force
Remove-Item (Join-Path $dest 'dxcompiler.dll'), (Join-Path $dest 'dxil.dll') -Force -ErrorAction SilentlyContinue
Write-Host "Electron $Version staged in $dest"
