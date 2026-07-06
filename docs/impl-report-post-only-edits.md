# Implementation report — preserve `post_only` on all order edits (maker guarantee)

**Date:** 2026-07-06
**Implementer:** Opus 4.8 (high)
**File:** `DeribitOrderPlacementApp/frmMainPageV2.vb` only. Build `dotnet build -c Debug` = **0/0**. Local commit, **not pushed** (owner runtime-tests first).

## Why

All limit orders (entry, TP, SL, reduce) are *placed* with `post_only: True` + `reject_post_only: False` (e.g. the SL leg at [:2841](../DeribitOrderPlacementApp/frmMainPageV2.vb#L2841)), which guarantees a maker: Deribit reprices a would-be-marketable order to the best maker price rather than crossing the spread as a taker. But every `private/edit` (repositions, manual edits) sent **only** `order_id`/`price`/`amount` — it did **not** re-send `post_only`. So the maker guarantee on any *edited* order depended on Deribit preserving the flag across an edit, which is not something we can confirm from the code. If it isn't preserved, a chase/manual edit that crosses in a fast book could fill as a **taker** — against the owner's maker-first preference (the M.SL market order is meant to be the *only* deliberate taker).

## Audit — every `private/edit` payload

| # | Path | id | Order edited | Had flags? |
|---|---|---|---|---|
| 1 | `SendRateLimitedUpdate` — trigger branch | 223346 | SL (entry reposition) | no → **fixed** |
| 2 | `SendRateLimitedUpdate` — regular branch | 223344 | entry main / TP (entry reposition) | no → **fixed** |
| 3 | `SendReduceRepositionEdit` | 223349 | reduce limit | no → **fixed** |
| 4 | `UpdateStopLossForTriggeredStopLossOrder` | 223350 | triggered SL (chase) | no → **fixed** |
| 5 | `UpdateStopLossForTrailingOrder` — main | 223347 | trailing main | no → **fixed** |
| 6 | `UpdateStopLossForTrailingOrder` — SL | 223348 | trailing SL | no → **fixed** |
| 7 | `btnEditTPPrice_Click` | 223345 | TP (manual button) | no → **fixed** |
| 8 | `btnEditSLPrice_Click` | 223346 | SL (manual button) | no → **fixed** |

The M.SL emergency close (`SendReduceMarketOrderAsync`) is a **market** order — deliberately a taker, out of scope (not an edit).

## The fix

Added `{"post_only", True}` + `{"reject_post_only", False}` to all 8 edit `params`, matching the placement flags. Safe either way: if Deribit already preserves the flag on edit, this is a harmless no-op; if it doesn't, it closes the taker hole. The app's own repositions target the maker side already (`bestBid`/`bestAsk`), so they won't normally reprice; the flag matters for (a) the rare fast-market cross between the chase decision and the edit landing, and (b) the manual buttons, where the user can type a marketable price.

## Known minor interaction (documented, not blocking)

On the **triggered-SL chase** (223350) only: if the chase target would cross when the edit lands and Deribit reprices it, the open echo returns the *repriced* price, which is not the value recorded in the commanded-price set — so the reconciliation (spec-reconcile-manual-sl-edits.md) logs a spurious `Manual SL edit: $X` and self-corrects `placedStopLossPrice`/`emergencyBaseline` to the true (repriced) SL. Cosmetic only (state ends correct), and rare (the chase targets the maker side, so a reprice needs a fast adverse tick mid-flight). Not worth pre-recording a price band (would risk missing real manual edits).

## Related item — NOT changed (flag for owner)

`reduce_only` is likewise not re-sent on edits. It's a lower risk than `post_only`: a price-only edit can't flip an order's side, so an SL/reduce order stays a closing order even if the flag were dropped. Left as-is to keep this change scoped to the maker guarantee. If the owner wants the same belt-and-suspenders treatment, add `{"reduce_only", True}` to the SL edits (223346/223348/223350 + manual SL) and the reduce edit (223349) — the entry (opens) and TP (note at [:2855](../DeribitOrderPlacementApp/frmMainPageV2.vb#L2855): reduce_only on TP cancels both legs) must stay non-reduce_only.

## Verify (owner, test sub-account)

1. Chase a triggered SL, then check the order on Deribit still shows **post_only** after the edit (confirms the flag now sticks).
2. Manual SL button to a marketable price → order rests as a maker (repriced), not a taker fill.
3. Regression: normal entry→trigger→chase→fill; no unexpected taker fills; the rare repriced-chase case may log a benign `Manual SL edit` (see above).
