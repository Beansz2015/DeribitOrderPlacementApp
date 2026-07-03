# Implementation Report — Cross-thread UI access in the receive loop

**Spec:** `docs/spec-cross-thread-fix.md`
**Branch:** `housekeeping-now` (9 local commits on top of `e467d1a`; **not pushed**)
**Build:** `dotnet build` — 0 errors, 0 warnings after every commit.
**Files touched:** `frmMainPageV2.vb` (all 9 commits), `FrmIndicators.vb` (commit 8 only).

---

## 1. Approach taken (and why)

**Backing fields + a marshalling helper** — the spec's preferred option, not the "route each handler onto the UI thread" alternative.

Reasons:
- It removes the stale-state runaway **by construction**: the reposition decision reads an engine-owned field that the engine advances synchronously, so a slow/failed textbox write can never feed a stale price back into the decision.
- It keeps the single `ReceiveAsync` loop on its own thread (no re-marshalling of all the API-send logic onto the UI thread, which would risk UI stutter and reentrancy).
- It aligns with audit #10 (engine decoupled from controls) — the controls are now display mirrors only.

**Mechanics added (commit 1):**
- `UiInvoke(action)` — `If Me.IsHandleCreated AndAlso Me.InvokeRequired Then Me.BeginInvoke(action) Else action()`. Non-blocking, so a display write can never stall/abort a receive-loop decision.
- Engine-owned fields (set by the engine): `placedPrice`, `placedStopLossPrice`, `indexPriceVal`, `equityBTCVal`.
- User-input mirror fields: `orderAmountVal`, `manualTPval`, `manualSLval`, `takeProfitOffset`, `stopLossOffset`, `triggerDistance`, `tpOffsetVal`, `commsVal`, `marketStopThreshold`, `maxSlippageATRmult`, plus (commit 9) `maxSlippageATRchecked`, `marketStopLossChecked`.
- `SyncTradeInputsFromUi()` (TryParse, blank→0) wired to **one** `TradeInput_Changed` handler over all input textboxes' `TextChanged`, plus `SyncToggleInputsFromUi()` on the two checkboxes' `CheckedChanged`. Both seeded in `frmMainPageV2_Load`. So every field stays `==` its control, updated on the UI thread.
- ATR (a control on the *other* form) is published by `FrmIndicators.CurrentATR` (a `_currentATR` backing field set wherever `lblATR.Text` is set).

`TradeMode` was already an engine field; `StopLossTriggerOriginal` already tracked the SL trigger.

---

## 2. Per-site summary (every control access moved off the receive thread)

### Writes — now marshalled via `UiInvoke`/`Me.Invoke`
| Function | Control(s) | Before | After |
|---|---|---|---|
| `HandleQuoteUpdates` Block A (entry reposition) | `txtPlacedPrice.Text` | direct write (threw) | `placedPrice = bestBid/bestAsk` then `UiInvoke(... = bestBid/bestAsk)` |
| `HandleQuoteUpdates` Block D (trailing reposition) | `txtPlacedPrice.Text` | direct write | same as Block A |
| `HandleQuoteUpdates` Block B (triggered-SL) | `txtPlacedStopLossPrice.Text` | direct write | `placedStopLossPrice = newStopPrice` then `UiInvoke(... = newStopPrice)` |
| `HandleOrderPositionUpdates` `filled` (Entry/EntryTrailing) | `lblOrderStatus.Text/.ForeColor` | direct write | `UiInvoke(...)` |
| `HandleOrderPositionUpdates` position-open | `txtManualSL/TP.Text = "0"` | direct write | `manualSLval=0 : manualTPval=0` + `UiInvoke(...)` |
| `HandleIndexUpdates` | `lblIndexPrice.Text`, `txtComms.Text` | `Me.Invoke` (sync) | `UiInvoke` (+ set `indexPriceVal`/`commsVal` first) |
| `HandleBalanceUpdates` | equity/balance/session labels **and the ForeColor block** | 3 `Me.Invoke` blocks + **unguarded** ForeColor block | one `UiInvoke` block incl. colours |
| `CancelOrderAsync` | `lblOrderStatus.Text/.ForeColor` | **unguarded** off-thread write | folded into the existing `Me.Invoke` |

