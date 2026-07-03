# Implementation report — Reduce-limit order repositioning

**Spec:** `docs/spec-reduce-reposition.md` (2026-07-02)
**Branch:** `housekeeping-now`
**Commits:** `ec64ab3` (1/2: context fields + echo capture/clear + CancelOrderAsync reset), `1a79135` (2/2: HandleQuoteUpdates chase block + edit sender)
**Build:** `dotnet build DeribitOrderPlacementApp.sln` → **0 errors / 0 warnings** after each commit.
**Prerequisite check:** `spec-audit2-quickfixes.md` HAS landed (fix 5's `RequestNameForId` present at `:919` pre-change; audit2 commits `39d7e82..638eb46` on the branch). The 223349 extension was therefore applied, not skipped.

All line numbers below are **post-change** positions in `DeribitOrderPlacementApp\frmMainPageV2.vb` unless marked "pre".

---

## Change 1 — engine fields (commit 1)

- **Where:** after `Private PositionTPOrderId, PositionSLOrderId As String` (pre `:1640`), i.e. directly under the `CurrentOpenOrderId/TP/SL` anchor the spec named. Now `:1642-1648`.
- **What:** the four fields verbatim from spec §3 (`ReduceOrderId`, `reduceOrderPrice`, `reduceOrderAmount`, `reduceOrderIsBuy`) with the spec's two comment lines.
- **Deviation:** none. (Placed after the *pair* of ID lines rather than between them — "next to the existing order-context fields" satisfied.)

## Change 2 — seed at placement (commit 1)

- **Where:** `SendReduceOrderAsync`, immediately after `Await SendWebSocketMessageAsync(payload.ToString())` and before the `orderDescription` confirmation log. Now `:2748-2754`.
- **What:** spec §4 block verbatim — limit orders only (`If Not isMarketOrder`), seeds `reduceOrderPrice = If(price, 0D)`, `reduceOrderAmount = amount`, `reduceOrderIsBuy = (direction = "buy")`.
- **Deviation:** none. `price` (`Decimal?`) and `direction` (`String`) confirmed in scope as spec assumed.

## Change 3 — open-echo capture (commit 1)

- **Where:** `HandleOrderPositionUpdates`, `orderState = "open"` branch, inside the existing `Me.Invoke(Sub() Select Case label …)`, new cases appended after `Case "TrailingStopLoss"` and before `End Select`. Now `:1817-1832`.
- **What:** spec §5 verbatim — `Case "ReduceLimitOrder"` gated on `Not cancelPending`: captures `ReduceOrderId = orderId`, always refreshes `reduceOrderIsBuy` from the echo's `direction` token, seeds price/amount only when 0 (single-writer). Plus the documentation-only `Case "ReduceMarketOrder"` no-op line (compiled without ambiguity, so kept).
- **Note:** `price` (`:1682` in-branch) and `orderId` (`:1672`) were already extracted as the spec stated; `order` is the loop variable and is captured by the lambda the same way the existing cases use it.
- **Deviation:** none.

## Change 4 — context clears (commit 1)

- **A. Filled echo** (`orderState = "filled"`, `Case "ReduceLimitOrder"`): three clear lines added at the end of the case, *after* the existing P/L computation (untouched) and before `Case "ReduceMarketOrder"`. Now `:1969-1972`.
- **B. Cancelled echo** (`orderState = "cancelled"`, `Case "ReduceLimitOrder"`): same three lines after the existing `OpenPositions = True`. Now `:2000-2003`.
- **C. `CancelOrderAsync`:** three clear lines directly under the existing `CurrentOpenOrderId/TP/SL = Nothing` nulls. Now `:2671-2673`.
- Branches A and B run on the receive thread outside any `Invoke` — engine-field writes only, as the spec specified.
- **Deviation:** none.

## Change 5 — chase block + edit sender + id name (commit 2)

- **Chase block:** inserted after the entry-reposition block's `Finally / Interlocked.Exchange(isRepositioning, 0) / End Try / End If` (pre `:1345`) and before the triggered-SL comment (`'For keeping triggered stop loss order…`). Now `:1347-1377`. Verbatim spec §7: gates in order `IsWebSocketConnected` → `Not IsCancelPending()` → `ReduceOrderId IsNot Nothing` → `reduceOrderPrice > 0D AndAlso reduceOrderAmount > 0D` → `Interlocked.Exchange(isRepositioning, 1) = 0`; buy chases bid up (`bestBid > reduceOrderPrice + 3`), sell chases ask down (`bestAsk < reduceOrderPrice - 3`); rate-limiter null-check + `CanMakeRequest`/`ConsumeCredits`; `Try/Finally` releases the shared guard.
- **`SendReduceRepositionEdit`:** placed after `SendRateLimitedUpdate` (spec's suggested spot). Now `:2884-2907`. Verbatim §7: id `223349`, `private/edit` with `ReduceOrderId`/`newPrice`/`reduceOrderAmount`, yellow reposition log, then the runaway-fix synchronous `reduceOrderPrice = newPrice`, red catch log. No textbox mirror, by design.
- **`RequestNameForId`:** edit-id case extended `223344…223348, 223350` → `…223348, 223349, 223350`. Now `:926`.
- **Deviation:** none.

## Verification

- Build 0/0 after each commit (Debug, `DeribitOrderPlacementApp.sln`).
- §9 grep assertions all pass:
  - New block: `(Not IsCancelPending())` at `:1352`, textually before `Interlocked.Exchange(isRepositioning, 1)` at `:1355`.
  - `Finally / Interlocked.Exchange(isRepositioning, 0)` at `:1374-1375` inside the new block.
  - `CancelOrderAsync` clears all three context fields (`:2671-2673`).
  - Filled (`:1970-1972`) and cancelled (`:2001-2003`) echo branches both clear the context.
- Entry-reposition, triggered-SL, and trailing blocks untouched (invariant 6): the only diff inside `HandleQuoteUpdates` is the inserted block between the entry block and the triggered-SL comment.

## Invariant compliance

1. No control access on the receive thread — new code touches engine fields only; output via self-marshalling `AppendColoredText`. The Change-3 case runs inside the *existing* `Me.Invoke` (UI thread), same as its sibling cases.
2. `IsCancelPending()` short-circuits before the single-flight acquire (structure copied from the entry block).
3. Shares `isRepositioning` with `Try/Finally` release.
4. Echo seeds price/amount only when 0; direction is always refreshed (spec-mandated exception to the seed rule).
5. `reduceOrderPrice = newPrice` advances synchronously immediately after the edit send, before anything else can tick.
6. `cancelPending` lifecycle untouched beyond the §6 clears.

## Known limitations (owner-approved in §2, restated)

- **One tracked reduce order at a time.** A second reduce limit overwrites the context (placement-side seed + next open echo repoints `ReduceOrderId`); the first order stays on the book, un-repositioned.
- No ATR-slippage guard on the reduce chase (intentional — exit intent).
- Partial fills: edits re-send `reduceOrderAmount` (the original total); Deribit's reduce-only flag caps at position size.
- Leeway ±3 hardcoded, matching the entry blocks (magic-number cleanup tracked separately).

## Suspicious-nearby, NOT touched (scope discipline)

- **Filled-echo P/L for reduce limits still keys off `TradeMode` and `placedPrice`** (`:1936-1967`): if the mode toggle is flipped after entry, or `placedPrice` was zeroed by "Cancel all open" before the reduce order was placed (the spec's own test scenario 1), the logged P/L direction/size can be wrong. Pre-existing behavior; the spec forbade changing the P/L computation.
- **`SendReduceOrderAsync` uses request id `1`** (`:2739`), shared with subscriptions — `RequestNameForId` reports it as " subscribe/reduce order". A placement *rejection* (e.g. post-only cross) is loggable via the audit2 error handler but not distinguishable by id; the context fields would then be seeded with no order on the book. Harmless (no `ReduceOrderId` ⇒ chase block never fires; next placement re-seeds), but worth knowing during test 1.
- The `Case "ReduceMarketOrder"` in the *filled* branch flags `OpenPositions = True` "for cleanup" even when flat — pre-existing, untouched.
