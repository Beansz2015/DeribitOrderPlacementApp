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
    # NOT Mandatory, defaults to "": blanking a box is a legitimate value (the window boxes,
    # where both-blank = unrestricted), and an empty-string argument gets DROPPED by nested
    # shell invocations — a Mandatory parameter then PROMPTS and hangs a non-interactive
    # harness run (found at the testnet pass). Omit the value to blank the box.
    [Parameter(Position=1)]
    [string]$Value = "",
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
            # Blur needs REAL keyboard focus, and Windows only grants focus to the foreground
            # application: with the terminal in front (or the window minimized), UIA SetFocus
            # silently no-ops, Leave never fires, and the app keeps its LAST committed mirror
            # while the box shows the new text (found at the testnet runtime pass). So for
            # -CommitViaBlur, foreground+restore the owning window first (thread-attach bypass).
            if ($CommitViaBlur) {
                Set-AppForeground -Hwnd ([IntPtr]$w.Current.NativeWindowHandle)
                Start-Sleep -Milliseconds 400
                # Focus the target BEFORE writing: it reproduces the human sequence
                # (enter box -> edit -> leave box) and, critically, gives the box a focus to
                # LOSE afterwards. Writing first and focusing later cannot produce a Leave.
                $e.SetFocus()
                Start-Sleep -Milliseconds 120
                $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
                if (-not (Test-SameElement -A $focused -B $e)) {
                    Write-Error "REFUSED: could not put keyboard focus on $label (focus is on '$($focused.Current.Name)'). Nothing was written. Is another window stealing the foreground?"
                    exit 3
                }
            }
            $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($Value)
            Write-Host "Set $label = '$Value'"
            if ($CommitViaBlur) {
                # Blur onto a DIFFERENT control. Focusing the owning window does NOT work: a
                # WinForms Form hands activation straight back to its last-active child, so
                # focus never leaves the box and Leave never fires. That is the bug that made
                # this script report commits it had not performed (2026-07-21 testnet pass).
                $sink = Get-FocusSink -Window $w -Exclude $e
                if ($null -eq $sink) {
                    Write-Error "REFUSED: no other focusable textbox in '$($w.Current.Name)' to blur onto, so the commit cannot be performed OR verified. The value was written to the box but is NOT in force."
                    exit 3
                }
                $sink.SetFocus()
                Start-Sleep -Milliseconds 200
                # VERIFY, do not assume. A harness that reports a commit it did not make sends
                # you hunting for product bugs that do not exist - which is exactly what happened
                # before this check existed.
                $after = [System.Windows.Automation.AutomationElement]::FocusedElement
                if (Test-SameElement -A $after -B $e) {
                    Write-Error "REFUSED: focus did not leave $label, so the app has NOT committed the value. The box shows '$Value' but the mirror still holds the previous one."
                    exit 3
                }
                Write-Host "Committed via blur (focus -> '$($sink.Current.Name)' (id '$($sink.Current.AutomationId)'); verified off the target)"
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
