# Spec — Decouple v2 (#10): public automation API, placement acks, scoped cancel

**Date:** 2026-07-03
**Supersedes:** `spec-medium-decouple.md` (2026-07-02). Same intent — a clean contract on `frmMainPageV2` for the incoming DeribitVerdictEngine — extended with everything landed/learned since: the position model (`d314fff..5da2e5b`), the silent-rejection ack gap (audit F2, rollback half), the **scoped-cancel finding** (runtime test 2026-07-02: the ATR-slippage guard's `cancel_all` killed an existing position's protective legs), and the cross-process thread contract.
**Recommended implementer:** Fable 5 at **high** (preferred — in the review window) or Opus at **high**. API design over live-order internals; payload parity and invariant preservation are the whole game.
**Target:** `DeribitOrderPlacementApp\frmMainPageV2.vb` only. Anchors reflect pushed HEAD **`5da2e5b`**. Locate by symbol + code anchor; line numbers drift.
**Build/commits:** `dotnet build DeribitOrderPlacementApp.sln` → 0 errors / 0 warnings before each commit; three commits (§4–§6); local only, never push.

---

## 1. Context

The VerdictEngine (separate process, `C:\Dev\DeribitVerdictEngine`) will drive this app through an IPC host that calls a **public API** on `frmMainPageV2`. Today no such API exists: auto-trade fakes button clicks, reads/writes the other form's controls, and mixes the passed instance with the VB default instance. Additionally:

- **Silent rejections + phantom state:** entry placements share JSON-RPC id 2, success/error responses are unhandled (only logged since audit2 fix 5), and `placedPrice`/`placedStopLossPrice` are seeded optimistically at send — a rejected order leaves phantom engine state until the next placement.
- **The slippage guard is a sledgehammer:** on excessive slippage the reposition blocks call `CancelOrderAsync()` = `private/cancel_all_by_instrument`, which also cancels an *existing position's* OCO TP/SL legs → naked position (observed live 2026-07-02).

This spec fixes both and builds the API on top, so the first external consumer gets acks, scoped cancels, and thread safety from day one.

### Invariants (binding; violating these has caused live incidents)

1. **No WinForms control access on the receive thread.** New receive-path code (`HandlePlacementResponse`, scoped-cancel core) touches engine fields only; displays via `UiInvoke`/`AppendColoredText` (self-marshalling).
2. **Public API methods must be callable from ANY thread** and marshal internally (§6 pattern). The VerdictEngine host will not be on the UI thread.
3. **Do not disturb:** the reposition gates and their ordering (`IsCancelPending()` before the `isRepositioning` acquire), the position model (`positionSizeUSD`/`positionAvgEntry` + the avg-entry retention rule), the reduce-reposition context, `BackoffStopLossRetry`, the null-safe id-guard style (`HasValue AndAlso`).
4. **`cancelPending` lifecycle stays:** set on cancels, cleared by cancelled-echo/placement, 4s self-clear. The scoped cancel uses the SAME flag/window (§4).
5. **Payload parity:** an order placed through `PlaceAutomatedOrder` must produce byte-equivalent params (modulo the unique request id) to the manual button path.
6. New code follows house style; this file stays `Option Strict Off` — be precise with conversions.

### Design decisions (owner may veto; flag deviations)

