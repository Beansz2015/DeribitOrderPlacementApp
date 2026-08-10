# Shared plumbing for the DeribitOrderPlacementApp UI-automation harness
# (docs/spec-ui-test-harness.md section 4; ported from DeribitVerdictEngine's tools/*.ps1).
# Dot-source from the sibling scripts:  . "$PSScriptRoot\harness-common.ps1"
#
# Conventions carried here (every script):
#   - The window is located by the FROZEN title prefix "Deribit Order Placement App"
#     (load-bearing, set by frmMainPageV2 at Load; the environment suffix — TESTNET / — LIVE
#     is what the script safety tier gates on).
#   - Exit codes: 0 = ok, 1 = app not found, 2 = target not found (all candidates printed
#     first — the self-diagnostic), 3 = refused or action failed.
#   - DRIVE scripts (anything that clicks/types/toggles/closes) operate ONLY on the
#     harness-launched PID's windows (verify/app.pid, written by launch-app.ps1) — never
#     the owner's session. Observation scripts (find/inspect/read/screenshot) may look at
#     any matching window.

Set-StrictMode -Version 2
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:TitlePrefix = "Deribit Order Placement App"
$script:RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:PidFile     = Join-Path $script:RepoRoot "verify\app.pid"
$script:OutDir      = Join-Path $script:RepoRoot "verify\out"

