# Coordinator review + rulings — N2b (chase preserves the placed size)

**Reviewed:** `8370541` (spec-back) · `82005d0` (§2 field + reads + clears) · `9b04972` (§3 display)
· the impl report, against `spec-chase-preserve-placed-size.md`.
**Reviewer gate at HEAD: GATE PASSED, OrderCheck 173/173.** Censuses re-run independently.

## VERDICT: **code-side APPROVED. One ruling changes the plan (D1 is REJECTED — N2 is NOT blocked
by it). D2 is UPHELD and is the only implementation work left before the runtime legs.**

---

# Part 1 — the three escalated items

## D1 — **REJECTED. The described harm cannot occur.** `ReanchorTPToFillAsync` never runs on a bridge entry.

The report says the re-anchor "fires on essentially every chased bridge entry". It cannot fire on
**any** of them. The staging gate carries a fourth conjunct, with its own comment saying exactly
this:

```vb
' Gate on manualTPval <= 0 => only auto-offset TPs re-anchor.
If legAnchorPrice <> 0D AndAlso entryFillPrice > 0D AndAlso entryFillPrice <> legAnchorPrice _
   AndAlso manualTPval <= 0D Then
```

A bridge act always sets a manual TP: `SignalBridge` calls
`_host.SetTradeTargets(manualTP:=RoundToTick(p.Target))`, which writes `txtManualTP.Text` and syncs
`manualTPval`; and gate 4.4 refuses `Target <= 0`, so it is always positive on an acted signal.
`manualTPval` is still live at the gate — the report's own quoted comment notes the `OpenPositions`
block clears it *after*. **So every bridge entry fails the gate and the re-anchor is never staged.**

This is also settled history rather than a fresh derivation: bridge trades skip the fill re-anchor
via the manual-TP gate, an emergent consequence of contract R2 recorded when the fill-reanchor fix
landed.

**Where the reasoning went wrong** is worth naming, because it is a reusable trap: the spec-back
quoted the full four-conjunct gate, then established that `entryFillPrice <> legAnchorPrice` is
effectively always true under `EntryOnlyChase` — which is correct — and generalised that to "the
gate is effectively always true". **One conjunct's reachability was proved and the others assumed.**

And on the path where the re-anchor *does* run — a manual entry with no manual TP — the position
size **is** the Amount box, so `orderAmountVal` is the correct source there. No fix. No commit.

Consequences of this ruling:

- **N2 is NOT blocked by D1.** The report's §7 recommendation ("N2 must NOT be enabled until this is
  ruled") is void; it rested entirely on the wrong premise.
- The OTOCO `first_hit` / no-`reduce_only` concern in the spec-back is moot for bridge trades, since
  no mismatched TP leg is ever created. It was correctly flagged as *not claimed*, and it stays
  unclaimed and unneeded.
- Acceptance 2's "check the TP leg's size on the web UI" step is **dropped** — there is nothing to
  see. Do not spend a runtime leg on it.

**One residual, recorded and accepted, not a fix:** on a *manual* entry, N2b now makes the resting
entry keep its placed size while the TP re-anchor would still use the current box. If the owner
edits the Amount box mid-chase, entry and re-anchored TP leg can differ. Manual-only, self-inflicted,
and strictly smaller than the pre-N2b behaviour it replaces. If it ever matters, the fix is the
one-liner the spec-back proposed (`If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)`,
the precedent already used for the triggered-SL edit) as its own micro-spec.

## D2 — **UPHELD, option A. Implement it in N2b.**

Verified: the id-778 restore's `Case "EntryLimitOrder", "EntryTrailingOrder"` branch adopts
`CurrentOpenOrderId` and `placedPrice` (and direction) but **not the amount**. So after a restart
with a live risk-sized entry resting, the field is `0`, the chase falls back to the box, and the
first post-restart reposition reintroduces exactly the defect N2b exists to fix.

The proposed two-line seed is right, and right in shape: single-writer (`If placedOrderSizeUsd = 0D`)
so a snapshot can never overwrite a live in-process value, matching `placedPrice` in the same loop.
Seeding a trailing entry's box-sized amount is harmless — it equals what the chase would have used.