- **Two-step API** (`SetTradeTargets` then `PlaceAutomatedOrder`): targets are written to the input textboxes on the UI thread, flowing through the existing `TextChanged → SyncTradeInputsFromUi` mirror mechanism — identical to today's btnATR-paste + click flow. No rewrite of `ExecuteOrderAsync`'s input plumbing during this window. (A parameter-direct v2 of the API can come post-VerdictEngine.)
- **v1 placement policy (strict):** `PlaceAutomatedOrder` refuses unless connected ∧ rate-limit OK ∧ **flat** (`positionSizeUSD = 0`) ∧ **no working entry** (`CurrentOpenOrderId Is Nothing`) ∧ not cancel-pending. The engine flattens first if it wants to flip. Manual buttons keep today's freedom.
- **Unique ids for ALL entry placements** (manual buttons included) from a reserved base (600000+). Manual placements get rejection-rollback for free — the phantom-`placedPrice` wart dies app-wide.
- **Rollback = restore, not zero:** the pending-placement registry snapshots `placedPrice`/`placedStopLossPrice` *before* the optimistic seed; a rejection restores the snapshots (zeroing `placedStopLossPrice` could stall an actively-trailing position SL).
- **Timeout ≠ rejection:** if no response arrives in 5 s, the ack returns `Accepted=False, Reason="timeout"` and state is **not** rolled back — the order may exist; echoes remain the source of truth.
- **Scoped cancel cancels the OTOCO primary only** (`private/cancel` on `CurrentOpenOrderId`); Deribit cancels the linked untriggered children with the primary — the test plan verifies this; if children survive on the test account, stop and report back (the spec then adds child cancels).

---

## 2. What already exists (do not re-create)

`IsWebSocketConnected`, `CanMakeAPIRequest` (public, keep). `USDPublicSession` public field (FrmIndicators reads it; keep until retirement — §6 adds the `SessionPnLUSD` property alongside). `ExecuteAutomatedOrder(orderType)` — **zero callers; delete in commit 3** (superseded by `PlaceAutomatedOrder`).

---

## 3. New shared plumbing (used by commits 1–3)

Add near the reduce-reposition/position-model fields:

```vb
    ' ============ Decouple v2 (docs/spec-decouple-v2.md) ============
    ' Unique JSON-RPC ids for entry placements (manual + API). Responses are consumed by
    ' HandlePlacementResponse; HandleUnhandledJsonRpcError skips this range (single owner).
    Private Const PlacementIdBase As Integer = 600000
    Private nextPlacementId As Integer = PlacementIdBase

    ' One entry per in-flight placement. Snapshots restore engine state on rejection
    ' (restore, not zero - a zero could stall an actively-trailing position SL).
    Private Class PendingPlacement
        Public RequestId As Integer
        Public Tcs As TaskCompletionSource(Of PlacementResult)   ' Nothing for manual placements
        Public PrevPlacedPrice As Decimal
        Public PrevPlacedSL As Decimal
        Public CreatedUtc As DateTime = DateTime.UtcNow
    End Class
    Private ReadOnly pendingPlacements As New Concurrent.ConcurrentDictionary(Of Integer, PendingPlacement)

    ' Ack result surfaced to API callers (and, later, over the IPC pipe).
    Public Class PlacementResult
        Public Property Accepted As Boolean
        Public Property OrderId As String     ' entry order id when accepted
        Public Property Reason As String      ' reject reason / "timeout" / gate refusal
    End Class
```

`Imports System.Collections.Concurrent` may be added at the top (or fully qualify, as above).

---

## 4. Commit 1 — scoped cancel for the working entry

**Problem recap:** `IsATRSlippageExcessive` → `Await CancelOrderAsync()` in the two entry-reposition blocks and the two trailing-reposition blocks of `HandleQuoteUpdates`. `CancelOrderAsync` is `cancel_all_by_instrument` — correct for the button and position-close cleanup, wrong for "abandon this entry": it also cancels the position's protective legs.

### 4a. The core (new method, near `CancelOrderAsync`)

