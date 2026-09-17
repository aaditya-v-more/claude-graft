param([string]$Executable = (Join-Path $PSScriptRoot 'dist/ClaudeGraft-x64/ClaudeGraft.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Join-Path $PSScriptRoot ('dist/ui-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'profiles/ClaudeGraft') | Out-Null
Set-Content -LiteralPath (Join-Path $root '.graft-test-root') -Value 'Disposable UI test'
Set-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/settings.json') -Value '{"startHidden":false,"theme":"Dark","backdrop":"None"}'
$process = Start-Process -FilePath $Executable -ArgumentList '--test-data-root', ('"' + $root + '"') -WindowStyle Hidden -PassThru
function Wait-Until([scriptblock]$Condition, [string]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if (& $Condition) { return }
        if ($process.HasExited) { throw "The test app exited. Inspect $root/profiles/ClaudeGraft/app-error.txt" }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Element([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Element($Element) {
    if ($null -eq $Element -or -not $Element.Current.IsEnabled) { throw 'The expected control is unavailable.' }
    ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
try {
    Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'The manager never opened.'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-Until { $null -ne (Element 'NameBox') } 'The main profile form never loaded.'
    $support = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Support Claude Graft on Ko-fi'))
    if ($null -eq $support -or $support.Current.IsOffscreen) { throw 'The Support link is missing from the sidebar.' }
    $newShortcut = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '+  New Shortcut'))
    Invoke-Element $newShortcut
    Wait-Until { $save = Element 'SaveButton'; $null -ne $save -and $save.Current.Name -eq 'Create Shortcut' } 'The shortcut form did not open.'
    Invoke-Element (Element 'SaveButton')
    $desktop = Join-Path $root 'profiles/Desktop'
    Wait-Until { Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk') } 'The shortcut was not created.'
    $nameField = Element 'NameBox'
    ([System.Windows.Automation.ValuePattern]$nameField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Work Account')
    Invoke-Element (Element 'SaveButton')
    Wait-Until { (Test-Path -LiteralPath (Join-Path $desktop 'Work Account.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk')) } 'Renaming removed the new shortcut or retained the old one.'
    $stored = Get-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/shortcuts.json') -Raw | ConvertFrom-Json
    if (@($stored).Count -ne 1 -or $stored[0].name -ne 'Work Account' -or $stored[0].folder -ne 'Claude-2') { throw 'The saved profile did not survive the rename.' }
    $id = ([guid]$stored[0].id).ToString('N')
    $manifest = Join-Path $root "profiles/ClaudeGraft/shortcuts/$id/graft.json"
    if (-not (Test-Path -LiteralPath $manifest)) { throw 'The independent shortcut configuration is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $desktop 'Work Account.lnk'))
    if (-not (Test-Path -LiteralPath $link.TargetPath) -or $link.Arguments -notlike '*--config*') { throw 'The shortcut does not target its launcher and configuration.' }
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $link.TargetPath) 'coreclr.dll'))) { throw 'The standalone launcher has no bundled runtime.' }
    Write-Output "Passed: startup, Support link, create shortcut, rename, manifest and standalone launcher. Fixtures: $root"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
