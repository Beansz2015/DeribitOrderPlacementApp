# Impl report — Fill re-anchor fix (TP-only, deferred)

**Spec:** `spec-fill-reanchor-fix.md` (2026-07-08). **Implementer:** Fable (entry-chase v2 / sl-chase-v2 seat).
**Commit:** `66b00cf` (one commit + docs, as §3 directed). **Build:** 0 errors / 0 warnings (`MSBuild /t:Rebuild`, net9.0-windows).
**Base:** on top of sl-chase-v2 (`b1b37e7`/`f0a2110`/`843b05a`) + the spec commit `8ff358d`; before the combined runtime session. `origin/master = 44cd51e`; everything since is local.

## Verdict accepted

§1–§2 of the spec are correct: the SL legs are Deribit-native `trigger_offset` trailing stops (confirmed in code — OTOCO leg `:2966`, trailing bracket `:3884`, both from `txtTriggerOffset`), so the exchange owns the trigger post-activation and a static `fill ± triggerDistance` re-anchor would fight the trail. TP-only is the right call. The spec-back's §4 SL-interleave worry is moot — there is no SL edit at all now.

## Changes (before → after, per spec §3)

| # | Before | After |
|---|---|---|
| §3.1 defer | filled `EntryLimitOrder` echo → `Await ReanchorLegsAsync(fill, includeTP:=True)` immediately (`:2395`) | stages `pendingReanchorFill = entryFillPrice` (new ORDER-context field) under the same `legAnchorPrice<>0 AndAlso fill>0 AndAlso fill<>anchor` guard; no edit here |
| §3.1 trailing | filled `EntryTrailingOrder` echo → `Await ReanchorLegsAsync(fill, includeTP:=False)` | **hook deleted** — trailing bracket has no TP leg and its SL is the native trail; nothing to re-anchor |
| §3.2 TP re-anchor | (did not exist) | in the **open** `TakeLimitProfit` echo (where `PositionTPOrderId = orderId`): if `pendingReanchorFill > 0`, compute fill-anchored TP (`manualTPval` honored), **skip-if-equal** (`Abs(newTP − echoed price) > 0.01`), consume the flag, dispatch ONE edit to **`PositionTPOrderId`** (the live post-fill leg) |
| §3.3 slim | `ReanchorLegsAsync` (3 code paths, SL edit + SL-context block) | **deleted**; replaced by `ReanchorTPToFillAsync(tpOrderId, fillPrice, newTPprice)` — one `SendRateLimitedUpdate("takeprofit", …)`, `ConsumeCredits`-before-send + amount guard kept; **no** SL edit, **no** `StopLossTriggerOriginal`/`emergencyBaseline`/`ResetCommandedSLPrices`/`RecordCommandedSLPrice` |
| §3.4 log | `Legs re-anchored to fill …` printed **before** the sends (optimistic; sat above the two errors) | `TP re-anchored to fill $F: $T` printed **inside** `ReanchorTPToFillAsync`, only after the send |
| §3.5 docs | reset-site count 8 | reverted to **7** (see below) |

New field: `pendingReanchorFill As Decimal = 0D`, reset alongside `legAnchorPrice = 0D` at all three order-context death sites (nuclear `CancelOrderAsync`, scoped `CancelWorkingEntryCoreAsync`, `CompletePositionClose`) **and** on consume in the TP branch.

## Thread-placement choice (the §3.2 ⚠️)

