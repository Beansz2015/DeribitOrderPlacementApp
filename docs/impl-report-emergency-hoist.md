# Impl report — N1: emergency-check hoist above the SL-edit throttle (single-fire latch)

**Spec:** `docs/spec-emergency-hoist.md` (ROADMAP-2026-08 §3 N1).
**Seat:** Opus 5 high, fresh conversation, this spec alone. **Date:** 2026-07-27.
**Base:** `5ef58c4` (clean tree). **Target:** `DeribitOrderPlacementApp/frmMainPageV2.vb` only.
**Gate:** EXECUTED at the commit — **GATE PASSED, OrderCheck 104/104**. Local commit, not pushed.
**Runtime:** NOT run. Owner testnet acceptance (spec §Acceptance 2/3) is the remaining gate.

---

## 1. Before → after: where the check lives

**Before** (`HandleQuoteUpdates`, triggered-SL region):

```
If IsWebSocketConnected AndAlso (Not IsCancelPending()) AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing Then
    Dim currentTime = UtcNow
    If (currentTime - lastStopLossUpdate).Ms >= MinStopLossUpdateInterval Then      <-- THROTTLE
        Dim currentStopPrice = placedStopLossPrice
        If currentStopPrice > 0 Then
            <emergency locals: emergencyThreshold / emergencyThresholdValid / emgBaseline / baselineKnown>
            <priceMovement by side>
            If <threshold met> Then If marketStopLossChecked Then ForceStopLossUpdate(touch) : Return   <-- EMERGENCY, inside the throttle
            <chase machinery: shouldUpdate / newStopPrice / isSLRepositioning / 50%-force / latch-settle>
        End If
    Else
        <rate-limited log>
    End If
End If
```

**After** — the block gate is untouched; the throttle now starts *below* the emergency:

```
If IsWebSocketConnected AndAlso (Not IsCancelPending()) AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing Then
    Dim currentTime = UtcNow
    Dim currentStopPrice = placedStopLossPrice                                      <-- hoisted, one read
    <emergency locals, verbatim, hoisted>
    Dim priceMovement As Decimal = 0D
    If currentStopPrice > 0 Then
        <priceMovement by side, verbatim>
        If <threshold met> Then If marketStopLossChecked AndAlso Not emergencyFired Then ForceStopLossUpdate(touch) : Return
    End If                                                                          <-- EMERGENCY, every qualifying tick

    If (currentTime - lastStopLossUpdate).Ms >= MinStopLossUpdateInterval Then      <-- THROTTLE, unchanged
        If currentStopPrice > 0 Then
            <chase machinery: byte-identical, reusing the hoisted locals>
        End If
    Else
        <rate-limited log, unchanged>
    End If
End If
```

The emergency locals and the `priceMovement` computation were **moved, not copied** — the chase's
50 %-force decision (`priceMovement >= emergencyThreshold * 0.5`) still reads the same four locals,
now from the enclosing scope. `git diff -w` shows those lines as unchanged (indentation only),
which is the mechanical proof that the comparison was not re-derived.

`Return` after the emergency dispatch still exits `HandleQuoteUpdates` (skipping the trailing-entry
chase block below) exactly as before.

**Equivalence argument for the move:** the hoisted locals are pure reads of engine fields
(`marketStopThreshold`, `emergencyBaseline`, `StopLossTriggerOriginal`, `placedStopLossPrice`) plus
the tick's `bestBid`/`bestAsk` locals — no side effects, so evaluating them on throttled-out ticks
changes nothing. On a tick where the throttle *passes*, inputs and results are identical to before.
On a tick where it *doesn't*, the emergency is now evaluated where previously it was skipped — which
is the entire point of N1.

## 2. The latch — all six sites quoted

Declaration (`frmMainPageV2.vb:169`, immediately after `emergencyBaselineSettled`):

```vb
Private emergencyFired As Boolean = False
```

