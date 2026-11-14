param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [string]$FixtureRoot = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$fixture = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ('relay-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Set-Content -LiteralPath (Join-Path $fixture '.graft-test-root') -Value 'Disposable relay fixture.'
$ownData = Join-Path $fixture 'ClaudeGraft'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$bytes = [Text.Encoding]::UTF8.GetBytes($ownData.ToUpperInvariant() + '|' + $sid)
$key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
$pipeName = 'ClaudeGraft.Pool.' + $key.Substring(0,32)
function Send-RelayCommand($Command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut,
        ([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly),
        [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(1000)
        $payload = [Text.Encoding]::UTF8.GetBytes(($Command | ConvertTo-Json -Depth 12 -Compress) + "`n")
        $pipe.Write($payload,0,$payload.Length)
        $reader = [IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
        try {
            $read = $reader.ReadLineAsync()
            if (-not $read.Wait(5000)) { throw 'The disposable relay did not answer.' }
            return $read.Result | ConvertFrom-Json
        } finally { $reader.Dispose() }
    } finally { $pipe.Dispose() }
}
function Start-FixtureRelay {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path $Executable
    $start.ArgumentList.Add('--test-data-root')
    $start.ArgumentList.Add($fixture)
    return [Diagnostics.Process]::Start($start)
}
$process = $null
$duplicate = $null
try {
    $process = Start-FixtureRelay
    $status = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw "The shipping relay exited before answering ($($process.ExitCode))." }
        try { $status = Send-RelayCommand @{}; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $status -or $status.pid -ne $process.Id -or $status.configured) { throw 'The fresh relay did not start with sender passthrough.' }
    $directory = Split-Path $Executable
    $bundle = @('GraftRelay.exe','GraftRelay.dll','ClaudeGraft.Core.dll') | ForEach-Object {
        $_ + ':' + (Get-FileHash -LiteralPath (Join-Path $directory $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($bundle -join "`n")))).ToLowerInvariant()
    if ($status.binary -ne $digest) { throw 'The running relay does not match the published bundle.' }
    $duplicate = Start-FixtureRelay
    if (-not $duplicate.WaitForExit(5000) -or $duplicate.ExitCode -ne 1) { throw 'A duplicate relay did not yield the exclusive lease.' }
    $configured = Send-RelayCommand @{ configure = @{ enabled = $true; members = @(@{
        id='fixture'; name='Fixture'; account='fixture'; identify=@(); token='disposable-fixture-token'; limits=$null; borrowsFrom=@()
    }); assignments=@{} } }
    if (-not $configured.configured -or -not $configured.enabled -or $configured.members.Count -ne 1) { throw 'The running relay did not accept same-user configuration.' }
    if (($configured | ConvertTo-Json -Depth 12) -match 'disposable-fixture-token') { throw 'Relay status exposed a token.' }
    $identity = Join-Path $ownData 'pool/relay-identity.bin'
    if (-not (Test-Path -LiteralPath $identity) -or -not (Test-Path -LiteralPath (Join-Path $ownData 'pool/relay-ca.pem'))) { throw 'The relay did not prepare its per-user TLS identity.' }
    $null = Send-RelayCommand @{ quit=$true }
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'The relay did not exit cleanly after yielding.' }
    $process.Dispose()
    $process = Start-FixtureRelay
    $restarted = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { $restarted = Send-RelayCommand @{}; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $restarted -or $restarted.configured -or $restarted.members.Count -ne 0) { throw 'A restarted relay retained account credentials.' }
    $null = Send-RelayCommand @{ quit=$true }
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'The replacement relay did not exit cleanly.' }
    Write-Output "Passed: native relay, matching bundle, per-user control, exclusive lease, token-free status, clean restart and exit. Fixture: $fixture"
} finally {
    foreach ($owned in @($duplicate,$process)) {
        if ($owned) {
            if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit() }
            $owned.Dispose()
        }
    }
}
