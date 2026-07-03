# Implementation report — Decouple v2 (#10)

**Spec:** `docs/spec-decouple-v2.md` (2026-07-03)
**Implementer:** Fable 5 (high)
**Base:** `5da2e5b` on `housekeeping-now` (stayed on this branch — it's where the previous spec series landed; spec named no branch)
**Commits:** `da288e6` (1/3), `b4a3327` (2/3), `fccdec5` (3/3) — local only, not pushed
**Target file:** `DeribitOrderPlacementApp\frmMainPageV2.vb` only. Line numbers below are at `fccdec5`.

---

## Commit 1 — `da288e6` — scoped cancel for the working entry

### Changes

1. **New method `CancelWorkingEntryCoreAsync(reason As String)`** (line 2932), placed directly after `CancelOrderAsync`. Body is the spec §4a snippet verbatim: `private/cancel` on `CurrentOpenOrderId` with id 31, then the same transition-race window as the nuclear cancel (`cancelPending = True`, `cancelPendingSince`, null the three `Current*OrderId`s, `placedPrice = 0D`, `ResetOrderAttempt()`), display resets via `UiInvoke` with the position-aware status label ("In Position" yellow if `positionSizeUSD <> 0D`, else "Awaiting Orders"), and the yellow "Working entry cancelled ({reason}) - position legs untouched" log line.
   - Deliberately absent (per spec §4a note): `placedStopLossPrice = 0D`, `SLTriggered`/`isTrailing*`/`StopLossTriggerOriginal` resets, `ReduceOrderId` clears, margin-display clears, `txtPlacedStopLossPrice` reset. Not harmonized.
2. **Call-site swap** — all four slippage-guard cancels in `HandleQuoteUpdates`:
   - Before: `Await CancelOrderAsync()` — After: `Await CancelWorkingEntryCoreAsync("ATR slippage")`
   - Entry-reposition LONG (line 1440), entry-reposition SHORT (1479), trailing-reposition LONG (1652), trailing-reposition SHORT (1678). The commented `'Return` lines beside each are untouched.

### Acceptance

- Build: **0 errors / 0 warnings**.
- Grep at HEAD: `CancelWorkingEntryCoreAsync` = 4 call sites in `HandleQuoteUpdates` + 1 public wrapper (commit 3) + definition. `Await CancelOrderAsync()` survives only at: position-close cleanup (in `HandleOrderPositionUpdates`), the two emergency market-stop paths (`UpdateStopLossForTriggeredStopLossOrder`), and the Cancel-All button — plus one commented-out line in `SendReduceOrderAsync` (pre-existing).

---

## Commit 2 — `b4a3327` — placement acks + rejection rollback

### Changes

1. **`Imports System.Collections.Concurrent`** added at the top (spec-sanctioned alternative to fully qualifying).
2. **§3 plumbing** inserted after the position-model fields (`PlacementIdBase` = 600000 at line 1866, `nextPlacementId`, `PendingPlacement` class, `pendingPlacements` ConcurrentDictionary, public `PlacementResult` class) plus the `RegisterPendingPlacement` helper (line 1889) — keeps a pre-registered entry (Tcs attached) if present, (re)takes both snapshots, sweeps entries older than 60 s. `.Where(...).ToList()` resolves via the project-level LINQ import (verified: `frmMainPageV2.vb` already uses `.Select` with no explicit import).
3. **`ExecuteOrderAsync`** signature: `(TypeOfOrder As String)` → `(TypeOfOrder As String, Optional requestId As Integer = 0)`. reqId allocation + `RegisterPendingPlacement(reqId)` inserted, and payload `New JProperty("id", 2)` → `New JProperty("id", reqId)`.
4. **`StopLossForTrailingOrderAsync`** (the `EntryTrailingOrder` placement): self-allocated reqId + registration; payload id 2 → reqId.
5. **`HandlePlacementResponse`** (line 1060), spec §5b verbatim, placed directly after `RequestNameForId` so the id-space owners sit together. Dispatch wired in `ReceiveWebSocketMessagesAsync` at line 799, immediately **before** `HandleUnhandledJsonRpcError(response)` (801). Appears exactly once in the dispatch.
6. **Single ownership**: in `HandleUnhandledJsonRpcError`, after the 3/999/777/890 skip: `If messageId.HasValue AndAlso messageId.Value >= PlacementIdBase Then Return` (null-safe `HasValue AndAlso` style).

### Deviation (flagged)

- Spec §5a says to insert the reqId block "immediately before `Await SendWebSocketMessageAsync(OrderPayload.ToString())`". `OrderPayload` is **constructed** (and captures the id) a few lines above the send, so the block goes immediately before the payload construction instead — anything later can't compile. Semantics are unchanged: registration (and both snapshots) still happen before the send and before the optimistic seed, which runs after the send. Same adjustment in both placement functions.

### §5b acknowledgment (required)

The success path does **not** seed `CurrentOpenOrderId`; echoes remain the single writer of order context in v1. Acknowledged and agreed: seeding from the ack would create a second writer racing the open echo and the `cancelPending` machinery — exactly the transition-race class the last three specs spent effort closing. Ack-side seeding can be revisited post-VerdictEngine if the engine needs the id faster than the echo delivers it.

### Acceptance

- Build: **0 errors / 0 warnings**.
- Greps at HEAD: the only remaining `New JProperty("id", 2)` is the `public/auth` payload in `AuthorizeWebSocketConnection` (line 352) — pre-existing, out of scope, and no longer shares an id space with placements. Both placement payloads use `reqId`.
- Runtime items (owner): §8 test 2 — 1-USD manual placement should now log `ORDER REJECTED (id 6000xx): … - engine state rolled back` exactly once (generic logger skips the range), restore `txtPlacedPrice`, and leave no phantom PnL.

---

## Commit 3 — `fccdec5` — public automation API

### Changes

1. **`SetTradeMode(isLong As Boolean)`** (line 4142): full bodies of `btnBuy_Click` (True branch) and `btnSell_Click` (False branch) moved verbatim — TradeMode assignment, button colors/text, group-box labels, control relocations. The two handlers are now one-liners (`SetTradeMode(True/False)`). Buy branch listed first; spec didn't dictate order. Behavior byte-identical.
2. **`ExecuteAutomatedOrder` deleted.** Grep across `*.vb`: zero references remain (re-verified zero callers before deleting).
3. **API region** (line 289, after `CanMakeAPIRequest`, inside the existing `'For AUTOMATED ORDER PLACEMENT` banner block): all spec §6c–§6f members verbatim —
   - Read-only: `OpenPositionSizeUSD`, `OpenPositionAvgEntry`, `SessionPnLUSD` (wraps the `USDPublicSession` field, which stays public for FrmIndicators), `HasWorkingEntryOrder`, `IsFlat`.
   - `SetTradeTargets(...)`: six optional nullables → textboxes on the UI thread (`Invoke` when `IsHandleCreated AndAlso InvokeRequired`); `TextChanged → SyncTradeInputsFromUi` does the mirroring.
   - `PlaceAutomatedOrder(side, kind, ackTimeoutMs)`: strict v1 gates in spec order (connected → rate limit → `IsCancelPending()` → flat → no working entry), side/kind mapping, ack pre-registration with `RunContinuationsAsynchronously`, placement marshalled onto the UI thread via `Func(Of Task)` + `CType(Me.Invoke(...), Task)`, `Task.WhenAny` 5 s default timeout. Timeout ≠ rejection: registry entry removed, **no rollback**.
   - Wrappers: `FlattenPositionAsync` → `SendReduceMarketOrderAsync` (position-model sized), `CancelAllOrdersAsync` → `CancelOrderAsync` (nuclear), `CancelWorkingEntryAsync` → `CancelWorkingEntryCoreAsync("API request")` (scoped).

### Payload parity (§1 invariant 5 / §8 test 3)

By construction: `PlaceAutomatedOrder` funnels into the **same** `ExecuteOrderAsync` the buttons call — same `Select Case`, same textbox reads, same `params` object; the only payload difference is the id (`reqId` passed vs self-allocated), and ids are unique on both paths since commit 2. `PlaceAutomatedOrder` deliberately skips btnLimit's `btnEstimateMargins_Click` + 500 ms delay (spec §7).

**Runtime confirmation (owner, 2026-07-03, via the uncommented payload log line):** manual btnLimit placements (ids 600001/600002, first session) and an API placement via `PlaceAutomatedOrder("long","limit")` (id 600001, second session) produced structurally identical `private/buy` payloads — same field set and ordering in `params` and both `otoco_config` legs, and the same offset math from the same textbox values (TP = entry + 60, trigger = entry − 60, SL limit = trigger − 30, amount 10). Differences: the request id and the absolute price levels (different best-bid moments). Parity confirmed.

### Acceptance

- Build: **0 errors / 0 warnings**.
- Thread-safety sweep of all new code: `CancelWorkingEntryCoreAsync` and `HandlePlacementResponse` touch engine fields only, displays via `UiInvoke`/`AppendColoredText`; `SetTradeTargets` writes controls only inside the marshalled `apply`; `SetTradeMode` runs only on the UI thread (button handlers, or inside `PlaceAutomatedOrder`'s marshalled `placeCall`); `PlaceAutomatedOrder` itself reads fields only. No new `.Text` reads outside UI-thread-marshalled blocks.

---

## Build results

`dotnet build DeribitOrderPlacementApp.sln` after each commit: **0 warnings / 0 errors** ×3. Total diff: 348 insertions, 85 deletions, one file.

## Invariant check (spec §1)

1. No control access on the receive thread in new code — see thread-safety sweep above. ✓
2. API members callable from any thread — gates/fields only, then marshal. ✓
3. Reposition gate ordering, position model, reduce-reposition context, `BackoffStopLossRetry`, id-guard style — untouched; new id guards use `HasValue AndAlso`. ✓
4. `cancelPending` lifecycle — scoped cancel sets the same flag/window; no new clear paths. ✓
5. Payload parity — by construction, owner runtime check pending. ✓
6. `Option Strict Off` preserved; no option lines added. ✓

## Suspicious nearby items — NOT touched

1. **Trailing-reposition LONG branch slippage check uses `bestAsk`** (`IsATRSlippageExcessive(bestAsk, "LONG")`, line 1652) while the entry-reposition LONG branch uses `bestBid` and the trailing block itself repositions with `bestBid`. Looks like a pre-existing copy-paste asymmetry; left alone per "do not disturb," but worth a look — for a long it makes the guard marginally *more* conservative (ask ≥ bid), so it's not dangerous, just inconsistent.
2. **`RequestNameForId` id-2 hint now stale**: says "auth/entry order" but after commit 2 only auth uses id 2. Cosmetic (best-effort hint); one-word fix whenever the file is next open.
3. **Duplicated `takeprofitprice` computation in the `BuyLimit` case** of `ExecuteOrderAsync` (an `If/Else` immediately followed by an equivalent inline `If` re-assignment). Pre-existing redundancy; didn't touch because that function is the payload-parity surface.
4. **Cancel acks**: the scoped cancel's id 31 has no dedicated response handling — an error on it surfaces through the generic F2 logger (no `RequestNameForId` hint). Matches spec §7 (no ack plumbing for cancels in v1).

## Post-test follow-up fixes (2026-07-03, after owner runtime tests 3–5)

Owner testing passed test 3 (regression + a live demonstration of the scoped cancel firing on the ATR guard) and test 4's visual check, and surfaced two pre-existing defects — both outside the decouple-v2 diff, fixed as follow-up commits:

- **`19fa162` — connection guards (test 4 finding).** `CancelOrderAsync` and `SendReduceOrderAsync` sent unconditionally; pre-connect, `webSocketClient`/`cancellationTokenSource` are `Nothing`, so Cancel All / Mkt Rdc clicks NREd inside `SendWebSocketMessageAsync`, whose catch then fired `HandleWebSocketDisconnect` — the reconnect machinery — before the first connect. Both now early-return with a "WebSocket is not connected" log line (the same guard the entry paths always had), before any engine-state mutation. Covers the buttons, internal callers, and the API wrappers.
- **`9cb97f4` — rate limiter armed at connect (test 5 finding).** `rateLimiter` was only created lazily (reposition fallback / edit-path emergency limiters / manual re-click of Connect while ONLINE), so a fresh connect with no manual order activity left it `Nothing` and `PlaceAutomatedOrder` refused everything with a misleading "rate limit". `ConnectToWebSocketDirectly` now fires `InitializeRateLimitsAfterAuth` right after the receive loop starts (initial connect only — the `Is Nothing` guard keeps reconnect behavior unchanged), with a conservative `2000/50` fallback if the account summary times out. The API gate also gained a distinct `"rate limiter not initialized"` reason.

- **`3714171` — log ordering on disconnected market reduce.** A pre-connect Mkt Rdc click logged the position-model fallback line before the guard's skip line; the guard is now also at the top of `SendReduceMarketOrderAsync`, so the disconnected click logs the skip line alone. `SendReduceOrderAsync` keeps its guard for the other callers.

Also resolved during testing: the "disabled entry textbox" observation in test 5 is by design — `txtPlacedPrice` is a display mirror; there is no entry-price input anywhere (entry price = best bid/ask at send), which is why `SetTradeTargets` has no entry-price parameter.

## Owner runtime test results (§8, 2026-07-03)

1. **Scoped cancel — PASS.** With an open position, the ATR guard cancelled the working entry only; the position's TP/SL legs survived and the entry's OTOCO children were cancelled with the primary (no orphaned children — the §1 caveat does not trigger). Also demonstrated live in the test-3 session.
2. **Rejection rollback — PASS.** 1-USD placement → `ORDER REJECTED (id 600002): code -32602 - Invalid params | {"reason":"must be a multiple of contract size","param":"amount"} - engine state rolled back`. Exactly one red line (generic logger skipped the range); display and PnL restored.
3. **Normal placement regression — PASS.** Placement/reposition/close/DB record unchanged; success acks consumed silently; payload parity confirmed (see above).
4. **Mode buttons — PASS** (visual check; plus the two pre-connect defects found and fixed, see follow-ups).
5. **API smoke — PASS.** Via a temporary debug button using `Task.Run` (any-thread contract exercised): while flat → `Accepted=True OrderId=163369307806`; immediately again → `Accepted=False Reason=working entry exists`. Gate refusals verified pre-fix: `rate limit` (now `rate limiter not initialized` for that state).

**Cleanup status:** payload log line in `ExecuteOrderAsync` re-commented and smoke-button handler removed (owner; `frmMainPageV2.vb` clean vs HEAD). Remaining: the orphaned smoke-button *control* still sits uncommitted in `frmMainPageV2.Designer.vb` — delete it in the designer, or keep it for tie-in testing (no handler, so it's inert).
