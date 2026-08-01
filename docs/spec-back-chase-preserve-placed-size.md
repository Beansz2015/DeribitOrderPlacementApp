# Spec-back — N2b: two box-mirror sites `spec-chase-preserve-placed-size.md` §2 does not cover

> ## COORDINATOR RULING 2026-08-01 — **D1 REJECTED · D2 UPHELD · D3 accepted.**
> Full reasoning and the code-side review: `review-chase-preserve-placed-size.md`.
>
> - **D1 — REJECTED, the harm cannot occur.** `ReanchorTPToFillAsync` never runs on a bridge entry.
>   The staging gate's fourth conjunct is `manualTPval <= 0D` — its own comment says *"only
>   auto-offset TPs re-anchor"* — and a bridge act always sets a manual TP
>   (`SetTradeTargets(manualTP:=RoundToTick(p.Target))`, with gate 4.4 refusing `Target <= 0`).
>   The reasoning error is worth naming: the full four-conjunct gate was quoted, one conjunct's
>   reachability was proved (`entryFillPrice <> legAnchorPrice` under `EntryOnlyChase`), and that was
>   generalised to the whole gate. **N2 is NOT blocked by this** — §7's recommendation is void — and
>   acceptance 2's TP-leg observation is dropped. On the manual path where the re-anchor does run,
>   the position size IS the box, so `orderAmountVal` is already correct.
> - **D2 — UPHELD, option A.** Verified: the id-778 restore adopts the entry's id and price but not
>   its amount. Implement the two-line single-writer seed in N2b. §4.6's new census line gains
>   `+ 1 restore seed`.
> - **D3 — accepted, and stronger than argued.** The `placedOrderSizeUsd = 0D` you placed at `:4860`
>   sits immediately before `:4869`, the file's only writer of `isTrailingStopLossPlaced`, so the
>   field is `0` by construction on every path reaching that third edit site. Census stays at 2 chase
>   reads.
>
> **Raising all three before implementing was right, and D2 in particular would have shipped a hole
> that only a restart could expose.** D1 was wrong, but flagged rather than acted on, with the
> unverified exchange behaviour explicitly not claimed — which is exactly how a suspected defect
> outside your scope should be handled.

