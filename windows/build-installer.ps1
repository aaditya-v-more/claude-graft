param(
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$Compiler,
    [switch]$SkipBuild,
    [switch]$TestMode,
    [string]$WebView2Bootstrapper,
    [string]$PublishRoot,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$PublisherThumbprint,
    [string]$SignTool,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
if ($PublisherThumbprint) {
    if ($TestMode -or -not $SkipBuild -or -not $PublishRoot) {
        throw 'Publisher signing requires -SkipBuild and -PublishRoot pointing to a separate, reviewed staging copy of the production payload.'
    }
    $tsa = $TimestampUrl -as [Uri]
    if (-not $tsa -or -not $tsa.IsAbsoluteUri -or $tsa.Scheme -notin 'http','https' -or
        $tsa.UserInfo -or $TimestampUrl -match '[\s"\x00-\x1f]') { throw 'Supply -TimestampUrl with the RFC 3161 timestamp service URL.' }
    . (Join-Path $PSScriptRoot 'installer/PublisherSigning.ps1')
    $SignTool = Get-GraftSignTool $SignTool
    Assert-GraftSigningCertificate $PublisherThumbprint
} elseif ($SignTool -or $TimestampUrl -or $PublishRoot) {
    throw '-SignTool, -TimestampUrl and -PublishRoot require -PublisherThumbprint.'
}
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
if ($PublisherThumbprint) {
    $stage = Get-Item -LiteralPath $PublishRoot
    if (-not $stage.PSIsContainer -or ($stage.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $stage.FullName.TrimEnd('\') -eq [IO.Path]::GetFullPath($published).TrimEnd('\')) { throw 'Use a separate release staging directory.' }
    $published = $stage.FullName.TrimEnd('\')
}
foreach ($required in @('ClaudeGraft.exe','ClaudeGraft.pri','coreclr.dll','launcher/GraftLaunch.exe','launcher/coreclr.dll','relay/GraftRelay.exe','relay/coreclr.dll','vc-runtime.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing published file: $required" }
}
if (-not $PublisherThumbprint) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination $published
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $published
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTING.md') -Destination $published
}
$runtime = Get-Content -LiteralPath (Join-Path $published 'vc-runtime.json') -Raw | ConvertFrom-Json
if ($runtime.architecture -ne $Architecture -or $runtime.files.name -notcontains 'msvcp140.dll' -or
    $runtime.files.name -notcontains 'vcruntime140.dll') { throw 'Rebuild the published Visual C++ runtime.' }
foreach ($file in $runtime.files) {
    foreach ($directory in $published, (Join-Path $published 'launcher'), (Join-Path $published 'relay')) {
        if ((Get-FileHash -LiteralPath (Join-Path $directory $file.name)).Hash -ne $file.sha256) {
            throw "Published Visual C++ runtime hash mismatch for $($file.name)."
        }
    }
}
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
$destination = Join-Path $PSScriptRoot 'dist'
if ($PublisherThumbprint) {
    Sign-GraftPayload $published $SignTool $PublisherThumbprint $TimestampUrl
    $destination = Join-Path $destination 'signed'
}
$arguments = @('/Qp', "/DAppVersion=$version", "/DArchitecture=$Architecture",
    "/DPublishRoot=$published", "/DInstallerOutput=$destination")
$webView2 = $null
if (-not $TestMode) {
    . (Join-Path $PSScriptRoot 'installer/WebView2Runtime.ps1')
    $webView2 = Get-GraftWebView2Bootstrapper (Join-Path $PSScriptRoot 'dist/tools/WebView2') $WebView2Bootstrapper
    $arguments += "/DWebView2Bootstrapper=$($webView2.path)"
    $arguments += "/DWebView2Sha256=$($webView2.sha256)"
}
if ($PublisherThumbprint) {
    $arguments += '/DPublisherSigned=1'
    $arguments += Get-GraftInnoSignToolArgument $SignTool $PublisherThumbprint $TimestampUrl
}
if ($TestMode) { $arguments += '/DTestMode=1' }
$arguments += Join-Path $PSScriptRoot 'installer/ClaudeGraft.iss'
& $Compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
$suffix = if ($TestMode) { '-test' } else { '' }
$installer = Join-Path $destination "ClaudeGraft-$version-windows-$Architecture-setup$suffix.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'The compiler did not produce the expected setup executable.' }
if ($PublisherThumbprint) {
    Assert-GraftPublisherSignature $installer $PublisherThumbprint
    & $SignTool verify /pa /all /tw $installer
    if ($LASTEXITCODE -ne 0) { throw "SignTool could not verify the timestamped setup ($LASTEXITCODE)." }
}
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$installerHash + '  ' + (Split-Path $installer -Leaf) |
    Set-Content -LiteralPath "$installer.sha256" -Encoding Ascii
$manifest = [ordered]@{
    version = $version
    architecture = $Architecture
    sourceCommit = (& git -C $PSScriptRoot rev-parse HEAD)
    sourceDirty = [bool](& git -C $PSScriptRoot status --porcelain)
    installerSha256 = $installerHash
    runtimeVersion = $runtime.version
    runtimeFiles = $runtime.files
}
if ($PublisherThumbprint) {
    $manifest.authenticode = @{ verified = $true; signerThumbprint = $PublisherThumbprint.ToUpperInvariant() }
}
if ($webView2) {
    $manifest.webView2Bootstrapper = @{ source = $webView2.source; sha256 = $webView2.sha256; signerThumbprint = $webView2.signerThumbprint }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$installer.build.json" -Encoding UTF8
Write-Output $installer
