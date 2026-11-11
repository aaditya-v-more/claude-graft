param([string]$Executable = (Join-Path $PSScriptRoot 'dist/ClaudeGraft-x64/ClaudeGraft.exe'))
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $Executable).Path
$root = Join-Path $PSScriptRoot ('dist/web-account-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root '.graft-test-root') -Value 'Disposable browser and export checks'
$process = Start-Process -FilePath $app -ArgumentList '--test-data-root', ('"' + $root + '"'), '--check-web-accounts' -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(180000)) { throw 'The disposable browser checks did not finish within three minutes.' }
    $report = Join-Path $root 'profiles/ClaudeGraft/web-account-checks.json'
    if (-not (Test-Path -LiteralPath $report)) { throw "The browser checks did not produce a report. Inspect $root." }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($result.failed -ne 0 -or $result.passed -lt 15) { throw "Browser checks failed: $($result.error) Report: $report" }
    Write-Output "$($result.passed) native account isolation and export checks passed. Report: $report"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