**For:** the owner + orchestrator. **From:** the N2b implementer seat (Opus HIGH), 2026-08-01.
**Status:** raised under the standing rule (H-4 §6 / H-5 §4) — spec defects escalate BEFORE
implementing rather than being coded around.
**Baseline verified myself:** `git rev-parse HEAD` = `a6e3af1`, 10 ahead of `origin/master`
(`53a2375`), tree clean; `tools/checks/verify-gate.ps1` → **GATE PASSED, OrderCheck 173/173**;
all seven §4.6 censuses match (`emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 ·
`RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `isPlacingOrder` 13 ·
`lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 0).

**Not a challenge to the spec's design.** §2's field, its `0`-means-the-box convention, its set
site and its clear set are all correct and I am implementing them as written. This document is
about **two OTHER sites that read the Amount-box mirror** and that §2's scope fence
(*"every SL/TP/reduce path"* out; the fill-transition clear) excludes. Both are the same defect
class the review named — *the box mirror leaks into an order whose real size is the placed size* —
and both are invisible at live-at-min-size for exactly the reason review §3 gives.

> ⚠️ Anchors are at `a6e3af1` and will drift. Locate by symbol.

**I am NOT blocked.** §2 and §3 as written are unambiguous and neither ruling changes a line of
them: D1's fix does not use the new field (see below) and D2 is an additional seed site. I am
proceeding with the in-scope commits while this is decided.

---

## D1 🚨 — `ReanchorTPToFillAsync` resizes the POSITION's take-profit leg to the Amount box

**Site:** `frmMainPageV2.vb:4305`, inside `ReanchorTPToFillAsync`:

```vb
' Cross-thread fix: read engine input fields, never the textboxes.
Dim amount As Decimal = orderAmountVal          ' <- the box mirror
...
Await SendRateLimitedUpdate("takeprofit", tpOrderId, newTPprice, amount)
```

This is the fill-reanchor fix's post-fill TP edit: it re-prices the position's live TP leg to
`fill + tpOffset`. It also **re-sends the amount**, from the box — so on a risk-sized entry it
resizes a 310-lot TP leg down to 10.

**It fires on essentially every chased bridge entry.** The staging gate is `:3264`:

```vb
If legAnchorPrice <> 0D AndAlso entryFillPrice > 0D AndAlso entryFillPrice <> legAnchorPrice
   AndAlso manualTPval <= 0D Then pendingReanchorFill = entryFillPrice
```

Under `EntryOnlyChase` (owner-ruled default ON) the chase takes the `UpdateEntryOrderOnlyAsync`
arm and **`legAnchorPrice` is deliberately not advanced** (`:2194`/`:2245`), so any chased entry
fills at a price that differs from the anchor and the re-anchor is staged. It is then dispatched at
`:3171` off the post-fill open `TakeLimitProfit` echo. The N2 3b runtime log in
`review-risk-sized-bridge-trades.md` §2 shows the chase running, so this path ran there too.

**Consequence, stated only as far as the code proves it:** after the re-anchor the position is 310
and its TP leg is 10. A TP touch closes 10 and leaves 300 open.

**A further concern I am flagging but explicitly NOT claiming:** the bracket is placed
`linked_order_type: one_triggers_one_cancels_other` with `trigger_fill_condition: first_hit`
(`:3836`–`:3837`), and the TP leg deliberately carries no `reduce_only` (`:3871`–`:3872`). If a
partial 10-lot TP fill counts as the "first hit" that cancels the sibling, the remaining 300 would
be left with no stop. **I have not verified that on the exchange and it should not be repeated as
fact** — but it is the reason I am raising D1 rather than filing it as cosmetic.

**Why this is not something I can just fix under §2.** The natural source is *not* the new field:
§2's own lifecycle requires `placedOrderSizeUsd` to be cleared at the fill transition, and the TP
re-anchor happens strictly **after** that, off a later echo. The correct source is the
exchange-derived position model, which is exactly the precedent the restore-hardening work already
set for the triggered-SL edit at `:4460`:

```vb
' :4460, today, for the triggered-SL edit
Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)
```

**Proposed fix — one line, `:4305` becomes that same expression**, with the same comment lineage
("a post-fill TP leg covers the POSITION"). The `orderAmountVal` fallback is retained deliberately:
`positionSizeUSD` is seeded on the receive thread from the position channel (`:2894`) and the
id-777 snapshot (`:5582`), and I cannot prove from the code that it is always seeded before this
particular echo — the fallback keeps today's behaviour in that window instead of sending 0.

**Census impact if adopted:** no change to any §4.6 line; `orderAmountVal` in `frmMainPageV2.vb`
stays at its current 24 occurrences (the read is wrapped, not removed). Adds one commit.

### D1 — the ruling I need

| option | effect |
|---|---|
| **A (my recommendation)** — fix inside N2b, own commit | Closes it now, one line, precedent-matching. N2 can be enabled on N2b's acceptance. |
| **B** — out of scope, own spec | I record it and write nothing. **N2 must then stay DISABLED past N2b**: a risk-sized position would carry a box-sized TP leg, which is a worse outcome than the defect N2b fixes. |
| **C** — out of scope, accept the risk, unblock N2 | Recorded only. I advise against this. |

---

## D2 — a restarted app forgets the placed size, so the defect survives a restart

**Site:** `HandleOpenOrdersSnapshot`, the id-778 restore, `frmMainPageV2.vb:5495`–`:5505`:

```vb
Case "EntryLimitOrder", "EntryTrailingOrder"
    If state = "open" Then
        CurrentOpenOrderId = id
        Dim seeded As Boolean = False
        If placedPrice = 0D Then                 ' <- price is restored, single-writer
            placedPrice = If(price, 0D)
            seeded = True
        End If
        ...
```

The restore adopts the working entry's **id** and **price**, and (from the restore-hardening runtime
fix) its **direction** — but not its **amount**. So after a restart with a live 310-lot bridge entry
resting, `placedOrderSizeUsd` is `0`, the chase falls back to the box, and the **first reposition
after the restart resizes the order to 10** — N2b's own defect, reintroduced by a restart.

This is inside the field's lifecycle, which §2 calls "the load-bearing part", but the spec's
enumerated clear/set list (fill transition · both cancel teardowns · the placement seeds) does not
reach it, because a restore is neither a placement nor a teardown.

**Proposed fix — two lines**, in that same branch, under the identical single-writer rule the price
already uses:

```vb
If placedOrderSizeUsd = 0D Then
    placedOrderSizeUsd = If(o.SelectToken("amount")?.ToObject(Of Decimal?)(), 0D)
