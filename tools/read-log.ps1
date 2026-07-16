# Reads the main form's txtLogs (RichTextBox) via UIA TextPattern and writes it to stdout —
# callers assert with Select-String (the spec-back section 9.4/9.5 disposition-token pattern:
#   powershell -NoProfile -File tools/read-log.ps1 | Select-String "refused: window").
# Observation-tier: reads any matching session, drives nothing.
#
# Usage:
#   powershell -NoProfile -File tools/read-log.ps1
#   powershell -NoProfile -File tools/read-log.ps1 -Tail 40
#
# Exit codes: 0 = dumped · 1 = app not running · 2 = txtLogs not found ·
#             3 = TextPattern unavailable.

param([int]$Tail = 0)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm

# txtLogs carries AccessibleName "txtLogs" (commit 2). RichTextBox surfaces as Document or
# Edit depending on the UIA stack — match by name across ALL descendants instead of one type.
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "txtLogs")
$log = $form.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $log) {
    $cond2 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "txtLogs")
    $log = $form.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond2)
}
if ($null -eq $log) {
    Write-Error "txtLogs not found under the main form (AccessibleName/AutomationId 'txtLogs')."
    exit 2
}

try {
    $tp = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $text = $tp.DocumentRange.GetText([int]::MaxValue)
} catch {
    Write-Error "txtLogs does not expose TextPattern: $_"
    exit 3
}

if ($Tail -gt 0) {
    # RichTextBox TextPattern separates lines with bare CR — split on all three conventions.
    $lines = $text -split "`r`n|`r|`n"
    $start = [Math]::Max(0, $lines.Count - $Tail)
    $text = ($lines[$start..($lines.Count - 1)]) -join "`n"
}
Write-Output $text
exit 0
