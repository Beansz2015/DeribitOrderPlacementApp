#requires -Version 5.1
<#
  tools/checks/lib/InvokeJev.ps1 -- shared TypeSafe/Jev System One HTTP call.

  PORTED 2026-10-06 VERBATIM from C:/Dev/DeribitVerdictEngine, tools/checks/lib/InvokeJev.ps1
  at engine f3455d0. In THIS repo it is dot-sourced only by tools/checks/doc-reranker.ps1.
  Every "docs/..." and other-harness citation below names the ENGINE repo, not this one.

  Engine callers, for the record:
    - tools/checks/rider-travel.ps1   (rider-arrival audit)
    - tools/checks/commit-walker.ps1  ([no-engine-change] tag audit)

  Extracted verbatim out of rider-travel.ps1 on 2026-09-22 per
  docs/commit-walker-check-spec.md section 6 ("Reuse its Invoke-Jev verbatim -- do
  not write a second HTTP call"). Same reasoning as tools/checks/lib/HeaderText.ps1's
  own extraction: a second copy drifts the first time the call shape changes, and
  this repo already rules on that defect class for constants and fixture literals.

  WHY THE MANUAL UTF-8 ENCODING MATTERS, AND IS NOT OPTIONAL: PowerShell 5.1's
  Invoke-RestMethod does NOT UTF-8-encode a plain [string] -Body -- it silently
  mis-encodes anything outside Latin-1, which corrupts the JSON bytes on the wire
  and the API returns 400 Bad Request. This was reproduced against a real rider-travel
  row (docs/rider-travel-check-spec.md): string body -> 400, identical JSON as explicit
  UTF-8 bytes -> 200. Both harnesses' state carries non-ASCII markup (em-dashes, section
  marks, star/prohibited glyphs in the ledger; em-dashes and section marks in commit
  subjects) so this is the median case for both callers, not an edge case.

  Any change here changes the observable HTTP behaviour of BOTH callers. Before it
  ships, re-run rider-travel's own acceptance items 3 and 6
  (docs/rider-travel-check-spec.md section 7) and confirm both still pass.

  ============================== REVISION 1 (2026-09-23 UTC) ==============================
  docs/jev-harnesses-adversarial-review-2026-09-22.md finding 11 ("Invoke-Jev never
  retries. One transient 5xx aborts a whole run"). Bounded retries with backoff, TRANSIENT
  failures only:
    - HTTP 5xx (Status 500-599): transient, retry.
    - No HTTP response at all (Status stays $null -- a timeout, a connection reset, a DNS
      failure: Invoke-RestMethod threw before any response arrived): transient, retry.
    - Any 4xx, INCLUDING the 403 WAF block above: NEVER retried. A firewall block is about
      the request TEXT, not a transient condition -- retrying sends the same text again and
      wastes the attempt. A generic 4xx (bad request, auth failure, rate limit) means
      something about THIS request or THIS key is wrong now, not "try again in a moment".
    $MaxRetries (default 3) bounds the retries; backoff is 2^attempt * $RetryBackoffBaseSec
    seconds (1, 2, 4 by default) -- exponential, not linear, so a run does not sit through
    four evenly-spaced attempts against a server that is actually down.

  RetryCount travels on EVERY return value (success or failure) as the number of retries
  actually spent on that one call (0 = succeeded or failed on the first attempt, never
  retried). Callers that already aggregate USAGE_INPUT_TOKENS/USAGE_OUTPUT_TOKENS per call
  (rider-travel.ps1, commit-walker.ps1) aggregate this the same way, printed alongside.

  TEST SEAM, OFF BY DEFAULT: -TransportOverride is a scriptblock that, if supplied, REPLACES
  the real Invoke-RestMethod call -- it receives the UTF-8 body bytes and must return the
  same one-attempt shape this function produces internally (Ok/Response/Error/Status/
  WafBlocked). No caller in this repo passes it during a normal run (fixture-parser.ps1 and
  doc-scanner.ps1 call `Invoke-Jev $key $body` positionally, exactly as before), so
  production behaviour is retries-only; a real network call still happens every time. It
  exists so a synthetic test can exercise the retry/backoff/no-retry paths without a real
  transient failure, a real 403, or a real API key -- see
  docs/harnesses-1-2-arming-spec-back.md for the runs.
  ==========================================================================================
#>

#  ============================== REVISION 2 (2026-09-24 UTC) ==============================
#  Second-reader item 3: every harness requests the alias `jev-latest`, which moves
#  silently, so a run record could not say which model answered. Invoke-Jev now tallies the
#  REQUESTED model (the body's `model`) and the RESOLVED model (the response's own `model`
#  field) for every call, per run. Each harness prints Get-JevModelLine in its coverage block
#  and writes it into its output file. The tally lives in the dot-sourcing script's scope,
#  so one harness run = one tally.
#  ==========================================================================================
if ($null -eq $script:JevModelTally) { $script:JevModelTally = @{ requested = @{}; resolved = @{} } }
function Add-JevModelTally([string]$kind, [string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { $name = 'UNSET' }
    $t = $script:JevModelTally[$kind]
    if (-not $t.ContainsKey($name)) { $t[$name] = 0 }
    $t[$name]++
}
function Format-JevModelTally([string]$kind) {
    $t = $script:JevModelTally[$kind]
    if ($t.Count -eq 0) { return 'none' }
    return (@($t.Keys | Sort-Object | ForEach-Object { "$_ x$($t[$_])" }) -join ', ')
}
# One line, the same on every harness. `resolved` counts SUCCESSFUL calls only; `none`
# means no call succeeded (or none was made). UNAVAILABLE means a response carried no model.
function Get-JevModelLine {
    "JEV_MODEL requested=[$(Format-JevModelTally 'requested')] resolved=[$(Format-JevModelTally 'resolved')] run_utc=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
}

function Invoke-Jev([string]$apiKey, [hashtable]$body, [int]$MaxRetries = 3, [double]$RetryBackoffBaseSec = 1, [scriptblock]$TransportOverride = $null) {
    $json = $body | ConvertTo-Json -Depth 12
    # See header comment above -- encoding the body ourselves is the fix, not a retry
    # or a fallback.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    # One HTTP attempt. Pulled out so the retry loop below and the -TransportOverride test
    # seam share EXACTLY this shape -- a synthetic test result is indistinguishable, to the
    # retry loop, from a real one.
    function Invoke-JevOneAttempt {
        if ($TransportOverride) { return (& $TransportOverride $bytes) }
        try {
            $resp = Invoke-RestMethod -Uri 'https://api.typesafe.ai/v1/systemone' -Method Post `
                -Headers @{ Authorization = "Bearer $apiKey" } -ContentType 'application/json; charset=utf-8' `
                -Body $bytes -TimeoutSec 30
            return @{ Ok = $true; Response = $resp; Error = $null; Status = 200; WafBlocked = $false }
        } catch {
            # 2026-09-22 (UTC): the API sits behind a web firewall. A request whose TEXT matches an
            # attack signature (measured: the comment phrase "and 4 + 7 = 11" reads as an SQL
            # "AND x=y" tautology) gets a 403 with an HTML block page, not a JSON error.
            # WafBlocked lets a caller record that ONE item as unjudgeable and carry on, instead
            # of treating a content block as an outage. Callers that ignore the new fields behave
            # exactly as before.
            $status = $null; $blocked = $false
            $resp = $_.Exception.Response
            if ($null -ne $resp) {
                try { $status = [int]$resp.StatusCode } catch { }
                if ($status -eq 403) {
                    try {
                        $rd = New-Object System.IO.StreamReader($resp.GetResponseStream())
                        $txt = $rd.ReadToEnd()
                        $blocked = ($txt -match '(?i)<!DOCTYPE html|<html')
                    } catch { }
                }
            }
            return @{ Ok = $false; Response = $null; Error = $_.Exception.Message; Status = $status; WafBlocked = $blocked }
        }
    }

    [void](Add-JevModelTally 'requested' ([string]$body.model))
    $attempt = 0
    while ($true) {
        $result = Invoke-JevOneAttempt
        if ($result.Ok) {
            $result.RetryCount = $attempt
            # REVISION 2: the RESOLVED model. docs.typesafe.ai/api.md "Response body": `model`
            # is required and names "the model that performed the evaluation" (e.g.
            # jev-1.13.0). The request alias jev-latest moves silently, so this is the only
            # record of which model answered. A response without it is tallied UNAVAILABLE.
            $m = $null
            if ($null -ne $result.Response) { $m = [string]$result.Response.model }
            if ([string]::IsNullOrWhiteSpace($m)) { $m = 'UNAVAILABLE' }
            $result.Model = $m
            [void](Add-JevModelTally 'resolved' $m)
            return $result
        }

        # REVISION 1: transient iff HTTP 5xx, or no HTTP response reached at all (timeout /
        # connection reset / DNS failure -- Status stays $null on those). A WAF block is a
        # 403 and is excluded explicitly for readability even though 403 is outside 500-599
        # anyway -- never retry it, whatever the range check would say on its own.
        $isTransient = (-not $result.WafBlocked) -and (($null -eq $result.Status) -or ($result.Status -ge 500 -and $result.Status -le 599))
        if ($isTransient -and $attempt -lt $MaxRetries) {
            $attempt++
            Start-Sleep -Seconds ([math]::Pow(2, $attempt - 1) * $RetryBackoffBaseSec)
            continue
        }
        $result.RetryCount = $attempt
        return $result
    }
}
