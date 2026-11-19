# CI only: captures the installer, every main-window page and the block screen into ./shots, plus short frame sequences of the
# main window's animations (picker, drag, resize, page navigation) into ./shots/anim/<name>/NN.png (Windows PowerShell 5.1).
# Usage: powershell -File eng/screenshots.ps1 -Setup out\WindowsDetox-Setup.exe -InstallDir "C:\Program Files\WindowsDetox" -Phase installer|app
param([string]$Setup, [string]$InstallDir, [string]$Phase)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Native {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  public static RECT Frame(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, 16); return r; }
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, IntPtr.Zero); mouse_event(4, 0, 0, 0, IntPtr.Zero); }
  public static void Move(int x, int y) { SetCursorPos(x, y); mouse_event(1, 0, 0, 0, IntPtr.Zero); }
  public static void Down() { mouse_event(2, 0, 0, 0, IntPtr.Zero); }
  public static void Up() { mouse_event(4, 0, 0, 0, IntPtr.Zero); }
  public static void Wheel(int x, int y, int notches) { SetCursorPos(x, y); mouse_event(0x0800, 0, 0, -120 * notches, IntPtr.Zero); }
}
"@
[Native]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force shots | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Wait-Window([int]$processId, [int]$seconds = 30) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
  for ($i = 0; $i -lt $seconds * 2; $i++) {
    $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($w -and $w.Current.BoundingRectangle.Width -gt 100) { return $w }
    Start-Sleep -Milliseconds 500
  }
  throw "no window for process $processId"
}

# DWM extended frame bounds: the visible window without its invisible resize border and shadow.
function Capture-Window($window) {
  $f = [Native]::Frame([IntPtr]$window.Current.NativeWindowHandle)
  $bmp = New-Object System.Drawing.Bitmap ($f.Right - $f.Left), ($f.Bottom - $f.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($f.Left, $f.Top, 0, 0, $bmp.Size)
  $g.Dispose()
  return $bmp
}

function Shot($window, [string]$name) {
  [Native]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 1200
  $bmp = Capture-Window $window
  $size = "$($bmp.Width)x$($bmp.Height)"
  $bmp.Save((Join-Path (Resolve-Path shots) "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  "saved $name.png ($size)"
}

# Records a short frame sequence of the window into shots\anim\<name>\NN.png. $steps drives the UI (real mouse input) and calls
# Snap wherever a frame is wanted; frames are paced at about 12 per second, halved in size, and at most 24 per sequence.
function Record($window, [string]$name, [scriptblock]$steps) {
  $dir = Join-Path (Join-Path (Resolve-Path shots) 'anim') $name
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  New-Item -ItemType Directory -Force $dir | Out-Null
  [Native]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 300
  $script:rec = @{ Window = $window; Dir = $dir; Frames = 0; Clock = [System.Diagnostics.Stopwatch]::StartNew(); Next = 0 }
  try { & $steps | Out-Null } finally { "recorded $($script:rec.Frames) frames of $name"; $script:rec = $null }
}

function Snap([int]$count = 1) {
  for ($i = 0; $i -lt $count -and $script:rec.Frames -lt 24; $i++) {
    $wait = $script:rec.Next - $script:rec.Clock.ElapsedMilliseconds
    if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    $script:rec.Next = $script:rec.Clock.ElapsedMilliseconds + 83
    $full = Capture-Window $script:rec.Window
    $small = New-Object System.Drawing.Bitmap $full.Width, $full.Height
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($full, 0, 0, $small.Width, $small.Height)
    $g.Dispose(); $full.Dispose()
    $script:rec.Frames++
    $small.Save((Join-Path $script:rec.Dir ('{0:D2}.png' -f $script:rec.Frames)), [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
  }
}

# The first visible element with this name, or $null.
function Find-Named($window, [string]$text) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    if ($e.Current.BoundingRectangle.Width -gt 0) { return $e }
  }
  return $null
}

# Moves the mouse onto the first plugin item of the widget picker rail ("Widgets from <plugin>"); $false when there is none.
function Move-ToRailItem($window) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $r = $e.Current.BoundingRectangle
    if ($e.Current.Name -like 'Widgets from *' -and $r.Width -gt 0) {
      [Native]::Move([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
      return $true
    }
  }
  Write-Host 'could not find a picker rail item'
  return $false
}

# Screen rectangles of the widget headers ("Open <name>"), in board order.
function Get-Widgets($window) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $found = @()
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $r = $e.Current.BoundingRectangle
    if ($e.Current.Name -like 'Open *' -and $r.Width -gt 0) { $found += [pscustomobject]@{ Name = $e.Current.Name; X = $r.X; Y = $r.Y; W = $r.Width; H = $r.Height } }
  }
  return $found
}

function Click-Named($window, [string]$text, [switch]$Anywhere) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
  $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($e in $all) {
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 0 -and ($Anywhere -or $r.X -lt ($window.Current.BoundingRectangle.X + 300))) {
      [Native]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); return $true
    }
  }
  Write-Host "could not find '$text'"
  return $false
}


