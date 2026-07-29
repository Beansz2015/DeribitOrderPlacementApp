# Spec — EV-aware chase budget (fee-awareness Rec 1; Deribit fee change 2026-08-01)

**Origin:** engine-seat relay `DeribitVerdictEngine/docs/fee-aware-order-app-relay-2026-07-27.md`
(maker 1.5 bps / taker 3.5 bps effective 2026-08-01). **Contract impact: NONE** (their §0 —
no payload fields, no token changes; fee constants deliberately duplicated per-repo, a schedule
change = a two-repo settings touch by relay).

**Engine-seat ack (owner relays this header):** Rec 1 ACCEPTED with one design correction — the
suggested `cancelled: ev_floor` DISPOSITION cannot exist: the disposition file is one-row-per-
payload at consumption, and the chase abort post-dates that payload's `acted` row; a second row
breaks the frozen join cardinality. The counterfactual instrument is the CANCEL REASON in the host
log (`Working entry cancelled (EV floor)` vs `(ATR slippage)`) — same instrument the live-ladder
audit used. Rec 2 ACCEPTED AS GUIDANCE in v1 (checklist §4 note: set M.SL net of the ~2 bps
crossing delta ≈ 0.0002 × price); the M.SL comparison is a flat dollar check with no break-even
arithmetic to fold into, and it is the exact code N1 just moved under a verbatim guarantee —
touching it pre-acceptance is wrong sequencing. A computed version, if wanted, is a later
micro-spec.

**Recommended implementer:** **Opus HIGH, fresh conversation** — the predicate joins the
`HandleQuoteUpdates` chase gates (entry-chase v2 territory, just normalized own-side by R2).
**Sequencing: jumps ahead of N2** (Aug-1 date pressure; N2 ships disabled and can wait). After N1
runtime acceptance, or parallel if the owner accepts two open runtime items.
**Target:** `frmMainPageV2.vb` (gates + one pure seam), `AutoTradeSettings` (one Tooling box),
`AppUserSettings.vb` (knob + fee block), `tools/OrderCheck`. **Ground rules:** standing (gate per
commit, never push, anchors by symbol re-verified at HEAD, impl report).
**Do-not-touch:** the emergency block and `emergencyFired` latch (N1, acceptance-pending); the
disposition file format/cardinality; `IsATRSlippageExcessive` internals; the §4 gate chain.

## §1 — The predicate (pure seam, fixture-pinned)

```vb
' Stop chasing when the remaining move to the target no longer clears round-trip fees plus the
' trader's minimum net move. Pure; Friend Shared; OrderCheck-pinned.
Friend Shared Function IsChaseEvExhausted(targetInForce As Decimal, currentPrice As Decimal,
                                          roundTripFeePct As Decimal, minNetMovePct As Decimal) As Boolean
    If minNetMovePct <= 0D OrElse targetInForce <= 0D OrElse currentPrice <= 0D Then Return False ' OFF/undefined = never binds
    Dim remainingNet As Decimal = Math.Abs(targetInForce - currentPrice) - roundTripFeePct * currentPrice
    Return remainingNet < minNetMovePct * currentPrice
End Function
```

- `roundTripFeePct` = maker entry + maker TP = **3.0 bps = 0.0003** (the trader's standing
  maker-first flow, per the relay).
- **`minNetMovePct <= 0` ⇒ the EV condition is OFF entirely** — ships with default 0 ⇒
  byte-identical to today; the owner enables it by typing a value before Aug-1. (Opt-in
  restriction, the session-policy shipping pattern.)

## §2 — Where it joins (the four reposition gates; whichever binds first)

At each of the four own-side reposition gates, the abort condition becomes
`IsATRSlippageExcessive(...) OrElse (chase-EV check)`, still under the SAME
`maxSlippageATRchecked` arm switch (housekeeping 8b made that checkbox the single arm for
chase-abort guards — keep it that way). When the EV predicate is the one that tripped, the cancel
reason is **`"EV floor"`** (→ `Working entry cancelled (EV floor) - position legs untouched`);
ATR trips keep `"ATR slippage"` byte-identical. The 6 pre-placement gates are **NOT touched** —
EV-exhaustion is a property of an ongoing chase, not of initial placement.

**As implemented + review-ratified (2026-07-28, `review-ev-chase-budget.md` D1/D2):**
- **The EV check's price input is PINNED to the same own-side quote the ATR guard reads**
  (`bestBid` LONG / `bestAsk` SHORT) — not `chaseTarget`. Ruled D1: binds strictly later than the
  economically-exact input in both directions (≈ spread + tick, <2% of threshold), so it only
  ever defers to today's behaviour; invariant-8 uniformity wins. Revisiting this is a spec change.
- The `OrElse` is realized as **`ChaseAbortReason(ownSideQuote, direction) As String`** (returns
  the cancel reason or `Nothing`) because two distinct reasons cannot come out of a bare `OrElse`.
  ATR is evaluated first inside it (keeps owning `originalSignalPrice` seeding + the
  attempt-reset side effect); each reason literal exists on exactly one code line.
