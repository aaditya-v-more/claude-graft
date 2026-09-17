param(
 [string]$Executable = (Join-Path $PSScriptRoot 'dist/ClaudeGraft-x64/ClaudeGraft.exe'),
 [ValidateSet('Dark','Light')][string]$Theme = 'Dark',
 [ValidateSet('Blue','Research')][string]$Preset = 'Blue',
 [switch]$KeepOpen
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('GraftUiProbe' -as [type])) {
 Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GraftUiProbe {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
'@
}
$root = Join-Path $PSScriptRoot ('dist/ui-test-' + $Theme.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'profiles/ClaudeGraft'),(Join-Path $root 'profiles/Claude-2') | Out-Null
Set-Content -LiteralPath (Join-Path $root '.graft-test-root') -Value 'Disposable UI test'
@{startHidden=$false;theme=$Theme;backdrop='None'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/settings.json')
@{samples=@(@{t=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();u=@{fh=18;sd=42}})} | ConvertTo-Json -Depth 5 |
 Set-Content -LiteralPath (Join-Path $root 'profiles/Claude-2/plan-usage-history.json')
$process = Start-Process -FilePath $Executable -ArgumentList '--test-data-root', ('"' + $root + '"') -WindowStyle Hidden -PassThru
Set-Content -LiteralPath (Join-Path $root 'process-id.txt') -Value $process.Id
function Wait-Until([scriptblock]$Condition,[string]$Failure) {
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 do {
  if (& $Condition) { return }
  if ($process.HasExited) { throw "The test app exited. Inspect $root/profiles/ClaudeGraft/app-error.txt" }
  Start-Sleep -Milliseconds 200
 } while ([DateTime]::UtcNow -lt $deadline)
 throw $Failure
}
function Element([string]$Id) {
 $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Invoke-Element($Element) {
 if ($null -eq $Element -or -not $Element.Current.IsEnabled) { throw 'The expected control is unavailable.' }
 ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Hit-Test([double]$X,[double]$Y) {
 $packed=(([int]$Y -band 0xffff) -shl 16) -bor ([int]$X -band 0xffff)
 [GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0084,[IntPtr]::Zero,[IntPtr]$packed).ToInt32()
}
try {
 Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'The manager never opened.'
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 Wait-Until { $null -ne (Element 'NameBox') } 'The main profile form never loaded.'
 $support=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Support Claude Graft on Ko-fi'))
 if ($null -eq $support -or $support.Current.IsOffscreen) { throw 'The Support link is missing.' }
 foreach ($control in @('CloseWindowButton','MinimizeWindowButton','ZoomWindowButton','NewShortcutToolbarButton','ToggleSidebarButton')) {
  $box=(Element $control).Current.BoundingRectangle
  if ((Hit-Test ($box.X+$box.Width/2) ($box.Y+$box.Height/2)) -ne 1) { throw "The title bar swallows clicks on $control." }
 }
 $scale=(Element 'CloseWindowButton').Current.BoundingRectangle.Height/26
 $box=$window.Current.BoundingRectangle
 if ((Hit-Test ($box.X+300*$scale) ($box.Y+25*$scale)) -ne 2) { throw 'The title bar is not draggable.' }
 Invoke-Element (Element 'ZoomWindowButton')
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized } 'The green control did not maximize.'
 Invoke-Element (Element 'ZoomWindowButton')
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Normal } 'The green control did not restore.'
 Invoke-Element (Element 'NewShortcutButton')
 Wait-Until { (Element 'SaveButton').Current.Name -eq 'Create Shortcut' } 'The shortcut form did not open.'
 $iconBox=Element 'IconBox'
 $itemCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
 Wait-Until { $iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition).Count -eq 12 } 'The icon grid does not contain twelve presets.'
 $icons=$iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition)
 Wait-Until { $currentIcons=$iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition); $tops=@($currentIcons | Where-Object { $_.Current.BoundingRectangle.Width -gt 0 } | ForEach-Object { [math]::Round($_.Current.BoundingRectangle.Y) } | Select-Object -Unique); $currentIcons.Count -eq 12 -and $tops.Count -eq 2 } 'The icons do not form two rows of six.'
 $chosen=@($icons | Where-Object { $_.Current.Name -eq $Preset })[0]
 ([System.Windows.Automation.SelectionItemPattern]$chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 Wait-Until { (Element 'IconLabel').Current.Name -eq $Preset } 'The selected icon label did not update.'
 $transform=[System.Windows.Automation.TransformPattern]$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
 $transform.Resize(720*$scale,560*$scale)
 Invoke-Element (Element 'SaveButton')
 $desktop=Join-Path $root 'profiles/Desktop'
 Wait-Until { Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk') } 'The shortcut was not created.'
 ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Work Account')
 Invoke-Element (Element 'SaveButton')
 Wait-Until { (Test-Path -LiteralPath (Join-Path $desktop 'Work Account.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk')) } 'Rename did not retain the new shortcut.'
 $stored=Get-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/shortcuts.json') -Raw | ConvertFrom-Json
 if (@($stored).Count -ne 1 -or $stored[0].name -ne 'Work Account' -or $stored[0].folder -ne 'Claude-2' -or $stored[0].iconPreset -ne $Preset) { throw 'Name, profile or icon changed during the rename.' }
 $id=([guid]$stored[0].id).ToString('N')
 if (-not (Test-Path -LiteralPath (Join-Path $root "profiles/ClaudeGraft/shortcuts/$id/graft.json"))) { throw 'Shortcut configuration is missing.' }
 $shell=New-Object -ComObject WScript.Shell
 $link=$shell.CreateShortcut((Join-Path $desktop 'Work Account.lnk'))
 if (-not (Test-Path -LiteralPath $link.TargetPath) -or $link.Arguments -notlike '*--config*') { throw 'The launcher is missing.' }
 if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $link.TargetPath) 'coreclr.dll'))) { throw 'The launcher runtime is missing.' }
 $iconPath=$link.IconLocation.Substring(0,$link.IconLocation.LastIndexOf(',')).Trim('"')
 if ((Split-Path $iconPath -Leaf) -ne "$Preset.ico") { throw 'The desktop shortcut uses the wrong icon.' }
 $iconBytes=[IO.File]::ReadAllBytes($iconPath)
 if ([BitConverter]::ToUInt16($iconBytes,2) -ne 1 -or [BitConverter]::ToUInt16($iconBytes,4) -ne 7) { throw 'The shortcut icon does not carry all Windows display sizes.' }
 $previewFiles=@(Get-ChildItem -LiteralPath (Split-Path $iconPath) -Filter '*.png')
 if ($previewFiles.Count -ne 12 -or @($previewFiles | Get-FileHash | Select-Object -ExpandProperty Hash -Unique).Count -ne 12) { throw 'The preset previews are missing or duplicate.' }
 $transform.Resize(900*$scale,790*$scale)
 (Element 'NameBox').SetFocus()
 Start-Sleep -Milliseconds 700
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($process.MainWindowHandle,[ref]$rect)
 $bitmap=New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try { [void][GraftUiProbe]::PrintWindow($process.MainWindowHandle,$dc,2) } finally { $graphics.ReleaseHdc($dc) }
 $bitmap.Save((Join-Path $root "$Theme-shortcut.png"),[System.Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $bitmap.Dispose()
 Write-Output "Passed: $Theme theme, window controls, drag region, twelve icons in two rows, icon selection, create, rename and seven icon sizes. Fixtures: $root"
}
finally { if (-not $KeepOpen -and -not $process.HasExited) { Stop-Process -Id $process.Id } }