# Presses a control without relying on where it sits on screen (a button below the fold of a small window): Invoke or Toggle
# through UI Automation when the control supports it, else a real click.
function Press-Named($window, [string]$text) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $pattern = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return $true }
    if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return $true }
  }
  return (Click-Named $window $text -Anywhere)
}

# Types a task into the Goals page's "New task" field with real key presses and presses Add.
function Add-Task($window, [string]$text) {
  # The text box itself (the label above it has the same name), else whatever carries the name.
  $field = $null
  $edit = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $edit)) {
    if ($e.Current.Name -eq 'New task') { $field = $e; break }
  }
  if (-not $field) { $field = Find-Named $window 'New task' }
  if (-not $field) { Write-Host 'could not find the New task field'; return $false }
  try { $field.SetFocus() } catch {
    $r = $field.Current.BoundingRectangle
    [Native]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  }
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait($text)
  Start-Sleep -Milliseconds 500
  Press-Named $window 'Add' | Out-Null
  Start-Sleep -Milliseconds 1200
  return $true
}

if ($Phase -eq 'installer') {
  $p = Start-Process $Setup -PassThru
  Shot (Wait-Window $p.Id) '01-installer'
  Stop-Process $p -Force
  exit 0
}

# Bundled plugins are compiled and tested by the service after install, one after the other; the marker lists each one
# when it is installed. Wait until every shipped package is listed (or 120 s) before opening the app.
$marker = Join-Path $env:ProgramData 'WindowsDetox\bundled-plugins.txt'
$shipped = @(Get-ChildItem (Join-Path $InstallDir 'bundled-plugins\*.zip') -ErrorAction SilentlyContinue).Count
for ($i = 0; $i -lt 120 -and -not ((Test-Path $marker) -and @(Get-Content $marker).Count -ge $shipped); $i++) { Start-Sleep -Seconds 1 }
"bundled plugins processed: $(Test-Path $marker) ($(if (Test-Path $marker) { @(Get-Content $marker).Count } else { 0 }) of $shipped)"
Select-String -Path (Join-Path $env:ProgramData 'WindowsDetox\logs\*') -Pattern 'Bundled plugin|\[Error\]|\[Critical\]|failed at step' -ErrorAction SilentlyContinue | Select-Object -Last 25 | ForEach-Object { $_.Line }
Get-ChildItem (Join-Path $env:ProgramData 'WindowsDetox.PluginData\host-logs') -ErrorAction SilentlyContinue | Get-Content -Tail 25 | ForEach-Object { "host: $_" }
$crash = Join-Path $env:ProgramData 'WindowsDetox.PluginData\plugin-host-crash.log'
if (Test-Path $crash) { "--- plugin host crash log"; Get-Content $crash | Select-Object -First 30 }
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -like '*PluginHost*' } | Select-Object -First 3 | ForEach-Object { "--- event $($_.Id) $($_.ProviderName)"; $_.Message.Substring(0, [Math]::Min(700, $_.Message.Length)) }
Start-Sleep -Seconds 3
$ui = Start-Process (Join-Path $InstallDir 'Blocker.UI.exe') -PassThru
Start-Sleep -Seconds 6
$main = Wait-Window $ui.Id
# The runner screen is small: put the window fully on screen at a size that fits.
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
[Native]::MoveWindow([IntPtr]$main.Current.NativeWindowHandle, 0, 0, [Math]::Min(1000, $screen.Width), [Math]::Min(660, $screen.Height), $true) | Out-Null
Start-Sleep -Seconds 1
Shot $main '02-session'
$n = 3
foreach ($page in 'Allowlist', 'Session setup', 'Plugins', 'Settings') {
  if (Click-Named $main $page) { Start-Sleep -Seconds 3; Shot $main ('{0:D2}-{1}' -f $n, ($page.ToLower() -replace ' ', '-')) }
  $n++
}
if (Click-Named $main 'Insights') {
  Start-Sleep -Seconds 3
  Shot $main '08-insights-empty'
  if (Click-Named $main 'Show example' -Anywhere) {
    Start-Sleep -Seconds 3
    Shot $main '09-insights'
    $f = [Native]::Frame([IntPtr]$main.Current.NativeWindowHandle)
    foreach ($k in 1..3) {
      [Native]::Wheel([int](($f.Left + $f.Right) / 2 + 100), [int](($f.Top + $f.Bottom) / 2), 5)
      Start-Sleep -Seconds 1
      Shot $main ('{0:D2}-insights-{1}' -f (9 + $k), $k)
    }
  }
}
if (Click-Named $main 'Session') {
  Start-Sleep -Seconds 3; Shot $main '13-session-with-widget'
  if (Click-Named $main 'Add widgets' -Anywhere) {
    # The picker is a side rail of plugins; resting the pointer on the first one opens its flyout of widgets.
    Start-Sleep -Seconds 1
    Move-ToRailItem $main | Out-Null
    Start-Sleep -Milliseconds 600
    Shot $main '14-widget-picker'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1
  }
  if (Click-Named $main 'Edit widgets' -Anywhere) {
    Start-Sleep -Seconds 2; Shot $main '15-widgets-edit'
    # Leave edit mode, then tap the Goals widget's header: the widget opens its plugin page.
    Click-Named $main 'Edit widgets' -Anywhere | Out-Null; Start-Sleep -Seconds 1
    if (Click-Named $main 'Open Goals & Tasks' -Anywhere) { Start-Sleep -Seconds 3; Shot $main '17-widget-opens-plugin' }
  }
}
if (Click-Named $main 'Goals') { Start-Sleep -Seconds 3; Shot $main '16-goals' }