```vb
    ' Scoped cancel (docs/spec-decouple-v2.md): abandon the WORKING ENTRY only. Cancels the OTOCO
    ' primary by id (Deribit cancels the linked untriggered children with it - verified in tests);
    ' an existing position's legs (PositionTPOrderId/PositionSLOrderId), its triggered-SL trailing
    ' state (SLTriggered/placedStopLossPrice/StopLossTriggerOriginal), and the trailing/position
    ' flags are deliberately NOT touched. Receive-thread safe: fields + self-marshalling output.
    Private Async Function CancelWorkingEntryCoreAsync(reason As String) As Task
        Dim entryId As String = CurrentOpenOrderId
        If entryId Is Nothing Then Return

        Dim cancelPayload As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", 31},
            {"method", "private/cancel"},
            {"params", New JObject From {{"order_id", entryId}}}
        }
        Await SendWebSocketMessageAsync(cancelPayload.ToString())

        ' Same transition-race protection as the nuclear cancel: gate repositions/echo-seeding
        ' for the window, drop the entry context, reset the entry price.
        cancelPending = True
        cancelPendingSince = DateTime.UtcNow
        CurrentOpenOrderId = Nothing
        CurrentTPOrderId = Nothing
        CurrentSLOrderId = Nothing
        placedPrice = 0D
        ResetOrderAttempt() ' reset ATR slippage tracking for the next attempt

        UiInvoke(Sub()
                     txtPlacedPrice.Text = "0"
                     txtPlacedTakeProfitPrice.Text = "0"
                     txtPlacedTrigStopPrice.Text = "0"
                     If positionSizeUSD <> 0D Then
                         lblOrderStatus.Text = "In Position"
                         lblOrderStatus.ForeColor = Color.Yellow
                     Else
                         lblOrderStatus.Text = "Awaiting Orders"
                         lblOrderStatus.ForeColor = Color.DeepSkyBlue
                     End If
                 End Sub)

        AppendColoredText(txtLogs, $"Working entry cancelled ({reason}) - position legs untouched", Color.Yellow)
    End Function
```

Note what is **absent** vs `CancelOrderAsync`: no `placedStopLossPrice = 0D`, no `SLTriggered`/`isTrailing*`/`StopLossTriggerOriginal` resets, no `ReduceOrderId` clears, no margin-display clearing, no `txtPlacedStopLossPrice` reset. That asymmetry is the point — document it, don't "harmonize" it.

### 4b. Call-site swap

In `HandleQuoteUpdates`, replace **all four** slippage-guard cancel calls — the two in the entry-reposition block and the two in the trailing-reposition block (anchor: `If maxSlippageATRchecked And IsATRSlippageExcessive(` … `Await CancelOrderAsync()`) — with:

```vb
                                    Await CancelWorkingEntryCoreAsync("ATR slippage")
```

The commented-out `'Return` lines next to them stay as they are. `CancelOrderAsync` itself, the Cancel-All button, position-close cleanup, and the emergency market-stop path are **untouched**.

**Acceptance (commit 1):** build green; grep — `CancelWorkingEntryCoreAsync` has exactly 4 call sites in `HandleQuoteUpdates` (+1 public wrapper added in commit 3); `CancelOrderAsync()` no longer appears inside the two reposition/two trailing slippage branches but still exists everywhere else.

---

## 5. Commit 2 — placement acks + rejection rollback

### 5a. Unique ids in the placement payloads

- `ExecuteOrderAsync` gains a parameter: `Private Async Function ExecuteOrderAsync(TypeOfOrder As String, Optional requestId As Integer = 0) As Task`. At the top of the `Try`, before the `Select Case`: nothing. **Immediately before** `Await SendWebSocketMessageAsync(OrderPayload.ToString())`, insert:

```vb
            ' Decouple v2: unique id per placement + registry entry (snapshots for rejection
            ' rollback). Manual buttons pass no id -> self-allocate, no ack awaiter.
            Dim reqId As Integer = If(requestId > 0, requestId, Interlocked.Increment(nextPlacementId))
            RegisterPendingPlacement(reqId)
```

  and change the payload id line `New JProperty("id", 2),` → `New JProperty("id", reqId),` in `OrderPayload`.
- `StopLossForTrailingOrderAsync` (the `EntryTrailingOrder` placement): same treatment — self-allocated `reqId` + `RegisterPendingPlacement(reqId)` before its send; payload `New JProperty("id", 2),` → `New JProperty("id", reqId),`.
- Helper (near the §3 fields):

