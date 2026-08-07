# Selects an item in a combo box: ExpandCollapse first, then SelectionItem on the matching
# ListItem (the engine-table recipe — WinForms combos only materialise their items expanded).
# Operates across all windows of the harness-launched PID.
#
# SAFETY: selecting an item matching LIVE into the bridge-mode combo is refused unless the
# window title carries TESTNET (spec section 6.6 — the owner flips anything touching live
# trading; log-only/off are fine anywhere).
#
# Usage:
#   powershell -NoProfile -File tools/select-combo-item.ps1 cboBridgeMode "Log-only"
#   powershell -NoProfile -File tools/select-combo-item.ps1 cboBridgeMode Off
#
# Exit codes: 0 = selected · 1 = app not running · 2 = combo or item not found (candidates
#             printed) · 3 = refused (Live outside TESTNET / not harness-launched) or failed.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$ComboPattern,
    [Parameter(Mandatory=$true, Position=1)]
    [string]$ItemPattern
)

. "$PSScriptRoot\harness-common.ps1"

$form = Get-MainForm -RequireHarnessPid
$isTestnet = $form.Current.Name.IndexOf("TESTNET", [StringComparison]::OrdinalIgnoreCase) -ge 0

if ($ItemPattern -match '(?i)live' -and -not $isTestnet) {
    Write-Error "REFUSED: item pattern '$ItemPattern' selects a LIVE mode and the window title is '$($form.Current.Name)' — live mode is never script-selected outside TESTNET (spec section 6.6)."
    exit 3
}

$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$m = Wait-ForMatchingElement -Windows $windows -TypeName ComboBox -Pattern $ComboPattern
if ($m.Result.Kind -eq 'Ambiguous') {
    $tied = $m.Result.Tied | ForEach-Object { $m.FullLabels[$_] }
    Write-Error "REFUSED: '$ComboPattern' matches more than one combo at the same tier — refusing to pick arbitrarily. Tied candidates:"
    foreach ($t in $tied) { Write-Host "  - $t" }
    exit 3
}
if ($m.Result.Kind -eq 'None') {
    Write-Error "No combo matched '$ComboPattern'. Available combos:"
    foreach ($n in $m.AllLabels) { Write-Host "  - $n" }
    exit 2
}
$cb = $m.Element
$w = $m.Window
$label = "'$($cb.Current.Name)' (id '$($cb.Current.AutomationId)', window '$($w.Current.Name)')"

try {
    $ec = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    Start-Sleep -Milliseconds 150
} catch {
    Write-Error "Combo $label does not support ExpandCollapsePattern: $_"
    exit 3
}

$items = Find-ByControlType -Element $cb -TypeName ListItem
$itemNames = New-Object System.Collections.Generic.List[string]
foreach ($it in $items) {
    $n = $it.Current.Name
    if ($n) { $itemNames.Add("'$n'") }
    if (-not ($n -and $n.IndexOf($ItemPattern, [StringComparison]::OrdinalIgnoreCase) -ge 0)) { continue }
    try {
        $sel = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        try { $ec.Collapse() } catch {}
        Write-Host "Selected '$n' in combo $label"
        exit 0
    } catch {
        try { $ec.Collapse() } catch {}
        Write-Error "Item '$n' does not support SelectionItemPattern: $_"
        exit 3
    }
}
try { $ec.Collapse() } catch {}
Write-Error "No item matched '$ItemPattern' in combo $label. Items:"
foreach ($n in $itemNames) { Write-Host "  - $n" }
exit 2
