# Exercise the production installer's real HKCU registrations on a disposable
# GitHub-hosted runner. Local install/uninstall testing uses test-installer.ps1.
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Production registration tests require a disposable GitHub-hosted runner.'
}
$Installer = (Resolve-Path -LiteralPath $Installer).Path
if (-not $Installer.EndsWith('-setup.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'Supply the production setup.' }
$registration = 'HKCU:\Software\RegisteredApplications'
$capabilities = 'HKCU:\Software\ClaudeGraft\Capabilities'
$handler = 'HKCU:\Software\Classes\ClaudeGraft.Link'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B0C98D46-A272-4C54-8EDC-A31D441BAE9B}_is1'
function RegistryValue($key, $name) {
    if (Test-Path -LiteralPath $key) { (Get-Item -LiteralPath $key).GetValue($name) }
}
function Assert-GraftAbsent {
    if ((Test-Path -LiteralPath $handler) -or (Test-Path -LiteralPath $capabilities) -or
        (Test-Path -LiteralPath $uninstall) -or (RegistryValue $registration 'Claude Graft')) {
        throw 'The runner already has Graft registrations or the uninstaller left them behind.'
    }
}
Assert-GraftAbsent

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HostedProtocolProbe {
    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
    [DllImport("shlwapi.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern int AssocQueryStringW(uint flags, uint str, string assoc, string extra, StringBuilder result, ref uint size);
    public static string OpenCommand() {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        uint size = 32768;
        var output = new StringBuilder((int)size);
        // ASSOCF_IS_PROTOCOL, ASSOCSTR_COMMAND. Query only; never open a link.
        Marshal.ThrowExceptionForHR(AssocQueryStringW(0x1000, 1, "claude", "open", output, ref size));
        return output.ToString();
    }
}
'@
$fixtureId = [guid]::NewGuid().ToString('N')
$fixture = Join-Path $env:RUNNER_TEMP "graft-production-$fixtureId"
$application = Join-Path $fixture 'app'
$oldHandler = 'HKCU:\Software\Classes\claude'
$userChoice = 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\claude\UserChoice'
$seedHandler = -not (Test-Path -LiteralPath $oldHandler)
$priorProgId = RegistryValue $userChoice 'ProgId'
$priorHash = RegistryValue $userChoice 'Hash'
function Assert-DefaultUnchanged($original) {
    if ([HostedProtocolProbe]::OpenCommand() -ne $original -or
        (RegistryValue $userChoice 'ProgId') -ne $priorProgId -or (RegistryValue $userChoice 'Hash') -ne $priorHash) {
        throw 'Production setup or uninstall changed the existing claude: default.'
    }
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    if ($seedHandler) {
        # A fresh runner has no Claude. Supply an existing, harmless handler and
        # confirm Windows can resolve it before testing whether setup preserves it.
        New-Item -Path "$oldHandler\shell\open\command" -Force | Out-Null
        Set-Item -LiteralPath $oldHandler -Value 'URL:Claude fixture'
        New-ItemProperty -LiteralPath $oldHandler -Name 'GraftFixture' -Value $fixtureId | Out-Null
        New-ItemProperty -LiteralPath $oldHandler -Name 'URL Protocol' -Value '' | Out-Null
        Set-Item -LiteralPath "$oldHandler\shell\open\command" -Value ('"' + "$env:SystemRoot\System32\notepad.exe" + '" "%1"')
    }
    $original = [HostedProtocolProbe]::OpenCommand()
    if ([string]::IsNullOrWhiteSpace($original)) { throw 'No existing protocol handler for the fixture.' }
    $argsForSetup = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $application + '"'))
    $setup = Start-Process -FilePath $Installer -ArgumentList $argsForSetup -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Production setup exited with $($setup.ExitCode)." }
    try {
        $expectedCommand = '"' + (Join-Path $application 'ClaudeGraft.exe') + '" --open-link "%1"'
        if ((RegistryValue $registration 'Claude Graft') -ne 'Software\ClaudeGraft\Capabilities' -or
            (RegistryValue "$capabilities\URLAssociations" 'claude') -ne 'ClaudeGraft.Link' -or
            (Get-Item -LiteralPath $handler).GetValueNames() -notcontains 'URL Protocol' -or
            (RegistryValue "$handler\shell\open\command" '') -cne $expectedCommand -or
            -not (Test-Path -LiteralPath $uninstall)) { throw 'Incorrect production installer registration.' }
        Assert-DefaultUnchanged $original
    } finally {
        $remove = Start-Process -FilePath (Join-Path $application 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait -PassThru
        if ($remove.ExitCode -ne 0) { throw "Production uninstall exited with $($remove.ExitCode)." }
    }
    Assert-GraftAbsent
    Assert-DefaultUnchanged $original
    Write-Output 'Verified production setup, quoted handler command, existing protocol default and uninstall cleanup.'
} finally {
    if ($seedHandler -and (RegistryValue $oldHandler 'GraftFixture') -eq $fixtureId) {
        Remove-Item -LiteralPath 'HKCU:\Software\Classes\claude' -Recurse
    }
}
