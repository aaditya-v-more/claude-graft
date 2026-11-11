param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$PublishRoot
)
$ErrorActionPreference = 'Stop'

# Only Microsoft's redistributable release CRT is licensed for app-local deployment.
# Use the version of the signed DLL, since its containing VS folder can have an older build number.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$candidates = @(
    if ($env:VCToolsRedistDir) {
        Get-ChildItem -Path (Join-Path $env:VCToolsRedistDir "$Architecture\Microsoft.VC*.CRT\vcruntime140.dll") -File -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -products '*' -all -find "VC\Redist\MSVC\*\$Architecture\Microsoft.VC*.CRT\vcruntime140.dll" |
            ForEach-Object { Get-Item -LiteralPath $_ }
    }
)
$runtime = $candidates |
    Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
if (-not $runtime -or $runtime.VersionInfo.FileVersionRaw -lt [version]'14.50.35719.0') {
    throw "Install Visual Studio C++ Build Tools with the $Architecture release CRT (14.50.35719 or later)."
}
$dlls = Get-ChildItem -LiteralPath $runtime.DirectoryName -Filter '*.dll' -File | Sort-Object Name
foreach ($required in 'vcruntime140.dll', 'msvcp140.dll') {
    if ($dlls.Name -notcontains $required) { throw "Missing $Architecture redistributable: $required" }
}

# Each profile shortcut carries its own copy of launcher/. Include the same CRT
# beside both executables so moving or uninstalling the manager cannot break a launcher.
$files = foreach ($dll in $dlls) {
    $signature = Get-AuthenticodeSignature -LiteralPath $dll.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "Invalid Microsoft signature on redistributable $($dll.Name)."
    }
    Copy-Item -LiteralPath $dll.FullName -Destination $PublishRoot -Force
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $PublishRoot 'launcher') -Force
    if (Test-Path -LiteralPath (Join-Path $PublishRoot 'relay')) {
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $PublishRoot 'relay') -Force
    }
    [ordered]@{ name = $dll.Name; sha256 = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash }
}
[ordered]@{ architecture = $Architecture; version = $runtime.VersionInfo.FileVersion; files = @($files) } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $PublishRoot 'vc-runtime.json')
