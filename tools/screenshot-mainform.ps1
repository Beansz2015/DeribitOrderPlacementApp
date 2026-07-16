# Captures the main form's VISIBLE area to a PNG via Win32 PrintWindow (works on
# non-foreground windows; no app cooperation needed). For content clipped off-screen use
# tools/screenshot-full.ps1 (the in-app DrawToBitmap hotkey). Observation-tier.
#
# Standing rule: screenshots go to git-ignored verify/out/ and are DELETED after use.
#
# Usage:  powershell -NoProfile -File tools/screenshot-mainform.ps1 [output-path]
#
# Exit codes: 0 = saved · 1 = app not running · 3 = capture failed.

param([string]$OutputPath = "")

. "$PSScriptRoot\harness-common.ps1"
Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) { $OutputPath = Join-Path $script:OutDir "screenshot.png" }

$form = Get-MainForm
$hwnd = [IntPtr]$form.Current.NativeWindowHandle

if (-not ("WPW" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WPW {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

$rect = New-Object WPW+RECT
if (-not [WPW]::GetWindowRect($hwnd, [ref]$rect)) { Write-Error "GetWindowRect failed"; exit 3 }
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { Write-Error "Invalid window dimensions: ${w}x${h}"; exit 3 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WPW]::PrintWindow($hwnd, $hdc, 0)
$g.ReleaseHdc($hdc); $g.Dispose()

if (-not $ok) { $bmp.Dispose(); Write-Error "PrintWindow returned false"; exit 3 }

$dir = Split-Path $OutputPath -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Saved $OutputPath (${w}x${h}) — delete after use (standing rule)."
exit 0
