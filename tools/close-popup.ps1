# Closes popup windows (MessageBox, dialogs) matched by title substring, EXCEPT the main form
# itself — the defensive helper that keeps an automated loop from stalling on a dialog.
# Scoped to the harness-launched PID's windows only (drive-tier: never touches the owner's).
#
# Usage:
#   powershell -NoProfile -File tools/close-popup.ps1 "Error"
#   powershell -NoProfile -File tools/close-popup.ps1 "AutoTradeSettings"
#
# Exit codes: 0 = closed N matches (N may be 0 — check stdout) · 1 = app not running ·
#             3 = not harness-launched.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$TitleSubstring
)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm -RequireHarnessPid
$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$closedCount = 0
foreach ($w in $windows) {
    $name = $w.Current.Name
    if (-not $name) { continue }
    # Never close the main form itself (frozen prefix match).
    if ($name.IndexOf($script:TitlePrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
    if ($name.IndexOf($TitleSubstring, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
    try {
        $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $wp.Close()
        Write-Host "Closed: '$name'"
        $closedCount++
    } catch {
        Write-Warning "Could not close '$name': $_"
    }
}

if ($closedCount -eq 0) {
    Write-Host "No windows matched '$TitleSubstring' (nothing to close)"
}
exit 0
