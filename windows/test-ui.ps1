param(
 [string]$Executable = (Join-Path $PSScriptRoot 'dist/ClaudeGraft-x64/ClaudeGraft.exe'),
 [ValidateSet('System','Dark','Light')][string]$Theme = 'System',
 [ValidateSet('Blue','Research')][string]$Preset = 'Blue',
 [switch]$KeepOpen,
 [switch]$CheckAppearance,
 [switch]$SkipPixelChecks,
 [switch]$CheckLocalRuntime,
 [ValidateSet('usage','artifacts','projects','search')][string[]]$Panes = @('usage','artifacts','projects','search')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('GraftUiProbe' -as [type])) {
 Add-Type @'
using System;
using System.Text;
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
 private delegate bool Visitor(IntPtr h,IntPtr data);
 [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor,IntPtr data);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
 public static IntPtr Manager(int pid) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,_)=>{uint owner;GetWindowThreadProcessId(h,out owner);if(owner!=pid||!IsWindowVisible(h))return true;
   var title=new StringBuilder(160);GetWindowText(h,title,160);Rect r;GetWindowRect(h,out r);
   if(title.ToString()=="Claude Graft"&&r.Right-r.Left>400&&r.Bottom-r.Top>300){found=h;return false;}return true;},IntPtr.Zero);
  return found;
 }
}
'@
}
$root = Join-Path $PSScriptRoot ('dist/ui-test-' + $Theme.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $root 'profiles/ClaudeGraft'),(Join-Path $root 'profiles/Claude-2') | Out-Null
Set-Content -LiteralPath (Join-Path $root '.graft-test-root') -Value 'Disposable UI test'
@{startHidden=$false;theme=$Theme;backdrop='None'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/settings.json')
@{samples=@(@{t=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();u=@{fh=18;sd=42}})} | ConvertTo-Json -Depth 5 |
 Set-Content -LiteralPath (Join-Path $root 'profiles/Claude-2/plan-usage-history.json')
$arguments = @('--test-data-root', ('"' + $root + '"'))
$process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
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
function Named-Element([string]$Name) {
 $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name))
}
function Select-Navigation([string]$Tag) {
 $id='Navigation.'+$Tag
 Wait-Until { $item=Element $id; if ($null -eq $item -and $Tag -eq 'settings') { $item=Element 'SettingsItem' }; $null -ne $item -and -not $item.Current.IsOffscreen } "The $Tag navigation item is unavailable."
 $item=Element $id
 if ($null -eq $item -and $Tag -eq 'settings') { $item=Element 'SettingsItem' }
 $pattern=$null
 if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) {
  ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
 } else { ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
}
function Assert-Visible([string]$Id) {
 Wait-Until { $item=Element $Id; $null -ne $item -and -not $item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Width -gt 0 } "The $Id control is not visible."
 $bounds=(Element $Id).Current.BoundingRectangle
 $frame=$window.Current.BoundingRectangle
 if ($bounds.Left -lt $frame.Left -or $bounds.Right -gt $frame.Right) { throw "The $Id control is horizontally clipped." }
}
function Reveal-Control([string]$Id) {
 Wait-Until {
  $item=Element $Id
  if ($null -eq $item) { return $false }
  if (-not $item.Current.IsOffscreen) { return $true }
  $parent=$item
  for ($depth=0;$depth -lt 12 -and $null -ne $parent;$depth++) {
   $scroll=$null
   if ($parent.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$scroll) -and $scroll.Current.VerticallyScrollable) {
    $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,[System.Windows.Automation.ScrollAmount]::LargeIncrement)
    break
   }
   $parent=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
  }
  return $false
 } "The $Id control could not be scrolled into view."
}
function Save-View([string]$Name) {
 Start-Sleep -Milliseconds 200
 $bitmap=Capture-Window
 try { $bitmap.Save((Join-Path $root ($Theme+'-'+$Name+'.png')),[System.Drawing.Imaging.ImageFormat]::Png) }
 finally { $bitmap.Dispose() }
 $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
  Select-Object -First 500 | ForEach-Object { $r=$_.Current.BoundingRectangle; @{id=$_.Current.AutomationId;name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName;offscreen=$_.Current.IsOffscreen;enabled=$_.Current.IsEnabled;bounds=@($r.X,$r.Y,$r.Width,$r.Height)} } |
  ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root ($Theme+'-'+$Name+'-uia.json'))
}
function Check-SignedOutPanes([string]$Size = 'wide') {
 if ($Panes -contains 'artifacts') {
 Select-Navigation 'artifacts'
 Assert-Visible 'AddWebAccount'
 Wait-Until { $null -ne (Named-Element 'Add an account to see its artifacts. Use Continue with email and the code claude.ai sends you.') } 'The signed-out Artifacts explanation is missing.'
 if ($null -eq (Named-Element 'Filter artifacts by title') -or $null -eq (Named-Element 'Manage groups…')) { throw 'Artifact filtering or group management is missing.' }
 Invoke-Element (Named-Element 'Refresh artifacts')
 Save-View ($Size+'-artifacts')
 }
 if ($Panes -contains 'usage') {
  Select-Navigation 'usage'
  Assert-Visible 'SharedUsageRefresh'
  Assert-Visible 'SharedUsageBorrower'
  Assert-Visible 'SharedUsageStop'
  Invoke-Control 'SharedUsageRefresh'
  Wait-Until { $null -ne (Named-Element 'Conversations appear here after the relay answers a Code request.') } 'The empty shared usage pane did not finish loading.'
  Save-View ($Size+'-usage')
 }
 if ($Panes -contains 'projects') {
 Select-Navigation 'projects'
 Assert-Visible 'projects.filter'
 Assert-Visible 'projects.empty'
 if ((Element 'projects.empty').Current.Name -ne 'Sign in to see projects' -and $null -eq (Named-Element 'Sign in to see projects')) { throw 'Projects does not explain its signed-out state.' }
 if ((Element 'projects.refresh').Current.IsEnabled) { throw 'Projects refresh must wait for an account.' }
 Save-View ($Size+'-projects')
 }
 if ($Panes -contains 'search') {
 Select-Navigation 'search'
 Assert-Visible 'search.query'
 Assert-Visible 'search.empty'
 if ((Element 'search.go').Current.IsEnabled) { throw 'A signed-out search must not be enabled.' }
 ([System.Windows.Automation.ValuePattern](Element 'search.query').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('disposable fixture query')
 if ((Element 'search.go').Current.IsEnabled) { throw 'Typing a query enabled search without a signed-in account.' }
 Save-View ($Size+'-search')
 Invoke-Control 'search.filters'
 foreach ($filter in @('kind','status','place','age','account','project','starred','reset')) {
  Wait-Until { $null -ne (Element ('search.filter.'+$filter)) } "The $filter search filter is missing."
 }
 Reveal-Control 'search.filter.reset'
 Invoke-Control 'search.filter.reset'
 }
 Select-Navigation 'profile:'
 Wait-Until { $null -ne (Element 'NameBox') } 'Returning from the shared panes lost the profile form.'
}
function Check-Settings {
 Select-Navigation 'settings'
 Assert-Visible 'ThemeBox'
 Assert-Visible 'GraftUpdateButton'
 Invoke-Element (Named-Element 'Claude Desktop updates…')
 Wait-Until { $null -ne (Named-Element 'The isolated test manager cannot update Claude Desktop.') } 'The manager Settings button did not reach the isolated Claude update gate.'
 Invoke-Control 'CloseButton'
 Wait-Until { $null -ne (Element 'ThemeBox') } 'Settings did not return after closing the Claude update dialog.'
 Invoke-Control 'CloseButton'
 Wait-Until { $null -eq (Element 'ThemeBox') } 'Cancel did not close Settings.'
}
function Assert-CurrentProfile([string]$Id,[string]$Name) {
 Wait-Until { $title=Element 'PageTitle'; $null -ne $title -and $title.Current.Name -eq $Name } "The saved $Name profile did not update the shell header."
 Wait-Until {
  $item=Element ('Navigation.profile:'+([guid]$Id).ToString())
  $null -ne $item -and ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
 } "The saved $Name profile is not selected in the shell navigation."
}
function Capture-Window {
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($script:managerHandle,[ref]$rect)
 $bitmap=New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try { [void][GraftUiProbe]::PrintWindow($script:managerHandle,$dc,2) }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 return $bitmap
}
function Has-Appearance([string]$Expected) {
 if ($Expected -eq 'System') {
  $preference=Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
  $Expected=if ($preference.AppsUseLightTheme -eq 0) { 'Dark' } else { 'Light' }
 }
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($script:managerHandle,[ref]$rect)
 $client=New-Object GraftUiProbe+Point
 [void][GraftUiProbe]::ClientToScreen($script:managerHandle,[ref]$client)
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
 Select-Navigation 'settings'
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
 if (-not $SkipPixelChecks) { Wait-Until { Has-Appearance $Choice } "The native caption and app content did not both switch to $Choice." }
 Wait-Until { (Get-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/settings.json') -Raw | ConvertFrom-Json).theme -eq $Choice } "The $Choice theme was not saved."
 $bitmap=Capture-Window
 try { $bitmap.Save((Join-Path $root "$Choice-appearance.png"),[System.Drawing.Imaging.ImageFormat]::Png) }
 finally { $bitmap.Dispose() }
 Save-View ('appearance-'+$Choice)
}
try {
 Wait-Until { $script:managerHandle=[GraftUiProbe]::Manager($process.Id); $script:managerHandle -ne 0 } 'The manager never opened.'
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($script:managerHandle)
 Wait-Until { $null -ne (Element 'NameBox') } 'The main profile form never loaded.'
 if ($CheckLocalRuntime) {
  $appDirectory = Split-Path -Parent (Get-Item -LiteralPath $Executable).FullName
  $manifest = Get-Content -LiteralPath (Join-Path $appDirectory 'vc-runtime.json') -Raw | ConvertFrom-Json
  $process.Refresh()
  # Some builds of WinUI use the OS hybrid CRT and load none of the optional
  # VC redistributable DLLs at startup. Check their paths whenever they do.
  $runtimeNames = @('coreclr.dll', 'Microsoft.UI.Xaml.dll') + $manifest.files.name
  $loaded = @($process.Modules | Where-Object { $runtimeNames -contains $_.ModuleName })
  if ($loaded.ModuleName -notcontains 'coreclr.dll' -or $loaded.ModuleName -notcontains 'Microsoft.UI.Xaml.dll') {
   throw 'Could not verify the loaded .NET and WinUI runtimes.'
  }
  foreach ($module in $loaded) {
   if ((Split-Path -Parent $module.FileName) -ne $appDirectory) { throw "Using external runtime: $($module.FileName)" }
  }
  Write-Output ("Loaded app-local runtimes: " + ($loaded.ModuleName -join ', '))
 }
 foreach ($destination in @('usage','artifacts','projects','search','profile:')) { Assert-Visible ('Navigation.'+$destination) }
 Check-SignedOutPanes
 Check-Settings

 foreach ($oldControl in @('CloseWindowButton','MinimizeWindowButton','ZoomWindowButton')) {
  if ($null -ne (Element $oldControl)) { throw 'Custom caption controls are still present.' }
 }
 $style=[GraftUiProbe]::GetWindowStyle($script:managerHandle,-16)
 foreach ($nativeFlag in @(0x00C00000,0x00080000,0x00040000,0x00020000,0x00010000)) {
  if (($style -band $nativeFlag) -ne $nativeFlag) { throw 'The standard Windows caption, frame or window buttons are missing.' }
 }
 if ([GraftUiProbe]::GetSystemMenu($script:managerHandle,$false) -eq [IntPtr]::Zero) { throw 'The Windows system menu is missing.' }
 $scale=[GraftUiProbe]::GetDpiForWindow($script:managerHandle)/96.0
 if (-not $SkipPixelChecks) { Wait-Until { Has-Appearance $Theme } 'The title bar and content do not match the requested appearance.' }
 if ($CheckAppearance) {
  foreach ($choice in @('Light','Dark','System')) { Set-Appearance $choice }
  if ($Theme -ne 'System') { Set-Appearance $Theme }
 }
 [void][GraftUiProbe]::SendMessage($script:managerHandle,0x0112,[IntPtr]0xF030,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized } 'The Windows maximize command did not maximize.'
 [void][GraftUiProbe]::SendMessage($script:managerHandle,0x0112,[IntPtr]0xF120,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Normal } 'The Windows restore command did not restore.'
 [void][GraftUiProbe]::SendMessage($script:managerHandle,0x0112,[IntPtr]0xF020,[IntPtr]::Zero)
 Wait-Until { ([System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Minimized } 'The Windows minimize command did not minimize.'
 [void][GraftUiProbe]::SendMessage($script:managerHandle,0x0112,[IntPtr]0xF120,[IntPtr]::Zero)
 Wait-Until { (Element 'NewShortcutToolbarButton').Current.IsOffscreen -eq $false } 'The window did not restore after minimizing.'
 Invoke-Control 'NewShortcutToolbarButton'
 Wait-Until { (Element 'SaveButton').Current.Name -eq 'Create Shortcut' } 'The shortcut form did not open.'
 if ((Element 'OpenButton').Current.IsEnabled -or (Element 'ProfileSignIn').Current.IsEnabled) { throw 'An unsaved shortcut can open or sign into a profile.' }
 Assert-Visible 'SourceBox'
 $iconBox=Element 'IconBox'
 $itemCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
 Wait-Until { $iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition).Count -eq 12 } 'The icon grid does not contain twelve presets.'
 $icons=$iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition)
 Wait-Until { $currentIcons=$iconBox.FindAll([System.Windows.Automation.TreeScope]::Children,$itemCondition); $tops=@($currentIcons | Where-Object { $_.Current.BoundingRectangle.Width -gt 0 } | ForEach-Object { [math]::Round($_.Current.BoundingRectangle.Y) } | Select-Object -Unique); $currentIcons.Count -eq 12 -and $tops.Count -eq 2 } 'The icons do not form two rows of six.'
 $chosen=@($icons | Where-Object { $_.Current.Name -eq $Preset })[0]
 ([System.Windows.Automation.SelectionItemPattern]$chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 Wait-Until { (Element 'IconLabel').Current.Name -eq $Preset } 'The selected icon label did not update.'
 $transform=[System.Windows.Automation.TransformPattern]$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
 $transform.Resize(820*$scale,620*$scale)
 Invoke-Control 'SaveButton'
 $desktop=Join-Path $root 'profiles/Desktop'
 Wait-Until { Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk') } 'The shortcut was not created.'
 Wait-Until { Test-Path -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/shortcuts.json') } 'The created shortcut was not saved.'
 $created=Get-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/shortcuts.json') -Raw | ConvertFrom-Json
 Assert-CurrentProfile $created[0].id 'Claude 2'
 $shellFolder = (New-Object -ComObject Shell.Application).NameSpace($desktop)
 $originalPinId = $shellFolder.ParseName('Claude 2.lnk').ExtendedProperty('System.AppUserModel.ID')
 ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Work Account')
 Invoke-Control 'SaveButton'
 Wait-Until { (Test-Path -LiteralPath (Join-Path $desktop 'Work Account.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $desktop 'Claude 2.lnk')) } 'Rename did not retain the new shortcut.'
 $stored=Get-Content -LiteralPath (Join-Path $root 'profiles/ClaudeGraft/shortcuts.json') -Raw | ConvertFrom-Json
 if (@($stored).Count -ne 1 -or $stored[0].name -ne 'Work Account' -or $stored[0].folder -ne 'Claude-2' -or $stored[0].iconPreset -ne $Preset) { throw 'Name, profile or icon changed during the rename.' }
 if ($stored[0].source.kind -ne 'own') { throw "Creating a shortcut unexpectedly linked another profile's chats." }
 Assert-CurrentProfile $stored[0].id 'Work Account'
 if (-not (Element 'ProfileSignIn').Current.IsEnabled) { throw 'The saved shortcut cannot start its own sign-in.' }
 $id=([guid]$stored[0].id).ToString('N')
 $renamedPinId = $shellFolder.ParseName('Work Account.lnk').ExtendedProperty('System.AppUserModel.ID')
 if ($originalPinId -cne "ClaudeGraft.Profile.$id" -or $renamedPinId -cne $originalPinId) { throw 'The profile shortcut lost its taskbar pin identity on rename.' }
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
 Wait-Until { $window.Current.BoundingRectangle.Width -ge 810*$scale } 'The manager did not enforce its minimum usable width.'
 $iconBounds=(Element 'IconBox').Current.BoundingRectangle
 $windowBounds=$window.Current.BoundingRectangle
 if ($iconBounds.Right -gt $windowBounds.Right -or $iconBounds.Left -lt $windowBounds.Left) { throw 'The icon picker is clipped in a narrow window.' }
 foreach ($action in @('DeleteButton','StartButton','OpenButton','SaveButton')) {
  $bounds=(Element $action).Current.BoundingRectangle
  if ($bounds.Right -gt $windowBounds.Right -or $bounds.Left -lt $windowBounds.Left) { throw "The $action action is clipped in a narrow window." }
 }
 $bitmap=Capture-Window
 try { $bitmap.Save((Join-Path $root "$Theme-minimum.png"),[System.Drawing.Imaging.ImageFormat]::Png) }
 finally { $bitmap.Dispose() }
 Check-SignedOutPanes 'minimum'
 Select-Navigation 'profile:'
 Wait-Until { ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'Claude' } 'Choosing Claude in the shell navigation did not open its details.'
 $transform.Resize(1000*$scale,790*$scale)
 $profileTag='profile:'+([guid]$stored[0].id).ToString()
 Select-Navigation $profileTag
 Wait-Until { ([System.Windows.Automation.ValuePattern](Element 'NameBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq 'Work Account' } 'Choosing the saved shortcut did not open its details.'
 Start-Sleep -Milliseconds 700
 $rect=New-Object GraftUiProbe+Rect
 [void][GraftUiProbe]::GetWindowRect($script:managerHandle,[ref]$rect)
 $bitmap=New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try { [void][GraftUiProbe]::PrintWindow($script:managerHandle,$dc,2) } finally { $graphics.ReleaseHdc($dc) }
 $bitmap.Save((Join-Path $root "$Theme-shortcut.png"),[System.Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $bitmap.Dispose()

 $originalHandle=$script:managerHandle
 [void][GraftUiProbe]::SendMessage($script:managerHandle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
 Wait-Until { -not [GraftUiProbe]::IsWindow($originalHandle) } 'Close hid the manager instead of closing its window.'
 $reopen=Start-Process -FilePath $Executable -ArgumentList ($arguments + '--show') -WindowStyle Hidden -PassThru
 Wait-Until { $script:managerHandle=[GraftUiProbe]::Manager($process.Id); $script:managerHandle -ne 0 } 'The tray app did not create a new manager window.'
 $window=[System.Windows.Automation.AutomationElement]::FromHandle($script:managerHandle)
 Wait-Until { $null -ne (Element 'NameBox') } 'The reopened manager did not load.'
 $restoredShortcut=Element ('Navigation.'+$profileTag)
 if ($null -eq $restoredShortcut) { throw 'The saved shortcut disappeared after closing and reopening.' }
 Check-SignedOutPanes 'reopened'
 $reopenedHandle=$script:managerHandle
 if (-not $SkipPixelChecks) { Wait-Until { Has-Appearance $Theme } 'The reopened window lost the requested appearance.' }
 Invoke-Control 'HelpButton'
 foreach ($link in @('Using Claude Graft','Source and documentation','Support Claude Graft')) {
  Wait-Until { $entry=Named-Element $link; $null -ne $entry -and -not $entry.Current.IsOffscreen } "The Help menu is missing $link."
 }
 [void][GraftUiProbe]::SendMessage($reopenedHandle,0x0112,[IntPtr]0xF060,[IntPtr]::Zero)
 Wait-Until { -not [GraftUiProbe]::IsWindow($reopenedHandle) } 'The standard close system command was intercepted.'
 $appearanceResult=if (-not $SkipPixelChecks) { "$Theme appearance" } elseif ($CheckAppearance) { 'theme persistence (pixel appearance not checked)' } else { 'pixel appearance not checked' }
 $paneResult=if ($Panes.Count -gt 0) { ($Panes -join '/')+' pane navigation and signed-out gates' } else { 'profile navigation' }
 Write-Output "Passed: $appearanceResult, minimum/wide layout, native frame and system commands, close/reopen, $paneResult, Settings/update handoff, Help links, twelve icons, create, rename and stable pin identity. Fixtures: $root"
}
catch {
 try {
  if ($null -ne $window) {
   $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
    Select-Object -First 500 | ForEach-Object { @{id=$_.Current.AutomationId;name=$_.Current.Name;type=$_.Current.ControlType.ProgrammaticName;offscreen=$_.Current.IsOffscreen} } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'uia-failure.json')
   Save-View 'failure'
  }
 } catch { }
 throw
}
finally { if (-not $KeepOpen -and -not $process.HasExited) { Stop-Process -Id $process.Id } }
