# Spec — Reduce-limit order repositioning

**Date:** 2026-07-02
**Origin:** owner runtime test 2026-07-02 — a resting reduce-only LIMIT order (btnReduceLimit) is static today; the owner wants it to track top-of-book like entry orders do.
**Recommended implementer:** Opus at **high**, or Fable 5 at **high**. This adds a new gate block to the receive-loop hot path (`HandleQuoteUpdates`) — invariant-sensitive work.
**Sequencing:** implement **after** `spec-audit2-quickfixes.md` lands (its fix 5, the JSON-RPC error logger, makes reduce-edit failures visible during this feature's testing).
**Target:** `DeribitOrderPlacementApp\frmMainPageV2.vb` only.

---

## 1. Context (self-contained)

- Repo: `DeribitOrderPlacementApp` — .NET 9 WinForms VB.NET Deribit BTC-PERPETUAL front-end. Branch **`housekeeping-now`**; build gate `dotnet build DeribitOrderPlacementApp.sln` → 0 errors / 0 warnings after every commit. Commit locally, **never push**.
- Today: `btnReduceLimit_Click` → `SendReduceOrderAsync(price, amount, direction, isMarketOrder:=False)` places a reduce-only limit order labeled `"ReduceLimitOrder"`. Nothing tracks its order id, so the reposition machinery (which requires the `CurrentOpenOrderId`/`CurrentTPOrderId`/`CurrentSLOrderId` entry trio) never touches it — the order sits where it was placed.
- Desired: the resting reduce order chases top-of-book with the same ±3 leeway as entry orders, so a position can be exited passively (maker) without manual re-placement.
- `Option Strict Off` in this file — implicit conversions compile silently; be precise.

### Invariants (must survive — violating these has caused live incidents)

`HandleQuoteUpdates` and the echo handler run on a **thread-pool receive thread**:

1. **No WinForms control access on the receive thread.** All new decision state lives in engine fields defined below. Display output goes through `AppendColoredText` (self-marshalling) only — this feature adds **no textbox mirror** (log lines only, by design).
2. **Gate ordering:** `Not IsCancelPending()` must be evaluated **before** `Interlocked.Exchange(isRepositioning, 1)` — short-circuit before acquiring the single-flight guard, or the guard leaks and wedges all repositioning. Copy the existing blocks' structure exactly.
3. **Single-flight:** the new block shares the existing `isRepositioning` guard (serializing reduce edits with entry/trailing edits), with the same `Try/Finally` release.
4. **Single-writer seeding:** exchange echoes seed engine prices **only when the field is 0** — a lagging echo must never reset a price the engine is actively advancing (this exact mistake caused a reposition-runaway flood).
5. **Runaway-fix pattern:** after a successful edit send, advance `reduceOrderPrice` **synchronously** before anything else, so the next tick compares against the new price even if the exchange echo lags.
6. Do not modify the entry-reposition, triggered-SL, or trailing blocks; do not touch the `cancelPending` lifecycle beyond the clearing specified in §6.

---

## 2. Design decisions (owner-approved defaults; flag deviations in the report)

- **Chase direction comes from the order, not the mode toggle.** A reduce **BUY** (closing a short) rests at the bid and chases **up** (edit when `bestBid > price + 3`); a reduce **SELL** (closing a long) rests at the ask and chases **down** (edit when `bestAsk < price - 3`). Direction is captured at placement and refreshed from the exchange echo — never derived from `TradeMode` at tick time, so flipping the Buy/Sell mode while a reduce order rests cannot invert the chase.
- **No ATR-slippage guard on the reduce chase.** The slippage guard protects *entry* quality; when reducing, the intent is to get out — chasing is desired. (The emergency market button remains the bail-out.)
- **One tracked reduce order at a time.** Placing a second reduce limit overwrites the context; the first order stops being repositioned (it stays on the book untouched). Acceptable limitation — note it in the report.
- **Partial fills:** edits re-send the original amount; Deribit treats `amount` as the order total and the reduce-only flag caps it at position size. Acceptable.
- **Edit id: `223349`** — currently unused in the id space (223344–223348, 223350 are taken).
- Leeway is the house constant **±3**, hardcoded like the entry blocks (magic-number cleanup is a separately tracked item — do not generalize here).

---

## 3. Change 1 — engine fields

Add next to the existing order-context fields (anchor: `Private CurrentOpenOrderId, CurrentTPOrderId, CurrentSLOrderId As String`, ~`:1585`):

```vb
    ' Reduce-limit reposition context (docs/spec-reduce-reposition.md). One tracked reduce order.
    ' Engine-owned; read/written on the receive thread - never read controls for these.
    Private ReduceOrderId As String = Nothing
    Private reduceOrderPrice As Decimal = 0D
    Private reduceOrderAmount As Decimal = 0D
    Private reduceOrderIsBuy As Boolean = False
```

---

## 4. Change 2 — seed the context at placement

In `SendReduceOrderAsync` (~`:2582`), after the `Await SendWebSocketMessageAsync(payload.ToString())` and before the confirmation log, add — **limit orders only**:

```vb
            If Not isMarketOrder Then
                ' Seed the reposition context at placement. The open echo captures the order id and
                ' re-seeds price/amount only when 0 (single-writer rule - see HandleOrderPositionUpdates).
                reduceOrderPrice = If(price, 0D)
                reduceOrderAmount = amount
                reduceOrderIsBuy = (direction = "buy")
            End If
```

(`price` is the `Decimal?` parameter; `direction` is the `"buy"`/`"sell"` string parameter — both already in scope.)

---

## 5. Change 3 — capture the order id from the exchange echo

In `HandleOrderPositionUpdates`, **"open"-state branch**, inside the existing `Me.Invoke(Sub() … Select Case label … End Select)` (~`:1632-1727`), add a new case alongside the existing ones:

```vb
                                                  Case "ReduceLimitOrder"
                                                      ' Reposition context: capture the id; seed price/amount only when
                                                      ' the engine doesn't already own them (single-writer). Direction
                                                      ' always refreshed from the exchange (source of truth).
                                                      If Not cancelPending Then
                                                          ReduceOrderId = orderId
                                                          reduceOrderIsBuy = (order.SelectToken("direction")?.ToString() = "buy")
                                                          If reduceOrderPrice = 0D Then
                                                              reduceOrderPrice = If(price, 0D)
                                                          End If
                                                          If reduceOrderAmount = 0D Then
                                                              reduceOrderAmount = If(order.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
                                                          End If
                                                      End If
                                                  Case "ReduceMarketOrder" ' (no-op here; market reduces are not repositioned)
```

Notes: `price` and `orderId` are already extracted in this branch. The amount/direction re-seed from the echo covers the reconnect/restart case where a reduce order already rests on the book with no placement-side seeding. Omit the `Case "ReduceMarketOrder"` line if it causes any ambiguity — it is documentation only.

---

## 6. Change 4 — clear the context when the order leaves the book

Three places:

**A. Filled echo** — existing `Case "ReduceLimitOrder"` in the `orderState = "filled"` branch (~`:1828`). At the end of that case (after the existing P/L computation, which must not change), add:

```vb
                                        ' Reduce order gone from the book - drop the reposition context.
                                        ReduceOrderId = Nothing
                                        reduceOrderPrice = 0D
                                        reduceOrderAmount = 0D
```

**B. Cancelled echo** — existing `Case "ReduceLimitOrder"` in the `orderState = "cancelled"` branch (~`:1881`). Add the same three lines.

**C. `CancelOrderAsync`** (~`:2533`) — next to the existing `CurrentOpenOrderId/TP/SL = Nothing` nulls, add:

```vb
        ReduceOrderId = Nothing
        reduceOrderPrice = 0D
        reduceOrderAmount = 0D
```

(The position-flat cleanup path calls `CancelOrderAsync`, so flat-position clearing comes for free. Deribit also auto-cancels reduce-only orders on flat; the cancelled echo then hits B — both paths converge.)

Branches A and B run on the receive thread **outside** any `Invoke` — engine-field writes only, no controls. Correct as specified.

---

## 7. Change 5 — the reposition block in `HandleQuoteUpdates`

Insert **after** the entry-reposition block's `End If` (the first block using `isRepositioning`, ending `Finally / Interlocked.Exchange(isRepositioning, 0) / End Try / End If`, ~`:1291`) and **before** the triggered-SL comment (`'For keeping triggered stop loss order at top of orderbook…`):

```vb
                'Reduce-limit reposition (docs/spec-reduce-reposition.md): keep a resting reduce-only
                'LIMIT order at top of book. Chase direction comes from the ORDER (reduceOrderIsBuy),
                'never TradeMode - a mode flip while the order rests must not invert the chase.
                'Same gate ordering as the entry block: IsCancelPending BEFORE the single-flight acquire.
                If IsWebSocketConnected _
                   AndAlso (Not IsCancelPending()) _
                   AndAlso ReduceOrderId IsNot Nothing _
                   AndAlso reduceOrderPrice > 0D AndAlso reduceOrderAmount > 0D _
                   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
                    Try
                        If reduceOrderIsBuy Then
                            ' Closing a short: reduce BUY rests at the bid - chase up
                            If bestBid IsNot Nothing AndAlso bestBid > (reduceOrderPrice + 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(bestBid)
                                End If
                            End If
                        Else
                            ' Closing a long: reduce SELL rests at the ask - chase down
                            If bestAsk IsNot Nothing AndAlso bestAsk < (reduceOrderPrice - 3) Then
                                If rateLimiter IsNot Nothing AndAlso rateLimiter.CanMakeRequest() Then
                                    rateLimiter.ConsumeCredits()
                                    Await SendReduceRepositionEdit(bestAsk)
                                End If
                            End If
                        End If
                    Finally
                        Interlocked.Exchange(isRepositioning, 0)
                    End Try
                End If
```

And the helper, placed near the other `private/edit` senders (suggested: after `SendRateLimitedUpdate`):

```vb
    ' Edits the tracked reduce-limit order to a new top-of-book price. Receive-thread safe:
    ' engine fields only; AppendColoredText self-marshals.
    Private Async Function SendReduceRepositionEdit(newPrice As Decimal) As Task
        Try
            Dim editPayload As New JObject From {
                {"jsonrpc", "2.0"},
                {"id", 223349},
                {"method", "private/edit"},
                {"params", New JObject From {
                    {"order_id", ReduceOrderId},
                    {"price", newPrice},
                    {"amount", reduceOrderAmount}
                }}
            }
            Await SendWebSocketMessageAsync(editPayload.ToString())

            AppendColoredText(txtLogs, $"Reduce order repositioned: ${reduceOrderPrice:F2} → ${newPrice:F2}", Color.Yellow)
            ' Runaway-fix pattern: advance engine state synchronously so the next tick compares
            ' against the new price even if the exchange echo lags.
            reduceOrderPrice = newPrice
        Catch ex As Exception
            AppendColoredText(txtLogs, $"Error repositioning reduce order: {ex.Message}", Color.Red)
        End Try
    End Function
```

Also update `RequestNameForId` (added by `spec-audit2-quickfixes.md` fix 5) — extend the edit-id case to include `223349`:

```vb
            Case 223344, 223345, 223346, 223347, 223348, 223349, 223350 : Return " order edit"
```

(If the quickfixes bundle has not landed yet, note this in the report and skip.)

---

## 8. Commit plan

Two commits:

1. `Reduce-reposition (1/2): context fields + echo capture/clear + CancelOrderAsync reset`
2. `Reduce-reposition (2/2): HandleQuoteUpdates chase block + edit sender`

Build green after each.

---

## 9. Test plan

**Implementer-verifiable:** build green; grep assertions — the new block checks `IsCancelPending()` textually *before* `Interlocked.Exchange(isRepositioning`, the `Finally` releases the guard, `CancelOrderAsync` clears all three context fields, both echo branches (filled + cancelled) clear the context.

**Owner runtime (test sub-account, VS debugger):**
1. Enter a short (limit, let it fill), press **Cancel all open** (legs cancelled, naked short — existing behavior), then **Reduce Buy**. Watch the reduce order on the Deribit UI: as the bid rises > $3 above it, expect `Reduce order repositioned: $X → $Y` and the order moving. Confirm it never chases *down*.
2. Let it fill → flat. Confirm reposition logs stop (context cleared), and no red `API ERROR … order edit` lines afterwards (a stale edit against the filled order would surface via the audit2 error logger — absence is the pass signal).
3. With a reduce order resting, press **Cancel all open** → order cancelled, no further reposition attempts, no API errors.
4. Mirror test on the long side (reduce SELL chases the ask down).
5. Regression: place a normal entry with OCO and confirm entry repositioning still works while no reduce order exists.

---

## 10. Implementation report (required)

Produce `docs/impl-report-reduce-reposition.md`: per change — exact lines touched (before → after), build results, deviations with justification, and anything suspicious noticed nearby that you did **not** touch (scope discipline). The owner pastes it back for code-level review; write it to be checkable, not persuasive.
