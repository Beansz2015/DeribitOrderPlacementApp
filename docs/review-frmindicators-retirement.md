# Coordinator review — retire FrmIndicators; engine ATR in every mode, switchable Flat ATR

**Spec:** `docs/spec-frmindicators-retirement.md`. **Report:** `docs/impl-report-frmindicators-retirement.md`.
**Commit reviewed:** `cc44ea9`. **Reviewer:** coordinator seat, Opus 5.5, high. **Date:** 2026-10-06.

## Verdict: ✅ APPROVED. Runtime checks owed (owner, testnet, after the x64 rebuild).

## 1. Verified at the artefact

| Claim | Result | How |
|---|---|---|
| Gate | ✅ `GATE PASSED`, OrderCheck **307/307** | Re-run at `cc44ea9`, and again after this review's two follow-ups |
| Nine `frmMainPageV2.vb` censuses | ✅ 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines**, unchanged | `Select-String -AllMatches -CaseSensitive`, summed |
| No `FrmIndicators` reference left | ✅ 0 hits in `DeribitOrderPlacementApp/*.vb` | `Select-String -CaseSensitive` |
| Off is read-only | ✅ `EvaluateLatestPayloadAsync` returns after the status snapshot in Off, **before** `ForceStop`, the de-dupe mark, the gate chain and `EmitDisposition` | Read the diff |
| The engine-facing feedback file cannot change in Off | ✅ `FeedbackLastSignal` has one writer, `EmitDisposition` (`SignalBridge.vb:1237`), which Off never reaches. `CaptureFeedbackSnapshot` reads only `Mode`, `LocalArmed`, `Started`, `BreakerTripped`, `FeedbackLastSignal` | grep + read |
| Off never alerts or stands down | ✅ `OnStalenessTick` in Off zeroes `_lastSignalAtr` and returns before the count | Read the diff |
| Constructor start is safe | ✅ both timers are created before `StartWatching()` | `SignalBridge.vb:183-184` |
| Seed before first commit | ✅ `SeedFlatAtrFromHost` (`AutoTradeSettings.vb:164`) precedes `CommitGateConfig`/`CommitToolingConfig`; the checkbox handler is attached after (`:201`) | grep line order |
| Host mirrors seeded before the form exists | ✅ `ApplyFlatAtrFromSettings` at Load, before construction | Read the diff |
| Persistence fixture is sandboxed | ✅ writes `AppContext.BaseDirectory` = OrderCheck's own bin, backs up and restores; same pattern as the min-net-profit fixture | `AppUserSettings.vb:104-108` |
| One ATR read feeds both the cap and the log line (`D1` of the spec, the wrong-ATR log line) | ✅ fixed | Read the diff |
| Do-not-touch list | ✅ no diff in the triggered-SL chase, `ChaseAbortReason` or the `maxSlippageATRchecked` arm | Read the diff |

**Not re-run:** the implementer's mutation run (fixture (e) failing against the old unmultiplied 70). The
fixture asserts `42` and `<> 70` directly, which cannot pass against the old code, so I accepted the
report's mutation result without repeating it.

## 2. The implementer's five decisions — all upheld

| # | Decision | Ruling |
|---|---|---|
| 1 | Start the watcher in the bridge constructor | ✅ **Upheld, and it is a spec omission.** `SB1` (a retirement spec-back finding — the spec never said the watcher must start at launch): the bridge is Off at every launch, so without this, §3 row 1 would never apply. The spec is the coordinator's own. |
| 2 | Do not update `_lastSignalSummary` in Off | ✅ Upheld — it would mislabel the "Last:" line |
| 3 | Reset the stale counters on entering Off | ✅ Upheld — `StopWatching` used to do it |
| 4 | Layout, and the caption-only rename | ✅ Upheld — the Min Net Profit precedent; harness names stay stable |
| 5 | Readout colours: green payload, cyan switched, orange fallback | ✅ Upheld |

## 3. Findings, and what was done

| ID (scope: this review) | Kind | Finding | Action |
|---|---|---|---|
| `D2` | Defect in code (a comment) | `SignalBridge.vb:308-309`: `LastSignalAtr`'s comment still said "read from CalculateATRSlippageLimit" and "0 when no actionable payload". Both stale after R2. | **Fixed in this review.** |
| `D3` | Defect in code (build hygiene) | `Skender.Stock.Indicators` is an unused `PackageReference` (0 uses in any `.vb`). | **Removed in this review.** Gate 307/307 after. |
| `SB1` | Spec-back, against the spec | See decision 1. | Upheld; recorded here. |

**Noted, no action:** in Off the malformed-timestamp log line still says "standing down". In Off there is
nothing to stand down. It is a log line on a malformed payload only, and the wording is accurate in
Log-only and Live.

## 4. Owed — owner, testnet, after an x64 rebuild

1. Spec §4 item 3: bridge Off, engine publishing → the readout says "signal payload"; **zero new rows in
   `bridge-dispositions.log`**; stop the engine for > 30 s → the readout falls to "flat ATR (fallback)",
   with no stale alert.
2. Spec §4 item 4: Off → Log-only gives the current payload exactly one disposition row.
3. Spec §4 item 5: no indicator connect lines in the log.
4. Spec §4 item 6: tick "Use flat ATR", set 55, close, relaunch → both restored.
5. Layout: "Use flat ATR" does not overlap the "Flat ATR:" caption.

Then `docs/spec-trade-slippage-fields.md` is unblocked.
