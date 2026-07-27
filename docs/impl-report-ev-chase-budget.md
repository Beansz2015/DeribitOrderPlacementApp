# Impl report — EV-aware chase budget (`docs/spec-ev-chase-budget.md`)

**Implementer seat:** Opus HIGH, fresh conversation, 2026-07-28.
**Commits:** `03bafe8` (predicate + config + fixtures) · `2ffbe1a` (gate integration + cancel
reason) · `fef4371` (Tooling box) · this report.
**Base:** `cd567e1`. **Gate at each commit:** GATE PASSED. **OrderCheck: 104 → 124.**
**Nothing pushed** (owner is the only pusher).

---

## 0. THE UNIT — read this before touching the knob

There is exactly one percent-flavoured surface in the whole path, and it is the UI box:

| Surface | Units | 5 bps looks like |
|---|---|---|
| Tooling box `Min Net Move:` | **percent** (label says `%`) | `0.05` |
| `min_net_move_pct` in `orderapp-settings.json` | **fraction** | `0.0005` |
| `frmMainPageV2.minNetMovePctVal` / `MinNetMovePct` | **fraction** | `0.0005` |
| `IsChaseEvExhausted`'s `minNetMovePct` argument | **fraction** | `0.0005` |

`CommitToolingConfig` divides by 100 exactly once on the way in; `SeedMinNetMoveFromHost`
multiplies by 100 exactly once on the way out. Both use **invariant culture** — under a
comma-decimal culture a plain `Decimal.TryParse("0.05")` reads the dot as a group separator and
returns **5**, i.e. five percent: a 100× error on a guard whose job is to abandon trades. The
JSON key keeps the spec's `_pct` name even though it holds a fraction; the example file's
`_comment_ev_chase_budget` says so in the file itself.

**0 (the shipped default, and what an absent key yields) = OFF.** So does any negative value.

---

## 1. What shipped

**§1 predicate** — `frmMainPageV2.IsChaseEvExhausted`, `Friend Shared`, pure, spec text verbatim.
Alongside it `RoundTripFeePctFromMakerBps(makerFeeBps) = 2 × bps / 10000`: the round trip
**derives** from the maker knob, so no `3.0`/`0.0003` is hardcoded anywhere and the next schedule
change is one number in the settings file.

**§2 gate join** — a private `ChaseAbortReason(ownSideQuote, direction)` returns the cancel REASON
or `Nothing`. Each of the four own-side reposition gates became:

```vb
Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(bestBid, "LONG"), Nothing)
If abortReason IsNot Nothing Then
    Await CancelWorkingEntryCoreAsync(abortReason)
Else
```

ATR is evaluated **first** and its body is untouched, so it keeps owning the `originalSignalPrice`
seeding and the `ResetOrderAttempt` side effect, and an ATR trip still logs the identical line. The
EV arm is second. Both stay under the one `maxSlippageATRchecked` switch (housekeeping 8b). The
two reason literals now exist in exactly one place each — inside `ChaseAbortReason`.

**§3 target in force** — `ChaseEvTargetInForce()` returns `manualTPval` when > 0, else 0 (which the
predicate treats as "never binds"). Verified rather than assumed: every Live bridge act calls
`SetTradeTargets(manualTP:=RoundToTick(p.Target), …)` (`SignalBridge.vb:739`) before
`PlaceAutomatedOrder`, and a payload with `target = 0` never reaches that line (it is
`refused: levels` upstream). So the relay's primary case is fully covered.

**§4 knobs** — `min_net_move_pct` / `maker_fee_bps` / `taker_fee_bps` in `AppUserSettings` on item
A's existing atomic save path, plus `orderapp-settings.example.json`. One Tooling box; the fee keys
are file-only as specced. Hot-path mirrors (`minNetMovePctVal`, `makerFeeBpsVal`) are plain fields —
the four gates read them on the receive thread on every quote tick, so they follow the ATR-tooling
convention, not the userSettings-property convention used by the once-per-signal breaker.

**§5 fixtures** — all seven listed cases plus extras: fee derivation (and that it tracks a schedule
change), OFF at knob 0 **and** at a negative knob, binds, slack, exact boundary on both sides, zero
target, zero price, and the three SHORT mirrors. Plus the §6.4 persistence round-trip (below).

---

## 2. Acceptance (§6)

