# Sets a textbox value via UIA ValuePattern, with -CommitViaBlur to fire the app's
# commit-on-blur mirrors (docs/spec-ui-test-harness.md section 4; the spec-back section 9.3
# lesson: SetValue alone does NOT raise Leave, so a gate-config box would keep serving its
# LAST committed value — focus must move off the box for the mirror to update).
#
# Targets are matched by AccessibleName/AutomationId or Name substring across ALL windows of
# the harness-launched PID (the gate boxes live on the separate AutoTradeSettings window).
#
# Usage:
#   powershell -NoProfile -File tools/set-textbox.ps1 txtAtrLength 7 -CommitViaBlur
#   powershell -NoProfile -File tools/set-textbox.ps1 txtAmount 100
#
# Exit codes: 0 = set (and committed if requested) · 1 = app not running ·
#             2 = no textbox matched (all candidates printed) · 3 = refused / set failed.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$NamePattern,
    [Parameter(Mandatory=$true, Position=1)]
    [string]$Value,
    [switch]$CommitViaBlur
)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm -RequireHarnessPid
$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$allNames = New-Object System.Collections.Generic.List[string]
foreach ($w in $windows) {
    foreach ($e in (Find-ByControlType -Element $w -TypeName Edit)) {
        $label = "'$($e.Current.Name)' (id '$($e.Current.AutomationId)', window '$($w.Current.Name)')"
        $allNames.Add($label)
        if (-not (Test-ElementMatch -Element $e -Pattern $NamePattern)) { continue }
        try {
            $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($Value)
            Write-Host "Set $label = '$Value'"
            if ($CommitViaBlur) {
                # Move focus onto the owning window itself: Leave fires on the box and the
                # app's CommitOnLeave handler commits the mirror.
                $e.SetFocus()
                Start-Sleep -Milliseconds 100
                $w.SetFocus()
                Start-Sleep -Milliseconds 150
                Write-Host "Committed via blur (focus handed to '$($w.Current.Name)')"
            } else {
                Write-Host "NOTE: value NOT committed to the app's mirrors until the box loses focus (rerun with -CommitViaBlur for gate-config boxes)."
            }
            exit 0
        } catch {
            Write-Error "Textbox $label rejected SetValue/focus: $_"
            exit 3
        }
    }
}

Write-Error "No textbox matched '$NamePattern'. Available textboxes:"
foreach ($n in $allNames) { Write-Host "  - $n" }
exit 2