# --- Animation recordings (docs/screenshots/anim/<name>/NN.png), driven with real mouse input.
# Page navigation: Goals -> Insights -> Session.
Record $main 'nav' {
  Snap 1
  Click-Named $main 'Insights'
  Snap 8
  Start-Sleep -Milliseconds 600
  Click-Named $main 'Session'
  Snap 8
}
Start-Sleep -Seconds 2

# Widget picker: the rail slides in, the pointer rests on the first plugin (its flyout fades in), Esc closes everything.
Record $main 'picker' {
  Snap 1
  Click-Named $main 'Add widgets' -Anywhere
  Snap 6
  Move-ToRailItem $main
  Snap 6
  Start-Sleep -Milliseconds 400
  Snap 1
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Snap 6
}
Start-Sleep -Milliseconds 500
if (Find-Named $main 'Close widget picker') { Click-Named $main 'Close widget picker' -Anywhere | Out-Null; Start-Sleep -Seconds 1 }

# Drag: long-press the first widget, carry it over the second one (the others reflow), drop it.
$f = [Native]::Frame([IntPtr]$main.Current.NativeWindowHandle)
[Native]::Wheel([int](($f.Left + $f.Right) / 2 + 100), [int](($f.Top + $f.Bottom) / 2), 10)
Start-Sleep -Seconds 1
$cards = @(Get-Widgets $main)
if ($cards.Count -ge 2) {
  $sx = [int]($cards[0].X + $cards[0].W / 2); $sy = [int]($cards[0].Y + $cards[0].H / 2)
  $tx = [int]($cards[1].X + $cards[1].W / 2); $ty = [int]($cards[1].Y + $cards[1].H / 2 + 50)
  Record $main 'drag' {
    [Native]::Move($sx, $sy)
    Start-Sleep -Milliseconds 150
    Snap 1
    [Native]::Down()
    Start-Sleep -Milliseconds 440
    Snap 3
    for ($i = 1; $i -le 15; $i++) {
      [Native]::Move([int]($sx + ($tx - $sx) * $i / 15), [int]($sy + ($ty - $sy) * $i / 15))
      Snap 1
    }
    [Native]::Up()
    Snap 5
  }
} else { Write-Host 'drag: fewer than two widgets found' }

