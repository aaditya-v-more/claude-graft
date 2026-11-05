param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$PublishRoot,
    [string]$Cache = (Join-Path $PSScriptRoot 'dist/sidebar-downloads')
)
$ErrorActionPreference = 'Stop'
$version = '44.9.3'
$checksum = @{
    x64 = '41a1ffa0541cb0f2c9b9fe920dd2cef1793ad0ea2ada1a9582958880f4d1b23c'
    arm64 = '66662802d00c2626a203f8909467dab3504def4ce8615c152135b994a5cec220'
}

# Pin the same browser engine as the supported Claude build. A newer Chromium
# can migrate the databases in a way the installed Claude cannot reopen.
$archive = "electron-v$version-win32-$Architecture.zip"
New-Item -ItemType Directory -Force -Path $Cache | Out-Null
$zip = Join-Path $Cache $archive
if (-not (Test-Path -LiteralPath $zip)) {
    $partial = Join-Path $Cache "$archive-$([guid]::NewGuid()).partial"
    try {
        Invoke-WebRequest -Uri "https://github.com/electron/electron/releases/download/v$version/$archive" -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial).Hash -ne $checksum[$Architecture]) { throw 'Invalid sidebar runtime checksum.' }
        Move-Item -LiteralPath $partial -Destination $zip
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
}
if ((Get-FileHash -LiteralPath $zip).Hash -ne $checksum[$Architecture]) { throw 'Invalid cached sidebar runtime checksum.' }
$runtime = Join-Path $PublishRoot 'launcher/sidebar-runtime'
Expand-Archive -LiteralPath $zip -DestinationPath $runtime -Force

# The manager and generated shortcuts ship the exact same offline storage
# code as the Mac launcher. Unknown source shapes must fail the build.
$text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Sources/Shared/SidebarStorage.swift'))
$script = [regex]::Match($text, '(?s)static let script = #"""\r?\n(.*?)\r?\n"""#')
if (-not $script.Success) { throw 'Could not locate the shared sidebar helper.' }
[IO.File]::WriteAllText((Join-Path $runtime 'sidebar.cjs'), $script.Groups[1].Value)
