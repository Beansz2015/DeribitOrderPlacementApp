# Impl report — the bounded-wait fix (`docs/spec-harness-owned-window.md` §Probe verdict, R8/R9)

**Seat:** Sonnet, medium — same conversation, continued per the ruling's seat assignment.
**Date:** 2026-08-07. **Scope:** `tools/` only. No `.vb` file touched. No trade.

---

## 1. What was built

Four commits, small and in order:

1. `2c6cb7b` — `Wait-ForMatchingElement` added to `harness-common.ps1`. New function only; no call
   site changed yet. Wraps `Select-MatchingElement` in a poll (default 500 ms deadline / 25 ms
   interval), returns the instant `Result.Kind` is anything other than `None` (so `Ambiguous` is
   never waited out), and returns the still-`None` result on timeout so every caller's existing
   not-found handling fires unchanged. Does not modify `Select-MatchingElement` or
   `Select-BestMatchIndex`.
2. `4fe4a33` — the five drive scripts (`set-textbox`, `toggle-checkbox`, `select-combo-item`,
   `click-button`, `click-PLACES-ORDER`) converted, one line each, `Select-MatchingElement` →
   `Wait-ForMatchingElement`, same arguments.
3. `9341888` — `Merge-ProcessWindows` and `tools/checks/test-merge-process-windows.ps1` removed
   (R9): the probe rules out every union shape, not just the withdrawn one, so there is nothing
   left for the seam to serve. Removed because the probe rules them out, not because they were
   wrong — the seam was correct and passed 5/5.
4. `689a812` — a comment added to `close-popup.ps1` recording that it cannot close the settings
   window (it iterates `Get-ProcessWindows`, which is root-children-only, and the probe proved
   `AutoTradeSettings` is never a root child). Recorded per R9, not fixed. The stale usage example
   (`close-popup.ps1 "AutoTradeSettings"`) was also dropped since it no longer does anything.

## 2. Acceptance — all six items

Preconditions: `tools/launch-app.ps1`, title confirmed `— TESTNET`.

### 1 — The wait works (20 rounds each, window opened fresh immediately before each attempt)

| Script | Target | Success | Elapsed (min–max, ms) |
|---|---|---|---|
| `set-textbox.ps1` | `txtAtrLength 9` | **20/20** | 1326 – 4042 |
| `toggle-checkbox.ps1` | `chkSessionPolicyOn` | **20/20** | 1318 – 2057 |
| `select-combo-item.ps1` | `cboBridgeMode Log-only` | **20/20** | 1658 – 1862 |

**60/60 exit 0.** All three values restored and read back to confirm: `txtAtrLength` → `7`,
`chkSessionPolicyOn` → `Off` (20 blind toggles, even count, returned to baseline — verified, not
assumed), `cboBridgeMode` → `Off`. None of the three targets are in the 11 fields
`CaptureUserSettingsFromControls` persists on `FormClosing` (`frmMainPageV2.vb:248-262` — that list
is `Amount`/`TakeProfit`/`Trigger`/`StopLoss`/`TriggerOffset`/`TpOffset`/`Comms`/`MarketStopLoss`/
`MaxSlippageATR` + 2 checkboxes, all main-form-only), so `HANDOVER-6.md` §5.4's risk did not apply
to any of these targets — checked before choosing them, not assumed.

🚨 **Read §3 before taking these elapsed times as a measure of the wait loop.** They are not.

### 2 — `Ambiguous` refuses instantly

`set-textbox.ps1 Trig 999` — bare `Trig` ties three ways on the main form
(`txtTrigger`/`txtTriggerOffset`/`txtPlacedTrigStopPrice`, all substring-tier hits, no exact hit at
either tier), exactly as predicted. **Exit 3**, all three tied candidates printed. Instrumented
directly against `Wait-ForMatchingElement` (bypassing process overhead): resolved on the **first**
poll attempt, `Kind=Ambiguous`, returned immediately — confirmed it does not wait out the deadline.

### 3 — A genuine not-found still fails loudly

`set-textbox.ps1 NoSuchControlXYZ test` → **exit 2**, full candidate list printed (28 textboxes
across both windows), unchanged in form from before this fix.

### 4 — Deny check and TESTNET gate still refuse

`click-button.ps1 "Limit BUY"` → **exit 3**, `"matches the trade-deny regex"`. `click-PLACES-ORDER.ps1`
verified **by diff** against `59de8c1` (the union-revert baseline, pre-dating any fix work): the
**only** line changed is the `Select-MatchingElement` → `Wait-ForMatchingElement` swap. The
`Get-MainForm -RequireTestnet -RequireHarnessPid` call above it is untouched, byte-for-byte.

### 5 — `test-select-best-match.ps1`

**7/7**, unaffected — run after every commit in this pass, not just once at the end.

### 6 — Gate and censuses

`tools/checks/verify-gate.ps1` → **GATE PASSED**, OrderCheck **268/268**. Censuses on
`frmMainPageV2.vb` (untouched file, run anyway per house rule): `emergencyFired` 10 ·
`IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 ·
`slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 ·
`placedOrderSizeUsd` 18 = **68, exactly matching the documented baseline.**

