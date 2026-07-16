# Locates the running order app's main form by the frozen title prefix and prints its title,
# PID and bounding rect. The harness's "is it up / which world is it in" probe.
#
# Usage:
#   powershell -NoProfile -File tools/find-mainform.ps1
#   powershell -NoProfile -File tools/find-mainform.ps1 -RequireTestnet
#
# Exit codes: 0 = found (and TESTNET confirmed if required) · 1 = app not running ·
#             3 = -RequireTestnet and the title does not carry TESTNET (kill rule).

param([switch]$RequireTestnet)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm -RequireTestnet:$RequireTestnet
$r = $form.Current.BoundingRectangle
Write-Host "Title: '$($form.Current.Name)'"
Write-Host "PID:   $($form.Current.ProcessId)"
Write-Host ("Rect:  X={0:F0} Y={1:F0} W={2:F0} H={3:F0}" -f $r.X, $r.Y, $r.Width, $r.Height)
exit 0
