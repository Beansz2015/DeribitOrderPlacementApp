# Spec — Audit-2 quick fixes (F1, F3, F6, F4, F2-logger, + reduce-market log)

**Date:** 2026-07-02 (fixes 6 and 7 added later the same day, after owner runtime tests surfaced them)
**Source:** `docs/CODE_AUDIT_FABLE5.md` findings F1, F3, F6, F4, the *logger half* of F2, plus two runtime-test findings.
**Recommended implementer:** Fable 5 at **medium** effort, or Opus at **high**. This spec is prescriptive — the job is precise application and verification, not design.
**Target:** seven small, independent fixes in `DeribitOrderPlacementApp\frmMainPageV2.vb`, one commit each.
**Status note:** fixes 1–6 were implemented (`39d7e82..0e57f2a`) and runtime-verified on 2026-07-02. **Fix 7 (section 6c) is the only outstanding item** — its anchors reflect the file *after* fixes 1–6.

---

## 1. Context (read once, self-contained)

- Repo: `DeribitOrderPlacementApp` — .NET 9 WinForms, VB.NET, live Deribit BTC-PERPETUAL trading front-end over WebSocket. All fixes land in **`frmMainPageV2.vb`** (the live trading form, ~4,500 lines).
- Branch: **`housekeeping-now`**, HEAD `e5735ef`. All line numbers below are valid at that commit; each fix also includes the exact current code as a search anchor — **match on the code, use line numbers as hints.**
- Build gate: `dotnet build DeribitOrderPlacementApp.sln` must be **0 errors / 0 warnings** after every commit. It is green at HEAD.
- Git: **commit locally, one commit per fix, never push.** Commit-message pattern: `Audit2 fix (n/5): <summary>`.
- `Option Strict Off` is on for this file — do not change it, and be careful: implicit conversions compile silently here.
- **Scope discipline:** change exactly what each fix specifies. You will see other problems nearby (there are known ones, catalogued in `docs/CODE_AUDIT_FABLE5.md`). Do **not** fix them — note them in the implementation report instead.

### Invariants that must survive (violating these has caused live incidents)

The WebSocket receive loop (`ReceiveWebSocketMessagesAsync` and every handler it calls, including `HandleOrderPositionUpdates` and `UpdateStopLossForTriggeredStopLossOrder`) runs on a **thread-pool thread**:

1. **No WinForms control reads or writes on the receive thread.** Decisions read engine backing fields (`placedPrice`, `marketStopThreshold`, `marketStopLossChecked`, …). `AppendColoredText` self-marshals and is safe to call from any thread. Fix 2 below must use the existing `marketStopLossChecked` field — **never** `chkMarketStopLoss.Checked`.
2. **Do not reorder or move the `CancelOrderAsync()` call** in the position-closed branch (Fix 1 adds a read *before* it; nothing else moves). `CancelOrderAsync` owns the `cancelPending` lifecycle and the `placedPrice = 0` reset.
3. **Do not add writes to `placedPrice`/`placedStopLossPrice`** anywhere. Fix 1 adds a *read* only.
4. **Nullable id guards must be null-safe.** In the new handler (Fix 5), gate with `messageId.HasValue AndAlso …`. Never use `<>` against a nullable (VB three-valued logic: `Nothing <> 3` is `Nothing` → falls through unexpectedly).
5. Do not touch the reposition gates, `isRepositioning`, or anything in `HandleQuoteUpdates`.

---

## 2. Fix 1 of 5 — record the real entry price (audit F1)

**Problem.** In `HandleOrderPositionUpdates`, the position-closed branch calls `Await CancelOrderAsync()` (which sets `placedPrice = 0D` before returning) and *then* passes `placedPrice` to `RecordCompletedTrade` as the entry price. Every DB-recorded trade gets `EntryPrice = 0`. The P/L amount (`PorLAmt`) is computed earlier in the orders loop and is correct — only the entry price is wrong.

**Location.** `frmMainPageV2.vb` ~`:1966` (branch start) and ~`:2015-2025` (the record call).

Anchor — branch start:

```vb
                                    If size = 0 Then ' Position has been closed

                                        'Reset all flags
                                        OpenPositions = False
```

Anchor — record call (later in the same branch):

```vb
                                        'To record to DB
                                        ' In HandleOrderPositionUpdates
                                        If PorLAmt > 0 Then
                                            Dim tradeId = RecordCompletedTrade(
                                                placedPrice,
                                                ExecPrice,
```

**Change.**

