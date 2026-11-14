param(
    [string]$Compiler,
    [string]$FixtureRoot = (Join-Path $PSScriptRoot 'dist'),
    [string]$Bootstrapper
)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Compiler = $command.Source }
    else {
        $Compiler = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
            (Join-Path $PSScriptRoot 'dist/tools/InnoSetup-7.1.0/ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) {
    throw 'Run build-installer.ps1 first, or supply -Compiler with the full path to ISCC.exe.'
}
$fixture = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ('webview2-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
. (Join-Path $PSScriptRoot 'installer/WebView2Runtime.ps1')
$unsigned = Join-Path $fixture 'unsigned.txt'
[IO.File]::WriteAllText($unsigned, 'fixture')
$rejected = $false
try { Assert-GraftWebView2Signature $unsigned } catch { $rejected = $true }
if (-not $rejected) { throw 'An unsigned bootstrapper was accepted.' }
if ($Bootstrapper) {
    $verified = Get-GraftWebView2Bootstrapper $fixture $Bootstrapper
    Write-Output "Verified Microsoft bootstrapper: SHA256=$($verified.sha256), signer=$($verified.signerThumbprint)"
}
& {
    function Get-GraftWebView2Signature($Path) { $script:fixtureSignature }
    foreach ($case in @(
        @{ Status='NotSigned'; SignatureType='None'; Subject='O=Microsoft Corporation'; Accepted=$false },
        @{ Status='HashMismatch'; SignatureType='Authenticode'; Subject='O=Microsoft Corporation'; Accepted=$false },
        @{ Status='Valid'; SignatureType='Catalog'; Subject='O=Microsoft Corporation'; Accepted=$false },
        @{ Status='Valid'; SignatureType='Authenticode'; Subject='O=Another publisher'; Accepted=$false },
        @{ Status='Valid'; SignatureType='Authenticode'; Subject='CN=Microsoft Corporation, O=Microsoft Corporation, C=US'; Accepted=$true }
    )) {
        $script:fixtureSignature = [pscustomobject]@{
            Status=$case.Status; SignatureType=$case.SignatureType
            SignerCertificate=[pscustomobject]@{ Subject=$case.Subject; Thumbprint=('A' * 40) }
        }
        $accepted = $true
        try { Get-GraftWebView2Bootstrapper $fixture $unsigned | Out-Null } catch { $accepted = $false }
        if ($accepted -ne $case.Accepted) { throw "Wrong signature decision: $($case | ConvertTo-Json -Compress)" }
    }
}
Remove-Variable fixtureSignature -Scope Script
$hash = (Get-FileHash -LiteralPath $unsigned -Algorithm SHA256).Hash
& $Compiler /Qp "/DFixtureOutput=$fixture" "/DWebView2Sha256=$hash" (Join-Path $PSScriptRoot 'installer/tests/WebView2Runtime.iss')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the prerequisite fixture.' }
$result = Join-Path $fixture 'result.txt'
$probe = Start-Process -FilePath (Join-Path $fixture 'webview2-prerequisite-test.exe') -ArgumentList @(
    '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/RESULTFILE="' + $result + '"'),
    ('/LOG="' + (Join-Path $fixture 'fixture.log') + '"')) -WindowStyle Hidden -Wait -PassThru
if ($probe.ExitCode -ne 1 -or -not (Test-Path -LiteralPath $result)) {
    throw "The prerequisite fixture did not complete ($($probe.ExitCode)). See $fixture."
}
$outcome = Get-Content -LiteralPath $result -Raw
if ($outcome -notmatch '^Passed 21 prerequisite assertions\.$') { throw $outcome }
$payload = Join-Path $fixture 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item -LiteralPath $unsigned -Destination (Join-Path $payload 'fixture.txt')
foreach ($architecture in 'x64','arm64') {
    $arguments = @('/Qp', '/DAppVersion=0.0.0-prerequisite-fixture', "/DArchitecture=$architecture",
        "/DPublishRoot=$payload", "/DInstallerOutput=$fixture")
    # These packages are compiled, never installed. The production prerequisite
    # must still compile when the isolated installer variant leaves it out.
    & $Compiler @arguments "/DWebView2Bootstrapper=$unsigned" "/DWebView2Sha256=$hash" (Join-Path $PSScriptRoot 'installer/ClaudeGraft.iss')
    if ($LASTEXITCODE -ne 0) { throw "Production $architecture prerequisite hook did not compile." }
    & $Compiler @arguments '/DTestMode=1' (Join-Path $PSScriptRoot 'installer/ClaudeGraft.iss')
    if ($LASTEXITCODE -ne 0) { throw "Isolated $architecture setup did not compile without a bootstrapper." }
}
Write-Output "Passed 6 signature checks. $outcome Both installer architectures and TestMode compiled. No runtime was installed. Logs: $fixture"
