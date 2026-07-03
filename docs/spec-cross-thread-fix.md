# Implementation Spec — Cross-thread UI access in the receive loop (edit-flood storm + SL safety)

**Severity:** 🔴 Critical (live-trading safety + connection stability). Do this **before** the gated `spec-medium-housekeeping.md` work.
**Source:** surfaced in live testing of audit #4/#5; this expands audit #4 (which was fixed only for `AuthorizeWebSocketConnection`).
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET, Deribit BTC-PERPETUAL over WebSocket, Newtonsoft.Json. `Option Strict Off` on legacy files (don't flip it here).
**Start point:** current `housekeeping-now` branch tip (includes HIGH #2–#7 + housekeeping). Build is green there.

> ⚠️ Locate code by function name + the quoted snippets; line numbers drift.

---

## 1. The bug (root cause)

`ReceiveWebSocketMessagesAsync` is started with `Task.Run(...)`, so it and **every handler it calls run on a thread-pool thread**, not the UI thread. Those handlers read **and** write WinForms controls directly:

- Reads of `.Text` are (empirically) tolerated by the runtime.
- **Writes** (`control.Text = …`, `lbl….Text = …`) and `button.PerformClick()` **throw `InvalidOperationException` (illegal cross-thread call)** when a debugger is attached (Visual Studio turns the check on). With no debugger the same code is *silently* undefined behavior — it does not become safe.

This is not new code — it predates the HIGH fixes. It surfaced now because testing runs under the VS debugger.

### Why it's worse than a stray exception — the storm

The engine uses **controls as its source of truth**. In `HandleQuoteUpdates` Block A (entry-order repositioning):

```vb
Await UpdateLimitOrderWithOTOCOAsync(bestBid)          ' API edit SUCCEEDS on Deribit
AppendColoredText(txtLogs, $"Order repositioned: ${currentPlacedPrice:F2} → ${bestBid:F2}", ...)
txtPlacedPrice.Text = bestBid                          ' THROWS cross-thread → write never lands
```

The reposition decision is `bestBid > (placedPrice + 3)`, where `placedPrice` is read from `txtPlacedPrice`. Because the write throws, `txtPlacedPrice` stays at the **stale** pre-reposition value, so **every subsequent quote tick re-triggers a reposition** → a flood of `private/edit` to Deribit → Deribit drops the socket ("remote party closed the WebSocket connection") → auto-reconnect → order still open, price still stale → floods again. Observed live as an infinite loop across repeated reconnect cycles.

Evidence (live log): `Order repositioned: $58912.00 → $58920.00` repeating with the **same from-price**, each followed by `Error in HandleQuoteUpdates: …txtPlacedPrice`, interleaved with `WebSocket Error: The remote party closed…` and `Reconnection attempt 1/10`.

### Confirmed throwing sites (not exhaustive — see §4)
- **Block A** `HandleQuoteUpdates`: `txtPlacedPrice.Text = bestBid` → flood (above).
- **Block B** `HandleQuoteUpdates` (triggered-SL reposition): `txtPlacedStopLossPrice.Text = newStopPrice.ToString("F2")`. Its `Catch` then sets `lastStopLossUpdate = DateTime.MinValue`, resetting the throttle so it **retries every tick** — a second amplifier.
- **`filled` branch** of `HandleOrderPositionUpdates`: `lblOrderStatus.Text = "In Position"` (the `open`/`untriggered` branches are already wrapped in `Me.Invoke`; the `filled` branch is not).

## 2. Impact

1. **Connection-killing edit flood** to Deribit — repeated `private/edit` every tick → socket drops → reconnect loop. This is an **account-abuse risk**, not just log noise.
2. **Stop-loss safety:** the emergency market-stop path (`UpdateStopLossForTriggeredStopLossOrder` → cancel + `btnReduceMarket.PerformClick`) runs the same cross-thread code. In a price **gap through** the resting SL limit (so it doesn't fill), this path is what closes the position — and it would throw instead of firing → **open position left unprotected**.

## 3. Required outcome (what "fixed" means)

1. **No WinForms control is read or written from the receive-loop thread (or anything it calls).** Audit the entire call tree (§4), not just the three sites above.
2. **Engine decisions must not depend on control state.** The values that drive repositioning and SL logic (placed price, order amount, SL trigger/limit prices, manual TP/SL, TP offset, comms, market-stop threshold, trade direction) must come from **backing fields owned by the engine**, kept current synchronously — so a display update that's slow/failed can **never** make a decision read stale data (this is what kills the runaway). Controls become **display mirrors only**.
3. **All display updates** (textboxes, labels, button text/colour/enabled) from the receive thread go through one thread-safe marshalling path (`Invoke`/`BeginInvoke`). `AppendColoredText` already self-marshals — leave logging as-is.
4. **No retry-amplifiers:** replace `lastStopLossUpdate = DateTime.MinValue`-on-error with a bounded backoff, so a persistent failure cannot loop every tick.
5. **Defensive guard:** don't attempt repositions/edits when `webSocketClient` is not `Open` (avoids piling edits into a closing socket during a drop).
6. Behaviour preserved: same reposition/SL/trailing semantics, same order payloads. This is a threading/state-ownership fix, not a logic change.

## 4. Recommended approach

**Primary: introduce backing fields + a marshalling helper.**

- Add engine-owned fields for the hot-path state, e.g. `placedPrice`, `orderAmount`, `slTriggerPrice`, `slLimitPrice`, `manualTP`, `manualSL`, `tpOffset`, `comms`, `marketStopThreshold`, plus the existing `TradeMode`. The engine reads/writes these fields directly (thread-safe for simple value types; use `SyncLock` only if you find a genuine race).
- Populate them from the UI **on the UI thread** at the point the user sets them / places an order (e.g. in the button handlers and `ExecuteOrderAsync`, which already run on the UI thread), and whenever the engine changes one (e.g. after a reposition, set `placedPrice = bestBid` on the field).
- The receive-loop decisions read the **fields**. The corresponding textbox is updated for display via the marshalling helper.
- Add helpers, e.g.:
  ```vb
  Private Sub UiInvoke(action As Action)
      If Me.IsHandleCreated AndAlso Me.InvokeRequired Then Me.BeginInvoke(action) Else action()
  End Sub
  ```
  Use it for every display write currently done directly on the receive thread. For the `filled` branch of `HandleOrderPositionUpdates`, wrap its control writes the same way the `open`/`untriggered` branches already do.
- For `btnReduceMarket.PerformClick()` in the emergency path: replace the cross-thread `PerformClick` with a direct call to the underlying reduce logic (the same method the click handler calls) so it doesn't depend on UI-thread control invocation — or marshal it. Prefer calling the logic directly.

**Acceptable alternative** if cleaner: route each receive-loop handler's body onto the UI thread (so all access is legal) — but only if you preserve the single `ReceiveAsync` loop on its own thread and don't reintroduce ordering/reentrancy issues. The backing-field approach is preferred because it also removes the stale-state runaway by construction and aligns with audit #10 (engine decoupled from controls).

### Call-tree inventory (audit all; this is the known surface)
Receive-thread entry points (all called from `ReceiveWebSocketMessagesAsync`): `HandleHeartbeat`, `HandleQuoteUpdates`, `HandleIndexUpdates`, `HandleBalanceUpdates`, `HandleOrderPositionUpdates`, `HandleTokenRefreshResponse`, `HandleAccountSummaryResponse`, `HandleRateLimitError`, `HandleMarginEstimationResponse`.
Of these, control-touching (verify each): `HandleQuoteUpdates` (Blocks A–E), `HandleIndexUpdates` (`lblIndexPrice`, `txtComms`), `HandleBalanceUpdates` (equity/balance/session labels; also **reads `lblIndexPrice.Text`** on the receive thread), `HandleOrderPositionUpdates` (esp. `filled` + position-closed branches), `HandleMarginEstimationResponse`→`ProcessPositionData` (margin labels).
Async helpers called from the above that read controls on the receive thread: `UpdateLimitOrderWithOTOCOAsync`, `UpdateStopLossForTriggeredStopLossOrder`, `UpdateStopLossForTrailingOrder`, `TrailingStopLossOrderAsync`, `ForceStopLossUpdate`, `CancelOrderAsync`, `SendReduceOrderAsync`, `RecordCompletedTrade`, `LogTradeDecision`. (`txtAmount`, `txtManualTP/SL`, `txtTakeProfit`, `txtStopLoss`, `txtTrigger`, `txtTPOffset`, `txtComms`, `txtMarketStopLoss`, `txtPlacedStopLossPrice`, `txtPlacedPrice` are read/written across these.)

## 5. Acceptance / test plan (owner tests on the test sub-account, under the VS debugger)

- **No cross-thread exceptions** anywhere during: connect, quotes flowing, placing a limit order, repositioning, SL trigger, position close.
- **No reposition runaway:** place a limit order in a moving market — `Order repositioned` fires only on genuine >$3 moves, the from-price **advances** each time, and there is **no per-tick repeat with a stale from-price**.
- **No edit-flood / socket drops** caused by the above (occasional unrelated reconnects are fine).
- **SL trigger:** drive price through the stop — the triggered-SL reposition and (if armed) the emergency market-stop run without throwing.
- **Blank-field safety still holds** (HIGH #6): clearing `txtManualTP/SL`/placed-price mid-trade doesn't break SL handling.
- Build green (`dotnet build`, 0 errors) after each commit.

## 6. Ground rules

1. Implement directly; commit locally per logical change (e.g. `Fix cross-thread: back placed-price with a field; marshal display updates`).
2. **Do NOT push.** Owner pushes after testing.
3. Build gate: 0 errors after each commit.
4. Stay in scope: this bug. Note anything else for the gated MEDIUM specs; don't fold #9/#10 in here (but this fix is a stepping stone toward #10 — keep it compatible).
5. Don't reintroduce hardcoded secrets; don't change order sizing, stop placement, or scoring.

## 7. Implementation report (REQUIRED — after implementation, for review)

Produce a report for the owner to paste back so the reviewer can check the actual code:
1. **Approach taken** (backing fields vs marshal-handler) and why.
2. **Per-site summary:** every control access you moved off the receive thread — function, control, before/after — and the new field set with where each is populated.
3. **Runaway fix:** show how the reposition decision now reads fresh state (the exact field + where it's updated).
4. **Retry-amplifier fix** and the connection-state guard.
5. **Build status**, **commits** (local hashes), and **deviations**.
6. **Test results / steps** for the owner per §5.
7. **Confirmation:** no control access remains on the receive thread (how you verified — e.g. a grep of the call tree); did not push; build green; behaviour preserved.
