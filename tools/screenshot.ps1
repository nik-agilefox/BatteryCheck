# Launches the Battery Check window, waits for data and saves a screenshot of it.
# With -HoverX/-HoverY (fractions of window width/height) moves the cursor there first.
param(
    [string]$Arguments = "",
    [int]$WaitSeconds = 15,
    [double]$HoverX = -1,
    [double]$HoverY = -1,
    [int]$HoverDelayMs = 150,
    [string]$Out = "$PSScriptRoot\..\bin\screenshot.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
'@
[Win]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot "..\bin\BatteryCheckGui.exe"
$p = if ($Arguments) { Start-Process $exe -ArgumentList $Arguments -PassThru } else { Start-Process $exe -PassThru }
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
if ($p.HasExited) { throw "Window exited, code $($p.ExitCode)" }

# The process also owns hidden helper windows (tray icon, tooltips): wait for the main one by its title.
for ($i = 0; $i -lt 50 -and $p.MainWindowTitle -notlike "*Battery Check"; $i++) { Start-Sleep -Milliseconds 100; $p.Refresh() }
$h = $p.MainWindowHandle
$r = New-Object Win+RECT
[Win]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T

if ($HoverX -ge 0) {
    # WM_MOUSEMOVE straight to the window: does not move the user's cursor and works when the window is covered.
    $pt = New-Object Win+POINT
    $pt.X = $r.L + [int]($w * $HoverX); $pt.Y = $r.T + [int]($hh * $HoverY)
    [Win]::ScreenToClient($h, [ref]$pt) | Out-Null
    $lp = [IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xFFFF))
    [Win]::PostMessage($h, 0x0200, [IntPtr]::Zero, $lp) | Out-Null
    # Short delay: the real cursor is elsewhere, so Windows soon sends WM_MOUSELEAVE and the tooltip disappears.
    Start-Sleep -Milliseconds $HoverDelayMs
}

$bmp = New-Object Drawing.Bitmap $w, $hh
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save((Resolve-Path -LiteralPath (Split-Path $Out)).Path + "\" + (Split-Path $Out -Leaf), [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Start-Process $exe -ArgumentList "--exit" -Wait   # closing the window only hides it to the tray
$p.WaitForExit(5000) | Out-Null
"Saved: $Out ($w x $hh)"
