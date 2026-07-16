# FULL-form capture including off-screen regions: writes the output path into the running
# bin's verify\.screenshot-target marker, foregrounds the app (Win11 foreground-steal bypass),
# sends Ctrl+Shift+S, and polls for the PNG. Requires harness.json {"enabled":true} beside the
# exe — otherwise the hotkey handler is not hooked and this times out (exit 2 says so).
#
# The marker directory is derived from the RUNNING process's exe path (not assumed Debug), so
# the script always talks to the bin the app actually loaded from.
#
# Standing rule: screenshots go to git-ignored verify/out/ and are DELETED after use.
#
# Usage:  powershell -NoProfile -File tools/screenshot-full.ps1 [output-path]
#
# Exit codes: 0 = saved · 1 = app not running · 2 = timed out (is harness.json enabled?) ·
#             3 = could not resolve the running bin.

param([string]$OutputPath = "")

. "$PSScriptRoot\harness-common.ps1"
Add-Type -AssemblyName System.Windows.Forms

if (-not $OutputPath) { $OutputPath = Join-Path $script:OutDir "screenshot-full.png" }
$absOut = [System.IO.Path]::GetFullPath($OutputPath)

$form = Get-MainForm

# Resolve the running bin from the window's process (AppContext.BaseDirectory = exe dir).
try {
    $exePath = (Get-Process -Id $form.Current.ProcessId -ErrorAction Stop).Path
    $binDir = Split-Path $exePath -Parent
} catch {
    Write-Error "Could not resolve the running exe's directory: $_"
    exit 3
}

$marker = Join-Path $binDir "verify\.screenshot-target"
$markerDir = Split-Path $marker -Parent
if (-not (Test-Path $markerDir)) { New-Item -ItemType Directory -Path $markerDir -Force | Out-Null }
$absOut | Out-File -FilePath $marker -Encoding utf8 -NoNewline

$outDir = Split-Path $absOut -Parent
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Foreground the app so SendKeys lands on it (bypass copied verbatim from the engine harness).
Set-AppForeground -Hwnd ([IntPtr]$form.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 500

[System.Windows.Forms.SendKeys]::SendWait("^+s")

# Poll for the PNG (the in-app handler takes ~200-500 ms).
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $absOut) {
        $size = (Get-Item $absOut).Length
        if ($size -gt 0) {
            Start-Sleep -Milliseconds 200   # let the Save fully flush
            Write-Host "Saved $absOut ($size bytes) — delete after use (standing rule)."
            exit 0
        }
    }
    Start-Sleep -Milliseconds 100
}
Remove-Item $marker -Force -ErrorAction SilentlyContinue
Write-Error "Timed out waiting for $absOut. Checks: (1) harness.json {""enabled"": true} beside the running exe ($binDir)? (2) another app window (e.g. AutoTradeSettings) holding keyboard focus? — the hotkey is KeyPreview on the MAIN form only; run tools/close-popup.ps1 AutoTradeSettings first."
exit 2
