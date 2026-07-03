# Implementation report — spec-restore-hardening.md

**Date:** 2026-07-03
**Implementer:** Opus 4.8 (high)
**Base:** `7fb075a` on `master` (resilience pass docs landed; tree had the owner's pending HANDOVER-2 resilience-status note + the untracked spec). Anchors matched the spec; no drift worth noting.
**Commits:** `0d078eb` (1/3), `310de5f` (2/3), `c6a893c` (3/3), + a docs commit (this report + HANDOVER-2 id-map). Build `dotnet build DeribitOrderPlacementApp.sln -c Debug` = **0 errors / 0 warnings before each commit** (verified three times, once per commit; baseline before commit 1 also 0/0). Committed locally, not pushed.

Line numbers refer to the file state **after** all three commits unless marked "was". All code changes are in `DeribitOrderPlacementApp/frmMainPageV2.vb`.

---

## Commit 1 — `0d078eb` — zero-baseline guard (safety prerequisite)

An unknown baseline (`StopLossTriggerOriginal = 0`, the post-restart state, and briefly before the id-778 snapshot lands) now disables the emergency market-stop. Rationale: once commit 2 makes `TradeMode` correct for a restored short, a zero baseline would fire an **instant** emergency close — `priceMovement = bestBid − 0` trivially clears the threshold. Same philosophy as threshold-0-disables; normal SL trailing is untouched.

- **`HandleQuoteUpdates` triggered-SL pre-check** (`frmMainPageV2.vb:1566`): added `Dim baselineKnown As Boolean = StopLossTriggerOriginal > 0D` alongside `emergencyThresholdValid`, with a comment. Both emergency gates now require it: the `priceMovement >= emergencyThreshold` gate (`:1578`) and the `* 0.5` force-path chooser (`:1607`).
- **`UpdateStopLossForTriggeredStopLossOrder` emergency branches** (`:3236` long, `:3242` short): `StopLossTriggerOriginal > 0D AndAlso` inserted into both conditions (after the `marketStopThreshold > 0D` term, before the `TradeMode` term). Comment extended on the existing block.

Diff: 13 insertions, 4 deletions.

## Commit 2 — `310de5f` — trade-context restore at connect

### 2a. TradeMode + placedPrice from the position (`ProcessPositionData` restore-announce block)

- `SetTradeMode(positionSize.Value > 0D)` added as the **first line inside the existing `UiInvoke`** (`:4081`) — it touches `btnBuy`/`btnSell`/leg-textbox layout, so it must run on the UI thread. This makes the SL-trailing and emergency branches run the correct side for a restored short.
- After the `UiInvoke`: `If placedPrice = 0D Then placedPrice = If(averagePrice, 0D)` (`:4090`). Restart-restore exception to placement-only seeding — `placedPrice` **is** 0 here and no working entry exists, so no reposition can act on it; restores the PnL/display basis and stops the 5 s "Placed price = 0" warning loop. `txtPlacedPrice` was already mirrored inside the `UiInvoke`. Spec's comment block inserted above.

### 2b. Open-orders snapshot (id 778)

- **`RequestOpenOrdersSnapshot()`** (`:1846`, send-only, modeled on `GetLivePositionData`): posts `private/get_open_orders_by_instrument` (`instrument_name` BTC-PERPETUAL, id 778); `type` omitted ⇒ Deribit default `all`, so untriggered stop/trigger orders are included. Consumes a credit if the limiter exists; swallow-all `Catch` → one red log line. Called (`:752`) in `ConnectToWebSocketDirectly` directly after `Await GetLivePositionData("BTC-PERPETUAL")`; the response drains once the receive loop starts (same pattern as the id-777 seed).
- **`HandleOpenOrdersSnapshot(response)`** (`:3947`) wired into the dispatch immediately after `HandleMarginEstimationResponse` (`:827`). Null-safe id-gate (`messageId.HasValue AndAlso = 778`); whole body gated on `Not cancelPending`; empty/absent `result` returns without seeding or announcing (flat restart). A first pass sets `workingEntryFound` (an entry leg still `open`); the main pass maps by `label` + `order_state` per the spec table:

  | label | state | engine | display |
  |---|---|---|---|
  | `EntryLimitOrder`/`EntryTrailingOrder` | `open` | `CurrentOpenOrderId = id`; `placedPrice` seed-if-0 | `txtPlacedPrice` (mirrored only when seeded) |
  | `TakeLimitProfit` | `untriggered`/`open` | `PositionTPOrderId = id`; `CurrentTPOrderId` only if `workingEntryFound` | `txtPlacedTakeProfitPrice` |
  | `StopLossOrder` | `untriggered` | `PositionSLOrderId = id`; `CurrentSLOrderId` only if `workingEntryFound`; `placedStopLossPrice` seed-if-0; `StopLossTriggerOriginal` seed-if-0 from `trigger_price` | trig + SL boxes (SL box mirrored only when seeded) |
  | `StopLossOrder` | `open` (triggered) | `SLTriggered = True`; `PositionSLOrderId = id`; `placedStopLossPrice` seed-if-0; `StopLossTriggerOriginal` seed-if-0 | trig + SL boxes |
  | `TrailingStopLoss` | any | out of scope v1 — yellow "manual re-attach" log | — |

  One summary line `Restored order context: entry=…, TP=…, SL=…` emitted only when at least one leg was restored.

### 2c. Defensive mid-session heal

- Open `StopLossOrder` echo case (`:2031`), right after `SLTriggered = True`: `If StopLossTriggerOriginal = 0D Then StopLossTriggerOriginal = If(triggerPrice, 0D)`. `triggerPrice` is already extracted in the enclosing `open` scope. Covers any path where the baseline was lost but the SL triggers later.

**Threading note (deviation-worth-flagging, benign):** `HandleOpenOrdersSnapshot` runs on the receive thread, so engine fields (`placedPrice`, `placedStopLossPrice`, `StopLossTriggerOriginal`, `Current*`/`Position*` ids, `SLTriggered`) are written **directly on the receive thread** and only control writes go through `UiInvoke` — i.e. it obeys invariant #1 more strictly than the existing `HandleOrderPositionUpdates` echo handler, which writes the same engine fields inside `Me.Invoke`. Both run only on the receive loop, so there is no cross-thread race with `HandleQuoteUpdates`. Display mirrors for the two *moving* values (`placedPrice`, `placedStopLossPrice`) are gated on the engine seed, so an active-trailing reconnect can't be clobbered; the static values (TP price, trigger price) are set directly.

Diff: 153 insertions.

## Commit 3 — `c6a893c` — triggered-SL edits sized from the position

- `UpdateStopLossForTriggeredStopLossOrder` (`:3275`): `Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)` (was `= orderAmountVal`). A triggered SL covers the **position**; after a restart, or whenever `txtAmount ≠ position size`, the old value would resize the stop off the position. Falls back to `orderAmountVal` only when the model is unseeded. Entry-context edits (`UpdateLimitOrderWithOTOCOAsync`, trailing) were **not** touched — they size a working order, not the position.

Diff: 5 insertions, 2 deletions.

---

## Acceptance summary (implementer-verifiable)

| Assertion | Result |
|---|---|
| Build green ×3 (once per commit) + baseline | ✅ 0 errors / 0 warnings each time |
| Commit 1 lands before commit 2 makes TradeMode correct | ✅ commit order `0d078eb` → `310de5f` |
| Both emergency sites (quote pre-check + SL-edit) guarded | ✅ `:1578`, `:1607`, `:3236`, `:3242` |
| id-778 send is send-only; drains via receive loop | ✅ `:752` send, `:827` dispatch, `:3947` handler |
| Snapshot handler: null-safe id-gate + `Not cancelPending` + seed-only-when-zero | ✅ by construction (`:3947`+) |
| Flat restart seeds nothing / no announce | ✅ empty-`result` early return |
| SL edit sizes from position, entry edits unchanged | ✅ `:3275` only; OTOCO/trailing sends untouched |

Owner runtime tests (spec test plan 1–5: the failing SHORT scenario re-run + LONG mirror, working-entry restart, emergency-stop regression on a normal session, flat restart, full trade-cycle regression) remain to be run on the test sub-account.

---

## Deviations

1. **Per-commit separation of one file:** commits 2 and 3 both live in `frmMainPageV2.vb`. To keep them as separate reviewable commits, the commit-3 hunk was reverted before committing commit 2, then reapplied — no functional effect, just staging mechanics.
2. **Docs commit carries the owner's pending note:** the HANDOVER-2 §4.5 id-map line (adds id 778) is committed with this report. That file already had an uncommitted, owner-authored "Resilience pass SHIPPED" bullet in §2; it is additive doc text and rides along in the same docs commit (flagged here rather than silently absorbed). Nothing was overwritten.

All other inserted blocks are the spec's code / the spec's comment text verbatim.

---

## Suspicious nearby — deliberately not touched

1. **Triggered/open `StopLossOrder` may lack `trigger_price` in the snapshot** — for the already-triggered (`open`) SL leg, `StopLossTriggerOriginal` seed-if-0 from `trigger_price` is best-effort; if Deribit omits it on a post-trigger stop-market order the baseline stays 0 and the emergency stop remains disabled (guard from commit 1) until the 2c echo heal repopulates it. Acceptable per the spec's philosophy (unknown baseline ⇒ emergency disabled); noted for the owner's restore test.
2. **`get_open_orders_by_instrument` default `type=all`** returns limit + trigger orders; if a future OTOCO variant uses a `stop_market`/`take_market` combo the `label` map still keys correctly, but any *new* labels would fall through the `Select Case` silently. No new labels exist today; a defensive `Case Else` log is a housekeeping candidate.
3. **Reconnect (not restart) re-runs the snapshot** — `positionRestoreAnnounced` is reset at connect, and a mid-session ONLINE re-click re-drains id 778. Seed-only-when-zero protects every engine field and the moving displays, so this is safe; the only visible effect is the "Restored order context" line reappearing on a manual reconnect. Left as-is (informative).
4. **`positionSizeUSD` torn-read class (commit 3):** the SL-edit `amount` reads `positionSizeUSD` on the receive thread while echoes write it on the same thread — no race. The accepted Decimal torn-read class (invariant #2/#6, UI buttons) is unchanged.