1. Immediately after `If size = 0 Then ' Position has been closed`, add:

```vb
                                        ' Audit2 F1: snapshot the entry price BEFORE CancelOrderAsync zeroes
                                        ' placedPrice, so the DB record gets the real entry, not 0.
                                        Dim entryPriceAtClose As Decimal = placedPrice
```

2. In the `RecordCompletedTrade` call, replace the first argument `placedPrice` with `entryPriceAtClose`. Nothing else in the argument list changes.

**Must not:** move the `Await CancelOrderAsync()` call, change flag resets, or alter `PorLAmt` logic.

**Acceptance:** build green; the only reference change is that one argument. (Known limitation, out of scope: `orderAmountVal`/`TradeMode` can also be stale if the user edits mid-trade — leave as is.)

---

## 3. Fix 2 of 5 — checkbox is the master switch for the emergency market-stop (audit F3)

**Problem.** Two emergency market-stop paths exist. The quote-handler path correctly checks the `chkMarketStopLoss` mirror field before firing. The path **inside `UpdateStopLossForTriggeredStopLossOrder`** checks only `marketStopThreshold > 0D` — so with the checkbox **unchecked** and a non-zero `txtMarketStopLoss` (Designer default is `"70"`), a normal SL-trail tick can still cancel everything and market-close the position. Intended semantics: the checkbox is the master enable; threshold 0/blank additionally disables.

**Location.** `frmMainPageV2.vb` ~`:2758-2770`, inside `UpdateStopLossForTriggeredStopLossOrder`.

Anchor — current code:

```vb
            If marketStopThreshold > 0D AndAlso (TradeMode = True) AndAlso (StopLossTriggerOriginal - newPrice >= marketStopThreshold) Then
```

and the matching `ElseIf`:

```vb
            ElseIf marketStopThreshold > 0D AndAlso (TradeMode = False) AndAlso (newPrice - StopLossTriggerOriginal >= marketStopThreshold) Then
```

**Change.** Prepend `marketStopLossChecked AndAlso` to **both** conditions:

```vb
            If marketStopLossChecked AndAlso marketStopThreshold > 0D AndAlso (TradeMode = True) AndAlso (StopLossTriggerOriginal - newPrice >= marketStopThreshold) Then
```

```vb
            ElseIf marketStopLossChecked AndAlso marketStopThreshold > 0D AndAlso (TradeMode = False) AndAlso (newPrice - StopLossTriggerOriginal >= marketStopThreshold) Then
```

Also extend the comment block directly above (the one beginning `' Your existing emergency market order logic first.`) with one line:

```vb
            ' Audit2 F3: chkMarketStopLoss (via the marketStopLossChecked mirror - receive thread!) is the
            ' master enable for this emergency market close; threshold 0/blank additionally disables.
```

**Must not:** use `chkMarketStopLoss.Checked` (this function runs on the receive thread — invariant 1). Do **not** remove the existing `marketStopLossChecked` check in `HandleQuoteUpdates` (~`:1323`) — the double gate is intentional defense in depth.

**Acceptance:** build green; both emergency branches contain `marketStopLossChecked AndAlso`; behavior with the box checked is unchanged.

---

## 4. Fix 3 of 5 — reconnect on graceful server close (audit F6)

**Problem.** In `ReceiveWebSocketMessagesAsync`, the server-initiated Close frame exits the loop with `reconnectNeeded` still `False`, so no reconnect is ever attempted. Deribit closes gracefully during maintenance and when heartbeat replies go missing — after that the app sits idle until some later send fails.

**Location.** `frmMainPageV2.vb` ~`:645-648`.

Anchor — current code:

```vb
                If result.MessageType = WebSocketMessageType.Close Then
                    AppendColoredText(txtLogs, "Server closed connection gracefully", Color.Yellow)
                    Exit While
                End If
```

**Change.**

```vb
                If result.MessageType = WebSocketMessageType.Close Then
                    ' Audit2 F6: a server-initiated close is a dead connection - schedule recovery.
                    ' isClosing (checked below) still suppresses this during user-initiated shutdown.
                    AppendColoredText(txtLogs, "Server closed connection - scheduling reconnect", Color.Yellow)
                    reconnectNeeded = True
                    Exit While
                End If
```

**Interactions that are already correct — do not modify:** the reconnect block at the bottom of this function already checks `If Not isClosing` (so `btnClose`'s close handshake won't trigger a reconnect), and `HandleWebSocketDisconnect` has an `Interlocked` single-flight guard (so a Close reply received during an in-progress reconnect is absorbed).