### Reads — now read engine fields (no control touched)
| Function(s) | Control read | Replaced with |
|---|---|---|
| `HandleQuoteUpdates` decisions/PnL | `txtPlacedPrice`, `txtAmount` | `placedPrice`, `orderAmountVal` |
| `HandleQuoteUpdates` Block B | `txtPlacedStopLossPrice`, `txtMarketStopLoss` | `placedStopLossPrice`, `marketStopThreshold` |
| `HandleQuoteUpdates` Block E | `txtManualTP`, `txtTPOffset`, `txtComms` | `manualTPval`, `tpOffsetVal`, `commsVal` |
| `HandleQuoteUpdates` Blocks A/B/D | `chkMaxSlippageATR.Checked`, `chkMarketStopLoss.Checked` | `maxSlippageATRchecked`, `marketStopLossChecked` |
| `UpdateLimitOrderWithOTOCOAsync` | `txtAmount/ManualTP/ManualSL/TakeProfit/StopLoss/Trigger` | matching fields |
| `UpdateStopLossForTrailingOrder` | `txtAmount/ManualSL/StopLoss/Trigger` | matching fields |
| `UpdateStopLossForTriggeredStopLossOrder` | `txtMarketStopLoss`, `txtAmount` | `marketStopThreshold`, `orderAmountVal` |
| `TrailingStopLossOrderAsync` | `txtAmount`, `txtTPOffset` | `orderAmountVal`, `tpOffsetVal` |
| `HandleOrderPositionUpdates` `filled` PnL + `RecordCompletedTrade` | `txtPlacedPrice`, `txtAmount` | `placedPrice`, `orderAmountVal` |
| `HandleBalanceUpdates` | `lblIndexPrice.Text` (guards + USD calc) | `indexPriceVal` |
| `GetEquityBTC` (→ `ProcessPositionData`) | `lblBTCEquity.Text` | `equityBTCVal` |
| `LogTradeDecision` | `txtPlacedPrice/TakeProfit/TrigStop` | snapshotted on the UI thread via `Me.Invoke` (TryParse) |
| `CalculateATRSlippageLimit`, `IsATRSlippageExcessive` | `_indicators.lblATR.Text`, `txtMaxSlippageATR` | `_indicators.CurrentATR`, `maxSlippageATRmult` |
| Emergency stop path | `btnReduceMarket.PerformClick()` | direct `Await SendReduceMarketOrderAsync()` |

### Where each field is populated (on the UI thread / synchronously by the engine)
- `placedPrice` — `ExecuteOrderAsync` & `StopLossForTrailingOrderAsync` (placement); exchange `open` `EntryLimitOrder`/`EntryTrailingOrder` branch; after each Block A/D reposition; reset `0` in `CancelOrderAsync`.
- `placedStopLossPrice` — placement (both order fns); exchange `open`/`untriggered` `StopLossOrder` branch; after each Block B reposition; reset `0` in `CancelOrderAsync`.
- `indexPriceVal`, `commsVal` — `HandleIndexUpdates`.
- `equityBTCVal` — `HandleBalanceUpdates`.
- input mirrors — `SyncTradeInputsFromUi` (TextChanged + Load).
- toggle mirrors — `SyncToggleInputsFromUi` (CheckedChanged + Load).
- `FrmIndicators._currentATR` — wherever `lblATR.Text` is set.

---

## 3. Runaway fix (the exact mechanism)

`HandleQuoteUpdates` Block A/D decision is unchanged in form — `bestBid > (placedPrice + 3)` — but `placedPrice` is now the **engine field**, not a value parsed from `txtPlacedPrice`. On a successful reposition:

