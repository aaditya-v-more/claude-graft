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
foreach ($required in @('ClaudeGraft.exe','ClaudeGraft.pri','coreclr.dll','launcher/GraftLaunch.exe','launcher/coreclr.dll','vc-runtime.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing published file: $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination $published
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $published
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTING.md') -Destination $published
$runtime = Get-Content -LiteralPath (Join-Path $published 'vc-runtime.json') -Raw | ConvertFrom-Json
if ($runtime.architecture -ne $Architecture -or $runtime.files.name -notcontains 'msvcp140.dll' -or
    $runtime.files.name -notcontains 'vcruntime140.dll') { throw 'Rebuild the published Visual C++ runtime.' }
foreach ($file in $runtime.files) {
    foreach ($directory in $published, (Join-Path $published 'launcher')) {
        if ((Get-FileHash -LiteralPath (Join-Path $directory $file.name)).Hash -ne $file.sha256) {
            throw "Published Visual C++ runtime hash mismatch for $($file.name)."
        }
    }
}
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
$destination = Join-Path $PSScriptRoot 'dist'
$arguments = @('/Qp', "/DAppVersion=$version", "/DArchitecture=$Architecture",
    "/DPublishRoot=$published", "/DInstallerOutput=$destination")
if ($TestMode) { $arguments += '/DTestMode=1' }
$arguments += Join-Path $PSScriptRoot 'installer/ClaudeGraft.iss'
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
$suffix = if ($TestMode) { '-test' } else { '' }
$installer = Join-Path $destination "ClaudeGraft-$version-windows-$Architecture-setup$suffix.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'The compiler did not produce the expected setup executable.' }
(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash + '  ' + (Split-Path $installer -Leaf) |
    Set-Content -LiteralPath "$installer.sha256" -Encoding Ascii
[ordered]@{
    version = $version
    architecture = $Architecture
    sourceCommit = (& git -C $PSScriptRoot rev-parse HEAD)
    sourceDirty = [bool](& git -C $PSScriptRoot status --porcelain)
    installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    runtimeVersion = $runtime.version
    runtimeFiles = $runtime.files
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$installer.build.json"
Write-Output $installer