```vb
    ' Registers a placement just before its send. If the API pre-registered this id (Tcs attached),
    ' keep that entry; otherwise create a snapshot-only entry. Sweeps stale entries (>60s) as hygiene.
    Private Sub RegisterPendingPlacement(reqId As Integer)
        If Not pendingPlacements.ContainsKey(reqId) Then
            pendingPlacements(reqId) = New PendingPlacement With {.RequestId = reqId}
        End If
        Dim entry = pendingPlacements(reqId)
        entry.PrevPlacedPrice = placedPrice
        entry.PrevPlacedSL = placedStopLossPrice
        For Each stale In pendingPlacements.Values.Where(Function(pp) (DateTime.UtcNow - pp.CreatedUtc).TotalSeconds > 60).ToList()
            Dim removed As PendingPlacement = Nothing
            pendingPlacements.TryRemove(stale.RequestId, removed)
        Next
    End Sub
```

  (`Imports System.Linq` is already implicit in the project; if `.Where` fails to resolve, use a plain `For Each` over `pendingPlacements.Values` with an ids list. Snapshots are (re)taken here — immediately before the optimistic seed that follows the send — so a restore lands on the true pre-placement values.)

### 5b. The response handler

New method + dispatch wiring. In `ReceiveWebSocketMessagesAsync`, insert **before** `HandleUnhandledJsonRpcError(response)`:

```vb
                ' Decouple v2: placement acks/rejections (ids >= PlacementIdBase)
                HandlePlacementResponse(response)
```

The handler:

```vb
    ' Consumes success/error responses for entry placements (ids >= PlacementIdBase). On rejection:
    ' restores the pre-placement engine snapshots (placedPrice/placedStopLossPrice were seeded
    ' optimistically at send) and completes the ack. On success: completes the ack with the order id.
    ' Runs on the receive thread - engine fields + self-marshalling output only.
    Private Sub HandlePlacementResponse(response As String)
        Try
            If response.IndexOf("""id""", StringComparison.Ordinal) < 0 Then Return
            Dim json = JObject.Parse(response)
            Dim messageId = json.SelectToken("id")?.ToObject(Of Integer)()
            If Not (messageId.HasValue AndAlso messageId.Value >= PlacementIdBase) Then Return

            Dim entry As PendingPlacement = Nothing
            If Not pendingPlacements.TryRemove(messageId.Value, entry) Then Return ' unknown/stale id

            Dim errorField = json.SelectToken("error")
            If errorField IsNot Nothing Then
                Dim code = errorField.SelectToken("code")?.ToObject(Of Integer)()
                Dim msg = errorField.SelectToken("message")?.ToString()
                Dim data = errorField.SelectToken("data")?.ToString(Newtonsoft.Json.Formatting.None)
                ' Rollback: restore, don't zero (a zero could stall an actively-trailing SL).
                placedPrice = entry.PrevPlacedPrice
                placedStopLossPrice = entry.PrevPlacedSL
                UiInvoke(Sub()
                             txtPlacedPrice.Text = entry.PrevPlacedPrice.ToString("F2")
                             txtPlacedStopLossPrice.Text = entry.PrevPlacedSL.ToString("F2")
                         End Sub)
                AppendColoredText(txtLogs,
                    $"ORDER REJECTED (id {messageId.Value}): code {If(code?.ToString(), "?")} - {msg}{If(data IsNot Nothing, " | " & data, "")} - engine state rolled back",
                    Color.Red)
                entry.Tcs?.TrySetResult(New PlacementResult With {.Accepted = False, .Reason = $"{code}: {msg}"})
                Return
            End If

            Dim orderId = json.SelectToken("result.order.order_id")?.ToString()
            entry.Tcs?.TrySetResult(New PlacementResult With {.Accepted = True, .OrderId = orderId})
        Catch
            ' Parse noise: ignore, like the other handlers.
        End Try
    End Sub
```