**Set — 2 sites, both fire paths, inside `UpdateStopLossForTriggeredStopLossOrder`** (`:4139-4144`,
`:4152-4154`). These two `SendReduceMarketOrderAsync` calls are the *only* M.SL emergency dispatches
in the file (verified: the other three `SendReduceMarketOrderAsync` occurrences are the declaration,
`FlattenPositionAsync`, and `btnReduceMarket_Click`). The historical "#68 quote / #71 send" pair is
**not** two dispatch sites — #68 is the quote handler *detecting* and routing in via
`ForceStopLossUpdate`, #71 is the same function's internal re-check firing off a normal chase edit.
Both converge on these two branches, so latching here covers both eras:

```vb
If marketStopLossChecked AndAlso Not emergencyFired AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = True) AndAlso (emgBaseline - newPrice >= marketStopThreshold) Then
    ' N1 single-fire latch: SET-THEN-SEND. ...
    emergencyFired = True
    Await CancelOrderAsync()
```

```vb
ElseIf marketStopLossChecked AndAlso Not emergencyFired AndAlso marketStopThreshold > 0D AndAlso emgBaseline > 0D AndAlso (TradeMode = False) AndAlso (newPrice - emgBaseline >= marketStopThreshold) Then
    ' N1 single-fire latch: SET-THEN-SEND (see the long branch above).
    emergencyFired = True
    Await CancelOrderAsync()
```

**Set-then-send is airtight, and provably so.** From the quote tick:
`Await ForceStopLossUpdate(...)` → `lastStopLossUpdate = MinValue` → `Await
UpdateStopLossForTriggeredStopLossOrder(...)` → branch test → `emergencyFired = True`. An async
method body runs **synchronously** to its first incomplete await; the first one on this path is
`Await CancelOrderAsync()`, *below* the assignment. So there is no yield between the test and the
set, and a re-entrant quote tick (HandleQuoteUpdates is `Async Sub` = fire-and-forget, so
re-entrancy is real) arriving during the close cannot pass the branch.

