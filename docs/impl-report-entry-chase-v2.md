# Impl report — Entry-chase v2 (`spec-entry-chase-v2.md`)

**Date:** 2026-07-07. **Implementer:** Fable (same seat as the spec author; the §9-fix region was already hot).
**Commits:** `9c3c351` (commit 1), `bb77191` (commit 2), `df4dfdc` (commit 3 — the optional one, implemented; isolated and trivially revertible if minimum scope is preferred).
**Build:** 0 errors / 0 warnings after every commit (`MSBuild /t:Rebuild`, net9.0-windows).
**Pre-req:** confirmed before starting — `origin/master` = local master; owner's runtime probe confirmed Deribit accepts `post_only` on the trigger-branch edits ("triggered SL orders had post-only status and repositioned"), so the §2 contingency (strip flags from trigger-branch payloads) is DEAD — no payload changes anywhere in this work.

## Commit 1 — `9c3c351`: target + throttle + headroom (§2 + §6)

| | Before | After |
|---|---|---|
| Entry chase gate (long) | `bestBid > placedPrice + 3` | `chaseTarget = bestAsk − ChaseTickUSD`; fires when `chaseTarget > placedPrice` AND ≥ `EntryChaseMinIntervalMs` (350 ms) since `lastEntryChaseUtc` |
| Entry chase gate (short) | `bestAsk < placedPrice − 3` | `chaseTarget = bestBid + ChaseTickUSD`; `chaseTarget < placedPrice` + same throttle |
| Trailing chase gates | same $3 distance gates | same rewrite; **shared** `lastEntryChaseUtc` stamp (spec: the two blocks share `isRepositioning` single-flight anyway) |
| Edit price sent | own-side top (`bestBid`/`bestAsk`) | `chaseTarget` (one tick inside the opposite side — preserves the NoSpread edge) |
| Limiter gate | `CanMakeRequest()` | `CanMakeRequest() AndAlso HasHeadroom(4)` (all four chase blocks) |
| Slippage sub-gates | `maxSlippageATRchecked And IsATRSlippageExcessive(...)` (×4) | `AndAlso` (×4) — supersedes the four-gate half of `spec-medium-housekeeping.md` addendum item 8; its six pre-placement gates remain there (that spec text NOT edited — see "not touched" below) |

