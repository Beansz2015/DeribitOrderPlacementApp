# Dumps BoundingRectangles for Buttons + CheckBoxes + Labels + Edits across the app's windows —
# the numeric layout check. Buttons/CheckBoxes are included deliberately (the btnAutoSettings
# caption-clip lesson: a WinForms Button silently wraps+clips a too-long caption, and only a
# geometry dump or screenshot catches it — a clean build never will).
#
# Each line: "TYPE Y= X= W= H= | 'Name' (id)". Coordinates are PHYSICAL screen pixels as UIA
# reports them — compare UIA-to-UIA between two dumps, never UIA-to-Designer (DPI scaling).
# Observation-tier: reads any matching session, drives nothing.
#
# Usage:
#   powershell -NoProfile -File tools/inspect-tree.ps1
#   powershell -NoProfile -File tools/inspect-tree.ps1 -Pattern "Auto|ATR"
#
# Exit codes: 0 = dumped (zero or more matches) · 1 = app not running.

param([string]$Pattern = "")

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm
$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$count = 0
foreach ($w in $windows) {
    $wr = $w.Current.BoundingRectangle
    Write-Host ("Window '{0}': X={1:F0} Y={2:F0} W={3:F0} H={4:F0}" -f $w.Current.Name, $wr.X, $wr.Y, $wr.Width, $wr.Height)
    foreach ($typeName in @("Button", "CheckBox", "Text", "Edit")) {   # UIA: Label => ControlType.Text
        foreach ($el in (Find-ByControlType -Element $w -TypeName $typeName)) {
            $n = $el.Current.Name
            $autoId = $el.Current.AutomationId
            $display = if ($n) { $n } elseif ($autoId) { "(id-only) $autoId" } else { continue }
            if ($Pattern -and ($display -notmatch $Pattern) -and ($autoId -notmatch $Pattern)) { continue }
            $r = $el.Current.BoundingRectangle
            Write-Host ("  {0,-8} Y={1,5:F0} X={2,5:F0} W={3,4:F0} H={4,3:F0} | '{5}' ({6})" -f
                $typeName, $r.Y, $r.X, $r.Width, $r.Height, $display, $autoId)
            $count++
        }
    }
}
Write-Host "Found $count element(s) matching pattern '$Pattern'"
exit 0