### 5c. Single ownership of the id range

In `HandleUnhandledJsonRpcError`, after the existing dedicated-handler skip, add:

```vb
            If messageId.HasValue AndAlso messageId.Value >= PlacementIdBase Then Return ' HandlePlacementResponse owns placements
```

**Acceptance (commit 2):** build green. Runtime: a 1-USD manual placement now logs `ORDER REJECTED (id 6000xx): … - engine state rolled back` (instead of the generic `API ERROR (id 2 …)`), the PnL label returns to 0 (no phantom), and `txtPlacedPrice` shows the pre-placement value. A valid placement behaves exactly as before (echoes drive context; the success response is consumed silently).

**Note:** the success path deliberately does **not** seed `CurrentOpenOrderId` — echoes remain the single writer of order context in v1 (less invariant churn). Flag in the report if you disagree; don't change it unilaterally.

---

## 6. Commit 3 — the public automation API

All in a banner-marked region near the existing `'For AUTOMATED ORDER PLACEMENT` block (which it replaces/extends).

### 6a. `SetTradeMode` refactor

Extract the mode-switching bodies of `btnSell_Click`/`btnBuy_Click` into `Private Sub SetTradeMode(isLong As Boolean)` (the full body of each handler — TradeMode assignment, button colors/text, label text, control relocations — becomes the two branches of this sub). The two click handlers become one-liners calling `SetTradeMode(True/False)`. Behavior byte-identical; this gives the API a UI-thread-safe way to set direction without `PerformClick`.

### 6b. Delete the dead stub

Remove `Public Async Function ExecuteAutomatedOrder(orderType As String) As Task` (zero callers — verified 2026-07-02).

### 6c. Read-only state properties

```vb
    ' ===== PUBLIC AUTOMATION API (contract: docs/integration-contract-verdictengine.md) =====
    ' Thread contract: every member here is callable from ANY thread.

    Public ReadOnly Property OpenPositionSizeUSD As Decimal   ' signed: + long / - short
        Get
            Return positionSizeUSD
        End Get
    End Property

    Public ReadOnly Property OpenPositionAvgEntry As Decimal
        Get
            Return positionAvgEntry
        End Get
    End Property

    Public ReadOnly Property SessionPnLUSD As Decimal
        Get
            Return USDPublicSession
        End Get
    End Property

    Public ReadOnly Property HasWorkingEntryOrder As Boolean
        Get
            Return CurrentOpenOrderId IsNot Nothing
        End Get
    End Property

    Public ReadOnly Property IsFlat As Boolean
        Get
            Return positionSizeUSD = 0D
        End Get
    End Property
```

(Names avoid case-insensitive collisions with the private fields. `IsWebSocketConnected`/`CanMakeAPIRequest` already exist and complete the read surface.)

### 6d. `SetTradeTargets`

```vb
    ' Writes the trade-input textboxes on the UI thread; TextChanged syncs the engine mirrors -
    ' the same path the manual flow and the old btnATR paste use. Nothing/negative = leave as is.
    Public Sub SetTradeTargets(Optional takeProfit As Decimal? = Nothing,
                               Optional triggerDistance As Decimal? = Nothing,
                               Optional stopLoss As Decimal? = Nothing,
                               Optional sizeUSD As Decimal? = Nothing,
                               Optional manualTP As Decimal? = Nothing,
                               Optional manualSL As Decimal? = Nothing)
        Dim apply As Action = Sub()
                                  If takeProfit.HasValue AndAlso takeProfit.Value >= 0D Then txtTakeProfit.Text = takeProfit.Value.ToString()
                                  If triggerDistance.HasValue AndAlso triggerDistance.Value >= 0D Then txtTrigger.Text = triggerDistance.Value.ToString()
                                  If stopLoss.HasValue AndAlso stopLoss.Value >= 0D Then txtStopLoss.Text = stopLoss.Value.ToString()
                                  If sizeUSD.HasValue AndAlso sizeUSD.Value > 0D Then txtAmount.Text = sizeUSD.Value.ToString()
                                  If manualTP.HasValue AndAlso manualTP.Value >= 0D Then txtManualTP.Text = manualTP.Value.ToString()
                                  If manualSL.HasValue AndAlso manualSL.Value >= 0D Then txtManualSL.Text = manualSL.Value.ToString()
                              End Sub
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then Me.Invoke(apply) Else apply()
    End Sub
```