| # | Item | Status |
|---|---|---|
| 1 | Gate per commit; fixtures counted | ✅ GATE PASSED ×3. OrderCheck **124/124** (was 104; +20) |
| 2 | Knob-0 parity | ✅ structural, see below |
| 3 | Testnet runtime | ⏳ **OWNER-RUN** — recipe in §5 |
| 4 | Persist round-trip + hand-editable fee keys | ✅ **automated**, see below |
| 5 | Greps | ✅ all clean, see below |

### Acceptance 2 — knob-0 parity, stated precisely

With `min_net_move_pct = 0`, `IsChaseEvExhausted` returns `False` on its first guard
(`minNetMovePct <= 0D`) without touching the arithmetic. `ChaseAbortReason` therefore returns
`"ATR slippage"` on exactly the inputs where the old expression was `True`, and `Nothing` on
exactly the inputs where it was `False` — so all four gates take the same branch as HEAD, with the
same side effects, the same reason string and the same log line.

One honest caveat on the word "without evaluating further": the two **arguments**
`ChaseEvTargetInForce()` and `RoundTripFeePct` are evaluated before the call even at knob 0. Both
are pure (a field compare and a multiply), no allocation, no side effect. The OFF check is
deliberately left inside the predicate as the single source of truth rather than duplicated at the
call site.

### Acceptance 4 — automated rather than deferred

`AppUserSettings.Save`/`Load` resolve a fixed path beside the running exe, which for OrderCheck is
**OrderCheck's own bin, never the app's**, so a real file round-trip is safe to run on the gate. The
fixture writes, reloads, asserts the fraction and the fee block survive, asserts the round trip
re-derives from the *reloaded* maker key, asserts all three keys appear as plain top-level numbers,
hand-edits the file to a different schedule and re-reads it, checks that a file with none of the EV
keys yields OFF + 1.5/3.5 — then restores/removes the file and **asserts the cleanup happened**
rather than trusting the write (the harness lesson). If a settings file is already sitting at that
path its bytes are preserved in memory and mirrored to a `.ordercheck-bak` for the duration.

### Acceptance 5 — greps

- `emergencyFired` census: **1 decl + 2 sets + 3 clears + 3 reads** — unchanged. The emergency
  block and the latch are not in the diff.
- `IsATRSlippageExcessive` body: unchanged (the only diff lines mentioning it are the four call
  sites moving into `ChaseAbortReason`).
- Disposition-file writers: **`SignalBridge.vb` is not in the diff at all**. No second row; the
  cancel reason is the only instrument.
- Reason strings: the quoted literals `"ATR slippage"` / `"EV floor"` appear on exactly two lines,
  both inside `ChaseAbortReason`. Two prose comments that originally quoted them were reworded
  before commit 4 — quoting tripwire tokens in prose pollutes the standing greps (HANDOVER-4 §5).
- The 6 pre-placement gates are untouched — they still read `BestPrice` directly and call
  `IsATRSlippageExcessive` inline.

---

## 3. Judgment calls (none of these are in the spec verbatim)

1. **Which price feeds the EV check: the own-side quote.** The spec fixes the predicate's signature
   but not the gate's `currentPrice` argument. I pass the *same* own-side quote the ATR guard gets
   (`bestBid` LONG / `bestAsk` SHORT), not `chaseTarget`. Reasons: it matches HANDOVER-4 §4
   invariant 8 (all four gates measure the own-side quote, R2), it keeps one guard input per gate,
   and the economic difference is the spread plus one tick (≈ $1) against a fee+floor scale of
   ≈ $50 — under 2%. Direction of the error is the safe one: for a LONG the bid is *further* from
   an above-market target than our resting limit would be, so the floor binds slightly **later**,
   never earlier. Flagging it because it is a real choice, not a transcription.

2. **`ChaseAbortReason` returns a reason instead of inlining `OrElse`.** The spec writes the
   condition as `IsATRSlippageExcessive(...) OrElse (chase-EV check)`, but the two arms need
   *different* cancel reasons, so a bare `OrElse` cannot tell the caller which fired. A
   reason-returning helper keeps the short-circuit (ATR first, side effects intact), keeps one arm
   switch, and puts each literal in exactly one place.

3. **Seed-before-commit numbering.** The spec calls this the *sixth* application. N2's fifth has not
   landed, so in the code today it is the fifth. The code comment says both so a future grep is not
   confused; nothing else depends on the number.