The open echo body runs inside a synchronous `Me.Invoke(Sub() … End Sub)` — no `Await` allowed inside. So: the `TakeLimitProfit` case (UI thread) **stages** into three receive-thread locals declared in the `If (orderState="open")` scope (`tpReanchorFill`/`tpReanchorTarget`/`tpReanchorId`) and consumes `pendingReanchorFill`; then **after** `Me.Invoke` returns (back on the receive thread, inside the `Async Sub`'s `For Each`), `If tpReanchorId IsNot Nothing Then Await ReanchorTPToFillAsync(...)`. VB closures capture those locals by reference and `Me.Invoke` is synchronous, so the post-lambda reads see the staged values. This mirrors the existing filled-branch pattern (stage on one thread, dispatch on the receive thread) rather than `Await`-ing inside the lambda or `Task.Run`-ing a fire-and-forget.

**Why targeting `PositionTPOrderId` is correct here:** it's assigned from `orderId` in the *same* `TakeLimitProfit` case, same echo — i.e. the live post-fill TP leg. The old bug used `CurrentTPOrderId`, the *pre-fill* leg the OTOCO fill retires. The `open` `TakeLimitProfit` echo is specifically the new leg (process-desc `:2166`: post-fill the old legs go `Triggered`, new ones go `Open`), so consuming the pending fill on the first `open` TP echo hits the right order.

## §3.5 — reset-site count reverted 8 → 7 (grep evidence)

Removing the SL edit + its four SL-context writes un-makes the re-anchor as a reset site. `grep 'emergencyBaseline = 0'` + `ResetCommandedSLPrices()` now pairs at exactly **7** sites: 4 SL-placement (`ExecuteOrderAsync`, `UpdateLimitOrderWithOTOCOAsync`, `UpdateStopLossForTrailingOrder`, `StopLossForTrailingOrderAsync`) + nuclear `CancelOrderAsync` + market-reduce + `CompletePositionClose`. (The two `emergencyBaseline = 0D` at the commanded-set comparison sites are reads, not resets.) Reversed in: `spec-back-session-2026-07-04.md` §1 (×2) + §10 bullet 4; `HANDOVER-2.md` §3 delta; `impl-report-sl-chase-v2.md` §4 (the "8 at this commit" note); and the two code comments (`:61`, `:4044`) — each with the "was briefly 8 (entry-chase v2), reverted by the TP-only fix" history note.

## SL path byte-untouched (spec §5 confirmation)

Verified by diffing `8ff358d..66b00cf`: the only SL-referencing lines in the diff are **deletions from the retired `ReanchorLegsAsync`** (its SL edit + bookkeeping). Zero changes to: the untriggered `StopLossOrder` branch (`CurrentSLOrderId`/`PositionSLOrderId`/`StopLossTriggerOriginal` trail-sync, `:2349`-area), the open `StopLossOrder` adopt/discriminator block (`:2230`-area), `UpdateStopLossForTriggeredStopLossOrder` (the SL-chase), or the trailing SL. The three-way echo classification and the trail are exactly as before.

## Follow-up (owner runtime test #49, same local stack)

Trade #49 confirmed the fix end-to-end: chased SHORT entry `63038→63028`, `TP re-anchored to fill $63028.00: $62968.00`, **zero `order_not_found`**, SL trailed/chased normally. It surfaced one display gap: the app's TP field stayed at the stale placement TP (`62978 = 63038−60`) while the live order sat at the re-anchored `62968` — because the post-fill open `TakeLimitProfit` echo never writes `txtPlacedTakeProfitPrice`. Fixed by a one-line `UiInvoke(Sub() txtPlacedTakeProfitPrice.Text = newTPprice.ToString("F2"))` in `ReanchorTPToFillAsync` after the send (mirrors the triggered-SL chase's own display refresh). Display-only; no logic/price change. Build 0/0.

## Deviations

None of substance. The spec offered "slim `ReanchorLegsAsync` → TP-only **or inline it**" — chose to **delete** the function and add a focused `ReanchorTPToFillAsync` rather than leave a one-caller slimmed shell (the TP dispatch is a clean helper; the open branch stays readable). No SL-interleave code was written (spec §2 decision), so the spec-back's feared adopt/discriminator sequencing never materialised.

## Owner runtime tests (spec §4)

1. **Chased entry (#47 scenario):** exactly one TP edit to the **new** TP id, fill-anchored price on the exchange UI; **zero** `order_not_found`; SL untouched (trail behaves as before); no spurious `Manual SL edit`.
2. **Non-chased entry:** fill == anchor ⇒ nothing staged, no edits.
3. **Manual-TP mode:** skip-if-equal ⇒ no pointless edit.
4. **Cancel between fill and TP echo** (best-effort): `pendingReanchorFill` cleared at context death; nothing fires next position.
5. **Trailing-entry chased fill:** no re-anchor edits at all, no errors.
6. **Docs/grep:** 7 paired reset sites; the doc locations read 7 with the history note.

Combined with the still-pending sl-chase-v2 §5 tests and its two owner decisions (P1 pullback tightening; the `Not IsCancelPending()`/emergency-gate interaction), this is the last item before the combined runtime session + push.
