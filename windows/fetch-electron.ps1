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
# Run after publishing/mirroring and before packaging. The version comes from
# electron-version.txt, the one place it is pinned: a newer Chromium than
# Claude's can upgrade its IndexedDB beyond what Claude reads, so it moves only
# with a try. Pass -Version to override; -Force re-extracts even if present.
param([string]$Version = "", [switch]$Force)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'dist\ClaudeGraft\electron'

if (-not $Version) { $Version = (Get-Content (Join-Path $PSScriptRoot 'electron-version.txt') -Raw).Trim().TrimStart('v') }

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