### 6e. `PlaceAutomatedOrder`

```vb
    ' Places an entry with the current targets. v1 policy: STRICT - refuses unless connected,
    ' rate-limit OK, flat, no working entry, and no cancel pending (the engine flattens first if
    ' it wants to flip). side: "long"/"short". kind: "limit"|"market"|"nospread". Returns the
    ' exchange ack (or a gate refusal / 5s timeout). Callable from any thread.
    Public Async Function PlaceAutomatedOrder(side As String, Optional kind As String = "limit",
                                              Optional ackTimeoutMs As Integer = 5000) As Task(Of PlacementResult)
        ' Gates (fields only - safe on any thread)
        If Not IsWebSocketConnected Then Return New PlacementResult With {.Accepted = False, .Reason = "not connected"}
        If Not CanMakeAPIRequest Then Return New PlacementResult With {.Accepted = False, .Reason = "rate limit"}
        If IsCancelPending() Then Return New PlacementResult With {.Accepted = False, .Reason = "cancel pending"}
        If positionSizeUSD <> 0D Then Return New PlacementResult With {.Accepted = False, .Reason = "position open (flatten first)"}
        If CurrentOpenOrderId IsNot Nothing Then Return New PlacementResult With {.Accepted = False, .Reason = "working entry exists"}

        Dim isLong As Boolean
        Select Case If(side, "").ToLowerInvariant()
            Case "long", "buy" : isLong = True
            Case "short", "sell" : isLong = False
            Case Else : Return New PlacementResult With {.Accepted = False, .Reason = $"unknown side '{side}'"}
        End Select

        Dim typeOfOrder As String
        Select Case If(kind, "").ToLowerInvariant()
            Case "limit" : typeOfOrder = If(isLong, "BuyLimit", "SellLimit")
            Case "market" : typeOfOrder = If(isLong, "BuyMarket", "SellMarket")
            Case "nospread" : typeOfOrder = If(isLong, "BuyNoSpread", "SellNoSpread")
            Case Else : Return New PlacementResult With {.Accepted = False, .Reason = $"unknown kind '{kind}'"}
        End Select

        ' Pre-register the ack BEFORE the placement seeds engine state (snapshots re-taken at send).
        Dim reqId As Integer = Interlocked.Increment(nextPlacementId)
        Dim tcs As New TaskCompletionSource(Of PlacementResult)(TaskCreationOptions.RunContinuationsAsynchronously)
        pendingPlacements(reqId) = New PendingPlacement With {.RequestId = reqId, .Tcs = tcs}

        ' Marshal the placement onto the UI thread (ExecuteOrderAsync reads controls) and await it
        ' from this thread. Control.Invoke of a Function(Of Task) returns the Task to await.
        Dim placeCall As Func(Of Task) = Function()
                                             SetTradeMode(isLong)
                                             Return ExecuteOrderAsync(typeOfOrder, reqId)
                                         End Function
        If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
            Await CType(Me.Invoke(placeCall), Task)
        Else
            Await placeCall()
        End If

        ' Await the exchange ack with a timeout. Timeout <> rejection: no rollback (the order may
        ' exist; echoes remain the source of truth) - the caller re-queries state.
        Dim done = Await Task.WhenAny(tcs.Task, Task.Delay(ackTimeoutMs))
        If done Is tcs.Task Then Return tcs.Task.Result
        Dim ignored As PendingPlacement = Nothing
        pendingPlacements.TryRemove(reqId, ignored)
        Return New PlacementResult With {.Accepted = False, .Reason = "timeout"}
    End Function
```

