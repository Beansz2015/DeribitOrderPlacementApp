# Impl report — fee-comms repoint (`docs/spec-fee-comms-repoint.md`)

**Implementer seat:** Opus HIGH, fresh conversation, 2026-07-30.
**Commit:** `60b95d6` (the single commit the spec asked for) · this report.
**Base:** `f6aa37a`. **Gate at the commit:** GATE PASSED. **OrderCheck: 140 → 146.**
**Nothing pushed** (owner is the only pusher). **No trades, no ARM, no bridge start.**

---

## 1. What shipped

**The one behaviour site.** `HandleIndexUpdates` (`frmMainPageV2.vb`, the
`deribit_price_index.btc_usd` branch) now reads the persisted schedule:

```vb
comms = DefaultCommsFromTakerBps(takerFeeBpsVal, indexPrice)
```

The 2024 constant and its comment are **deleted** (spec's ruling: a second live schedule is
exactly the defect). Nothing else in the method moved — `comms` is still computed before the
`IsNumeric` guard and still consumed only inside it, so a `Nothing` price still yields 0 and a
garbage price still throws the same `InvalidCastException` into the same `Catch` (the conversion
that used to happen in `Decimal * String` now happens on the parameter; same helper, same throw).

**The seam.** `Friend Shared DefaultCommsFromTakerBps(takerFeeBps, indexPrice)`, pure, placed
beside `RoundTripFeePctFromMakerBps` — the `NextSlBackoff` / `IsChaseEvExhausted` precedent, so no
new file and no constants moved. The rounding is carried over verbatim
(`Math.Abs(Math.Round(x, 0, MidpointRounding.AwayFromZero))`): **only the rate changed.**

**The mirror.** `Private takerFeeBpsVal As Decimal = 3.5D`, seeded in
`ApplyEvChaseBudgetFromSettings` beside `makerFeeBpsVal`. Plain field, not a `userSettings`
property read: `HandleIndexUpdates` runs on the **receive thread** on every index tick, so it
follows the hot-path convention the EV pass established. The field default matches the
`AppUserSettings` default, so a tick that somehow beat `Load` would still see 3.5 bps.

**Prose corrected where it had gone stale** (the taker key was documented as unread in v1):
the `AppUserSettings` fee-block comment and `orderapp-settings.example.json`'s
`_comment_ev_chase_budget`. No behaviour in either.

## 2. The behaviour delta (this is the whole point)

| index | comms before (5 bps, 2024) | comms after (3.5 bps) | delta |
|---|---|---|---|
| 60 000 | 30 | 21 | −9 |
| 64 000 | 32 | 22 | −10 |
| 70 000 | 35 | 25 (24.5, away from zero) | −10 |

`commsVal` (and the box the Trail flow parses) feeds, unchanged, exactly these consumers:

- the derived TP on the pre-fill trail and at placement: `placedPrice ± (tpOffsetVal + commsVal)`,
  and the `txtPlacedTakeProfitPrice` displays — **each derived TP moves ≈ $10 closer to entry**;
- the manual-TP sanity guards (`manualTPval < displayPlacedPrice + commsVal`), which loosen by the
  same $10;
- the **item-E break-even trigger** `RoundToTick(positionAvgEntry ± commsVal)` — the B.E. stop now
  sits ≈ $22 above/below entry instead of ≈ $32, i.e. it arms nearer the entry and covers the fee
  the exchange actually charges from 2026-08-01;
- the Trail-flow sites that parse `txtComms.Text` on the UI thread — they read the same number the
  tick just wrote, so they move with it and needed no edit.

Everything above is the *intended* consequence, and it is what the owner's acceptance 2 is for.

## 3. Do-not-touch, honoured

The EV predicate and its maker-derived round trip (still `2 × maker` — the maker-first flow is
untouched by this spec); the emergency block; the M.SL comparison (Rec 2 stays guidance); the four
reposition gates. Diff is four files: the two code sites above, the fixtures, and two comment/JSON
prose blocks.

## 4. Fixtures (six, 140 → 146)

Two groups, both alongside the existing EV fee fixtures rather than in a new section:

*The arithmetic* — 3.5 bps of 64k = 22 (with the old 32 named in the label so the delta is legible
in the gate output); **5 bps re-derives the retired constant's 32**, which pins that this was a
repoint and not a new formula; the away-from-zero midpoint (24.5 → 25); a zero index → 0.

*End to end from the file* (spec acceptance 1) — inside the existing persistence block, the
hand-edited `{ "taker_fee_bps": 4.0 }` bytes are loaded off disk and drive the comms number
(4 bps at 64k → 26); and the absent-keys reload still yields the shipped 3.5 bps → 22.

## 5. Censuses and greps, as RUN (not as reasoned)

Re-run after the prose edits, per the standing tripwire lesson:

| grep | result |
|---|---|
| the 2024 constant's identifier, `*.vb` + `*.json` | **0** (spec acceptance 3 ✓) |
| `emergencyFired` raw, `frmMainPageV2.vb` | 10 — unchanged |
| `slUpdateFailures = 0` | 1 — unchanged |
| `NextSlBackoff` | 2 — unchanged |
| `takerFeeBpsVal` | 3 (decl · seed · the one read) |

## 6. Open — owner runtime acceptance 2

Not runnable from this seat (no trades, and the number only lands on a live index tick):

1. Rebuild the **x64** bin (the gate builds AnyCPU only) — then **read the window title** and
   confirm `— TESTNET` before anything else; the rebuild clobbers the bin's `secrets.json` from
   project source.
2. Connect and watch the comms box after the first index tick: it should read **≈ 22** at a 64k
   index (≈ 32 before). No position needed.
3. Accept the derived-TP / break-even shift in §2, or re-tune `tp_offset` / the trigger offsets to
   absorb it. `FormClosing` persists geometry unconditionally — back up `orderapp-settings.json`
   first if the runtime pass touches any box.

## 7. Two observations for the reviewer (no code asked for)

1. **`comms` is one taker leg, deliberately.** The spec's arithmetic is `taker × index`, one-way,
   and the break-even trigger consumes it as such. Whether B.E. should cover a *round trip* (and
   whether the maker-first flow means the TP's comms cushion is now conservative in the other
   direction) is the deferred Rec-2 crossing-delta question, not this spec's.
2. **The persisted `comms` key still round-trips** (`orderapp-settings.json`, restored at startup)
   and is then overwritten by the first index tick, exactly as before. So a stale saved `30` is
   cosmetic for the seconds before the first tick — unchanged behaviour, noted only because the
   runtime pass looks straight at that box.