**Cleared — 3 sites** (see §4 for why 3 and not the spec's stated 2):

| # | Site | Line | Code |
|---|------|------|------|
| 1 | `CompletePositionClose` | `:4938` | `emergencyFired = False             ' N1: the trade is over - a new position gets its own single emergency` |
| 2 | `ExecuteOrderAsync` placement seed | `:3638` | `emergencyFired = False                ' N1: a fresh order re-establishes a clean emergency context too` |
| 3 | `StopLossForTrailingOrderAsync` placement seed | `:4535` | same line as #2 |

Sites 2 and 3 are exactly the two `cancelPending = False  ' transition-race fix: a fresh order
re-establishes a clean context` seeds, and the clear is placed immediately beside each — the same
paired-reset discipline as every other reset in the file. Site 1 sits with the existing
`emergencyBaseline = 0` / `emergencyBaselineSettled = False` block.

**NOT cleared** — verified by the grep in §5, which lists every occurrence: not in any SL-edit
path, not in `CancelOrderAsync`, not in `CancelWorkingEntryCoreAsync`, not at the `cancelPending =
False` teardown/timeout/raced-abort sites (`:372` timeout, `:1515` raced abort, `:3052` cancelled
echo). A mid-cancel emergency latch survives the cancel window, as specified.

**It is not an 8th SL-context reset site:** it zeroes no price, touches no commanded set, moves no
baseline. `ResetCommandedSLPrices()` stays at 8; `emergencyBaseline = 0` stays at 11.

## 3. Gating conditions — ticked one by one against the code

| # | Spec condition | Status | Evidence |
|---|---|---|---|
| 1 | Measure from the FROZEN `emergencyBaseline`, never `placedStopLossPrice`; `= 0` ⇒ no check | **PRESERVED, verbatim** | `Dim emgBaseline As Decimal = If(emergencyBaseline > 0D, emergencyBaseline, StopLossTriggerOriginal)` + `baselineKnown` moved unchanged; the fallback to `StopLossTriggerOriginal` (restore-hardening) is kept. `placedStopLossPrice` appears in the hoisted region only as `currentStopPrice`, used for the `> 0` guard and the chase — never in `priceMovement`. |
| 2 | `SLTriggered` context only | **PRESERVED** | The enclosing block gate `If IsWebSocketConnected AndAlso (Not IsCancelPending()) AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing` is untouched; the hoist lands *inside* it. |
| 3 | M.SL checkbox + value gating exactly as today | **PRESERVED** | `marketStopLossChecked` (the `chkMarketStopLoss` receive-thread mirror) and `emergencyThresholdValid = marketStopThreshold > 0D` (the `txtMarketStopLoss` mirror; blank-or-0 disables) both moved unchanged, in the same nested shape. The second, independent re-check inside `UpdateStopLossForTriggeredStopLossOrder` also still tests both. |
| 4 | `IsCancelPending()` keeps gating the emergency (2026-07-08 owner decision) | **PRESERVED — deliberately not ungated** | Same block gate as #2. No owner question raised: the hoisted check sits under the identical gate, so the ≤ 4 s cancel window still suppresses the emergency exactly as it did. (Note this is also load-bearing for anti-double-fire today — see §6 obs (c).) |
| 5 | Rate-limiter guard on the fire path; latch NOT set if the fire didn't happen | **N/A as written — see §4(b); intent honoured** | The M.SL fire path has **no** `CanMakeRequest` guard at HEAD and never had one: the emergency branch returns *before* the limiter section of `UpdateStopLossForTriggeredStopLossOrder`, and neither `CancelOrderAsync` nor `SendReduceMarketOrderAsync` consults the limiter. Nothing was added or removed. The spec's actual requirement — set the latch only where the send is genuinely dispatched — is met: the set sits after every gate, on the same synchronous straight line as the dispatch. |

**Comparison direction, copied verbatim (not re-derived):** long/`TradeMode = True` measures
`priceMovement = emgBaseline - bestAsk` (an adverse long move drops the ask below the anchor);
short measures `priceMovement = bestBid - emgBaseline`. The fire-side re-check keeps its own
mirrored form (`emgBaseline - newPrice` / `newPrice - emgBaseline`), untouched. The dollar semantics
(anchor ∓ M.SL) are unchanged on both sides.

## 4. Deviations and spec discrepancies

**(a) Clear count: 3, not the acceptance line's "exactly 2".** The spec body says clear at
"(1) `CompletePositionClose`; (2) the placement seeds where a fresh order re-establishes a clean
context (the `cancelPending = False` 'fresh order' sites)" — *plural*, and there are exactly two such
sites in the file (`ExecuteOrderAsync :3638`, `StopLossForTrailingOrderAsync :4535`). The acceptance
grep line then says "exactly 2 clears". These cannot both hold. **Implemented: 3 clears** (the body's
reading), because clearing only one of the two placement seeds is an arbitrary asymmetry that leaves
a real hole — a bracket placed via `StopLossForTrailingOrderAsync` after an emergency that never
reached `CompletePositionClose` would run with the cap latched off. **Flagged for coordinator/owner
ratification;** the alternative (2 clears) is strictly less safe, so the acceptance line is read as a
miscount rather than a design intent.

**(b) Spec §5's premise is stale.** "Rate-limiter guard on the fire path as today (`CanMakeRequest`)"
describes a guard that does not exist on the M.SL emergency path at HEAD. No code was changed on
account of it; the derived requirement (latch only at real dispatch) is satisfied. Raised here rather
than pre-commit because it is an observation about the spec's description of existing code, not a
design question that changes the implementation.

**(c) The latch is also read at the hoisted quote-level check** (`:2186`,
`marketStopLossChecked AndAlso Not emergencyFired`), which the spec does not explicitly call for.
It is load-bearing, and the exposed window is precisely bounded — see §6 for the derivation. In
short: `CancelOrderAsync` (the first thing the fire path does) closes this block's gate three ways
over — `cancelPending = True`, `SLTriggered = False`, `emergencyBaseline = 0`, `placedStopLossPrice
= 0` — but it does all of that **after** its own `Await SendWebSocketMessageAsync` returns. Until
then the gate is still open and `ForceStopLossUpdate` has set `lastStopLossUpdate = MinValue`, so
the throttle is wide open too. A tick landing in that window with the fire branch already latched
out would fall through to **editing the resting SL to the own-side touch**. Reading the latch at the
entry closes it. (An earlier draft of this report and of the code comment said "every tick while the
close settles" — that over-scoped it; the window is one WS send-await, not the whole close.)

No other deviations. Nothing else in the file was touched.

## 5. Acceptance §1 — greps, run at the commit

```
$ grep -n "emergencyFired" frmMainPageV2.vb          (comment lines omitted)
 169:    Private emergencyFired As Boolean = False                        <- declaration
2186:        If marketStopLossChecked AndAlso Not emergencyFired Then      <- hoisted-check read
3638:            emergencyFired = False    ' placement seed (ExecuteOrderAsync)          <- clear 2
4139:    If marketStopLossChecked AndAlso Not emergencyFired AndAlso ... (TradeMode = True)  ...
4144:                emergencyFired = True                                 <- SET, fire path 1
4152:    ElseIf marketStopLossChecked AndAlso Not emergencyFired AndAlso ... (TradeMode = False) ...
4154:                emergencyFired = True                                 <- SET, fire path 2
4535:            emergencyFired = False    ' placement seed (StopLossForTrailingOrderAsync) <- clear 3
4938:        emergencyFired = False        ' CompletePositionClose                       <- clear 1
```
= 1 declaration + 2 sets + 3 clears + 3 reads (1 hoisted check + the 2 branch tests). Nothing else.

Tripwires, HEAD `5ef58c4` vs the commit — **all identical**:

| Token | HEAD | After |
|---|---|---|
| `ResetCommandedSLPrices()` | 8 | **8** |
| `emergencyBaseline = 0` | 11 | **11** |
| `cancelPending = False` | 5 | **5** |
| `emergencyBaselineSettled` | 10 | **10** |
| `RecordCommandedSLPrice` | 3 | **3** |
| `SendReduceMarketOrderAsync` | 5 | **5** |

(Two comment drafts initially inflated the `cancelPending = False` and `emergencyBaseline = 0`
counts to 6 and 12 by containing those literals in prose; the comments were reworded so the standing
greps stay clean. Worth knowing for future doc-comments near tripwire tokens.)

`git diff --stat`: `frmMainPageV2.vb | 131 +++++----`, 87 insertions / 44 deletions — the great
majority comment. `git diff -w` non-comment lines: the field declaration, the two block-structure
moves, the two `Not emergencyFired` conjuncts, the two sets, the three clears. Nothing else.

## 6. What is byte-identical (spec §"What must be byte-identical")

- **SL-edit / chase machinery and its throttle** — `shouldUpdate` / `newStopPrice` derivation, the
  `isSLRepositioning` single-flight and its `Finally`, the 50 %-force branch, the
  `placedStopLossPrice = newStopPrice` advance, `lastStopLossUpdate`/`slUpdateFailures`,
  `BackoffStopLossRetry`, the rate-limited log line: **unchanged** (verified by `git diff -w`
  showing only the two `If` re-nestings around them).
- **`UpdateStopLossForTriggeredStopLossOrder`** — the single send point, its limiter wait, size
  derivation, payload, `post_only`/`reject_post_only`, and the one `RecordCommandedSLPrice(newPrice)`
  at the send: **unchanged**. The only edits in this function are the two latch conjuncts + sets.
- **All 7 paired `emergencyBaseline = 0` / `ResetCommandedSLPrices()` reset sites**: untouched.
- **The `emergencyBaselineSettled` hybrid latch** (10 sites): untouched.
- **Emergency dollar semantics** (anchor ∓ M.SL, per-side comparison direction): copied verbatim.

## 6a. The latch closes a PRE-EXISTING double-fire race (not only one created by the hoist)

Derived while scoping §4(c), and worth the coordinator's attention because it changes what N1 is:
the double-fire this latch prevents **was already reachable before the hoist**.

`CancelOrderAsync` is the first statement of both fire branches. Its state resets — `cancelPending =
True` (`:3702`), `placedStopLossPrice = 0` (`:3695`), `SLTriggered = False` (`:3731`),
`emergencyBaseline = 0` (`:3733`) — all execute **after** `Await SendWebSocketMessageAsync`
(`:3691`). So for the duration of that one send-await, every gate that would otherwise stop a
re-entrant quote tick is still open:

- block gate: `SLTriggered` still True, `Not IsCancelPending()` still True, socket still connected;
- throttle: `ForceStopLossUpdate` has just set `lastStopLossUpdate = DateTime.MinValue`, so it
  passes unconditionally — **this was equally true before N1**;
- `currentStopPrice > 0`: `placedStopLossPrice` not yet zeroed;
- the emergency comparison: `emergencyBaseline` not yet zeroed, price still beyond the cap.

`HandleQuoteUpdates` is `Async Sub` (fire-and-forget), so a tick arriving in that window runs
concurrently rather than queueing. Pre-N1 it would have re-entered the emergency and dispatched a
**second `CancelOrderAsync` + `SendReduceMarketOrderAsync`**. Narrow — one WS send — but real, and
on the taker-close path. N1's latch is the first thing that structurally forecloses it.

This does not change the implementation; it does mean the honest characterisation of N1 is
"hoist + close a latent double-fire race", not "hoist, plus a latch to pay for the hoist".

## 6b. Named residual: latch set, send silently not dispatched

The one behaviour change I cannot argue away, stated plainly for ratification. `emergencyFired` is
set immediately before `Await CancelOrderAsync()`, but `SendReduceMarketOrderAsync` — two frames
later — has two early returns of its own:

1. `If Not IsWebSocketConnected` → "reduce order skipped" (`:5801`-area);
2. position model empty **and** `orderAmountVal <= 0` → "Invalid amount."

If either fires, the latch is set and no reduce was sent. **Pre-N1** the next tick would retry the
emergency; **post-N1** the cap stays latched off for that position until `CompletePositionClose` or
a fresh placement.

Why I implemented it this way anyway, and why I recommend accepting it:

- Both paths are narrow. Path 1 needs the socket to drop between this block's `IsWebSocketConnected`
  gate and the send, across `CancelOrderAsync` — and if the socket is down, the retry that N1
  removes could not have sent anything either. Path 2 needs `positionSizeUSD = 0` in a
  `SLTriggered` context with a working `PositionSLOrderId`, which the position model makes close to
  unreachable.
- The alternatives are worse. Setting the latch *after* the send returns reintroduces the
  double-fire race in §6a (a re-entrant tick during `CancelOrderAsync` would see it clear) — trading
  a rare missed retry for a rare **duplicate taker close**, which is the more expensive error.
  Clearing the latch inside `SendReduceMarketOrderAsync`'s guard-returns is wrong because that
  function is shared with `FlattenPositionAsync` and `btnReduceMarket_Click`.

**Ask:** ratify "accept + document", or direct a different trade-off.

## 7. Open items for the owner

1. **Testnet acceptance (spec §Acceptance 2)** — enter, trigger the SL, force a persistent SL-edit
   failure, push price beyond the cap: the emergency fires **without waiting for the throttle
   window**, exactly once (one fire line, one reduce). Then re-arm on a fresh position. Use the
   isolated-harness protocol; the engine stays untouched. **Not run by this seat** (standing safety
   boundary: this seat places no trades and arms no bridge).
2. **Normal-session parity (spec §Acceptance 3)** — with edits succeeding, behaviour is unchanged.
   The check passing is invisible; the throttled block still does every edit.
3. **Ratify §4(a)** — 3 clears vs the acceptance line's 2.
4. Reminder carried from `spec-back-quickwins-acceptance-2026-07-25.md` §6: the circuit breaker is
   still at the test value **$1** — reset before the ladder. Unrelated to N1, still open.