### 6f. Action wrappers

```vb
    ' Flatten the actual position at market (position-model sized; emergency-grade path).
    Public Async Function FlattenPositionAsync() As Task
        Await SendReduceMarketOrderAsync()
    End Function

    ' Nuclear: cancel every order on the instrument (the Cancel-All button's path).
    Public Async Function CancelAllOrdersAsync() As Task
        Await CancelOrderAsync()
    End Function

    ' Scoped: abandon the working entry only; an existing position's legs stay.
    Public Async Function CancelWorkingEntryAsync() As Task
        Await CancelWorkingEntryCoreAsync("API request")
    End Function
```

(`SendReduceMarketOrderAsync` and `CancelWorkingEntryCoreAsync` are already receive-thread/any-thread safe — fields only.)

**Acceptance (commit 3):** build green; manual Buy/Sell buttons behave identically (mode colors/layout swaps intact via `SetTradeMode`); `ExecuteAutomatedOrder` gone (grep 0); the API region compiles with the documented surface. Payload parity: with identical textbox values, `PlaceAutomatedOrder("long","limit")` and btnLimit produce identical params except the id (verify by temporarily uncommenting the payload log line in `ExecuteOrderAsync` during the owner test, then re-commenting).

---

## 7. Out of scope (do not do)

- **No FrmIndicators rewiring.** It keeps `PerformClick`/default-instance reads until retirement (tie-in spec). `USDPublicSession` stays a public field for it.
- No IPC host (that's the tie-in spec, per the integration contract).
- No ack plumbing for edits/cancels/reduces (entry placements only in v1).
- No parameter-direct `ExecuteOrderAsync` rewrite.
- The 500 ms margin-estimate delay on manual buttons stays (housekeeping item); note `PlaceAutomatedOrder` deliberately does NOT inherit it.

---

## 8. Test plan

**Implementer-verifiable:** build green ×3; greps per commit above; plus — `HandlePlacementResponse` appears exactly once in the dispatch, before `HandleUnhandledJsonRpcError`; the id-range skip exists in the generic logger; all four slippage call sites use the scoped cancel; no new `.Text` reads outside UI-thread-marshalled blocks.

**Owner runtime (test sub-account, VS debugger):**
1. **Scoped cancel (the test-3 scenario):** open a position, place a second entry, let the ATR-slippage guard fire (small `ATRSlip` value helps) → the entry disappears, **the position's TP/SL legs survive on the Deribit UI**, status shows In Position, log says "position legs untouched". **Verify the OTOCO children of the cancelled entry are gone too** — if any untriggered child survives, STOP and report (spec §1 caveat).
2. **Rejection rollback:** 1-USD manual placement → `ORDER REJECTED (id 6000xx) … engine state rolled back`, placed-price display restored, PnL label back to 0. Exactly one red line (no duplicate from the generic logger).
3. **Normal placement regression:** a valid manual limit entry + reposition + close behaves exactly as before (unique id changes nothing else).
4. **Mode buttons:** Buy/Sell toggle identical to before (colors, labels, layout swap).
5. **API smoke (under debugger):** call `PlaceAutomatedOrder("long", "limit")` from the Immediate window (or a temporary `#If DEBUG` button if preferred) while flat → order placed with current targets, returns `Accepted=True` + order id; call again while the entry works → `Accepted=False, Reason="working entry exists"`. Full API exercise waits for the tie-in.

---

## 9. Implementation report (required)

`docs/impl-report-decouple-v2.md`: per commit — exact changes (before → after), build results, deviations with justification, the §5b "success path doesn't seed context" acknowledgment, payload-parity evidence (test 3 of §8), and suspicious-nearby items not touched. The owner pastes it back for code-level review; write it to be checkable, not persuasive.
