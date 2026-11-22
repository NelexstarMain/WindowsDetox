# CI only: captures the installer, every main-window page and the block screen into ./shots, plus short frame sequences of the
# main window's animations (picker, drag, resize, page navigation) into ./shots/anim/<name>/NN.png (Windows PowerShell 5.1).
# Frames are full window size (the DWM frame, 1000 x 660 on CI) at about 15 per second, at most 48 per sequence.
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

# DWM extended frame bounds: the visible window without its invisible resize border and shadow. -Pointer paints the arrow
# cursor into the picture (CopyFromScreen never includes it), so a viewer of a recording can follow the mouse.
function Capture-Window($window, [switch]$Pointer) {
  $f = [Native]::Frame([IntPtr]$window.Current.NativeWindowHandle)
  $bmp = New-Object System.Drawing.Bitmap ($f.Right - $f.Left), ($f.Bottom - $f.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($f.Left, $f.Top, 0, 0, $bmp.Size)
  if ($Pointer) {
    try {
      $c = [System.Windows.Forms.Cursor]::Position
      $box = New-Object System.Drawing.Rectangle ($c.X - $f.Left), ($c.Y - $f.Top), 32, 32
      [System.Windows.Forms.Cursors]::Arrow.Draw($g, $box)
    } catch { }
  }
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
# Snap wherever a frame is wanted. Frames are paced at about 15 per second at full window size and are kept in memory (one
# Bitmap each) while the steps run: encoding a PNG per frame costs more than a frame interval and made earlier recordings
# stall. They are written out, and every bitmap disposed, only after the sequence ends. At most $script:MaxFrames per sequence.
$script:MaxFrames = 48
$script:FrameMs = 66
function Record($window, [string]$name, [scriptblock]$steps) {
  $dir = Join-Path (Join-Path (Resolve-Path shots) 'anim') $name
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  New-Item -ItemType Directory -Force $dir | Out-Null
  [Native]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 300
  $frames = New-Object 'System.Collections.Generic.List[System.Drawing.Bitmap]'
  $script:rec = @{ Window = $window; Frames = $frames; Clock = [System.Diagnostics.Stopwatch]::StartNew(); Next = 0; Dropped = 0 }
  $ms = 0; $dropped = 0
  try { & $steps | Out-Null }
  finally {
    $ms = $script:rec.Clock.ElapsedMilliseconds; $dropped = $script:rec.Dropped
    $script:rec = $null
    $i = 0
    foreach ($b in $frames) {
      try { $i++; $b.Save((Join-Path $dir ('{0:D2}.png' -f $i)), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $b.Dispose() }
    }
    $frames.Clear()
    $fps = 0
    if ($ms -gt 0) { $fps = [Math]::Round($i * 1000.0 / $ms, 1) }
    "recorded $i frames of $name in $ms ms ($fps fps, $dropped over the $($script:MaxFrames) frame cap)"
  }
}

# Takes $count frames, each at least one frame interval after the previous one, so Snap also works as a timed pause.
function Snap([int]$count = 1) {
  for ($i = 0; $i -lt $count; $i++) {
    if ($script:rec.Frames.Count -ge $script:MaxFrames) { $script:rec.Dropped++; continue }
    $wait = $script:rec.Next - $script:rec.Clock.ElapsedMilliseconds
    if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    $script:rec.Next = $script:rec.Clock.ElapsedMilliseconds + $script:FrameMs
    $script:rec.Frames.Add((Capture-Window $script:rec.Window -Pointer))
  }
}

# Moves the pointer to a screen point in $steps eased steps (slow at both ends, like a hand); a frame is taken per step while
# recording, so the viewer sees the pointer travel.
function Move-Smooth([int]$x, [int]$y, [int]$steps = 8) {
  $p = [System.Windows.Forms.Cursor]::Position
  $x0 = $p.X; $y0 = $p.Y
  for ($i = 1; $i -le $steps; $i++) {
    $t = $i / $steps
    $e = $t * $t * (3 - 2 * $t)
    [Native]::Move([int][Math]::Round($x0 + ($x - $x0) * $e), [int][Math]::Round($y0 + ($y - $y0) * $e))
    if ($script:rec) { Snap 1 } else { Start-Sleep -Milliseconds 40 }
  }
}

# Centre of an automation element on screen, as an object with X and Y.
function Get-Center($element) {
  $r = $element.Current.BoundingRectangle
  return [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
}

# True while the widget board is in edit mode (only then do the widgets carry a resize handle).
function Test-Editing($window) { return [bool](Find-Named $window 'Resize widget') }

# Enters or leaves edit mode through the Edit/Done button; it does nothing when the board is already in the wanted state.
function Set-Editing($window, [bool]$on) {
  if ((Test-Editing $window) -ne $on) {
    Click-Named $window 'Edit widgets' -Anywhere | Out-Null
    Start-Sleep -Milliseconds 900
  }
}

# The widgets' resize handles, top to bottom and then left to right (the first one belongs to the first widget of the board).
function Get-Handles($window) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Resize widget')
  $found = @()
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 0) { $found += [pscustomobject]@{ Element = $e; X = $r.X; Y = $r.Y } }
  }
  return @($found | Sort-Object Y, X)
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

# Replaces the text of the text box with this label (UI Automation name) with real key presses; $false when there is no such box.
function Set-Field($window, [string]$label, [string]$text) {
  $field = $null
  $edit = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $edit)) {
    if ($e.Current.Name -eq $label) { $field = $e; break }
  }
  if (-not $field) { Write-Host "could not find the '$label' field"; return $false }
  try { $field.SetFocus() } catch {
    $r = $field.Current.BoundingRectangle
    [Native]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  }
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('^a')
  [System.Windows.Forms.SendKeys]::SendWait($text)
  Start-Sleep -Milliseconds 400
  return $true
}

