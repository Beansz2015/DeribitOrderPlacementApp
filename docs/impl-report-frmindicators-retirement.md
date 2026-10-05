# Implementation report — retire FrmIndicators; engine ATR in every mode, switchable Flat ATR

**Spec:** `docs/spec-frmindicators-retirement.md` (owner rulings R1–R6a, 2026-10-06).
**Built:** 2026-10-06, on top of `b7b32e8`. One commit. **Not pushed** (the owner is the only pusher).
**Implementer:** Opus 5.5, high. **Review owed:** coordinator, Opus, high.

## 1. What changed, by spec section

| Spec section | Change | Where |
|---|---|---|
| §2.1 Off tap (R2) | Mode Off no longer calls `StopWatching`. `EvaluateLatestPayloadAsync` returns right after the status snapshot in Off. `EvaluateNow` runs in Off. The staleness tick in Off zeroes `_lastSignalAtr` only — no count, no stand-down, no alert. `StopWatching` is now dispose-only. New Off log line. | `SignalBridge.vb` |
| §2.2 ATR source + limit (R3, R5, R6) | `GetEffectiveAtr` → pure `SelectEffectiveAtr(useFlat, bridgeAtr, flatAtr)`. Limit → pure `SlippageLimitFromAtr(atr, mult)` = ATR × mult, 0.6 when mult ≤ 0. No "none" branch. `atrFallbackVal` → `flatAtrVal`; new `useFlatAtr`. | `frmMainPageV2.vb` |
| §2.3 log line (`D1` in that spec — the slippage log divided by the wrong ATR) | `IsATRSlippageExcessive` takes ONE `GetEffectiveAtr()` read and derives both the limit and the "x ATR" figure from it. Body edit only; no new call. | `frmMainPageV2.vb` |
| §2.4 settings form (R4, R5, R3) | `txtAtrLength` row removed (box, two labels, validation, commit-list entry, re-seed branch). Caption "ATR Fallback:" → "Flat ATR:". New `chkUseFlatAtr` beside it, commits on change. `SetToolingValues(flatAtr, useFlat)`. Re-seed branch points at `FlatAtrUsd`. `lblAtrNow` tooltip priority text rewritten. | `AutoTradeSettings.vb`, `.Designer.vb` |
| §2.5 persistence | `flat_atr_usd` (default 70) and `use_flat_atr` (default false) in `orderapp-settings.json`, item A's save path. Seeded before the first commit (`ApplyFlatAtrFromSettings` at host Load, `SeedFlatAtrFromHost` in `InitialiseSettings`). Both keys added to the example JSON with a comment. | `AppUserSettings.vb`, `frmMainPageV2.vb`, `orderapp-settings.example.json` |
| §2.6 retire the form | Field, construction, `StartHeadless`, `atrLengthVal`, `AtrLength` deleted. `FrmIndicators.vb`, `.Designer.vb`, `.resx` deleted (`git rm`). Every comment that named the form or the 14-period ATR rewritten, including the 2026-07-16 ruling text that R2 supersedes. | all of the above |

## 2. Decisions the spec did not spell out — review these

| # | Decision | Why |
|---|---|---|
| 1 | **The watcher starts in the bridge constructor**, followed by one `EvaluateNow()`. | The bridge is Off at every launch (never persisted). Without this, R2 would apply only after the owner toggled Off → Log-only → Off, and `spec-frmindicators-retirement.md` §3 row 1 (manual trade, bridge Off) would still get the Flat ATR. |
| 2 | **`_lastSignalSummary` is not updated in Off.** One guarded line inside the snapshot block. | The panel's "Last:" line pairs the summary with `_lastDisposition`. Off writes no disposition, so a new summary would sit beside an older payload's disposition and mislabel it. |
| 3 | **Entering Off resets `_staleChecks` and `_staleAlerted`.** | `StopWatching` used to do this. Without it, Off → Log-only could inherit a part-count and alert early. |
| 4 | **Layout:** Flat ATR moved up into the old ATR Length row; the second row is left empty. Control names keep `AtrFallback` (caption-only rename, like the Min Net Profit precedent). | The risk rows and the SIZE button keep their places, and the form keeps its height match with the host. Harness `AccessibleName` values do not move. |
| 5 | **Readout colours:** green = "signal payload", cyan = "flat ATR (switched)", orange = "flat ATR (fallback)". | Orange still means "the engine ATR is not in force, and you did not choose that". |

## 3. Side effects worth knowing

- **The status line now shows engine ARM and payload freshness in Off.** It used to read "stale/none" in Off always.
- **The payload directory is created at launch if missing.** `StartWatching` was already create-if-missing; it now runs at construction, not only on entering Log-only or Live.
- **The bridge's malformed-payload log lines fire in Off.** The identity, parse and timestamp checks run before the snapshot, so they log in Off as they do in Log-only. They are log lines, not the stand-down alert.
- **`Skender.Stock.Indicators` is now an unused package reference** in `DeribitOrderPlacementApp.vbproj`. The `.vbproj` is outside this spec's scope, so it stays. A one-line follow-up.
- **The owner's local, untracked `DeribitOrderPlacementApp.vbproj.user`** still has a `Compile Update="FrmIndicators.vb"` entry. It is harmless; Visual Studio drops it on its next save.
- `tools/set-textbox.ps1`'s usage example named `txtAtrLength`; it now names `txtAtrFallback`.

## 4. Verified, and how

| Claim | How |
|---|---|
| Gate passes | `tools/checks/verify-gate.ps1` → `GATE PASSED`, Release sln + Debug app + Debug OrderCheck builds, 0 warnings in a `--no-incremental` app build |
| OrderCheck 307/307 | Same gate run. 294 before + 13 new (spec §4 item 2 a–e, plus a Save → Load round-trip and the old-file default) |
| Fixture (e) fails against the old semantics | Mutation run: fallback put back to `(0D, "none")` and an unmultiplied `Return 70D`. OrderCheck → `FAILED 2/307`: (c) "got 0 'none'", (e) "got 70". File restored; `MUTANT` count 0 afterwards |
| Nine censuses unchanged | Occurrence counts per symbol (`grep -oF`, case-sensitive): 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 = **68 across 64 lines**, before and after |
| Spec §4 item 5, the grep half | `grep -rn FrmIndicators DeribitOrderPlacementApp/*.vb` → no output, exit 1 |

## 5. NOT verified — owner-driven, on a TESTNET-titled session

⚠ **Rebuild the x64 bin first.** The gate builds AnyCPU only.

- Spec §4 item 3: bridge Off with the engine publishing → readout says "signal payload"; zero new rows in `bridge-dispositions.log`; no stale alert after the engine stops for more than 3 checks; readout falls to "flat ATR (fallback)".
- Spec §4 item 4: Off → Log-only gives the current payload exactly one disposition row.
- Spec §4 item 5, the runtime half: one websocket to Deribit (no indicator connect lines in the log).
- Spec §4 item 6: tick "Use flat ATR", set 55, close, relaunch → both restored. The Save → Load path is fixture-tested; the UI round-trip is not.
- The Tooling layout. I did not launch the app or take a screenshot. Check that "Use flat ATR" does not overlap the "Flat ATR:" caption.

## 6. Do-not-touch list — confirmed untouched

- The triggered-SL chase: no diff in that region.
- The gate chain, dispositions, de-dupe watermark and act path: no diff below the new Off return.
- `ChaseAbortReason` order: no diff.
- `maxSlippageATRchecked` as the single arm: no diff.