4. **`taker_fee_bps` round-trips but is deliberately unread in v1.** The round trip is 2 × maker.
   Taker is carried so the schedule lives in one block and the deferred crossing-delta micro-spec
   (relay Rec 2) has its input. Called out here so a reviewer does not read it as dead weight.

5. **No clamping of hand-edited fee values.** A negative `maker_fee_bps` would only ever *increase*
   `remainingNet` and so make the floor bind **less** — there is no unsafe direction, and a negative
   `min_net_move_pct` is caught by the predicate's own OFF guard. Non-numeric garbage is caught by
   `Load`'s whole-file catch (Designer defaults + a yellow note), as with every other key.

6. **Form geometry.** `grpTooling` grew 48px for the new row and the form's `ClientSize` grew with
   it (856 → 904); `lblAtrNow` moved from y=226 to y=274. Nothing else moved. Worth an eyeball on
   the owner's display, since this window is `TopMost` and sticks to the main form's top-right.

---

## 4. Two things for the owner (neither is blocking, neither was in scope)

**A. `TakerFeeRate` is now stale, and it is LIVE behaviour.** `frmMainPageV2.vb:354` still holds
`Private Const TakerFeeRate As Decimal = 0.0005D` (the 2024 schedule, labelled as such). It is not
decorative: `HandleIndexUpdates` recomputes `comms = TakerFeeRate × index` on every index tick and
writes both `commsVal` and `txtComms`, and `commsVal` feeds the derived TP and the break-even
trigger. Against the 2026-08-01 taker of 3.5 bps it is over-stating the fee by ~43% — roughly $32
vs $22 at a 64k index.

I did **not** touch it. Repointing it would change every derived TP by ~$10 at current prices,
which is a behaviour change well outside this spec (and Rec 2 — the fee-net M.SL question — was
accepted as *guidance only* in v1, explicitly to avoid touching that code before N1 acceptance
closes). It is a clean, self-contained follow-up: one constant, or one more read of the new
`taker_fee_bps` key. Owner's call whether it becomes a micro-spec.

**B. The trailing-entry gates will often run ATR-only.** `manualTPval` is cleared at the
`OpenPositions = True` echo (`frmMainPageV2.vb:3198`). Gates 3 and 4 are the *trailing*-entry chase,
which follows a filled position, so by then the target is usually 0 and `ChaseEvTargetInForce`
returns the §3 SKIP. That is the documented limitation, not a new one, and it fails safe — those
gates behave exactly as they do today. Flagging it because "all four gates" can read as "the EV arm
is live at all four", and in practice it is the two entry-chase gates that will exercise it.

---

## 5. Acceptance 3 — the runtime pass, for the owner

Not run here: implementer seats do not place trades or arm the bridge, and this needs a bridge act.
Standing traps that apply (HANDOVER-4 §5): the gate builds AnyCPU only, so **rebuild the x64 bin**
first; that rebuild **clobbers the bin's `secrets.json` from project source** — re-check the window
title reads `— TESTNET` before anything else; and back up `orderapp-settings.json`, because
`FormClosing` persists all 11 geometry fields unconditionally.

1. Confirm the new box: Auto Settings → Tooling → `Min Net Move:` with a `%` unit, default `0`.
   Hover the caption — the tooltip must state the percent unit.
2. **Knob 0 (parity):** isolated-harness bridge act with a near target → the chase runs to the ATR
   cap and logs `Working entry cancelled (ATR slippage) - position legs untouched`, exactly as
   today.
3. **Knob high:** type `0.5` (= 0.5%, ~$320 at 64k), Tab out to commit — the box must not warn.
   Repeat the act → the chase must abort on the **first** reposition evaluation with
   `Working entry cancelled (EV floor) - position legs untouched`, and `bridge-dispositions.log`
   must still hold **exactly one row** for that payload (the `acted` row — the abort is
   post-`acted` and must not add a second).
4. Restart with the knob still at `0.5` → the box must come back at `0.5`, not `0` (the
   seed-before-commit proof), and `orderapp-settings.json` must read `"min_net_move_pct": 0.005`.
5. Set the knob back to `0` and commit before returning to normal use, unless enabling it now is
   the intent.

> ⚠️ Any read of `bridge-dispositions.log` during this pass: report the row count and note that the
> end-of-soak column-level join is still due.