# Resize: enter edit mode (the outlines fade in), pull the first widget's corner one column wider and a little taller, release.
if (Find-Named $main 'Resize widget') { Click-Named $main 'Edit widgets' -Anywhere | Out-Null; Start-Sleep -Milliseconds 800 }
$cards = @(Get-Widgets $main)
$colStep = 372
if ($cards.Count -ge 2) { $colStep = [int]($cards[1].X - $cards[0].X) }
Record $main 'resize' {
  Snap 1
  Click-Named $main 'Edit widgets' -Anywhere
  Snap 4
  $handle = Find-Named $main 'Resize widget'
  if ($handle) {
    $r = $handle.Current.BoundingRectangle
    $hx = [int]($r.X + $r.Width / 2); $hy = [int]($r.Y + $r.Height / 2)
    [Native]::Move($hx, $hy)
    Start-Sleep -Milliseconds 150
    [Native]::Down()
    Snap 1
    for ($i = 1; $i -le 10; $i++) {
      [Native]::Move([int]($hx + $colStep * $i / 10), [int]($hy + 110 * $i / 10))
      Snap 1
    }
    [Native]::Up()
    Snap 6
  } else { Write-Host 'resize: no resize handle found' }
}
if (Find-Named $main 'Resize widget') { Click-Named $main 'Edit widgets' -Anywhere | Out-Null; Start-Sleep -Seconds 1 }

# --- In-use shots, taken last so nothing above sees the tasks. Goals page: three tasks typed in, the first ticked.
# Then the Session page, whose Goals and Tasks widget now shows them. (Focus Insights stays at 0 min: no session has run.)
if (Click-Named $main 'Goals') {
  Start-Sleep -Seconds 2
  $tasks = 'Draft chapter 3', 'Reply to supervisor', 'Review pull request'
  foreach ($t in $tasks) { Add-Task $main $t | Out-Null }
  Press-Named $main $tasks[0] | Out-Null
  Start-Sleep -Seconds 1
  # The window is small: scroll down until the last task is on screen (at most six notches).
  $f = [Native]::Frame([IntPtr]$main.Current.NativeWindowHandle)
  for ($k = 0; $k -lt 6; $k++) {
    $last = Find-Named $main $tasks[2]
    if ($last -and $last.Current.BoundingRectangle.Bottom -lt ($f.Bottom - 8)) { break }
    [Native]::Wheel([int](($f.Left + $f.Right) / 2 + 100), [int](($f.Top + $f.Bottom) / 2), 1)
    Start-Sleep -Milliseconds 500
  }
  Shot $main '18-goals-in-use'
}
if (Click-Named $main 'Session') { Start-Sleep -Seconds 3; Shot $main '19-session-widgets-in-use' }

Stop-Process $ui -Force -ErrorAction SilentlyContinue
Get-Process Blocker.UI -ErrorAction SilentlyContinue | Stop-Process -Force

$stub = Start-Process (Join-Path $InstallDir 'Blocker.Stub.exe') -ArgumentList '--blocked', "`"$env:WINDIR\notepad.exe`"", '--reason', '"Not on your allowlist"' -PassThru
Shot (Wait-Window $stub.Id) '07-block-screen'
Stop-Process $stub -Force -ErrorAction SilentlyContinue
