#requires -Version 5.1
<#
  tools/checks/doc-reranker.ps1 -- the Jev doc re-ranker: "where was this decided / defined?"
  Ranks this repo's doc sections against one free-text query. THE CONTEXT SAVER: run it
  BEFORE grepping or reading docs for a "where" question, then read only the top hits.

  PORTED 2026-10-06 from C:/Dev/DeribitVerdictEngine, tools/checks/doc-reranker.ps1 at engine
  f3455d0 (harness 5 of the engine's Jev programme). Owner ruling 2026-10-06: port it here
  rather than reach into the read-only engine repo. LIVE -Query MODE ONLY. The engine's
  -AcceptanceRun, -Subset reserved and -NoAnswerOnly modes measure the tool against a
  reserved query set; this repo has none, so they and the spent-query ledger are not ported.
  The -Query path below is the engine's own, unchanged in behaviour.

  ADVISORY ONLY. Never wired into verify-gate.ps1 or the pre-push hook. It returns a ranked
  list; the seat reads the docs.

  THE SPLIT: CODE (Python, lib/doc_sections.py + lib/doc_reranker_shortlist.py) builds a
  BM25 shortlist of K sections from docs/**/*.md plus CLAUDE.md at a PINNED git revision
  (never the working tree -- uncommitted doc edits are invisible to it). JEV judges ONE
  question per (query, section) pair -- "does this section answer the query?" -- plus a
  separate no-answer question over the top 5. CODE sorts by noul; within a 0.01 noul
  bucket it breaks ties newest-commit first. Jev never sees or compares dates.

  SCOPE: the numbered handovers before docs/HANDOVER-6.md are excluded (CLAUDE.md: do not
  read them). See SUPERSEDED in lib/doc_sections.py. Archive docs are ranked and labelled.

  COST: K Jev calls plus one, per query (31 at the default K=30). Sampled ONCE in live mode.

  KEY: $env:TYPESAFE_API_KEY if set, else read from -KeyFile (default: the engine's
  gitignored typesafe.local.env, read in place -- the key is never copied into this repo).

  LOG: each run appends one line (query, top 5, timestamp) to the gitignored
  doc-reranker-query-log.jsonl at this repo's root.

  USAGE:
    powershell -NoProfile -File tools/checks/doc-reranker.ps1 -Query "<question>"
#>
param(
    [string]$Query,
    [string]$Rev = 'HEAD',
    [int]$K = 30,
    [int]$Samples = 1,
    [int]$NoAnswerTopN = 5,
    [string]$LogPath = 'doc-reranker-query-log.jsonl',
    [string]$KeyFile = 'C:\Dev\DeribitVerdictEngine\typesafe.local.env',
    [string]$Python = 'python',
    # TEST SEAM ONLY: forwarded to Invoke-Jev's -TransportOverride. A real run never passes it.
    [scriptblock]$TestTransportOverride = $null
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:PYTHONIOENCODING = 'utf-8'
. (Join-Path $PSScriptRoot 'lib\InvokeJev.ps1')

function Resolve-RepoPath([string]$p) {
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $repo $p)
}

if (-not $Query) {
    Write-Host "EXIT_REASON=NO_MODE"
    Write-Host "Pass -Query '<question>'."
    exit 2
}

$apiKey = $env:TYPESAFE_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey) -and (Test-Path $KeyFile)) {
    foreach ($ln in (Get-Content -Path $KeyFile)) {
        if ($ln -match '^\s*TYPESAFE_API_KEY\s*=\s*(.+?)\s*$') { $apiKey = $Matches[1].Trim('"', "'") }
    }
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Host "EXIT_REASON=API_FAILED"
    Write-Host "No key: TYPESAFE_API_KEY is not set and '$KeyFile' has no TYPESAFE_API_KEY line."
    exit 2
}

$answerCriteria = [ordered]@{
    true  = 'The section states, describes, or directly implies the specific fact, ruling, value, or reason the query asks about -- a reader who reads only this section would learn the answer.'
    false = 'The section is on a related topic but does not state the specific answer, or is unrelated.'
}
$noAnswerCriteria = [ordered]@{
    true  = 'At least one of the listed candidate sections states, describes, or directly implies the specific answer to the query.'
    false = 'None of the listed candidate sections answers the query -- they are on related topics at best, or unrelated.'
}

function Invoke-SectionNoul([string]$key, [string]$q, $cand, [int]$samples) {
    $draws = New-Object System.Collections.Generic.List[object]
    $inTok = [long]0
    for ($i = 0; $i -lt $samples; $i++) {
        $uid = "$($cand.id):$i`:$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $state = [ordered]@{
            query = $q; doc_path = $cand.path; heading_chain = $cand.heading_chain
            section_text = $cand.text; is_archive = [bool]$cand.is_archive; sample_uid = $uid
        }
        # Noul, not Choice: a yes/no question returns one probability under .noul.
        $qdef = @{ type = 'noul'; criteria = $answerCriteria
                   instructions = 'Read the query and this one document section. Does this section answer the query -- does it state the specific fact, ruling, value or reason asked about?' }
        $call = Invoke-Jev $key @{ model = 'jev-latest'; state = $state; questions = @{ answers = $qdef } } 3 1 $TestTransportOverride
        if (-not $call.Ok) { return @{ Ok = $false; Error = $call.Error; WafBlocked = $call.WafBlocked } }
        $noul = [double]$call.Response.answers.answers.noul
        if ($call.Response.usage -and $call.Response.usage.input_tokens) { $inTok += [long]$call.Response.usage.input_tokens }
        $draws.Add($noul)
    }
    $mean = ($draws | Measure-Object -Average).Average
    # .ToArray(), not @($draws): @() over a List[object] throws "Argument types do not match"
    # in PS 5.1 (engine finding, reproduced there).
    return @{ Ok = $true; MeanNoul = $mean; Draws = $draws.ToArray(); InputTokens = $inTok; Calls = $samples }
}