**A note on how that last number was obtained, because it is itself the census trap in miniature.**
My first pass used the grep tool's line-counting mode and got `placedOrderSizeUsd` = 14, total 64 —
the exact "reads as four missing" trap `HANDOVER-6.md` §5 warns about, reproduced by me in the act of
checking for it. Re-ran with true occurrence counting (`-o`, one match per output line, counted) and
got 18, total 68. Recording the wrong first answer here rather than only the corrected one, per
`HANDOVER-6.md` §7 lesson 4 (prove every conjunct).

## 3. What the acceptance-1 timings actually show, honestly

**The 1.3–4.0 s figures in §2's table are dominated by cold-process overhead, not by the wait loop.**
I did not stop at the round numbers — I instrumented three layers to find out what they meant:

1. **Bare `powershell.exe` + dot-source `harness-common.ps1`, no query at all: ~270 ms** (8 runs,
   267–291 ms). This is fixed cost every one of these scripts pays before doing anything.
2. **A single `Select-MatchingElement` call against an already-open, long-settled settings window,
   from a FRESH process: ~740–950 ms**, consistently, across 9 separate trials (2 batches). The
   window had been open for seconds in every one of these trials — this is not a render-latency
   wait. It is the first UI Automation query a fresh process's client makes, and it appears to carry
   its own one-time cold-start cost independent of window age.
3. **Instrumented `Wait-ForMatchingElement` itself, per poll attempt:** in every trial I ran — the
   `Trig` ambiguous case, the genuine not-found case, and the txtAtrLength case — **it resolved (or
   timed out) on attempt 1.** Zero cases in my testing needed a second 25 ms poll iteration. The
   `~740–950` ms single-attempt cost, on a genuine not-found, already exceeds the 500 ms deadline by
   itself, so the loop's deadline check fires immediately after that one slow attempt — it does not
   compound across multiple attempts (confirmed: 3 not-found trials, all 1 attempt, all ~1.2 s total
   process time, no runaway).

**So: the fix is doing exactly what R8 specified, and it is safe — no compounding, correct instant
`Ambiguous`, correct loud timeout. But the mechanism that makes acceptance-1 succeed 60/60 in
practice is NOT "many fast 25 ms retries converging inside the 500 ms budget," the way the probe's
9–22 ms in-process figures might suggest.** It is: (dot-source + `Get-MainForm` + `Get-ProcessWindows`
≈ 300 ms of unavoidable process/query overhead) + (one cold-process query, itself ~700–950 ms, which
by the time it runs has had enough wall-clock time since `Show()` for the render race the probe
measured to have already resolved). The explicit poll loop functions as the safety net R8 asked for;
in every real invocation I tested, it did not need to loop to provide that net — the incidental
latency of a fresh process launch already exceeded the window's render-latency race on its own.

**This means the "500 ms deadline, >20x margin over the probe's 9-22 ms" framing in R8 describes the
probe's in-process measurement, not what a real (cold-process) caller experiences.** A real caller's
single first attempt can itself take longer than the nominal deadline, so a genuine not-found times
out at whatever that one slow attempt costs (observed ~1.2 s total process time), not at a crisp
500 ms mark. This is not a defect — every acceptance item passed — but I am not reporting "500 ms" as
the practical worst case, because it isn't one I observed.

## 4. R10 — left open, not chased

- Set 1 round 1's root cause (window open, neither UIA query resolving for the full probe window) —
  unestablished, not reproduced or investigated further here, per R10.
- Only `ComboBox`/`cboBridgeMode` was probed for the *original* render-latency question. This
  acceptance pass's item 1 additionally exercised `Edit` (`txtAtrLength`) and `CheckBox`
  (`chkSessionPolicyOn`) end to end, 20/20 each — R10 said this "closes cheaply" via the acceptance,
  and it did: no divergent behaviour observed across the three control types.

## 5. What this pass did NOT establish

- Whether the cold-process ~700–950 ms single-query cost is itself reducible (e.g. some UIA client
  warm-up available to a script) — out of scope for this spec, not investigated.
- Behaviour under real system load (antivirus, many concurrent harness runs) — all testing here was
  on an otherwise-idle machine running one app instance at a time.
- Whether a target that only appears LATE within a single already-warm process (the scenario the
  probe actually simulated) would still resolve via a second or third poll attempt rather than the
  first — none of my acceptance trials exercised that path, because every trial was a fresh process.
  The mechanism exists and is exercised by the Ambiguous/not-found tests (which prove the loop logic
  itself is correct), just not by a case that needed more than one attempt to find a target.

## 6. Was Sonnet at medium right for the fix?

Yes. `tools/` only, no `.vb` file, no order/stop-loss/receive/bridge/act path. The three places R8
named as where the thinking should go all mattered in practice: the `Ambiguous`-must-not-wait rule
(verified directly, not assumed), the five conversions not disturbing deny/TESTNET/PID logic
(verified by diff), and the honest framing in R8/here (§3 exists because the first, naive read of
the acceptance-1 numbers would have overstated what the fix does). Nothing here needed architectural
judgment or touched the app. One thing worth flagging for the tiering table: this pass required
building throwaway instrumentation (three layers of it, §3) to avoid reporting a plausible-looking
but wrong number — that is more diagnostic effort than the probe itself needed, and is the kind of
thing that would be easy to skip under time pressure. Medium effort supported doing it; nothing
suggests it needed more.