```vb
Await UpdateLimitOrderWithOTOCOAsync(bestBid)
If placedPrice > 0 Then AppendColoredText(..., $"Order repositioned: ${placedPrice:F2} → ${bestBid:F2}", ...)
placedPrice = bestBid                          ' engine state advances SYNCHRONOUSLY
UiInvoke(Sub() txtPlacedPrice.Text = bestBid)  ' display mirror, non-blocking
```

Because `placedPrice` advances before the (non-blocking) display write, the next tick's `bestBid > placedPrice + 3` reads the new price even if the textbox update is delayed or fails — so a stale from-price can no longer re-trigger every tick. The "from" price in the log now reads the field (the actual previous placed price) instead of re-reading the textbox.

---

## 4. Retry-amplifier fix + connection-state guard

**Retry amplifier (#4).** Both SL-failure sites that did `lastStopLossUpdate = DateTime.MinValue` (Block B `Catch`; the rate-limit `Catch` in `UpdateStopLossForTriggeredStopLossOrder`) now call:

```vb
Private Sub BackoffStopLossRetry(failedAt As DateTime)
    slUpdateFailures = Math.Min(slUpdateFailures + 1, 8)
    Dim backoffMs As Double = Math.Min(MinStopLossUpdateInterval * (2 ^ slUpdateFailures), SLUpdateMaxBackoffMs) ' cap 5s
    lastStopLossUpdate = failedAt.AddMilliseconds(backoffMs - MinStopLossUpdateInterval) ' delay next attempt to failedAt+backoff
End Sub
```

A success resets `slUpdateFailures = 0`. So a persistent failure backs off (333ms → … → 5s cap) instead of retrying on every quote tick. `ForceStopLossUpdate` still sets `lastStopLossUpdate = DateTime.MinValue` deliberately to bypass the throttle for a genuine emergency — unchanged.

**Connection-state guard (#5).** Every edit/order block in `HandleQuoteUpdates` (entry reposition A, triggered-SL B, trailing reposition D, trailing-TP trigger E) is now gated `If IsWebSocketConnected AndAlso …`. `AndAlso` short-circuits, so when the socket isn't `Open` the block is skipped *and* the single-flight `Interlocked.Exchange` is never even acquired — no edits piled into a closing socket.

---

## 5. Build status, commits, deviations

**Build:** green after every commit (`dotnet build … -c Debug` → 0 errors / 0 warnings).

**Commits (local, oldest→newest):**
```
b01f40e (1/8) add engine backing fields + UiInvoke marshalling
052b408 (2/8) kill the reposition runaway (entry + trailing)
2298131 (3/8) triggered-SL block -> fields + bounded backoff
98c1245 (4/8) trailing trigger + SL helpers read fields
b3e10db (5/8) marshal HandleOrderPositionUpdates filled branch
59da683 (6/8) decouple index/balance/equity from controls
36b4aac (7/8) emergency market-stop via direct call, not PerformClick
b798664 (8/8) ATR slippage check reads fields, not cross-form label
96eb781 (9/9) back chkMaxSlippageATR/chkMarketStopLoss with fields
```
(Commit 9 was an audit follow-up: the two checkbox `.Checked` reads were the last control reads on the receive thread.)

**Deviations (all behaviour-preserving in normal use; safer in the edge cases):**
1. **Blank vs "0" disables instead of mis-fires.** Fields use `TryParse` (blank→0). Where the old code distinguished blank from "0":
   - `marketStopThreshold` 0/blank now **disables** the emergency market-stop (Block B and `UpdateStopLossForTriggeredStopLossOrder`). Old: blank made `Decimal.Parse` throw and abort the whole SL update; "0" armed an always-trigger threshold.
   - `maxSlippageATRmult` 0/blank now defaults to **0.6×** (old: only blank defaulted; "0" gave a 0× = always-excessive limit).
2. **"No edit on bad amount" preserved explicitly.** `UpdateLimitOrderWithOTOCOAsync` / `UpdateStopLossForTrailingOrder` now `Return` when `orderAmountVal <= 0` (old code relied on `Decimal.Parse` throwing on blank → caught → no edit). Avoids ever sending a 0-amount edit.
3. **PnL one-tick timing.** After a reposition, the same-tick PnL label now reflects the just-updated `placedPrice` (≈0 on an unfilled chased entry) instead of the pre-reposition value for one tick. Cosmetic; the *decision* logic is unchanged.
4. **Equity/session labels** now refresh even when a value is exactly 0 (old `<> Nothing` guards skipped those); `USDPublicSession` (circuit-breaker input) is computed identically whenever session PnL ≠ 0.
5. **`txtPlacedPrice` reposition display** keeps the original nullable-decimal `ToString` (no `F2`) to preserve the exact prior display string.

**Out of scope / left as-is (not on the receive thread):**
- `ButtonDisabler`/`ButtonEnabler` (`.Enabled` writes) — **no callers**, dead.
- `LogFailedEntry` (reads `_autotradesettings.txtCooloff.Text`) — only a commented-out call, unreachable.
- `_indicators.IsAutoTradingEnabled` / `lastAutoTradeTime` — a property/field, not a WinForms control (the `lastAutoTradeTime` cross-thread write predates this and belongs to the auto-trade engine being replaced).
- `ExecuteOrderAsync` / `StopLossForTrailingOrderAsync` / `btnEdit*Price` / `btnEstimateMargins` — run on the UI thread; their control reads are legal and were left untouched.

---

## 6. Test steps for the owner (per spec §5, on the test sub-account, under the VS debugger)

1. **No cross-thread exceptions** during: connect, quotes flowing, placing a limit order, repositioning, SL trigger, position close.
2. **No reposition runaway:** place a limit order in a moving market — `Order repositioned` fires only on genuine >$3 moves, the **from-price advances** each time, **no per-tick repeat with a stale from-price**.
3. **No edit-flood / socket drops** from the above (occasional unrelated reconnects are fine).
4. **SL trigger:** drive price through the stop — triggered-SL reposition and (if armed) the emergency market-stop run without throwing. Verify the emergency market-stop still fires (now a direct awaited call, not `PerformClick`).
5. **Bounded backoff:** if an SL edit fails repeatedly (e.g. force a rate-limit), confirm the retries slow down (≤ ~5s apart) instead of every tick.
6. **Blank-field safety (#6):** clearing `txtManualTP/SL` / placed-price mid-trade doesn't break SL handling. Note the deviation: a blank/`0` **Market SL** field now disables the emergency market-stop — set a positive value to keep it armed.
7. **Mid-trade edits take effect:** change `txtManualTP`/`txtMarketStopLoss`/`txtAmount` while a position is open and confirm the engine uses the new value on the next tick (the `TextChanged` sync).
8. **Displays still update:** placed price, SL price, index, equity/balance/session (incl. firebrick/green colours), PnL, order-status label, margin labels.

---

## 7. Confirmation

- **No WinForms control is read or written from the receive-loop thread**, on either form. Verified by grepping the entire receive-thread call tree (`Handle*` + `Update*` + `Trailing*` + `CancelOrderAsync` + `SendReduce*` + `ProcessPositionData`/`GetEquityBTC` + `IsATRSlippageExcessive`/`CalculateATRSlippageLimit` + `LogTradeDecision`) for `.Text`/`.ForeColor`/`.BackColor`/`.Checked`/`.Enabled`/`.PerformClick`/`lbl*`/`txt*`/`chk*`/`_indicators.lbl*` and confirming every remaining hit is either inside a `Me.Invoke`/`UiInvoke`/`BeginInvoke` lambda, in a UI-thread-only function, or in dead code.
- **Did not push.** All 9 commits are local on `housekeeping-now`.
- **Build green** (0/0) after each commit.
- **Behaviour preserved** — same reposition/SL/trailing semantics and same order payloads; the only changes are state ownership, marshalling, the bounded backoff, the connection guard, and the documented edge-case deviations in §5.
