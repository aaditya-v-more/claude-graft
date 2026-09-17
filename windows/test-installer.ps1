param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [switch]$SkipUi
)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path -LiteralPath $Installer).Path
if (-not $Installer.EndsWith('-setup-test.exe',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use the isolated -TestMode installer, not the user-facing setup.'
}
$workspaceDist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$fixture = [IO.Path]::GetFullPath((Join-Path $workspaceDist ('installer-test-' + [guid]::NewGuid().ToString('N'))))
$application = Join-Path $fixture 'app'
if (-not $fixture.StartsWith($workspaceDist + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer test path is outside the build workspace.'
}
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$externalData = Join-Path $fixture 'profile-data-must-stay.txt'
Set-Content -LiteralPath $externalData -Value 'Keep chat and profile data.'
$setup = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES',
    '/NORESTART','/NOICONS',('/DIR="' + $application + '"'),('/LOG="' + (Join-Path $fixture 'install.log') + '"')) -WindowStyle Hidden -PassThru -Wait
if ($setup.ExitCode -ne 0) { throw "Test installation failed ($($setup.ExitCode)). See $fixture/install.log." }
foreach ($required in @('ClaudeGraft.exe','ClaudeGraft.pri','coreclr.dll','launcher/GraftLaunch.exe','launcher/coreclr.dll','unins000.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $application $required))) { throw "Installed file missing: $required" }
}
if (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeGraft-Installer-Test_is1') {
    throw 'The isolated test unexpectedly registered an installed application.'
}
if (-not $SkipUi) { & (Join-Path $PSScriptRoot 'test-ui.ps1') -Executable (Join-Path $application 'ClaudeGraft.exe') }
$unmanagedFile = Join-Path $application 'user-file-must-stay.txt'
Set-Content -LiteralPath $unmanagedFile -Value 'This was not installed by setup.'
$uninstaller = [IO.Path]::GetFullPath((Join-Path $application 'unins000.exe'))
if (-not $uninstaller.StartsWith($fixture + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'The test uninstaller is outside its isolated directory.'
}
$remove = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
    ('/LOG="' + (Join-Path $fixture 'uninstall.log') + '"')) -WindowStyle Hidden -PassThru -Wait
if ($remove.ExitCode -ne 0) { throw "Test uninstall failed ($($remove.ExitCode))." }
if (Test-Path -LiteralPath (Join-Path $application 'ClaudeGraft.exe')) { throw 'The installed executable was not removed.' }
if (-not (Test-Path -LiteralPath $externalData) -or -not (Test-Path -LiteralPath $unmanagedFile)) {
    throw 'Uninstall removed data outside its installation manifest.'
}
Write-Output "Passed: install, bundled runtimes, installed UI, uninstall, and preservation of unrelated files. Logs: $fixture"