End If
```

`amount` is present on Deribit order objects and the codebase already reads it off an order echo at
`:3160`. Seeding only when `0` keeps the snapshot from ever overwriting a live in-process value,
matching `placedPrice`/`placedStopLossPrice` in the same loop.

**Census impact if adopted:** `placedOrderSizeUsd` becomes 1 decl + 1 set + 2 chase reads +
1 restore seed + the clear sites, i.e. §4.6's new line needs "+ 1 restore seed". No other census moves.

### D2 — the ruling I need

| option | effect |
|---|---|
| **A (my recommendation)** — seed it in N2b | Two lines, closes the restart hole, matches the restore-hardening precedent. §4.6's new census line gains "+ 1 restore seed". |
| **B** — out of scope | Field stays `0` after a restart; the chase falls back to the box exactly as today. Recorded in the impl report as a known hole. |

---

## D3 — informational only, NO ruling needed: the third working-entry edit site

Recorded so the reviewer is not surprised by it, and so §4.6's **"2 chase reads"** is understood to
be a *proved* number rather than an assumed one.

There is a **third** function that edits the working entry with `amount = orderAmountVal`:
`UpdateStopLossForTrailingOrder` (`:4542`), which edits `CurrentOpenOrderId` at `:4580`. The spec
names only `:4193` and `:4275`.

**I am not adding a read to it, because it can never see a non-zero `placedOrderSizeUsd`:**

1. It is called only from the **trailing** reposition block, `:2547` / `:2585`.
2. That block is gated on `isTrailingStopLossPlaced = True` (`:2524`).
3. `isTrailingStopLossPlaced = True` is written at **exactly one site**: `:4815`, in
   `StopLossForTrailingOrderAsync` — which is the placement of the trailing order itself, at
   `amount = orderAmountVal` (`:4843`), i.e. **the box**.
4. `:4805`–`:4811` is one of the two "a fresh order re-establishes a clean context" placement seeds
   (it resets `cancelPending`, `emergencyFired`, `legAnchorPrice`). §2 names those seeds as clear
   sites, so `placedOrderSizeUsd = 0D` lands there — set to the box's own meaning, immediately
   before the only site that can arm this block.
5. Independently, the entry-chase and trailing-chase blocks cannot both own the order context: the
   entry block additionally requires `CurrentTPOrderId`/`CurrentSLOrderId` (`:2171`), and a trailing
   bracket has no TP leg.

So the field is provably `0` on every path that reaches `:4542`, `orderAmountVal` is the correct
source there, and adding a read would be dead code that breaks the pinned census. **If the
orchestrator disagrees with this reachability argument, that changes §4.6's "2 chase reads" to 3 and
I would want to hear it.**

---

## What I am doing meanwhile

Implementing §2 and §3 exactly as written, gate per commit:

- `placedOrderSizeUsd As Decimal = 0D`, set **unconditionally** in `ExecuteOrderAsync` where the
  payload amount is finalised (§2: *"records exactly what was sent"*). Acceptance 3 sanctions
  either "0 **or** equal to the box" for manual placements; unconditional is the safer of the two
  because **the placement is then itself a clear** — a manual order after a rejected 310-lot bridge
  act overwrites the stale value instead of inheriting it, which is acceptance 4's exact risk.
  One behaviour delta this creates is recorded in the impl report: editing the Amount box while an
  order rests no longer resizes that resting order mid-chase.
- Both chase reads take `If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd`.
- Clear set matched to `cancelPending`/`emergencyFired` as §5 demands, with the enumeration and the
  no-leak argument written out in the impl report.
- §3's `Position entered:` line: **the filled echo's own `amount`**, falling back to the retained
  size then the box — mirroring the `average_price` read two lines above it at `:3243`. Rationale
  in the impl report, per §3's instruction to say which value I chose.

Related: `spec-chase-preserve-placed-size.md` (the spec), `review-risk-sized-bridge-trades.md`
§2/§3 (the runtime evidence and the pre-existing attribution),
`spec-risk-sized-bridge-trades.md` (N2, which this blocks), `spec-fill-reanchor-fix.md` (the
origin of the D1 site), `spec-restore-hardening.md` (the `:4460` precedent D1's fix copies and the
id-778 restore D2 extends), `spec-entry-chase-v2.md` §4 (`EntryOnlyChase` and `legAnchorPrice`,
which is why D1's gate is effectively always true).