- **Grep note:** the standing "four reposition gates call the ATR guard" grep is superseded —
  `IsATRSlippageExcessive` is now 8 sites (1 decl + 1 call inside `ChaseAbortReason` + 6
  pre-placement); the four gates grep as `ChaseAbortReason`.

## §3 — `targetInForce` (D1, ruled): `manualTPval` when > 0, else SKIP

- Bridge trades always carry `manualTPval` (= the engine target, set at act) — the relay's primary
  case, fully covered. Manual trades with a typed TP likewise.
- Manual OFFSET-flow trades (no manual TP) fall back to **ATR cap only, exactly as today** — the
  offset target moves with the chase anchor, so a v1 EV check against it would be self-referential.
  Documented limitation, revisit only if the owner asks.
- **Review correction (2026-07-28, replaces the spec-back §3 residual's derivation):** the rule is
  uniform across ALL FOUR gates — the EV floor is live wherever `manualTPval > 0` at evaluation.
  Gates 3/4 are the **pre-fill chase of the manual Trail flow** (BuyTrail/SellTrail sets
  `isTrailingStopLossPlaced`; the block requires a working entry), NOT a post-fill context: the
  position-open clear has not run there, so a Trail entry with a typed manual TP has the EV floor
  live, correctly (`manualTPval` seeds the placed-TP display and `TPTrailprice`). Gates 3/4 still
  *usually* run ATR-only — because the Trail flow is offset-centric, not because the target was
  cleared. No separate trailing target source is needed.

## §4 — Knobs + persistence

- `AppUserSettings`: `MinNetMovePct As Decimal = 0D` (key `min_net_move_pct`, absent ⇒ 0 = OFF) and
  a fee block `maker_fee_bps = 1.5` / `taker_fee_bps = 3.5` (keys documented in the example json;
  the round-trip constant derives as 2 × maker — no hardcoded 3.0 left behind when the schedule
  next changes). All on item A's atomic path.
- `AutoTradeSettings` Tooling: one box `txtMinNetMove` (caption **`Min Net Profit:`** — renamed from
  `Min Net Move:` by owner ruling 2026-07-30, **display text only**: the control name, the field, the
  `min_net_move_pct` key and the harness AccessibleName all still say MinNetMove — unit `%` — value
  entered as a percent, e.g. `0.05` = 5 bps; parse invariant, ÷100 to the pct fraction — state the
  unit in the tooltip AND the impl report, this is the classic knob-unit trap) + seed-before-commit
  (sixth application) + commit-push like the breaker; keep-last-good on garbage. Fee bps are
  file-only knobs (no UI) — they change once a year by exchange announcement, by relay.
- Bridge/manual both read the same mirrors — the check lives host-side in the gates, no bridge
  code at all.

## §5 — Fixtures (OrderCheck)

`IsChaseEvExhausted`: OFF at 0 knob · binds when remaining move < fees+floor (e.g. target 64800,
price 64786, 3 bps fees ≈ 19.4, floor 5 bps ≈ 32.4 ⇒ remaining 14 − 19.4 < 32.4 TRUE) · slack when
far (remaining 60 ⇒ FALSE at same knobs) · exact-boundary FALSE/TRUE around equality · zero/absent
target never binds · SHORT symmetry (target below price, Abs handles it) · fee derivation
2 × maker_bps.

## §6 — Acceptance

1. Gate per commit; fixtures counted.
2. **Knob-0 parity (ship-safe proof):** default state ⇒ all four gates byte-identical to HEAD
   (structural: the predicate returns False without evaluating further).
3. **Testnet (isolated harness):** knob set high (e.g. 0.5%) + a bridge act with a near target ⇒
   chase aborts with `Working entry cancelled (EV floor)` on the FIRST reposition evaluation;
   knob 0 ⇒ same setup chases to the ATR cap exactly as today.
4. Persist round-trip for the knob; fee keys hand-editable and read back. **AUTOMATED (review
   D3 ruling): OrderCheck carries 7 persistence fixtures incl. a leave-the-path-as-found
   assertion — this item is OFF the owner's runtime list.**
5. Greps: emergency block untouched (`emergencyFired` census unchanged 1+2+3+3 — **note a raw
   grep returns 10: 9 code sites + 1 pre-existing N1-era comment at the throttle block**);
   `IsATRSlippageExcessive` internals untouched (8 total sites post-collapse, see §2's grep
   note); disposition file writers untouched; reason string is the ONLY new host-log surface.

## Commits

1. `EV chase budget: predicate + fee/knob config (+ fixtures)` · 2. `gate integration + cancel
reason` · 3. `Tooling box (seed-before-commit)` · 4. impl report
(`docs/impl-report-ev-chase-budget.md`).
