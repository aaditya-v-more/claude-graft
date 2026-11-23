# Read the manager as it stands. Do not scroll an off-screen control into view to find it.
using namespace System.Windows.Automation

param(
    [ValidateSet('Sidebar', 'Appearance', 'Startup', 'Taskbar', 'Updates', 'About')]
    [string] $Page = 'Sidebar',
    [string[]] $ShortcutFolders = @('Claude-Test-A', 'Claude-Test-B')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-Control($root, [string] $id) {
    $condition = [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, $id)
    $root.FindFirst([TreeScope]::Descendants, $condition)
}

$session = (Get-Process -Id $PID).SessionId
$grafts = @(Get-Process -Name ClaudeGraft | Where-Object { $_.SessionId -eq $session })
if ($grafts.Count -ne 1) { throw 'Expected one running Graft in this interactive session.' }
$pidCondition = [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $grafts[0].Id)
$windows = [AutomationElement]::RootElement.FindAll([TreeScope]::Children, $pidCondition)
$manager = $windows | Where-Object { !$_.Current.IsOffscreen -and (Find-Control $_ 'sidebar.settings') } | Select-Object -First 1
if (!$manager) { throw 'Open the Graft manager and leave its sidebar expanded.' }
$frame = $manager.Current.BoundingRectangle

function Read-Visible([string] $id) {
    $control = Find-Control $manager $id
    if (!$control -or $control.Current.IsOffscreen) { throw "$id is missing or off screen." }
    $r = $control.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Height -le 0 -or $r.Top -lt $frame.Top -or $r.Bottom -gt $frame.Bottom) {
        throw "$id has bounds $r outside the manager ($frame)."
    }
    # The switch template has an invisible trailing area. Check the width of the actual labels and links instead.
    if ($Page -eq 'About' -and ($r.Left -lt $frame.Left -or $r.Right -gt $frame.Right)) {
        throw "$id is past the side of the manager ($r)."
    }
    Write-Host "$id : $r"
    $control
}

function Require-NoScroll($control) {
    $walker = [TreeWalker]::RawViewWalker
    for ($p = $walker.GetParent($control); $p -and $p.Current.ProcessId -eq $grafts[0].Id; $p = $walker.GetParent($p)) {
        $pattern = $null
        if ($p.TryGetCurrentPattern([ScrollPattern]::Pattern, [ref]$pattern) -and $pattern.Current.VerticallyScrollable) {
            throw "The area holding $($control.Current.AutomationId) still needs to scroll."
        }
    }
}

$items = @('main', 'artifacts', 'projects', 'search') + $ShortcutFolders + @('newShortcut', 'settings')
foreach ($item in $items) {
    $row = Read-Visible "sidebar.$item"
    if ($item -eq 'newShortcut') { Require-NoScroll $row }
}

if ($Page -ne 'Sidebar') {
    $null = Read-Visible 'settings.section'
    $controls = @{
        Appearance = @('theme', 'mode', 'backdrop')
        Startup = @('startWithWindows', 'startHidden')
        Taskbar = @('taskbarStrip', 'frontIcon', 'frontName', 'chime')
        Updates = @('holdUpdates')   # Manual Claude updates are not offered for Store installs.
        About = @('checkUpdates', 'helpHeading', 'helpDescription', 'help', 'whatsNew', 'reportProblem', 'email', 'windowsPort')
    }
    foreach ($name in $controls[$Page]) { $last = Read-Visible "settings.$name" }
    Require-NoScroll $last
}
Write-Output "$Page fits the manager ($frame)."
