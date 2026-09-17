param(
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$Compiler,
    [switch]$SkipBuild,
    [switch]$TestMode
)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Compiler = $command.Source }
    else {
        $Compiler = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) {
    $compilerRoot = Join-Path $PSScriptRoot 'dist/tools/InnoSetup-7.1.0'
    $Compiler = Join-Path $compilerRoot 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $Compiler)) {
        $toolsDirectory = Split-Path $compilerRoot
        New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
        $download = Join-Path $toolsDirectory 'innosetup-7.1.0-x64.exe'
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $download
        $compilerSignature = Get-AuthenticodeSignature -LiteralPath $download
        if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notmatch '(^|, )O=Pyrsys B\.V\.(,|$)') {
            throw 'The installer compiler signature is not valid.'
        }
        $bootstrap = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1','/CURRENTUSER',
            '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $compilerRoot + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($bootstrap.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Compiler)) {
            throw 'The portable installer compiler could not be prepared.'
        }
    }
}
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Architecture $Architecture }
$published = Join-Path $PSScriptRoot "dist/ClaudeGraft-$Architecture"
foreach ($required in @('ClaudeGraft.exe','ClaudeGraft.pri','coreclr.dll','launcher/GraftLaunch.exe','launcher/coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing published file: $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination $published
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $published
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTING.md') -Destination $published
$prerequisites = Join-Path $PSScriptRoot 'dist/prerequisites'
New-Item -ItemType Directory -Force -Path $prerequisites | Out-Null
$runtime = Join-Path $prerequisites "vc_redist.$Architecture.exe"
if (-not (Test-Path -LiteralPath $runtime)) {
    Invoke-WebRequest -Uri "https://aka.ms/vc14/vc_redist.$Architecture.exe" -OutFile $runtime
}
$signature = Get-AuthenticodeSignature -LiteralPath $runtime
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
    throw 'The Microsoft runtime signature is not valid; the installer was not created.'
}
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
$destination = Join-Path $PSScriptRoot 'dist'
$arguments = @('/Qp', "/DAppVersion=$version", "/DArchitecture=$Architecture",
    "/DPublishRoot=$published", "/DVcRedist=$runtime", "/DInstallerOutput=$destination")
if ($TestMode) { $arguments += '/DTestMode=1' }
$arguments += Join-Path $PSScriptRoot 'installer/ClaudeGraft.iss'
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
$suffix = if ($TestMode) { '-test' } else { '' }
$installer = Join-Path $destination "ClaudeGraft-$version-windows-$Architecture-setup$suffix.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'The compiler did not produce the expected setup executable.' }
(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash + '  ' + (Split-Path $installer -Leaf) |
    Set-Content -LiteralPath "$installer.sha256"
[ordered]@{
    version = $version
    architecture = $Architecture
    sourceCommit = (& git -C $PSScriptRoot rev-parse HEAD)
    installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    runtimeVersion = (Get-Item -LiteralPath $runtime).VersionInfo.FileVersion
    runtimeSha256 = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath "$installer.build.json"
Write-Output $installer