# Scrolls the page until a text that contains this fragment is fully on screen (at most eight notches).
function Scroll-ToText($window, [string]$fragment) {
  $f = [Native]::Frame([IntPtr]$window.Current.NativeWindowHandle)
  $textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  for ($k = 0; $k -lt 8; $k++) {
    foreach ($e in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
      if ($e.Current.Name -like "*$fragment*" -and $e.Current.BoundingRectangle.Width -gt 0 -and $e.Current.BoundingRectangle.Bottom -lt ($f.Bottom - 8)) { return }
    }
    [Native]::Wheel([int](($f.Left + $f.Right) / 2 + 100), [int](($f.Top + $f.Bottom) / 2), 1)
    Start-Sleep -Milliseconds 500
  }
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
# The Calendar plugin is fully local: its page opens on an empty calendar (with an inviting empty state), so this shot needs no sample data and no network.
# 21-calendar-in-use is taken with the in-use shots at the end.
if (Click-Named $main 'Calendar') { Start-Sleep -Seconds 3; Shot $main '20-calendar' }

# --- Animation recordings (docs/screenshots/anim/<name>/NN.png), driven with real mouse input at a human pace.
# Every clip starts on the Session page with the board out of edit mode and scrolled to the top (the page is not tall enough to
# scroll with two 1 x 1 widgets, so that is the resting state; nothing below scrolls it). Order matters: picker, drag, resize.
$f = [Native]::Frame([IntPtr]$main.Current.NativeWindowHandle)
$cx = [int](($f.Left + $f.Right) / 2 + 100); $cy = [int](($f.Top + $f.Bottom) / 2)

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

# The picker clip has to show a widget being added, so the Goals and Tasks widget must not be on the board yet: leave nothing but
# Focus Insights. Edit mode, the widget's own remove button (named "Remove <title>"), then out of edit mode.
Set-Editing $main $false
if (Find-Named $main 'Open Goals & Tasks') {
  Set-Editing $main $true
  $removed = $false
  if (Find-Named $main 'Remove Goals & Tasks') { $removed = Press-Named $main 'Remove Goals & Tasks' }
  Start-Sleep -Milliseconds 1000
  if (Find-Named $main 'Open Goals & Tasks') { Write-Host 'picker: the Goals & Tasks widget is still on the board after the remove click' }
  else { Write-Host "picker: removed the Goals & Tasks widget from the board ($removed)" }
  Set-Editing $main $false
}
else { Write-Host 'picker: the Goals & Tasks widget was not on the board' }
[Native]::Wheel($cx, $cy, -10)
Start-Sleep -Seconds 1

# Widget picker: click "+", the rail slides in, rest the pointer on the Goals rail item (its flyout fades in), move down to the
# banner "Add Goals & Tasks" and click it: the widget lands on the board. Esc closes the rail.
Record $main 'picker' {
  Snap 1
  $plus = Find-Named $main 'Add widgets'
  if ($plus) {
    $c = Get-Center $plus
    Move-Smooth $c.X $c.Y 5
    Snap 1
    [Native]::Click($c.X, $c.Y)
    Snap 5
    # The rail item: approached from the left at its own height, so the pointer does not cross the other rail items on the way.
    $rail = Find-Named $main 'Widgets from Goals & Tasks'
    if (-not $rail) {
      foreach ($e in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
        if (-not $rail -and $e.Current.Name -like 'Widgets from *' -and $e.Current.BoundingRectangle.Width -gt 0) { $rail = $e }
      }
    }
    if ($rail) {
      $r = Get-Center $rail
      Move-Smooth ($r.X - 70) $r.Y 4
      $r = Get-Center $rail
      Move-Smooth $r.X $r.Y 4
      Snap 4
      $banner = $null
      for ($k = 0; $k -lt 30 -and -not $banner; $k++) {
        $banner = Find-Named $main 'Add Goals & Tasks'
        if (-not $banner) { Start-Sleep -Milliseconds 100 }
      }
      if ($banner) {
        $b = Get-Center $banner
        Move-Smooth $b.X $b.Y 6
        Snap 1
        $b = Get-Center $banner
        [Native]::Click($b.X, $b.Y)
        Snap 9
      } else { Write-Host 'picker: the Add Goals & Tasks banner did not appear' }
    } else { Write-Host 'picker: no rail item found' }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Snap 5
  } else { Write-Host 'picker: no Add widgets button found' }
}
Start-Sleep -Milliseconds 500
if (Find-Named $main 'Close widget picker') { Click-Named $main 'Close widget picker' -Anywhere | Out-Null; Start-Sleep -Seconds 1 }

# Drag: long-press the first widget's header and keep still for 600 ms (WidgetBoardInteraction lifts it after a 450 ms press, and a
# move of more than a few pixels before that cancels the press), then carry it slowly over the second widget, hold while the
# others reflow, drop it. The long press switches the board into edit mode, so the clip ends with the outlines still showing.
Set-Editing $main $false
[Native]::Wheel($cx, $cy, -10)
Start-Sleep -Milliseconds 500
$cards = @(Get-Widgets $main)
if ($cards.Count -ge 2) {
  $sx = [int]($cards[0].X + $cards[0].W / 2); $sy = [int]($cards[0].Y + $cards[0].H / 2)
  # Inside the second widget's body, clear of its header and of the 24 px the edit-mode hint line moves the board down.
  $tx = [int]($cards[1].X + 120); $ty = [int]($cards[1].Y + 60)
  Record $main 'drag' {
    Move-Smooth $sx $sy 5
    Snap 1
    [Native]::Down()
    Snap 9
    # The lift is confirmed by the board entering edit mode; wait a little longer (still without moving) if it has not yet.
    for ($k = 0; $k -lt 6 -and -not (Test-Editing $main); $k++) { Snap 1 }
    Move-Smooth $tx $ty 24
    Snap 4
    [Native]::Up()
    Snap 2
  }
} else { Write-Host 'drag: fewer than two widgets found' }
Set-Editing $main $false
Start-Sleep -Milliseconds 500

# Resize: click Edit (the outlines fade in), pull the first widget's corner right by one column (the widget becomes two columns
# wide and the other one moves down), release, click Done. Resize handles are found by their automation name.
$cards = @(Get-Widgets $main)
$colStep = 372
if ($cards.Count -ge 2) { $colStep = [int][Math]::Abs($cards[1].X - $cards[0].X) }
if ($colStep -lt 100) { $colStep = 372 }
Record $main 'resize' {
  Snap 1
  $edit = Find-Named $main 'Edit widgets'
  if ($edit) {
    $c = Get-Center $edit
    Move-Smooth $c.X $c.Y 4
    [Native]::Click($c.X, $c.Y)
    Snap 4
  }
  $handles = @(Get-Handles $main)
  if ($handles.Count -gt 0) {
    $r = $handles[0].Element.Current.BoundingRectangle
    $hx = [int]($r.X + $r.Width / 2); $hy = [int]($r.Y + $r.Height / 2)
    Move-Smooth $hx $hy 5
    Snap 1
    [Native]::Down()
    Snap 1
    Move-Smooth ($hx + $colStep) $hy 12
    Snap 4
    [Native]::Up()
    Snap 2
    $edit = Find-Named $main 'Edit widgets'
    if ($edit) {
      $c = Get-Center $edit
      Move-Smooth $c.X $c.Y 5
      [Native]::Click($c.X, $c.Y)
      Snap 5
    }
  } else { Write-Host 'resize: no resize handle found' }
}
# Not recorded: put the widget back to one column and leave edit mode, so the shots below see the board as the other clips did.
Start-Sleep -Milliseconds 500
if (Test-Editing $main) {
  Set-Editing $main $false
  Start-Sleep -Milliseconds 500
}
Set-Editing $main $true
$handles = @(Get-Handles $main)
if ($handles.Count -gt 0) {
  $r = $handles[0].Element.Current.BoundingRectangle
  $hx = [int]($r.X + $r.Width / 2); $hy = [int]($r.Y + $r.Height / 2)
  [Native]::Move($hx, $hy); Start-Sleep -Milliseconds 200
  [Native]::Down(); Start-Sleep -Milliseconds 200
  Move-Smooth ($hx - $colStep) $hy 8
  Start-Sleep -Milliseconds 200
  [Native]::Up(); Start-Sleep -Milliseconds 500
}
Set-Editing $main $false
Start-Sleep -Seconds 1

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
if (Click-Named $main 'Calendar') {
  # Entries are made by dragging on the time grid, which UI Automation cannot do, so nothing is seeded: the week (the default view),
  # then the month, each opened with the real view switch.
  Start-Sleep -Seconds 2
  Shot $main '21-calendar-in-use'
  if (Press-Named $main 'Month') {
    Start-Sleep -Seconds 2
    Shot $main '22-calendar-month'
  }
}
if (Click-Named $main 'Session') { Start-Sleep -Seconds 3; Shot $main '19-session-widgets-in-use' }

Stop-Process $ui -Force -ErrorAction SilentlyContinue
Get-Process Blocker.UI -ErrorAction SilentlyContinue | Stop-Process -Force

$stub = Start-Process (Join-Path $InstallDir 'Blocker.Stub.exe') -ArgumentList '--blocked', "`"$env:WINDIR\notepad.exe`"", '--reason', '"Not on your allowlist"' -PassThru
Shot (Wait-Window $stub.Id) '07-block-screen'
Stop-Process $stub -Force -ErrorAction SilentlyContinue
