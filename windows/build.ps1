param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Build on Windows.' }
    if (-not $SkipTests) {
        & dotnet test ClaudeGraft.Tests -c Release -p:RestoreLockedMode=true --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }
    }
    $platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    $output = Join-Path $PSScriptRoot "dist/ClaudeGraft-$Architecture"
    & dotnet publish ClaudeGraft -c Release -p:Platform=$platform -r "win-$Architecture" --self-contained true -p:RestoreLockedMode=true -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows publishing failed.' }
    foreach ($required in @('ClaudeGraft.exe', 'ClaudeGraft.pri', 'Assets/AppIcon.ico',
                            'coreclr.dll', 'Microsoft.UI.Xaml.dll',
                            'launcher/GraftLaunch.exe', 'launcher/coreclr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $required))) { throw "Missing published file: $required" }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTING.md') -Destination $output
    $version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
    $archive = Join-Path $PSScriptRoot "dist/ClaudeGraft-$version-windows-$Architecture.zip"
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash + '  ' + (Split-Path $archive -Leaf) |
        Set-Content -LiteralPath "$archive.sha256"
    Write-Output $archive
}
finally { Pop-Location }
