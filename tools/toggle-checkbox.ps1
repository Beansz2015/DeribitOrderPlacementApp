# Toggles (or explicitly sets) a checkbox via UIA TogglePattern across all windows of the
# harness-launched PID. Reads Current.ToggleState first so -State on/off is idempotent.
#
# SAFETY: checkboxes whose name matches ARM (the SIGNAL BRIDGE dual-arm interlock's local
# toggle) are refused unless the window title carries TESTNET — spec section 6.6: no script
# ever changes ARM state in live mode.
#
# Usage:
#   powershell -NoProfile -File tools/toggle-checkbox.ps1 "ATRSlip"
#   powershell -NoProfile -File tools/toggle-checkbox.ps1 "ARM AUTOTRADE" -State on   # TESTNET only
#
# Exit codes: 0 = toggled/already-in-state · 1 = app not running · 2 = no checkbox matched ·
#             3 = refused (ARM outside TESTNET / not harness-launched) or toggle failed.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$NamePattern,
    [ValidateSet("", "on", "off")]
    [string]$State = ""
)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm -RequireHarnessPid
$isTestnet = $form.Current.Name.IndexOf("TESTNET", [StringComparison]::OrdinalIgnoreCase) -ge 0
$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$m = Wait-ForMatchingElement -Windows $windows -TypeName CheckBox -Pattern $NamePattern
if ($m.Result.Kind -eq 'Ambiguous') {
    $tied = $m.Result.Tied | ForEach-Object { $m.FullLabels[$_] }
    Write-Error "REFUSED: '$NamePattern' matches more than one checkbox at the same tier — refusing to pick arbitrarily. Tied candidates:"
    foreach ($t in $tied) { Write-Host "  - $t" }
    exit 3
}
if ($m.Result.Kind -eq 'None') {
    Write-Error "No checkbox matched '$NamePattern'. Available checkboxes:"
    foreach ($n in $m.AllLabels) { Write-Host "  - $n" }
    exit 2
}
$c = $m.Element
$w = $m.Window
$label = "'$($c.Current.Name)' (id '$($c.Current.AutomationId)', window '$($w.Current.Name)')"

foreach ($candidate in @($c.Current.Name, $c.Current.AutomationId)) {
    if ($candidate -and $candidate -match '(?i)arm' -and -not $isTestnet) {
        Write-Error "REFUSED: checkbox $label is an ARM control and the window title is '$($form.Current.Name)' — ARM state is never script-changed outside TESTNET (spec section 6.6)."
        exit 3
    }
}
try {
    $tp = $c.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $current = $tp.Current.ToggleState
    if ($State -eq "on" -and $current -eq [System.Windows.Automation.ToggleState]::On) {
        Write-Host "$label already ON"; exit 0
    }
    if ($State -eq "off" -and $current -eq [System.Windows.Automation.ToggleState]::Off) {
        Write-Host "$label already OFF"; exit 0
    }
    $tp.Toggle()
    Write-Host "Toggled $label ($current -> $($tp.Current.ToggleState))"
    exit 0
} catch {
    Write-Error "Checkbox $label does not support TogglePattern: $_"
    exit 3
}
