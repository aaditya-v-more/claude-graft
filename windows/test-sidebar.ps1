param([Parameter(Mandatory)][string]$Runtime)
$ErrorActionPreference = 'Stop'
$runtimePath = (Resolve-Path -LiteralPath $Runtime).Path
if (-not (Test-Path -LiteralPath (Join-Path $runtimePath 'sidebar.cjs'))) { throw 'Supply the published sidebar runtime directory.' }
$oldRuntime = $env:GRAFT_SIDEBAR_TEST_RUNTIME
$oldSeed = $env:GRAFT_SIDEBAR_TEST_SEED
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('graft-sidebar-seed-' + [guid]::NewGuid() + '.cjs')
try {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Tests/Sidebar/main.swift'))
    $seed = [regex]::Match($text, '(?s)let seedScript = #"""\r?\n(.*?)\r?\n"""#')
    if (-not $seed.Success) { throw 'Could not locate the upstream browser fixture.' }
    [IO.File]::WriteAllText($fixture, $seed.Groups[1].Value)
    $env:GRAFT_SIDEBAR_TEST_RUNTIME = $runtimePath
    $env:GRAFT_SIDEBAR_TEST_SEED = $fixture
    & dotnet test (Join-Path $PSScriptRoot 'ClaudeGraft.Tests') -c Release -p:RestoreLockedMode=true --filter 'FullyQualifiedName~SidebarTests|FullyQualifiedName~SidebarProjectTests' --nologo -- RunConfiguration.TreatNoTestsAsError=true
    if ($LASTEXITCODE -ne 0) { throw 'Offline sidebar checks failed.' }
} finally {
    $env:GRAFT_SIDEBAR_TEST_RUNTIME = $oldRuntime
    $env:GRAFT_SIDEBAR_TEST_SEED = $oldSeed
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
}