**§4.6's new census line becomes `1 decl + 1 set + 2 chase reads + 1 restore seed + 5 clears
+ 2 display reads`.**

## D3 — **no ruling needed; the argument is ACCEPTED, and it is now structural.**

`UpdateStopLossForTrailingOrder` is indeed a third working-entry edit site reading the box, and it
correctly has no read. The proof does not rest on the reachability essay: the implementer put
`placedOrderSizeUsd = 0D` at `:4860`, **immediately before `:4869` — the single site in the file that
sets `isTrailingStopLossPlaced`**, which is the sole gate on the trailing reposition block. The field
is therefore `0` by construction on every path that can reach `:4589`, not merely by argument.
Adding a read would be dead code. **§4.6 stays at 2 chase reads.**

---

# Part 2 — the code that landed

Verified at the sites, not from the report.

- **Field, set, reads, clears** all present and in the spec's exact shapes: decl `:411`; the set at
  `ExecuteOrderAsync :3947`; both chase reads `:4237`/`:4322` as
  `If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd`, placed after the existing
  `orderAmountVal` read so the `amount <= 0D` guard is untouched; five clears (fill `:3422`, nuclear
  `:4007`, scoped `:4101`, trailing seed `:4860`, close `:5250`).
- **The unconditional set is the right call**, and the reasoning is better than the spec's. Making
  the placement itself a clear means a manual order cannot inherit a prior act's size *even through a
  missed teardown* — it converts acceptance 4 from an enumeration that must be exhaustive into a
  structural property. The spec sanctioned either form; this is the stronger one.
- **The `Await`-window ordering check is the load-bearing part of that argument and it holds.** The
  set sits after the send, so the question is whether a chase could read a stale value in between.
  It cannot: the chase gate needs all three working-entry ids non-`Nothing`, every id installer runs
  inside a `Me.Invoke` lambda while `ExecuteOrderAsync` holds the UI thread, so ids cannot transition
  during the window — leaving only "ids already `Nothing`" (no chase possible) or "ids belong to a
  previous entry" (whose own placement set the field to its own correct size).
- **Placing the set after the send also buys a property the spec did not ask for:** the early
  `Return`s between the fold and the send (bad quote, ATR abort, unsupported type) cannot leave a
  size retained for an order that was never placed.
- **§3's value choice is better than either option the spec offered.** The echo's own `amount` is the
  fill's own size; `positionSizeUSD` would misreport an add as a whole-book entry and is not ordered
  against this echo, and the retained size is what was *sent* — the very thing the N2 review warned
  against treating as evidence of what was *held*. The fallback ordering was checked against the
  `:3422` clear running after the loop.
- **The two declared residuals are correctly scoped**: an exchange rejection leaves the field set but
  installs no ids (no chase can fire) and the next placement overwrites; an externally-cancelled
  working entry is pre-existing behaviour shared with `placedPrice`/`legAnchorPrice`, and the dead-id
  edit is already downgraded to a benign gray line.
- **Censuses**: the seven §4.6 tokens all unchanged. The flagged `orderAmountVal` 24 → 26 was
  **verified**: code-carrying lines are **16 before and 16 after**; comment lines 8 → 10. The
  movement is entirely prose, exactly as claimed — a textbook instance of the standing
  "prose pollutes tripwires" trap, correctly anticipated rather than repaired after the fact.

## Behaviour delta — **ACCEPTED, and the owner should know about it**

Editing the Amount box while an order rests **no longer resizes that resting order** on the next
reposition. Before N2b every reposition re-read the box, so typing a new number silently resized a
live order.

I judge the new behaviour clearly better — the box is the input for the *next* order, and silently
resizing a live order because a number was typed is the kind of surprise this era has been removing.
It is also inseparable from the fix: the whole point is that the chase re-sends what was placed.
**But it is a real change to daily manual behaviour, so it is the owner's to veto.**

---

# Part 3 — what remains

1. **D2's two-line restore seed** — the only implementation work left. Same seat.
2. **Runtime acceptances 2–5**, per the impl report §8, with two corrections:
   - **Drop** the D1 TP-leg observation from acceptance 2. Nothing to see.
   - Acceptance 5 needs the Amount box **above the clamp** (e.g. 40). At box 10 a `0.5` mult clamps
     back to 10 and the divergence vanishes — the exact blind spot that hid this defect for weeks.
3. **N2 stays disabled until N2b's acceptance 2 passes** — on N2b's own merits, not D1's.