function Get-RerankedOrder($candidates, $meanNouls) {
    # sort by mean noul desc; within a 0.01-wide bucket, break ties by commit date desc.
    $withScore = @(for ($i = 0; $i -lt $candidates.Count; $i++) {
        [PSCustomObject]@{ Cand = $candidates[$i]; Noul = $meanNouls[$i]; Bucket = [math]::Round($meanNouls[$i], 2) }
    })
    @($withScore | Sort-Object -Property @{Expression = 'Bucket'; Descending = $true}, @{Expression = { $_.Cand.commit_date_epoch }; Descending = $true})
}

if ($Samples -lt 1) { $Samples = 1 }
$slFile = [System.IO.Path]::GetTempFileName()
try {
    & $Python (Join-Path $PSScriptRoot 'lib\doc_reranker_shortlist.py') shortlist --rev $Rev --query $Query --k $K --out $slFile | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "EXIT_REASON=SHORTLIST_FAILED"; exit 2 }
    $sl = Get-Content -Raw -Encoding UTF8 -Path $slFile | ConvertFrom-Json
} finally { Remove-Item -Force $slFile -ErrorAction SilentlyContinue }

Write-Host "REV=$($sl.rev7)  DOC_SCOPE=$($sl.doc_scope_count) docs  SECTIONS=$($sl.section_count)  SHORTLIST_K=$($sl.shortlist.Count)"
if ($Samples -eq 1) { Write-Host "SAMPLED_ONCE=true" }

$cands = @($sl.shortlist)
$means = New-Object System.Collections.Generic.List[double]
$jevCalls = 0; $usageIn = [long]0; $sectionsWaf = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($c in $cands) {
    $r = Invoke-SectionNoul $apiKey $Query $c $Samples
    if (-not $r.Ok) {
        # A WAF-blocked section ranks LAST (noul -1) and is COUNTED, never silent.
        if ($r.WafBlocked) { $means.Add(-1.0); $sectionsWaf++; continue }
        Write-Host "EXIT_REASON=API_FAILED"; Write-Host "Jev request failed for $($c.id): $($r.Error)"; exit 2
    }
    $means.Add($r.MeanNoul); $jevCalls += $r.Calls; $usageIn += $r.InputTokens
}
$ordered = @(Get-RerankedOrder $cands $means)
$top5 = @($ordered | Select-Object -First $NoAnswerTopN)

# no-answer Noul over the top N (by the RE-RANKED order, the tool's final say).
$naState = [ordered]@{ query = $Query; sample_uid = [guid]::NewGuid().ToString('N').Substring(0,8) }
$i = 0
foreach ($t in $top5) { $i++; $naState["candidate_$i`_path"] = $t.Cand.path; $naState["candidate_$i`_heading"] = $t.Cand.heading_chain
                        $naState["candidate_$i`_excerpt"] = ($t.Cand.text.Substring(0, [Math]::Min(600, $t.Cand.text.Length))) }
$naQ = @{ type = 'noul'; criteria = $noAnswerCriteria
          instructions = 'Read the query and the listed candidate sections (path, heading, excerpt). Does ANY of them answer the query?' }
$naCall = Invoke-Jev $apiKey @{ model = 'jev-latest'; state = $naState; questions = @{ any_answer = $naQ } } 3 1 $TestTransportOverride
$sw.Stop()
$noAnswerVerdict = 'API_FAILED'
if ($naCall.Ok) {
    $naNoul = [double]$naCall.Response.answers.any_answer.noul
    $noAnswerVerdict = "noul=$([math]::Round($naNoul,3)) ($(if ($naNoul -ge 0.5) {'yes, an answer exists'} else {'no, none answers it'}))"
    $jevCalls++
    if ($naCall.Response.usage.input_tokens) { $usageIn += [long]$naCall.Response.usage.input_tokens }
}

Write-Host "NO_ANSWER_NOUL(any_answer)=$noAnswerVerdict"
Write-Host "JEV_CALLS=$jevCalls  USAGE_INPUT_TOKENS=$usageIn  WALL_TIME_SEC=$([math]::Round($sw.Elapsed.TotalSeconds,2))  SECTIONS_WAF_BLOCKED=$sectionsWaf"
Write-Host (Get-JevModelLine)
Write-Host "TOP $($top5.Count):"
$rank = 0
foreach ($t in $top5) {
    $rank++
    $arch = if ($t.Cand.is_archive) { ' [ARCHIVE]' } else { '' }
    $dt = [DateTimeOffset]::FromUnixTimeSeconds([long]$t.Cand.commit_date_epoch).UtcDateTime.ToString('yyyy-MM-dd')
    Write-Host "  $rank. noul=$([math]::Round($t.Noul,3)) $($t.Cand.path):$($t.Cand.start_line)-$($t.Cand.end_line)$arch  ::  $($t.Cand.heading_chain)  [commit $dt]"
}

$logEntry = [ordered]@{
    ts_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    query = $Query; rev = $sl.rev7; sampled_once = ($Samples -eq 1)
    no_answer_verdict = $noAnswerVerdict; sections_waf_blocked = $sectionsWaf
    jev_model = ((Get-JevModelLine) -replace '^JEV_MODEL ', '')
    top5 = @($top5 | ForEach-Object { @{ path = $_.Cand.path; heading_chain = $_.Cand.heading_chain; noul = $_.Noul } })
}
$logFull = Resolve-RepoPath $LogPath
(ConvertTo-Json -InputObject $logEntry -Depth 6 -Compress) | Add-Content -Encoding UTF8 -Path $logFull
Write-Host "Logged to $LogPath"
exit 0