- New fields: `ChaseTickUSD = 0.5D`, `EntryChaseMinIntervalMs = 350`, `lastEntryChaseUtc` (engine-field block). Stamp advances only after a successful edit send, same place `placedPrice` advances (and in the slippage-cancel branch it deliberately does NOT).
- **`HasHeadroom(requests)` semantics:** under `_lockObject`, after `RefillCredits()`, returns `_currentCredits >= requests × _costPerRequest`. It reserves without consuming — a floor check, not accounting. Chase gates use 4 (entry-only mode: 1 edit + 3 reserve so nuclear-cancel/emergency sends are never credit-starved; bracket mode's 3 + 1 is covered by the same floor). Existing `CanMakeRequest`/`ConsumeCredits` calls untouched.
- Gate chain otherwise preserved verbatim: socket, `IsCancelPending`, id null-checks, `Interlocked` single-flight, limiter-null fallback branches, slippage guard **inputs** (raw own-side quote). Trailing-LONG still passes `bestAsk` to `IsATRSlippageExcessive` — left as-is per spec, now marked with a NOTE comment for the owner decision.
- Nullable detail: `chaseTarget` is `Decimal?`; a missing quote side makes the lifted comparison false (no edit, no throw). The long/short entry gates also keep an explicit `IsNot Nothing` on the *other* side because the slippage guard reads it.

## Commit 2 — `bb77191`: entry-only chase + deferred leg re-anchor (§4, default ON)

- New fields: `EntryOnlyChase = True` (owner ruling; one-line revert — if reverted, raise `EntryChaseMinIntervalMs` to 700–1000 per the field comment), `legAnchorPrice`, `legReanchorDriftMax`. Explicitly commented as ORDER-context fields, not SL context.
- **Placement seeding** (both sites: `ExecuteOrderAsync` and `StopLossForTrailingOrderAsync`, right after `placedPrice = BestPrice`): `legAnchorPrice = BestPrice`; `legReanchorDriftMax = Min(takeProfitOffset, triggerDistance)/2`, fallback `10D` when ≤ 0. At the trailing site the TP term is geometrically irrelevant (no TP leg) but can only *tighten* the bound — spec formula used verbatim at both sites.
- **Resets** where `placedPrice` context dies: nuclear `CancelOrderAsync`, scoped `CancelWorkingEntryCoreAsync`, `CompletePositionClose` (redundant with the nuclear cancel it awaits, but makes the spec's three sites literal). NOT reset in the placement-rejection restore path (`HandlePlacementResponse`) — benign: order IDs are gone so no chase can fire, and the next placement re-seeds; flagged here rather than added (spec didn't list it).
- **Chase-time decision** (all four blocks): within the bound → `UpdateEntryOrderOnlyAsync(chaseTarget)` (1 edit); beyond → the existing full path (`UpdateLimitOrderWithOTOCOAsync` 3-edit / `UpdateStopLossForTrailingOrder` 2-edit) + `legAnchorPrice = chaseTarget`. After a restore, `legAnchorPrice = 0` forces the first chase down the full path, which re-seeds the anchor — self-healing.
- **`UpdateEntryOrderOnlyAsync`**: same limiter-existence/wait/consume and amount guards as the bracket path, then one `SendRateLimitedUpdate("main", CurrentOpenOrderId, …)` (id 223344 — the 223344–223350 response handling is range-based, so reuse from the trailing block is safe). Touches NO SL bookkeeping (`StopLossTriggerOriginal`/`emergencyBaseline`/commanded set) — the resting SL didn't move, records stay truthful.
- **`ReanchorLegsAsync(fillPrice, includeTP)`**: prices re-derived from `fillPrice` with the exact `UpdateLimitOrderWithOTOCOAsync` formulas (manual overrides honored); `HasHeadroom(2)`/`(1)` pre-check (skip = legs stay at old geometry, logged orange, records untouched — degraded but truthful); TP edit only when `includeTP`; then in spec order: `StopLossTriggerOriginal = newTrigSLprice` → `emergencyBaseline = 0D` → `ResetCommandedSLPrices()` → `RecordCommandedSLPrice(newSLprice)` → `legAnchorPrice = fillPrice`. Cyan log line. Runs on the receive thread (engine fields only, self-marshalling output).
- **Fill hooks:** filled-`EntryLimitOrder` echo (the `Position entered:` site) → `ReanchorLegsAsync(fill, includeTP:=True)`. Fill price = `average_price` with `price` fallback, gated `> 0` and `<> legAnchorPrice`.
- **Trailing fill-anchor point (the §4 open item):** the filled-**`EntryTrailingOrder`** echo, same Select Case, via `ReanchorLegsAsync(fill, includeTP:=False)`. **Deviation from a literal spec reading:** §4 says "full `UpdateStopLossForTrailingOrder` … beyond it *or at fill*" — calling that function at fill would also edit the just-filled main order (guaranteed `already_closed`, one wasted credit). The SL-only re-anchor performs the same SL edit with the same trigger bookkeeping **plus** the mandatory `RecordCommandedSLPrice` (which `UpdateStopLossForTrailingOrder` doesn't do, being a pre-trigger path) — so the deviation is strictly closer to the spec's own §4 invariant.
- **Doc counts (same commit):** `spec-back-session-2026-07-04.md` §10 bullet 4 and `HANDOVER-2.md` §3 state-delta line updated 7 → 8 naming `ReanchorLegsAsync`, as mandated. Also updated for internal consistency: the two §1 field-list mentions in the same 07-04 doc, and the two live code comments (commanded-set header, `ResetCommandedSLPrices`). Verified by grep: exactly 8 `emergencyBaseline = 0` reset sites, each paired with `ResetCommandedSLPrices()`.
- **Deliberately NOT touched (per "two canonical counts" + scope discipline):** the "7 sites" mentions in the historical session records (`impl-report-reconcile-manual-sl-edits.md`, `spec-back-reconcile-manual-sl-edits.md`, `spec-back-session-2026-07-07.md` §"Commanded-price set") — accurate as of their dates. **One forward-looking stale spot for the owner:** `spec-medium-housekeeping.md` line ~112 do-not-touch list says "the 7 SL-context reset sites" and its addendum item 8 still contains the four-gate half superseded by commit 1 — recommend a one-line touch-up before that spec is handed to its implementer.

## Commit 3 — `df4dfdc`: reduce-chase target + throttle (§5)

| | Before | After |
|---|---|---|
| Buy exit (closing short) | `bestBid > reduceOrderPrice + 3` → edit to `bestBid` | `chaseTarget = bestAsk − tick`; `chaseTarget > reduceOrderPrice` + throttle → edit to `chaseTarget` |
| Sell exit (closing long) | `bestAsk < reduceOrderPrice − 3` → edit to `bestAsk` | `chaseTarget = bestBid + tick`; `chaseTarget < reduceOrderPrice` + throttle → edit to `chaseTarget` |

Direction from `reduceOrderIsBuy` (never `TradeMode`), new separate stamp `lastReduceChaseUtc` (shared `EntryChaseMinIntervalMs` constant), still 1 edit per reposition, no leg logic, payload flags already present from `45a8da5`. No `HasHeadroom` added here — §5 names exactly two upgrades.

## Known/accepted behaviors (not new)

- On a swallowed edit skip (insufficient credits / blank amount inside the update functions) the caller still advances `placedPrice` + throttle stamp — same phantom-advance class as accepted review finding F1 from the 07-07 session; self-heals on the next tick's live-book comparison.
- Constants left at spec values (`350 ms`, tick `0.5`, headroom `4`); the connect-time limiter (account `MaxCredits`, cost 50/request) plus `HasHeadroom(4)` is the effective credit guard. Worst case both chases active ≈ 5.7 edits/s, inside the base-tier burst envelope and far under the old fast-tape behavior.

## Owner runtime test plan

Spec §8, unchanged — items 1–7 all apply (commit 3 landed, so item 7 is live). Suggested extra eye during item 3: the cyan `Legs re-anchored to fill $…` line should appear exactly once per filled entry whose fill price differs from the leg anchor, and never produce a `Manual SL edit` line (that's the §9-regression check in item 5).