# Returns the AutomationElement of the main form, or $null. Match = title substring on the
# frozen prefix (case-insensitive), root children only.
function Find-MainFormElement {
    $root  = [System.Windows.Automation.AutomationElement]::RootElement
    $forms = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
                           [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($f in $forms) {
        $n = $f.Current.Name
        if ($n -and $n.IndexOf($script:TitlePrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $f
        }
    }
    return $null
}

# Main form or exit 1. -RequireTestnet: exit 3 unless the title carries TESTNET (kill rule:
# cannot confirm the environment => do nothing). -RequireHarnessPid: exit 3 unless the window
# belongs to the PID in verify/app.pid (drive scripts must never drive the owner's session).
function Get-MainForm {
    param(
        [switch]$RequireTestnet,
        [switch]$RequireHarnessPid
    )
    $form = Find-MainFormElement
    if ($null -eq $form) {
        Write-Error "Main form not found (title prefix '$script:TitlePrefix') — app not running?"
        exit 1
    }
    $title = $form.Current.Name
    if ($RequireTestnet -and $title.IndexOf("TESTNET", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Error "REFUSED: window title is '$title' — TESTNET required and not confirmed."
        exit 3
    }
    if ($RequireHarnessPid) {
        $harnessPid = Get-HarnessPid
        if ($null -eq $harnessPid) {
            Write-Error "REFUSED: no harness PID file ($script:PidFile). Drive scripts only operate on a session launched by launch-app.ps1 — never a pre-existing (owner) session."
            exit 3
        }
        if ($form.Current.ProcessId -ne $harnessPid) {
            Write-Error "REFUSED: window PID $($form.Current.ProcessId) is not the harness-launched PID $harnessPid. Not driving a session the harness did not launch."
            exit 3
        }
    }
    return $form
}

# The PID launch-app.ps1 recorded, or $null (missing file / stale PID whose process is gone).
function Get-HarnessPid {
    if (-not (Test-Path $script:PidFile)) { return $null }
    $raw = (Get-Content $script:PidFile -TotalCount 1).Trim()
    $procId = 0
    if (-not [int]::TryParse($raw, [ref]$procId)) { return $null }
    try {
        $null = Get-Process -Id $procId -ErrorAction Stop
        return $procId
    } catch {
        return $null
    }
}

# All top-level windows belonging to a process (the settings window "AutoTradeSettings" is a
# separate top-level window of the same PID — drive scripts search every window they own).
function Get-ProcessWindows {
    param([Parameter(Mandatory=$true)][int]$OwnerPid)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerPid)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

# Descendants of $Element with the given control type ("Button", "Edit", "CheckBox", ...).
function Find-ByControlType {
    param(
        [Parameter(Mandatory=$true)]$Element,
        [Parameter(Mandatory=$true)][string]$TypeName
    )
    $ct = [System.Windows.Automation.ControlType]::$TypeName
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return $Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Pure decision seam (docs/spec-harness-exact-match.md section 2.1) — a candidate-set-aware
# selection so the caller can never silently act on the wrong element among several substring
# matches. $Candidates is an array of @{Name=...; AutomationId=...} (plain data, no UIA object,
# so this is exercisable with no running app).
#
# Precedence, first non-empty tier wins: 1) exact AutomationId (OrdinalIgnoreCase)
# 2) exact Name (OrdinalIgnoreCase) 3) substring on either (the pre-existing rule, preserved as
# the fallback). Within the winning tier: exactly one candidate wins outright; more than one is
# Ambiguous and every tied index is returned — never picked arbitrarily.
function Select-BestMatchIndex {
    param(
        [Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Candidates,
        [Parameter(Mandatory=$true)][string]$Pattern
    )
    function Get-TierIndices([object[]]$Cands, [string]$Tier, [string]$Pat) {
        $idx = New-Object System.Collections.Generic.List[int]
        for ($i = 0; $i -lt $Cands.Count; $i++) {
            $name = $Cands[$i].Name
            $autoId = $Cands[$i].AutomationId
            $hit = $false
            switch ($Tier) {
                'ExactId'   { $hit = ($autoId -and $autoId.Equals($Pat, [StringComparison]::OrdinalIgnoreCase)) }
                'ExactName' { $hit = ($name -and $name.Equals($Pat, [StringComparison]::OrdinalIgnoreCase)) }
                'Substring' {
                    foreach ($v in @($name, $autoId)) {
                        if ($v -and $v.IndexOf($Pat, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $true; break }
                    }
                }
            }
            if ($hit) { $idx.Add($i) }
        }
        # Comma-wrap: an un-wrapped `return $idx` lets PowerShell unroll the List[int] on the
        # pipeline, so a 0- or 1-element result arrives at the caller as $null or a bare int
        # instead of a collection, and .Count silently breaks.
        return ,$idx
    }

    foreach ($tier in @('ExactId', 'ExactName', 'Substring')) {
        $idx = Get-TierIndices $Candidates $tier $Pattern
        if ($idx.Count -eq 0) { continue }
        if ($idx.Count -eq 1) { return @{ Index = $idx[0]; Kind = $tier; Tied = @() } }
        return @{ Index = -1; Kind = 'Ambiguous'; Tied = @($idx) }
    }
    return @{ Index = -1; Kind = 'None'; Tied = @() }
}

# Gathers every $TypeName descendant across all $Windows FIRST, then decides once via
# Select-BestMatchIndex — the structural fix (spec section 2.2). Returns the chosen
# AutomationElement (or $null), the raw decision, and two label lists:
#   AllLabels  - the not-found diagnostic exactly as each call site built it before this change
#                (built via $LabelBuilder, which may omit a candidate the way click-button.ps1's
#                blank-name guard always did)
#   FullLabels - one label per candidate, index-aligned with Result.Tied, always populated —
#                used only to name every tied candidate on an Ambiguous result.
function Select-MatchingElement {
    param(
        [Parameter(Mandatory=$true)]$Windows,
        [Parameter(Mandatory=$true)][string]$TypeName,
        [Parameter(Mandatory=$true)][string]$Pattern,
        [scriptblock]$LabelBuilder = { param($Element, $Window)
            "'$($Element.Current.Name)' (id '$($Element.Current.AutomationId)', window '$($Window.Current.Name)')"
        }
    )
    $elements   = New-Object System.Collections.Generic.List[object]
    $ownerWins  = New-Object System.Collections.Generic.List[object]
    $candidates = New-Object System.Collections.Generic.List[object]
    $allLabels  = New-Object System.Collections.Generic.List[string]
    $fullLabels = New-Object System.Collections.Generic.List[string]
    foreach ($w in $Windows) {
        foreach ($e in (Find-ByControlType -Element $w -TypeName $TypeName)) {
            $elements.Add($e)
            $ownerWins.Add($w)
            $candidates.Add(@{ Name = $e.Current.Name; AutomationId = $e.Current.AutomationId })
            $fullLabels.Add("'$($e.Current.Name)' (id '$($e.Current.AutomationId)', window '$($w.Current.Name)')")
            $label = & $LabelBuilder $e $w
            if ($label) { $allLabels.Add($label) }
        }
    }
    # NOT `@($candidates)`: on this PS 5.1 build, `@()` around a System.Collections.Generic.List
    # throws "Argument types do not match" (reproduces even for a List[object] of plain strings,
    # with Set-StrictMode off, in a fresh process — a genuine PS quirk, not session corruption).
    # Passing the List straight into an [object[]] parameter binds fine, as does .ToArray().
    $result = Select-BestMatchIndex -Candidates $candidates -Pattern $Pattern
    $chosen = $null
    $chosenWin = $null
    if ($result.Index -ge 0) { $chosen = $elements[$result.Index]; $chosenWin = $ownerWins[$result.Index] }
    return @{ Element = $chosen; Window = $chosenWin; Result = $result; AllLabels = $allLabels; FullLabels = $fullLabels }
}

# Bounded poll around Select-MatchingElement (docs/spec-harness-owned-window.md section R8/R7) -
# the settings window is NEVER a root child of the desktop (probe: Query A 0/39,
# impl-report-harness-owned-window-probe.md), so a control search inside it depends on the main
# form's OWN descendant search having resolved, which is a render-latency race, not a missing
# window. This wraps Select-MatchingElement; it does not modify it or Select-BestMatchIndex
# (do-not-touch table).
#
# Polls until Result.Kind is anything other than 'None', or the deadline expires - it does NOT
# wait out an 'Ambiguous' result. An ambiguous match is a real, immediate refusal, and waiting on
# it could let a late-appearing control silently change the answer, so it is returned the instant
# it appears. On timeout the result is still 'None', so every caller's existing not-found handling
# (exit 2, candidates printed) fires completely unchanged - loud, never silent.
#
# 🚨 D1 (docs/spec-harness-owned-window.md section R11.4): the deadline clock starts AFTER the
# first attempt, not before. The first query's cost is fixed process/UIA-client-warmup overhead
# (observed 740-950 ms from a fresh process, impl-report-harness-owned-window-fix.md section 3),
# unrelated to the render-latency race the deadline exists to wait out. Starting the clock before
# attempt 1 meant a slow-but-legitimate first attempt could already exceed the deadline on its
# own, so the loop returned without ever reaching Start-Sleep - the retry path was dead in every
# real (cold-process) caller. Attempt 1 always runs unconditionally; the 500 ms / 25 ms budget
# governs only the retries after it.
function Wait-ForMatchingElement {
    param(
        [Parameter(Mandatory=$true)]$Windows,
        [Parameter(Mandatory=$true)][string]$TypeName,
        [Parameter(Mandatory=$true)][string]$Pattern,
        [scriptblock]$LabelBuilder,
        [int]$DeadlineMs = 500,
        [int]$IntervalMs = 25
    )
    $callParams = @{ Windows = $Windows; TypeName = $TypeName; Pattern = $Pattern }
    if ($LabelBuilder) { $callParams.LabelBuilder = $LabelBuilder }

    $m = Select-MatchingElement @callParams
    if ($m.Result.Kind -ne 'None') { return $m }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $DeadlineMs) {
        Start-Sleep -Milliseconds $IntervalMs
        $m = Select-MatchingElement @callParams
        if ($m.Result.Kind -ne 'None') { return $m }
    }
    return $m
}

# True when two AutomationElements are the SAME control. RuntimeId is the reliable identity -
# AutomationElement instances are not reference-equal across separate queries, so comparing the
# objects (or their Names) gives wrong answers.
function Test-SameElement {
    param($A, $B)
    if ($null -eq $A -or $null -eq $B) { return $false }
    try { $ra = $A.GetRuntimeId(); $rb = $B.GetRuntimeId() } catch { return $false }
    if ($null -eq $ra -or $null -eq $rb -or $ra.Length -ne $rb.Length) { return $false }
    for ($i = 0; $i -lt $ra.Length; $i++) { if ($ra[$i] -ne $rb[$i]) { return $false } }
    return $true
}

# Picks a control in $Window to hand keyboard focus to when blurring $Exclude.
#
# WHY THIS EXISTS: focusing the WINDOW does not blur a child control. A WinForms Form routes
# activation straight back to its last-active child, so "focus the box, then focus the form"
# leaves focus exactly where it started and the Leave event never fires - which is precisely how
# set-textbox used to report a successful commit that never happened (2026-07-21 testnet pass).
# A real blur has to land on a DIFFERENT focusable control.
#
# Edits only, deliberately: focusing a control never activates it (no click, no invoke), but
# restricting the sink to text boxes keeps focus away from the deny-listed trade buttons entirely
# rather than relying on that argument. Every window in this app that has a target Edit has
# others; if none is found the caller must fail loudly rather than fall back to the broken
# focus-the-window behaviour.
function Get-FocusSink {
    param($Window, $Exclude)
    foreach ($c in (Find-ByControlType -Element $Window -TypeName Edit)) {
        if (Test-SameElement -A $c -B $Exclude) { continue }
        if (-not $c.Current.IsKeyboardFocusable) { continue }
        if ($c.Current.IsOffscreen) { continue }
        return $c
    }
    return $null
}

# Win11 blocks SetForegroundWindow under foreground-steal restrictions; attaching to the current
# foreground's input queue lifts the lockout so focus genuinely transfers and SendKeys reaches
# the app instead of the calling terminal. Copied verbatim from the engine harness.
function Set-AppForeground {
    param([Parameter(Mandatory=$true)][IntPtr]$Hwnd)
    if (-not ("WFG" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WFG {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@
    }
    $fgHwnd    = [WFG]::GetForegroundWindow()
    $fgThread  = [WFG]::GetWindowThreadProcessId($fgHwnd, [IntPtr]::Zero)
    $curThread = [WFG]::GetCurrentThreadId()
    [WFG]::AttachThreadInput($curThread, $fgThread, $true)  | Out-Null
    [WFG]::ShowWindow($Hwnd, 9) | Out-Null    # SW_RESTORE — undo any minimize
    [WFG]::BringWindowToTop($Hwnd) | Out-Null
    [WFG]::SetForegroundWindow($Hwnd) | Out-Null
    [WFG]::AttachThreadInput($curThread, $fgThread, $false) | Out-Null
}
