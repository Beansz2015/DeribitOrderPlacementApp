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

# UIA quirk (found at the testnet runtime pass): WinForms RichTextBox does NOT surface
# AccessibleName as the UIA Name (a neighbouring-label heuristic wins) and its AutomationId is
# a volatile numeric handle — so match by ControlType.Document instead: txtLogs is the ONLY
# RichTextBox/Document on the main form, which makes this deterministic.
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Document)
$log = $form.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $log) {
    Write-Error "No Document (RichTextBox) element found under the main form — txtLogs missing?"
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