**Acceptance:** build green; the Close branch sets `reconnectNeeded = True`; no other reconnect logic changed.

---

## 5. Fix 4 of 5 — manual TP/SL edit buttons: wrong id field + missing Return (audit F4)

**Problem.** Two bugs in the manual edit buttons (both are UI-thread `Click` handlers — control access is fine here):

- `btnEditSLPrice_Click` tests `PositionTPOrderId` (the **TP** id) but assigns `PositionSLOrderId` — wrong field in the condition.
- **Both** `btnEditTPPrice_Click` and `btnEditSLPrice_Click` log "Order ID not found for edit." and then **fall through and send** `private/edit` with `order_id = Nothing` anyway (missing `Return`). The exchange error is currently invisible (that's Fix 5's job to surface, and this fix's job to prevent).

**Location A.** `btnEditSLPrice_Click`, ~`:4008-4014`. Anchor — current code:

```vb
                If CurrentSLOrderId IsNot Nothing Then
                    SLOrderID = CurrentSLOrderId
                ElseIf PositionTPOrderId IsNot Nothing Then
                    SLOrderID = PositionSLOrderId
                Else
                    AppendColoredText(txtLogs, "S.L. Order ID not found for edit.", Color.Yellow)
                End If
```

**Change A:**

```vb
                If CurrentSLOrderId IsNot Nothing Then
                    SLOrderID = CurrentSLOrderId
                ElseIf PositionSLOrderId IsNot Nothing Then
                    SLOrderID = PositionSLOrderId
                Else
                    AppendColoredText(txtLogs, "S.L. Order ID not found for edit.", Color.Yellow)
                    Return ' Audit2 F4: don't send an edit with a null order_id
                End If
```

**Location B.** `btnEditTPPrice_Click`, ~`:3959-3965`. Anchor — current code (the id fields here are already correct; only the `Return` is missing):

```vb
                    If CurrentTPOrderId IsNot Nothing Then
                        TPOrderID = CurrentTPOrderId
                    ElseIf PositionTPOrderId IsNot Nothing Then
                        TPOrderID = PositionTPOrderId
                    Else
                        AppendColoredText(txtLogs, "T.P. Order ID not found for edit.", Color.Yellow)
                    End If
```

**Change B:** add `Return ' Audit2 F4: don't send an edit with a null order_id` after the `AppendColoredText` line in the `Else` branch.

**Acceptance:** build green; both `Else` branches end in `Return`; the SL condition tests `PositionSLOrderId`.

---

## 6. Fix 5 of 5 — generic JSON-RPC error logger (audit F2, logger half only)

**Problem.** No handler surfaces JSON-RPC **error** responses except: code 10028 (`HandleRateLimitError`), id 3 (`HandleTokenRefreshResponse`), id 999 (`HandleAccountSummaryResponse`), ids 777/890 (`HandleMarginEstimationResponse`). Everything else — **order placement rejections (id 2), cancel failures (id 30), edit failures (ids 223344–223350), reduce-order rejections (id 1)** — is dropped silently while the log optimistically says "order placed".

**Scope limit — logging only.** Do **not** mutate any engine state (`placedPrice`, order ids, flags) on error. State rollback is deliberately deferred to the #10 decouple work.

**Change 1 — new handler.** Add these two methods near the other `Handle*` subs (suggested: directly after `HandleRateLimitError`, ~`:876`):

```vb
    ' Audit2 F2 (logger half): surface JSON-RPC error responses that no dedicated handler owns.
    ' Ids 3/999/777/890 already log their own errors in their handlers; code 10028 is owned by
    ' HandleRateLimitError. Everything else (entry orders id 2, cancels id 30, edits 223344-223350,
    ' reduce orders id 1, subscribes) was previously dropped silently - a rejected order looked
    ' identical to a working one. LOGGING ONLY: no engine state is touched here (rollback is #10's job).
    Private Sub HandleUnhandledJsonRpcError(response As String)
        Try
            ' Fast path: skip the JSON parse for the vast majority of messages (quotes, echoes).
            If response.IndexOf("""error""", StringComparison.Ordinal) < 0 Then Return

            Dim json = JObject.Parse(response)
            Dim errorField = json.SelectToken("error")
            If errorField Is Nothing Then Return

            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            ' Skip errors that already have dedicated logging (null-safe: HasValue AndAlso, never <>)
            If messageId.HasValue AndAlso
               (messageId.Value = 3 OrElse messageId.Value = 999 OrElse
                messageId.Value = 777 OrElse messageId.Value = 890) Then Return

            Dim errorCode = errorField.SelectToken("code")?.ToObject(Of Integer)()
            If errorCode.HasValue AndAlso errorCode.Value = 10028 Then Return ' HandleRateLimitError owns 10028

            Dim errorMessage = errorField.SelectToken("message")?.ToString()
            Dim errorData = errorField.SelectToken("data")?.ToString(Newtonsoft.Json.Formatting.None)

            AppendColoredText(txtLogs,
                $"API ERROR (id {If(messageId?.ToString(), "-")}{RequestNameForId(messageId)}): " &
                $"code {If(errorCode?.ToString(), "?")} - {errorMessage}" &
                $"{If(errorData IsNot Nothing, " | " & errorData, "")}",
                Color.Red)
        Catch
            ' Parse noise / unexpected shapes: ignore, like the other handlers.
        End Try
    End Sub

    ' Best-effort request-class hint for the ad-hoc id space (see CODE_AUDIT_FABLE5.md F15).
    Private Function RequestNameForId(messageId As Integer?) As String
        If Not messageId.HasValue Then Return ""
        Select Case messageId.Value
            Case 2 : Return " auth/entry order"
            Case 30 : Return " cancel-all/trailing stop"
            Case 1 : Return " subscribe/reduce order"
            Case 1001 : Return " set_heartbeat"
            Case 223344, 223345, 223346, 223347, 223348, 223350 : Return " order edit"
            Case Else : Return ""
        End Select
    End Function
```

**Change 2 — wire into the receive dispatch.** In `ReceiveWebSocketMessagesAsync`, after the last handler call (`HandleMarginEstimationResponse(response)`, ~`:672`), add:

```vb
                ' Audit2 F2: surface JSON-RPC errors no dedicated handler owns (silent order rejections)
                HandleUnhandledJsonRpcError(response)
```

**Notes.**
- `AppendColoredText` self-marshals — safe from the receive thread. No control access in the new code.
- Yes, this adds one more parse per error-bearing message on top of the known parse-per-handler pattern (audit #9); the string fast-path keeps the hot path (quotes/echoes) parse-free. The parse-once dispatcher remains deferred — do not attempt it here.
- `Newtonsoft.Json.Formatting` must be fully qualified (the file imports only `Newtonsoft.Json.Linq`).

**Acceptance:** build green; the handler is called once per received message; an intentionally invalid order (see test plan) produces a red `API ERROR (id 2 …)` log line.

---

## 6b. Fix 6 of 6 — manual market-reduce fills log a stale price of 0 (runtime-test finding)

**Problem.** The `ReduceMarketOrder` fill echo logs `"Position executed at {newPricePublic}."` — but `newPricePublic` is only ever set by the **emergency market-stop path** (`UpdateStopLossForTriggeredStopLossOrder`). A manual `btnReduceMarket` press leaves it `0` (or stale from a past emergency), so the owner sees `Position executed at 0.` Also `"Loss: Check order history."` is hardcoded — a market reduce is not necessarily a loss.

Confirmed live in the 2026-07-02 runtime test (two manual market reduces, both logged `Position executed at 0`).

**Location.** `HandleOrderPositionUpdates`, `orderState = "filled"` branch, `Case "ReduceMarketOrder"` (~`:1861-1869`).

Anchor — current code:

```vb
                                    Case "ReduceMarketOrder"
                                        OpenPositions = True 'Actually no positions but flagged true to use code in openpositions segment for cleanup
                                        If _indicators.IsAutoTradingEnabled Then
                                            LogTradeDecision("Exit Position - Market Order Loss", 0, 0) 'For autotrade log for when trade exit position
                                        End If
                                        ResetOrderAttempt() ' Reset ATR slippage tracking
                                        AppendColoredText(txtLogs, $"Position executed at {newPricePublic}.", Color.Crimson)
                                        AppendColoredText(txtLogs, $"Loss: Check order history.", Color.Crimson)
```

**Change.** Replace the two `AppendColoredText` lines with:

```vb
                                        ' Audit2 fix 6: log the echo's actual fill price - newPricePublic is only set by the
                                        ' emergency market-stop path, so manual reduces printed a stale 0. Wording neutralized
                                        ' (a market reduce is not necessarily a loss).
                                        Dim reduceFill = order.SelectToken("average_price")?.ToObject(Of Decimal?)()
                                        AppendColoredText(txtLogs, $"Position reduced at {If(reduceFill?.ToString("F2"), If(newPricePublic > 0D, newPricePublic.ToString("F2"), "?"))} (market order).", Color.Crimson)
                                        AppendColoredText(txtLogs, "P/L not tracked for market reduces - check order history.", Color.Crimson)
```

Leave the `LogTradeDecision(...)` line and the flags exactly as they are (the auto-trade file log is out of scope). This case runs on the **receive thread** outside any `Invoke` — the change touches no controls (`AppendColoredText` self-marshals), which is why it must read the price from the echo, not from a textbox.

**Acceptance:** build green; a manual market reduce logs the real average fill price; the emergency path (which sets `newPricePublic`) still logs a price via the fallback.

---

## 6c. Fix 7 of 7 — scratch closes: no log, no DB record (runtime-test finding)

**Problem.** The position-closed reporting block and the `RecordCompletedTrade` call are both gated on `PorLAmt > 0`. `PorLAmt` is rounded to 2 decimals, so a close whose P/L rounds to **$0.00** (e.g. long 10 @ 60300 exited at 60291.50 → $0.0014) produces **no log lines and no DB row** — the operator gets zero feedback on going flat, and the trade statistics silently under-count real trades. Confirmed live in the 2026-07-02 runtime test.

**Design.** The discriminator between "computed close" and "untracked close" is **`label4DB`**: every fill case that computes P/L (`TakeLimitProfit`, `StopLossOrder`, `TrailingStopLoss`, `ReduceLimitOrder`) also sets `label4DB`; `ReduceMarketOrder` sets neither (it has its own fix-6 logging and is untracked **by design** — keep it that way). So:

- Scratch **logging**: add an `ElseIf label4DB IsNot Nothing` branch to the profit/loss message block.
- Scratch **recording**: change the DB gate from `If PorLAmt > 0` to `If label4DB IsNot Nothing` — a strict superset of the old behavior (every case that sets `PorLAmt > 0` also sets `label4DB`), adding exactly the scratch closes.
- **No `LogTradeDecision` call** in the scratch branch: that function has no scratch case and would append an empty line to `AutoTradeLog.txt`. Deliberate omission — note it, don't extend the function (scope).
- Known cosmetic: a scratch records `IsProfit` from whatever `PorL` the computing branch chose, so View Trades shows it as WIN or LOSS with $0.00. Acceptable; do not add logic for it.

**Location.** `HandleOrderPositionUpdates`, `size = 0` branch — the block directly after the margin-display clearing. Both anchors are in the file **after fixes 1–6** (the record call already passes `entryPriceAtClose`).

Anchor A — the close-message block; add the `ElseIf` before its `End If`:

```vb
                                        ElseIf (PorL = False) And (PorLAmt > 0) Then
                                            AppendColoredText(txtLogs, $"Position executed at {ExecPrice}.", Color.Crimson)
                                            AppendColoredText(txtLogs, $"Loss of: ${PorLAmt}.", Color.Crimson)

                                            If _indicators.IsAutoTradingEnabled Then
                                                LogTradeDecision("Exit Position - Loss", PorLAmt, ExecPrice) 'For autotrade log for when trade exit position
                                            End If

                                        End If
```

becomes:

```vb
                                        ElseIf (PorL = False) And (PorLAmt > 0) Then
                                            AppendColoredText(txtLogs, $"Position executed at {ExecPrice}.", Color.Crimson)
                                            AppendColoredText(txtLogs, $"Loss of: ${PorLAmt}.", Color.Crimson)

                                            If _indicators.IsAutoTradingEnabled Then
                                                LogTradeDecision("Exit Position - Loss", PorLAmt, ExecPrice) 'For autotrade log for when trade exit position
                                            End If

                                        ElseIf label4DB IsNot Nothing Then
                                            ' Audit2 fix 7: a tracked close whose P/L rounds to $0.00 (scratch) previously
                                            ' logged nothing and was never recorded. Market reduces (label4DB Is Nothing)
                                            ' keep their fix-6 logging and stay untracked by design. No LogTradeDecision
                                            ' call here: it has no scratch branch and would write an empty file line.
                                            AppendColoredText(txtLogs, $"Position executed at {ExecPrice}.", Color.Yellow)
                                            AppendColoredText(txtLogs, "Scratch close: P/L ≈ $0.00.", Color.Yellow)
                                        End If
```

Anchor B — the DB gate:

```vb
                                        'To record to DB
                                        ' In HandleOrderPositionUpdates
                                        If PorLAmt > 0 Then
                                            Dim tradeId = RecordCompletedTrade(
                                                entryPriceAtClose,
```

becomes:

```vb
                                        'To record to DB
                                        ' In HandleOrderPositionUpdates
                                        ' Audit2 fix 7: record every computed close (label4DB set), including $0.00
                                        ' scratches - real trades whose absence biased the stats. Market reduces
                                        ' (label4DB Is Nothing) remain untracked by design.
                                        If label4DB IsNot Nothing Then
                                            Dim tradeId = RecordCompletedTrade(
                                                entryPriceAtClose,
```

**Must not:** touch the `ReduceMarketOrder` case, `LogTradeDecision`, `CancelOrderAsync`, or anything else in the branch. This block runs on the receive thread — the new lines touch no controls (`AppendColoredText` self-marshals), and `ExecPrice`/`label4DB` are handler locals.

**Acceptance:** build green; a close with P/L ≥ $0.01 behaves exactly as before; a scratch close logs the two yellow lines and appears in View Trades with P/L 0.00; a market reduce still logs only the fix-6 lines and records nothing.

---

## 7. Commit plan

One commit per fix, in this order, each building clean before commit:

1. `Audit2 fix (1/6): record real entry price before cancel zeroes placedPrice` ✅ `39d7e82`
2. `Audit2 fix (2/6): chkMarketStopLoss gates the internal emergency market-stop path` ✅ `48ae3ec`
3. `Audit2 fix (3/6): reconnect on server-initiated graceful close` ✅ `8f79894`
4. `Audit2 fix (4/6): edit-button SL id condition + missing Returns on not-found` ✅ `53789e8`
5. `Audit2 fix (5/6): generic JSON-RPC error logger for unowned error responses` ✅ `56248d2`
6. `Audit2 fix (6/6): market-reduce fill logs actual price, not stale newPricePublic` ✅ `0e57f2a`
7. `Audit2 fix (7/7): scratch closes log and record (label4DB gate replaces PorLAmt gate)` — outstanding

---

## 8. Test plan

**Implementer-verifiable (do all):**
- `dotnet build` → 0 errors / 0 warnings after each commit.
- Grep assertions: both emergency branches contain `marketStopLossChecked AndAlso`; both edit-button `Else` branches contain `Return`; `RecordCompletedTrade(` call no longer passes `placedPrice` directly; `HandleUnhandledJsonRpcError(response)` appears exactly once in the receive dispatch.

**Owner runtime tests (test sub-account, under the VS debugger as usual):**
1. **Fix 5 + Fix 1 together:** place a limit order with an invalid amount (e.g. `1` — below Deribit's 10-USD contract step) → expect the green "order placed" line *followed by* a red `API ERROR (id 2 auth/entry order): …` line. Then place a valid small trade, exit via reduce-limit or TP, open **View Trades** → entry price must equal the actual entry, not 0.
2. **Fix 4:** with no open orders, type a value into the placed-TP textbox and click `btnEditTPPrice` → expect "T.P. Order ID not found for edit." and **no** red API error afterwards (previously a null-id edit was sent silently). Same for `btnEditSLPrice`.
3. **Fix 2:** code-review acceptance is sufficient. Optional staged test: uncheck `chkMarketStopLoss`, set `txtMarketStopLoss` to a small value, enter a tiny position, let the SL trigger, verify only normal SL repositions occur (no "Emergency … Market Order Executed"); re-check the box and verify the emergency close fires again.
4. **Fix 3:** hard to stage — accept via code review; it will prove itself the next time Deribit closes the socket (log should show "scheduling reconnect" followed by the normal recovery sequence).
5. **Fix 6:** enter a small position, press the market-reduce button → the log must show `Position reduced at <real price> (market order).` instead of `Position executed at 0.`
6. **Fix 7:** enter a small position (amount 10) and exit via reduce-limit within a couple of ticks (the scenario that closed silently on 2026-07-02) → expect `Position executed at X` + `Scratch close: P/L ≈ $0.00.` + `Trade #N recorded in database`, and the row visible in View Trades with P/L 0.00. Then one market reduce → still **no** DB record (unchanged by design).

---

## 9. Implementation report (required)

Produce `docs/impl-report-audit2-quickfixes.md` containing, per fix: the exact change made (file/lines, before → after), the build result, any deviation from this spec **with justification**, and anything suspicious noticed nearby that you deliberately did *not* touch (per scope discipline). The owner pastes this report back into the reviewing conversation, where it is verified against the actual code — write it to be checkable, not persuasive.
