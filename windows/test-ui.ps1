param(
 [string]$Executable = (Join-Path $PSScriptRoot 'dist/ClaudeGraft-x64/ClaudeGraft.exe'),
 [ValidateSet('System','Dark','Light')][string]$Theme = 'System',
 [ValidateSet('Blue','Research')][string]$Preset = 'Blue',
 [switch]$KeepOpen,
 [switch]$CheckAppearance
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('GraftUiProbe' -as [type])) {
 Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GraftUiProbe {
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern int GetWindowStyle(IntPtr h,int index);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr h,bool revert);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect rect);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h,ref Point point);
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
 if ($null -eq $Element) { throw 'The expected control is unavailable.' }
 Wait-Until { $Element.Current.IsEnabled } 'The expected control never became enabled.'
 ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Invoke-Control([string]$Id) {
 Wait-Until { $control=Element $Id; $null -ne $control -and $control.Current.IsEnabled -and -not $control.Current.IsOffscreen } "The $Id control is unavailable."
 Invoke-Element (Element $Id)
}
function Capture-Window {
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($process.MainWindowHandle,[ref]$rect)
 $bitmap=New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try { [void][GraftUiProbe]::PrintWindow($process.MainWindowHandle,$dc,2) }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 return $bitmap
}
function Has-Appearance([string]$Expected) {
 if ($Expected -eq 'System') {
  $preference=Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
  $Expected=if ($preference.AppsUseLightTheme -eq 0) { 'Dark' } else { 'Light' }
 }
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($process.MainWindowHandle,[ref]$rect)
 $client=New-Object GraftUiProbe+Point
 [void][GraftUiProbe]::ClientToScreen($process.MainWindowHandle,[ref]$client)
 $bitmap=Capture-Window
 try {
  $x=[int]($bitmap.Width / 2)
  $caption=$bitmap.GetPixel($x,[int](16*$scale))
  $content=$bitmap.GetPixel($x,[int]($client.Y-$rect.Top+8*$scale))
  $dark=$Expected -eq 'Dark'
  return (($caption.GetBrightness() -lt 0.5) -eq $dark) -and (($content.GetBrightness() -lt 0.5) -eq $dark)
 }
 finally { $bitmap.Dispose() }
}
function Set-Appearance([string]$Choice) {
 Invoke-Control 'SettingsButton'
 Wait-Until { $null -ne (Element 'ThemeBox') } 'Appearance settings did not open.'
 $box=Element 'ThemeBox'
 ([System.Windows.Automation.ExpandCollapsePattern]$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
 $option=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.AndCondition]::new(
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Choice),
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
 if ($null -eq $option) { throw "The $Choice theme option is missing." }
 ([System.Windows.Automation.SelectionItemPattern]$option.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 ([System.Windows.Automation.ExpandCollapsePattern]$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
 Invoke-Control 'PrimaryButton'
 Wait-Until { $null -eq (Element 'ThemeBox') } 'Appearance settings did not close.'
 Wait-Until { Has-Appearance $Choice } "The native caption and app content did not both switch to $Choice."
 $bitmap=Capture-Window
 try { $bitmap.Save((Join-Path $root "$Choice-appearance.png"),[System.Drawing.Imaging.ImageFormat]::Png) }
 finally { $bitmap.Dispose() }
}
try {
 Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'The manager never opened.'
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 Wait-Until { $null -ne (Element 'NameBox') } 'The main profile form never loaded.'
 $support=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Support Claude Graft on Ko-fi'))
 if ($null -eq $support -or $support.Current.IsOffscreen) { throw 'The Support link is missing.' }

 foreach ($oldControl in @('CloseWindowButton','MinimizeWindowButton','ZoomWindowButton')) {
  if ($null -ne (Element $oldControl)) { throw 'Custom caption controls are still present.' }
 }
 $style=[GraftUiProbe]::GetWindowStyle($process.MainWindowHandle,-16)
 foreach ($nativeFlag in @(0x00C00000,0x00080000,0x00040000,0x00020000,0x00010000)) {
  if (($style -band $nativeFlag) -ne $nativeFlag) { throw 'The standard Windows caption, frame or window buttons are missing.' }
 }
 if ([GraftUiProbe]::GetSystemMenu($process.MainWindowHandle,$false) -eq [IntPtr]::Zero) { throw 'The Windows system menu is missing.' }
 $scale=[GraftUiProbe]::GetDpiForWindow($process.MainWindowHandle)/96.0
 Wait-Until { Has-Appearance $Theme } 'The title bar and content do not match the requested appearance.'
 if ($CheckAppearance) {
  foreach ($choice in @('Light','Dark','System')) { Set-Appearance $choice }
  if ($Theme -ne 'System') { Set-Appearance $Theme }
 }
 [void][GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0112,[IntPtr]0xF030,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized } 'The Windows maximize command did not maximize.'
 [void][GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0112,[IntPtr]0xF120,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Normal } 'The Windows restore command did not restore.'
 [void][GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0112,[IntPtr]0xF020,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Minimized } 'The Windows minimize command did not minimize.'
 [void][GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0112,[IntPtr]0xF120,[IntPtr]::Zero)
 Wait-Until { (Element 'NewShortcutButton').Current.IsOffscreen -eq $false } 'The window did not restore after minimizing.'
 Invoke-Control 'NewShortcutToolbarButton'
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
 Invoke-Control 'SaveButton'
 $desktop=Join-Path $root 'profiles/Desktop'
 Wait-Until { Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk') } 'The shortcut was not created.'
 ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Work Account')
 Invoke-Control 'SaveButton'
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
 $transform.Resize(500*$scale,700*$scale)
 Wait-Until { (Element 'NameBox').Current.BoundingRectangle.X - $window.Current.BoundingRectangle.X -lt 180*$scale } 'The narrow window still reserves space for the sidebar.'
 $iconBounds=(Element 'IconBox').Current.BoundingRectangle
 $windowBounds=$window.Current.BoundingRectangle
 if ($iconBounds.Right -gt $windowBounds.Right -or $iconBounds.Left -lt $windowBounds.Left) { throw 'The icon picker is clipped in a narrow window.' }
 foreach ($action in @('DeleteButton','StartButton','OpenButton','SaveButton')) {
  $bounds=(Element $action).Current.BoundingRectangle
  if ($bounds.Right -gt $windowBounds.Right -or $bounds.Left -lt $windowBounds.Left) { throw "The $action action is clipped in a narrow window." }
 }
 $bitmap=Capture-Window
 try { $bitmap.Save((Join-Path $root "$Theme-narrow.png"),[System.Drawing.Imaging.ImageFormat]::Png) }
 finally { $bitmap.Dispose() }
 Invoke-Control 'ToggleSidebarButton'
 Wait-Until { -not (Element 'NewShortcutButton').Current.IsOffscreen } 'The narrow sidebar did not open.'
 Invoke-Control 'MainButton'
 Wait-Until { ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'Claude' } 'Choosing a profile in the overlay did not open its details.'
 $transform.Resize(900*$scale,790*$scale)
 Wait-Until { -not (Element 'NewShortcutButton').Current.IsOffscreen } 'The wide sidebar was not restored.'
 $savedRow=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.AndCondition]::new($itemCondition,
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Work Account')))
 ([System.Windows.Automation.SelectionItemPattern]$savedRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
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

 $originalHandle=$process.MainWindowHandle
 [void][GraftUiProbe]::SendMessage($process.MainWindowHandle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
 Wait-Until { -not [GraftUiProbe]::IsWindow($originalHandle) } 'Close hid the manager instead of closing its window.'
 $reopen=Start-Process -FilePath $Executable -ArgumentList '--test-data-root', ('"' + $root + '"'), '--show' -WindowStyle Hidden -PassThru
 Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'The tray app did not create a new manager window.'
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
 Wait-Until { $null -ne (Element 'NameBox') } 'The reopened manager did not load.'
 $restoredShortcut=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.AndCondition]::new($itemCondition,
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Work Account')))
 if ($null -eq $restoredShortcut) { throw 'The saved shortcut disappeared after closing and reopening.' }
 $reopenedHandle=$process.MainWindowHandle
 Wait-Until { Has-Appearance $Theme } 'The reopened window lost the requested appearance.'
 [void][GraftUiProbe]::SendMessage($reopenedHandle,0x0112,[IntPtr]0xF060,[IntPtr]::Zero)
 Wait-Until { -not [GraftUiProbe]::IsWindow($reopenedHandle) } 'The standard close system command was intercepted.'
 Write-Output "Passed: $Theme content and native title bar, narrow/wide layout, caption/frame/system menu, minimize/maximize/restore/close/reopen, twelve icons, create, rename and icon sizes. Fixtures: $root"
}
finally { if (-not $KeepOpen -and -not $process.HasExited) { Stop-Process -Id $process.Id } }
