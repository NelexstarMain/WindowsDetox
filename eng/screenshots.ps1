# CI only: captures the installer, every main-window page and the block screen into ./shots (Windows PowerShell 5.1).
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

function Shot($window, [string]$name) {
  [Native]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 1200
  # DWM extended frame bounds: the visible window without its invisible resize border and shadow.
  $f = [Native]::Frame([IntPtr]$window.Current.NativeWindowHandle)
  $bmp = New-Object System.Drawing.Bitmap ($f.Right - $f.Left), ($f.Bottom - $f.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($f.Left, $f.Top, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path (Resolve-Path shots) "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  "saved $name.png ($($bmp.Width)x$($bmp.Height))"
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
  if (Click-Named $main 'Add widgets' -Anywhere) { Start-Sleep -Seconds 2; Shot $main '14-widget-picker'; [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 1 }
  if (Click-Named $main 'Edit widgets' -Anywhere) {
    Start-Sleep -Seconds 2; Shot $main '15-widgets-edit'
    # Leave edit mode, then tap the Goals widget's header: the widget opens its plugin page.
    Click-Named $main 'Edit widgets' -Anywhere | Out-Null; Start-Sleep -Seconds 1
    if (Click-Named $main 'Open Goals & Tasks' -Anywhere) { Start-Sleep -Seconds 3; Shot $main '17-widget-opens-plugin' }
  }
}
if (Click-Named $main 'Goals') { Start-Sleep -Seconds 3; Shot $main '16-goals' }
Stop-Process $ui -Force -ErrorAction SilentlyContinue
Get-Process Blocker.UI -ErrorAction SilentlyContinue | Stop-Process -Force

$stub = Start-Process (Join-Path $InstallDir 'Blocker.Stub.exe') -ArgumentList '--blocked', "`"$env:WINDIR\notepad.exe`"", '--reason', '"Not on your allowlist"' -PassThru
Shot (Wait-Window $stub.Id) '07-block-screen'
Stop-Process $stub -Force -ErrorAction SilentlyContinue
