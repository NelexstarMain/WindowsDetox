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

function Click-Named($window, [string]$text) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
  $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($e in $all) {
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 0 -and $r.X -lt ($window.Current.BoundingRectangle.X + 300)) {
      [Native]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); return $true
    }
  }
  "could not find '$text'"; return $false
}


if ($Phase -eq 'installer') {
  $p = Start-Process $Setup -PassThru
  Shot (Wait-Window $p.Id) '01-installer'
  Stop-Process $p -Force
  exit 0
}

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
Stop-Process $ui -Force -ErrorAction SilentlyContinue
Get-Process Blocker.UI -ErrorAction SilentlyContinue | Stop-Process -Force

$stub = Start-Process (Join-Path $InstallDir 'Blocker.Stub.exe') -ArgumentList '--blocked', "`"$env:WINDIR\notepad.exe`"", '--reason', '"Not on your allowlist"' -PassThru
Shot (Wait-Window $stub.Id) '07-block-screen'
Stop-Process $stub -Force -ErrorAction SilentlyContinue
