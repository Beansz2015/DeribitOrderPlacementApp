# GENERIC-TIER button click (docs/spec-ui-test-harness.md section 4) — carries the trade-deny
# rule, enforced in code, not convention: any button whose UIA Name or AutomationId matches the
# deny regex or an entry in tools/trade-buttons.txt is REFUSED (exit 3, reason named). Only
# tools/click-PLACES-ORDER.ps1 can click those, and it hard-requires a TESTNET-titled window.
# False-positive refusals are safe by design.
#
# Drive-tier: operates only on the harness-launched PID's windows (main form + settings window).
#
# Usage:
#   powershell -NoProfile -File tools/click-button.ps1 "Auto Settings"
#   powershell -NoProfile -File tools/click-button.ps1 Connect
#
# Exit codes: 0 = clicked · 1 = app not running · 2 = no button matched (all names printed) ·
#             3 = refused by the deny rule / not harness-launched / not invokable.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$NamePattern
)

. "$PSScriptRoot\harness-common.ps1"

# The deny rule (spec section 4 table — the fixed regex) + the maintained list.
$DenyRegex = '(?i)buy|sell|reduce|rdc|cancel|trail|sprd|limit|mkt|edit t|^ts$'
$DenyList  = @()
$denyFile  = Join-Path $PSScriptRoot "trade-buttons.txt"
if (Test-Path $denyFile) {
    $DenyList = @(Get-Content $denyFile | ForEach-Object { $_.Trim() } |
                  Where-Object { $_ -and -not $_.StartsWith('#') })
}

function Get-DenyReason([string]$Name) {
    if ($Name -match $DenyRegex) { return "matches the trade-deny regex" }
    foreach ($entry in $DenyList) {
        if ($Name.IndexOf($entry, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return "matches trade-buttons.txt entry '$entry'"
        }
    }
    return $null
}

$form = Get-MainForm -RequireHarnessPid
$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId

$allNames = New-Object System.Collections.Generic.List[string]
foreach ($w in $windows) {
    foreach ($b in (Find-ByControlType -Element $w -TypeName Button)) {
        $name = $b.Current.Name
        $autoId = $b.Current.AutomationId
        if ($name) { $allNames.Add("'$name' (id '$autoId', window '$($w.Current.Name)')") }
        if (-not (Test-ElementMatch -Element $b -Pattern $NamePattern)) { continue }

        # Deny rule — checked on BOTH the visible caption and the designer id, so a caption
        # change can never sneak a trade button past the tier.
        foreach ($candidate in @($name, $autoId)) {
            if (-not $candidate) { continue }
            $reason = Get-DenyReason $candidate
            if ($reason) {
                Write-Error "REFUSED: button '$name' (id '$autoId') is trade-affecting — $reason. Use tools/click-PLACES-ORDER.ps1 (TESTNET only) if this is intentional."
                exit 3
            }
        }
        try {
            $invoke = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            Write-Host "Clicked: '$name' (window '$($w.Current.Name)')"
            exit 0
        } catch {
            Write-Error "Button '$name' is not invokable: $_"
            exit 3
        }
    }
}

Write-Error "No button matched '$NamePattern'. Available buttons:"
foreach ($n in $allNames) { Write-Host "  - $n" }
exit 2
