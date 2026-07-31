# Runtime record — EV chase budget §6.3, 2026-08-01

**Driven by the coordinator (Opus seat) on the harness**, permitted by the amended WATCH protocol
(`investigation-triple-placement-2026-08-01.md`). TESTNET, harness-launched AnyCPU Debug bin — so
the owner's `orderapp-settings.json` and x64 journal were never touched, and no x64 rebuild was
needed. Trade #68 in the harness DB. Harness bin restored to its pre-run state (knob 0, manual TP 0,
ATRSlip unchecked).

## VERDICT: **§6.3 PASSES on the manual arm. EV is closed except the bridge-act leg, which is
owner-only.**

## Setup

Amount 10 · T.Prof 400 · Trig.P 5 · S.Loss 1 · M.SL 200 checked · **ATRSlip CHECKED at 0.6** ·
Comms 22. Bid 63019.50 / ask 63025.00 at the start.

**The one step the impl report's runbook omits: ATRSlip must be CHECKED.** Both chase-abort arms sit
under that single switch —
`If(maxSlippageATRchecked, ChaseAbortReason(...), Nothing)` — so with it unchecked neither the ATR
cap nor the EV floor is ever evaluated and the acceptance cannot produce either observable. The
harness bin had it unchecked by default.

**Arithmetic for the knob-high leg**, at bid ≈ 63020 with maker 1.5 bps:
round-trip fees = `0.0003 × 63020` = **$18.91** · floor at 0.5% = `0.005 × 63020` = **$315.10** ⇒
the EV floor binds while the TP sits within **≈ $334** of price. Manual TP was set 150 above — well
inside, and comfortably above the comms-22 minimum the manual-TP validation enforces.

## Leg A — knob 0 (the control): no EV abort

```
Buy limit order placed For 10 at 63038.5.
Order repositioned: $63038.50 → $63039.50
Position entered: LONG 10 @ $63039.38
Triggered SL placed @ $63032.5
...
Position executed at 63032.   Scratch close: P/L = $0.00.   Trade #68
```

The chase **repositioned normally and no `EV floor` line appeared** — knob 0 behaves exactly as
before, which is the ship-safe default the whole feature rests on.

The entry then filled rather than running on to the ATR cap. That is the known testnet instant-fill
behaviour, not a finding: the acceptance's discriminating claim at knob 0 is that the EV floor does
**not** bind, and it did not. Reaching the ATR cap instead would have required the book to move ~$14
before a fill, which is not arrangeable on demand.

## Leg B — knob 0.5% (the acceptance): aborts on the FIRST reposition

Knob set to `0.5` via commit-on-blur, no warning. Manual TP 63180. The entire log:

```
Buy limit order placed For 10 at 63026.
Working entry cancelled (EV floor) - position legs untouched
```

**Two lines. There is no `Order repositioned` line at all** — the abort happened on the first
reposition evaluation, which is exactly what §6.3 specifies, and the direct contrast with Leg A
(which repositioned at the equivalent moment) isolates the knob as the only difference. No position
was opened; "position legs untouched" is accurate because there were none.

## Persistence — the seed-before-commit proof

- On close, `orderapp-settings.json` holds **`"min_net_move_pct": 0.005`** alongside
  `maker_fee_bps: 1.5` / `taker_fee_bps: 3.5`. The box takes a **percent** (`0.5`), the file stores
  the **fraction** (`0.005`) — the ÷100 conversion is correct in both directions.
- After a restart the box reads back **`0.500`, not `0`**. This is the trap `SeedRiskSizingFromHost`
  /`SeedMinNetMoveFromHost` exist to prevent — `InitialiseSettings` commits before handlers are
  wired, so without the seed step the Designer default would silently overwrite the persisted value
  on every start. It does not.

## UI check

The Tooling row renders as **`Min Net Profit:`** with a `%` unit — the 2026-07-30 caption rename is
live. Reminder for future greps: the caption and the identifiers deliberately disagree
(`txtMinNetMove`, `min_net_move_pct`), so both spellings must be grepped.

## What is NOT covered — the one owner-only item

**The bridge-act leg.** §6.3's second half asserts that when the EV floor aborts a *bridge* entry,
`bridge-dispositions.log` still holds **exactly one row** for that payload (the `acted` row) — the
disposition-cardinality invariant, with the cancel REASON as the counterfactual instrument rather
than a second row. That needs ARM + START + a staged payload, and arming the bridge is outside the
implementer/coordinator safety boundary: the owner drives every ARM and START.

It also needs the payload to pass the live session policy to act at all (LONDON accepts MEDIUM +
CONFIRMED only; ASIA HIGH/MEDIUM), and the payload must land **while** the bridge is STARTED.

Everything the manual arm can prove is proven: the predicate, the gate placement, the knob unit, the
first-evaluation timing, the persistence round-trip and the knob-0 parity. The bridge leg re-tests
the same predicate through a different target source (`manualTPval` set from the engine target
instead of typed), plus the cardinality assertion.

## Residual

The EV floor was exercised on the **entry-chase** gates. Gates 3/4 — the pre-fill chase of the manual
Trail flow — were not exercised; per the review's corrected derivation they are the same uniform
rule (`manualTPval > 0` at evaluation) and usually run ATR-only because the Trail flow is
offset-centric. Unchanged from the review, not a new finding.
