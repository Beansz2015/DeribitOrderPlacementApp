#requires -Version 5.1
<#
  tools/checks/decision-bias.ps1 -- the Jev decision-bias tripwire (engine harness 6).

  PORTED 2026-10-06 from C:/Dev/DeribitVerdictEngine, tools/checks/measure/decision-bias/
  run-decision-bias.ps1 at engine f3455d0, on the engine orchestrator's advice. Changes: the
  repo-root path (this file sits two levels down, not four), the key-file fallback (as
  doc-reranker.ps1), and this header. The judging logic and refusals are the engine's, unchanged.
  The population builder and scorer are NOT ported: here the seat hand-writes a 1-8 item
  population. "docs/..." citations below name THIS repo's copy of the protocol where one exists
  (docs/harness-shadow-mode-protocol.md); anything else names an engine doc.

  FIRE IT when a seat makes a NEW recommendation on a decision with options. Label your own read
  first (the baseline), then run. A modal verdict of gives_up_for_economy SENDS THE DECISION TO
  THE OWNER: the recommendation gave up a richer option to save work.

  POPULATION FORMAT (hand-written, UTF-8):
    { "rev": "<full 40-hex HEAD at writing>",
      "items": [ { "id": "<doc path>|<decision label>@<rev7>",
                   "question": "...",
                   "options": [ { "label": "(a)", "text": "..." }, ... ],
                   "recommended": "(a)",
                   "rationale": "the author's stated reasons, including why not the others" } ] }

  Asks Jev ONE question per decision: why did the author not pick the other options? One Choice,
  id `verdict`. It is the only answer code reads. No Noul is asked, so none can be combined.

  Write the population to docs/harness-runs/decision-bias-<stamp>-population.json.
  The state sent to Jev is ONLY {decision: {question, options[{label,text}], recommended, rationale}, uid}.
  The id, doc path, revision and provenance are never sent.

  REFUSALS -- each exits 2 BEFORE any API call (docs/harness-shadow-mode-protocol.md section 2 step 1:
  structural, not a convention):
    BASELINE_MISSING      the -Baseline file does not exist
    BASELINE_REV_MISMATCH the baseline's recorded rev is not the population's rev
    BASELINE_INCOMPLETE   a population id has no seat label in the baseline
    BASELINE_INVALID      a seat label is outside the vocabulary
    API_KEY_MISSING       TYPESAFE_API_KEY is not set (checked last, after every baseline gate)
  A population file that is missing or has zero items is also exit 2: a zero count is an error,
  never a pass (protocol section 5).

  The candidate list (ids only, no judgments) is printed FIRST, before any gate, so the seat can
  write its baseline from it.

  Baseline format (JSON):  { "rev": "<the population rev, 7+ hex chars>",
                             "labels": { "<id>": "<label>", ... } }
  label is one of: gives_up_for_economy, richer_option_wrong, no_richer_option, ambiguous, unsure.
  'unsure' rows are excluded from the seat-agreement score by the scorer, never counted either way.

  USAGE (bash):
    powershell -NoProfile -File tools/checks/decision-bias.ps1 `
      -Population docs/harness-runs/decision-bias-<stamp>-population.json `
      -Baseline   docs/harness-runs/decision-bias-<stamp>-baseline.json `
      -OutPath    docs/harness-runs/decision-bias-<stamp>-jev.json [-Samples 5] [-CountersOnly]

  EXIT: 0 = every item judged (or WAF-blocked, recorded) and the output written; 2 = a refusal or
  an API failure. The exit code carries no verdict: nothing in this programme holds a verdict.
#>
[CmdletBinding()]
param(
    [string]$Population = '',
    [string]$Baseline = '',
    [string]$OutPath = '',
    [int]$Samples = 5,
    [string]$KeyFile = 'C:\Dev\DeribitVerdictEngine\typesafe.local.env',
    # Withholds every per-item verdict from the console (the output file still holds them).
    [switch]$CountersOnly,
    # Prints each call's state KEY NAMES and uid only -- never content, never the API key.
    [switch]$DebugState,
    # TEST SEAM ONLY (added 2026-09-24 UTC by the second reader, the pattern the other four
    # harnesses already carry): forwarded to Invoke-Jev's -TransportOverride, so the WAF and
    # retry paths can be exercised with synthetic answers and zero spend. A real run never
    # passes it.
    [scriptblock]$TestTransportOverride = $null
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'tools\checks\lib\InvokeJev.ps1')

$VOCAB = @('gives_up_for_economy', 'richer_option_wrong', 'no_richer_option', 'ambiguous', 'unsure')
$QUESTION_INSTRUCTIONS = 'The state holds one design decision: its options, the option the author recommended, and the author''s stated reasons. Classify why the author did not pick the other options.'
$CRITERIA = [ordered]@{
    gives_up_for_economy = 'Another option records more information, guarantees more, or is more self-describing than the recommended option, and the author''s reason for not picking it is that it costs more work or code, or that the recommended option is adequate or good enough, or that the cost can be deferred until later.'
    richer_option_wrong  = 'Another option records more information or guarantees more, and the author''s reason for not picking it is that it would be incorrect, meaningless or uninterpretable, or is forbidden by an earlier ruling.'
    no_richer_option     = 'No other option records more information, guarantees more, or is more self-describing than the recommended option.'
    ambiguous            = 'The stated reasons do not make clear which of the other three applies.'
}

function Resolve-RepoPath([string]$p) {
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $repo $p)
}
function Read-Json([string]$path) {
    # PowerShell 5.1 reads a BOM-less file as ANSI unless told; these files are UTF-8.
    return (Get-Content -Raw -Encoding UTF8 -Path $path | ConvertFrom-Json)
}

if ($Samples -lt 1) { "EXIT_REASON=BAD_SAMPLES"; exit 2 }
if ([string]::IsNullOrWhiteSpace($Population)) { "EXIT_REASON=POPULATION_MISSING"; "Pass -Population <file>."; exit 2 }
$popFull = Resolve-RepoPath $Population
if (-not (Test-Path $popFull)) { "EXIT_REASON=POPULATION_MISSING"; "No population file at '$Population'."; exit 2 }
$pop = Read-Json $popFull
$items = @($pop.items)
if ($items.Count -eq 0) { "EXIT_REASON=POPULATION_EMPTY"; "Zero items is an error, not a pass."; exit 2 }
$popRev = [string]$pop.rev

# ---- Step 1: the candidate list, ids only, no judgments --------------------------------------
"POPULATION_REV=$popRev"
"CANDIDATES=$($items.Count) (ids only, no judgments, for baseline labelling):"
foreach ($it in $items) { "  $($it.id)" }

# ---- Step 2: the baseline gates. No API call can happen above or inside this block. ---------
if ([string]::IsNullOrWhiteSpace($Baseline) -or -not (Test-Path (Resolve-RepoPath $Baseline))) {
    "EXIT_REASON=BASELINE_MISSING"
    "No baseline file at '$Baseline'. The SEAT writes its own read of every id above BEFORE this runs, as { ""rev"": ""$($popRev.Substring(0,7))"", ""labels"": { ""<id>"": ""<label>"" } }, label one of: $($VOCAB -join ', '). An implementer never writes it (docs/harness-shadow-mode-protocol.md section 4c)."
    exit 2
}
$bl = Read-Json (Resolve-RepoPath $Baseline)
$blRev = [string]$bl.rev
if ([string]::IsNullOrWhiteSpace($blRev) -or $blRev.Length -lt 7 -or -not $popRev.StartsWith($blRev, [System.StringComparison]::OrdinalIgnoreCase)) {
    "EXIT_REASON=BASELINE_REV_MISMATCH"
    "Baseline rev '$blRev' is not the population rev '$popRev'. A baseline labels one population at one revision; re-label rather than reuse."
    exit 2
}
$labels = @{}
if ($null -ne $bl.labels) { foreach ($p in $bl.labels.PSObject.Properties) { $labels[$p.Name] = [string]$p.Value } }
$missing = New-Object System.Collections.Generic.List[string]
foreach ($it in $items) { if (-not $labels.ContainsKey([string]$it.id)) { [void]$missing.Add([string]$it.id) } }
if ($missing.Count -gt 0) {
    "EXIT_REASON=BASELINE_INCOMPLETE"
    "UNBASELINED_ITEMS=$($missing.Count) -- no seat label for:"
    foreach ($m in $missing) { "  $m" }
    "Nothing was judged. An unbaselined item judged once is spent for ever (protocol section 5)."
    exit 2
}
$bad = @($labels.GetEnumerator() | Where-Object { $VOCAB -notcontains $_.Value })
if ($bad.Count -gt 0) {
    "EXIT_REASON=BASELINE_INVALID"
    foreach ($b in $bad) { "  $($b.Key) -> '$($b.Value)'" }
    "Labels must be one of: $($VOCAB -join ', ')."
    exit 2
}
$apiKey = $env:TYPESAFE_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey) -and (Test-Path $KeyFile)) {
    foreach ($ln in (Get-Content -Path $KeyFile)) {
        if ($ln -match '^\s*TYPESAFE_API_KEY\s*=\s*(.+?)\s*$') { $apiKey = $Matches[1].Trim('"', "'") }
    }
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    "EXIT_REASON=API_KEY_MISSING"
    "No key: TYPESAFE_API_KEY is not set and '$KeyFile' has no TYPESAFE_API_KEY line."
    exit 2
}
"BASELINE_GATES=PASSED labels=$($labels.Count)"

# ---- Step 3: judge -------------------------------------------------------------------------
function New-DecisionState($it, [string]$uid) {
    $opts = @()
    foreach ($o in @($it.options)) { $opts += [ordered]@{ label = [string]$o.label; text = [string]$o.text } }
    $decision = [ordered]@{
        question    = [string]$it.question
        options     = $opts
        recommended = [string]$it.recommended
        rationale   = [string]$it.rationale
    }
    return [ordered]@{ decision = $decision; uid = $uid }
}

function Invoke-Verdict([string]$key, $it) {
    $uid = [guid]::NewGuid().ToString()
    $state = New-DecisionState $it $uid
    if ($DebugState) { Write-Host "STATE_DEBUG keys=[$($state.Keys -join ',')] decision_keys=[$($state.decision.Keys -join ',')] uid=$uid" }
    $body = @{
        model     = 'jev-latest'
        state     = $state
        questions = @{
            verdict = @{
                type         = 'choice'
                instructions = $QUESTION_INSTRUCTIONS
                criteria     = $CRITERIA
            }
        }
    }
    $call = Invoke-Jev $key $body 3 1 $TestTransportOverride
    if (-not $call.Ok) { return @{ Ok = $false; Error = $call.Error; WafBlocked = $call.WafBlocked } }
    $ans = $call.Response.answers
    $probs = @{}
    $top = $null
    if ($ans.verdict.probabilities) {
        foreach ($p in $ans.verdict.probabilities.PSObject.Properties) { $probs[$p.Name] = [double]$p.Value }
        if ($probs.Count -gt 0) { $top = ($probs.Values | Measure-Object -Maximum).Maximum }
    }
    $usageIn = 0
    if ($call.Response.usage -and $call.Response.usage.input_tokens) { $usageIn = [int]$call.Response.usage.input_tokens }
    return @{ Ok = $true; Verdict = [string]$ans.verdict.choice; TopProbability = $top; UsageIn = $usageIn; Uid = $uid }
}

$results = New-Object System.Collections.Generic.List[object]
$totalIn = 0
$calls = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($it in $items) {
    $sampleResults = New-Object System.Collections.Generic.List[object]
    $blocked = $false
    for ($i = 0; $i -lt $Samples; $i++) {
        $r = Invoke-Verdict $apiKey $it
        $calls++
        if (-not $r.Ok) {
            if ($r.WafBlocked) { $blocked = $true; break }
            $sw.Stop()
            "EXIT_REASON=API_FAILED"
            "Jev request failed on $($it.id) sample $($i + 1)/$($Samples): $($r.Error)"
            "CALLS=$calls USAGE_INPUT_TOKENS=$totalIn WALL_TIME_SEC=$([math]::Round($sw.Elapsed.TotalSeconds, 2))"
            exit 2
        }
        $totalIn += $r.UsageIn
        $sampleResults.Add($r)
    }
    if ($blocked) {
        # A loop, not Measure-Object -Property: PS 5.1's Measure-Object cannot see hashtable
        # keys, so a block on sample 2+ threw under ErrorActionPreference=Stop and aborted the
        # whole run (second-reader finding, 2026-09-24 UTC).
        $blockedIn = 0; foreach ($s in $sampleResults) { $blockedIn += $s.UsageIn }
        $results.Add([ordered]@{ id = [string]$it.id; verdict = 'WAF_BLOCKED'; agreement_rate = $null; stable = $null
                                 mean_top_probability = $null; sample_verdicts = @(); sample_top_probabilities = @()
                                 usage_input_tokens = $blockedIn; seat_label = $labels[[string]$it.id] })
        continue
    }
    $counts = @{}
    foreach ($s in $sampleResults) { if (-not $counts.ContainsKey($s.Verdict)) { $counts[$s.Verdict] = 0 }; $counts[$s.Verdict]++ }
    # Modal verdict; a tie resolves to the verdict that reached the top count first, so it is reproducible from sample_verdicts.
    $modal = $null; $best = -1
    foreach ($s in $sampleResults) { if ($counts[$s.Verdict] -gt $best) { $best = $counts[$s.Verdict]; $modal = $s.Verdict } }
    $tops = @($sampleResults | Where-Object { $null -ne $_.TopProbability } | ForEach-Object { [double]$_.TopProbability })
    $meanTop = if ($tops.Count -gt 0) { [math]::Round((($tops | Measure-Object -Average).Average), 4) } else { $null }
    $itemIn = 0; foreach ($s in $sampleResults) { $itemIn += $s.UsageIn }
    $results.Add([ordered]@{
        id = [string]$it.id; verdict = $modal
        agreement_rate = [math]::Round($best / $sampleResults.Count, 3); stable = ($best -eq $sampleResults.Count)
        mean_top_probability = $meanTop
        sample_verdicts = @($sampleResults | ForEach-Object { $_.Verdict })
        sample_top_probabilities = @($sampleResults | ForEach-Object { if ($null -ne $_.TopProbability) { [math]::Round([double]$_.TopProbability, 4) } else { $null } })
        usage_input_tokens = $itemIn
        seat_label = $labels[[string]$it.id]
    })
}
$sw.Stop()

# ---- Step 4: report ------------------------------------------------------------------------
"CALLS=$calls"
"USAGE_INPUT_TOKENS=$totalIn"
"COST_USD_AT_0.042_PER_MTOK_INPUT=$([math]::Round($totalIn * 0.042 / 1000000, 6))"
"WALL_TIME_SEC=$([math]::Round($sw.Elapsed.TotalSeconds, 2))"
Get-JevModelLine
"ITEMS_JUDGED=$(@($results | Where-Object { $_.verdict -ne 'WAF_BLOCKED' }).Count) WAF_BLOCKED=$(@($results | Where-Object { $_.verdict -eq 'WAF_BLOCKED' }).Count) UNSTABLE=$(@($results | Where-Object { $_.stable -eq $false }).Count)"
if (-not $CountersOnly) {
    "ID | VERDICT (modal) | AGREEMENT | MEAN_TOP_P | INPUT_TOKENS"
    foreach ($r in $results) { "$($r.id) | $($r.verdict) | $($r.agreement_rate) | $($r.mean_top_probability) | $($r.usage_input_tokens)" }
}
if (-not [string]::IsNullOrWhiteSpace($OutPath)) {
    $out = [ordered]@{
        rev = $popRev; population = $Population; baseline = $Baseline; samples = $Samples; model = 'jev-latest'
        jev_model = ((Get-JevModelLine) -replace '^JEV_MODEL ', '')
        question = [ordered]@{ id = 'verdict'; type = 'choice'; instructions = $QUESTION_INSTRUCTIONS; criteria = $CRITERIA }
        calls = $calls; usage_input_tokens = $totalIn; wall_time_sec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        items = $results
    }
    $json = $out | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText((Resolve-RepoPath $OutPath), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
    "WROTE $OutPath"
}
exit 0
