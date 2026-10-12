# Spec — partial fills: exit legs track the filled quantity; the take-profit never opens a position

**Written 2026-10-12 by the coordinator seat (Opus 5.5), on the owner's go the same day.**
**Origin:** audit row `A14` (the engine report's partial-fill finding, from stop-lifecycle audit `G3`) and its
option set `C-17`; both are named and sourced in `docs/triage-adversarial-audit-2026-10.md` §1 and §3. Owner
ruling 2026-10-07 (`docs/triage-adversarial-audit-2026-10.md` §7): **no code cap**; the owner holds `N2 LIVE`
and any Amount above 10 USD until this fix ships.
**Status:** open. Phase 1 (an experiment) and Phase 2 (the fix) are both unbuilt. **All four owner rulings
in §0 are in (2026-10-12).** Nothing here waits for the owner until `N2 LIVE` (§6 step 5).
**Scope:** `frmMainPageV2.vb`; a temporary testnet-only module for Phase 1; OrderCheck. **Order, receive and
edit paths → Opus, high.**
**Facts below verified at HEAD `96371d4` on 2026-10-12** by read and grep, unless marked otherwise.

Finding and decision IDs in this doc carry the prefix `PF-` (partial fill). Do not confuse `PF-R1`…`PF-R4`
with batch 2's `R1`…`R4` in `docs/spec-protection-batch2.md` §0.

---

## §0 — Decisions for the owner

| # | Decision | Options | Coordinator recommendation | Status |
|---|---|---|---|---|
| PF-R1 | Run the X-2 testnet experiment (§3), with three temporary testnet-only switches | (a) yes · (b) no — design from Deribit's docs only | (a) | ✅ **RULED 2026-10-12: (a), a seat runs X-2 after batch 2 lands** |
| PF-R2 | The mechanism (§4 item F1) | (a) `trigger_fill_condition: incremental` + the app guards · (b) app-side resizing only, keep `first_hit` · (c) `complete_fill` · (d) cancel the remainder at the first partial fill | (a), **conditional** on X-2 confirming §3 questions Q1–Q4 and Q6–Q7; if X-2 refutes any, fall back to (b) | ✅ **RULED 2026-10-12: (a), conditional as written.** The coordinator applies the condition from the X-2 record; no further owner ruling needed either way |
| PF-R3 | What the leg-size check (§4 item F3) does when a take-profit rests **larger than the position** | (a) edit the TP down to the position, then alert · (b) alert only | (a) | ✅ **RULED 2026-10-12: (a)** |
| PF-R4 | What the leg-size check does when a take-profit rests **while the position is flat** (it would open a position) | (a) cancel it, then alert · (b) alert only | (a) | ✅ **RULED 2026-10-12: (a)** |

Owner rule (`docs/triage-adversarial-audit-2026-10.md` §1): anything that changes live order behaviour needs
the owner's ruling before it ships. PF-R2, PF-R3 and PF-R4 all change live order behaviour.

**Decision-bias tripwire** (`docs/harness-runs/decision-bias-20261012T0920Z-*`, baseline written first):
- PF-R2's mechanism: `no_richer_option`, stable 5/5. Agrees with the seat's baseline. Not flagged.
- PF-R1's evidence route: `richer_option_wrong`, stable 5/5. The seat's baseline said `no_richer_option`.
  Not flagged (only `gives_up_for_economy` goes to the owner). The richer option the seat rejected is the
  owner producing a partial fill by hand from a second sub-account before 2026-10-14.

## §1 — The defect, as the code stands

**The order.** `ExecuteOrderAsync` (`frmMainPageV2.vb:4489`–`4523`) sends the entry as an OTOCO with
`trigger_fill_condition: "first_hit"` (`:4496`). Both legs carry the **full order `amount`** (`:4500`, `:4509`).
The take-profit has no `reduce_only` (`:4530`–`4531`, commented out on purpose). The stop leg has
`reduce_only: true` (`:4518`).

**Deribit's documented semantics** (docs.deribit.com, `private/buy`, read 2026-10-12; **not observed**):
- `first_hit`: "any execution of the primary order will fully cancel/place all secondary orders."
- `complete_fill`: "a complete execution (meaning the primary order no longer exists) will cancel/place the
  secondary orders."
- `incremental`: "any fill of the primary order will cause proportional partial cancellation/placement of the
  secondary order. The amount that will be subtracted/added to the secondary order will be rounded down to
  the contract size."

**The S0 path** (audit proof scenario `partial`, Amount 500: position −490, 0 alerts):
1. A 500 USD entry fills 10. Under `first_hit` both legs are armed at 500.
2. The entry remainder is cancelled (chase abort, stand-down, the owner).
3. The 500 USD take-profit fills against a 10 USD long. The account is now **short 490**.
4. The OCO cancels the stop. The short has **no stop**. `TradeMode` still says long.

The stop leg alone cannot cause this: it is reduce-only, so it caps at the position.

**Why a reduce-only take-profit is not the fix.** X-1 experiment (f) observed it: with `reduce_only` on the
TP, an SL trigger cancels **both** legs and leaves the position with no stop
(`docs/runtime-record-x1-testnet-2026-10-10.md` Appendix A.6).

## §2 — Second cause: the app's own edits resize the take-profit

Under any mechanism, these edits re-send a leg `amount` that is **not the position**. Each one can re-inflate a
correctly sized take-profit:

| Site | Function | Amount sent | Leg | Risk after a partial fill |
|---|---|---|---|---|
| `:5009` → `:5021` | `ReanchorTPToFillAsync` | `orderAmountVal` (the Amount box) | post-fill TP | **re-inflates the TP to the box** |
| `:7127` → `:7154` | `btnEditTPPrice_Click` | `orderAmountVal` | TP (pre- or post-fill) | **re-inflates the TP to the box** |
| `:7183` | `EditStopLossTo` | `Decimal.Parse(txtAmount.Text)` | SL | none — reduce-only caps it; an under-size stop is possible if box < position |
| `:4893`–`4894` → `:4940`, `:4942` | `UpdateLimitOrderWithOTOCOAsync` (bracket chase arm) | placed size | pre-fill TP and SL templates | depends on how a template edit behaves after a partial fill (§3 Q5) |
| `:5241` | `UpdateStopLossForTriggeredStopLossOrder` | `|positionSizeUSD|`, else the box | triggered SL | none — already position-sourced |
| `:5323`, `:5650` | trailing-entry paths | `orderAmountVal` | SL only (reduce-only) | none for this defect; out of scope |

**The re-anchor was examined before, and correctly cleared for its time.** `docs/spec-back-chase-preserve-placed-size.md`
raised it as `D1` (a spec-back finding: the re-anchor resizes the position's TP to the Amount box).
`docs/review-chase-preserve-placed-size.md` rejected that `D1`: a bridge entry always sets a manual TP, so the
re-anchor never runs on one, and "on the manual path … the position size IS the box". **A partial fill breaks
that premise.** On a partially filled manual entry the position is smaller than the box, so the re-anchor sets an
oversize, non-reduce-only TP. This is not a defect today, because under `first_hit` the TP is already full size.
It **becomes** a defect the moment F1 ships, unless F2 ships with it.

## §3 — Phase 1: the X-2 testnet experiment (PF-R1)

**Why a partial fill CAN be produced on testnet.** X-1 tried 500 and 2000 USD **post-only** entries; both filled
whole (`docs/runtime-record-x1-testnet-2026-10-10.md` Appendix A.5). A maker's fill size is set by whoever takes
it. A **taker** limit that is priced at the best ask, with an amount larger than the size resting there, takes
that level and rests the remainder. That is a partial fill by construction. The app cannot send one today:
limit entries are always `post_only` (`:4537`).

**Procedure.** Reuse X-1's: `docs/spec-x1-testnet-experiments.md` §0 (safety rules, unchanged) and its
temporary-switch pattern (`cd15c8d`, removed in `7f253fc`).
- New module `X2Testnet.vb`, same gating as X-1's: a switch is ON only when `AppSecrets.IsTestnet` **and** a flag
  file of its name exists in the exe folder. A live session can never honour it.
  - `X2_RAW_ECHO` — X-1's raw echo, restored verbatim (log name `x2-raw-echo.log`).
  - `X2_TAKER_ENTRY` — the `EntryLimitOrder` placement sends `post_only: false`, priced at the best ask (buy) or
    the best bid (sell). Only the placement changes; chase edits stay as today.
  - `X2_INCREMENTAL` — the OTOCO sends `trigger_fill_condition: "incremental"`.
- **Sizing.** Before each placement, read the top-of-book size with a public, keyless `public/ticker` call
  (`best_ask_amount` / `best_bid_amount`). Set Amount to about **3× that size**, at most 5000 USD. If the entry
  fills whole, flatten and retry once with a larger amount. Testnet funds only.
- Every experiment ends **flat with no open orders**. Never let an oversize TP fill: flatten with reduce-only
  market and cancel the legs before moving on.
- The bridge stays Off. Harness placement only through `tools/place-and-verify.ps1`.

**Experiments** (verbatim raw echoes for each):

| # | Setup | Observe |
|---|---|---|
| (a) | `first_hit`, taker entry, partial fill | Q0: are both legs armed at the **full** amount? (the audit's claim, unverified) |
| (b) | `incremental`, taker entry, partial fill | Q1: leg amounts after the fill. Q2: one order per leg whose amount grows, or a new order per fill? Order ids, `oto_order_ids`, the templates' `filled_amount` |
| (c) | (b), then cancel the entry remainder | Q3: legs stay at the filled size? What happens to the OTO templates? |
| (d) | (b), let the remainder fill (chase on) | Q2 again: how the legs grow |
| (e) | `incremental`, normal post-only maker entry, whole fill | Q4: echo shape and ids **identical** to `first_hit` (Appendix A.5)? This is every normal live trade |
| (f) | (b), `private/edit` the post-fill TP amount down by one contract, then back up | Q5: amount edits accepted on a placed leg? Also: edit a template's amount after a partial fill, and record what happens |
| (g) | `incremental`, whole fill, push price through the SL trigger | Q6: the SL trigger cancels the TP and the SL limit closes the position, as under `first_hit` |
| (h) | `incremental`, whole fill, TP fills | Q7: the TP fill cancels the stop, as under `first_hit` |

**Out of scope, recorded:** a **partial TP fill** and what the OCO does to the stop's size. It is not specific
to partial entries. Note it in the record if seen.

**After the run:** record it in `docs/runtime-record-x2-testnet-<date>.md`; the coordinator reviews it. Keep
`X2Testnet.vb` until the Phase 2 acceptance run (§5 item 3) has used `X2_TAKER_ENTRY`. Then remove it, and
grep for `X2Testnet.` to show zero call sites.

## §4 — Phase 2: the fix (after X-2, after batch 2 lands)

**F1 — the mechanism (PF-R2).** If X-2 confirms Q1–Q4 and Q6–Q7: `ExecuteOrderAsync` sends
`trigger_fill_condition: "incremental"`. The trailing-entry OTO (`StopLossForTrailingOrderAsync`, `:5543`) stays
`first_hit`: its only leg is a reduce-only stop, which cannot open a position. If X-2 refutes any of Q1–Q4 or
Q6–Q7, F1 is dropped, and the fix is F2 + F3 alone (PF-R2 option (b)).

**F2 — one amount source for every take-profit and stop edit.** Add one helper, `LegAmountForEdit()`:
- a position exists (`positionSizeUSD <> 0`) → `|positionSizeUSD|`;
- otherwise (pre-fill templates) → the placed size (`placedOrderSizeUsd` if > 0, else `orderAmountVal`).
Use it at `ReanchorTPToFillAsync`, `btnEditTPPrice_Click` and `EditStopLossTo`. The bracket chase arm edits the
templates only while the entry has **no** fill. After any fill it edits the entry alone (exact rule set by Q5).
`UpdateStopLossForTriggeredStopLossOrder` already follows this rule; leave it. Pure seam → OrderCheck.

**F3 — the leg-size check.** It runs in batch 2's one-second watchdog tick (`docs/spec-protection-batch2.md` §2).
It does not fork that tick. Track the live take-profit's `amount` from its echo (exchange truth; a new field).
- TP resting with `amount > |positionSizeUSD|` for more than 2 s → the PF-R3 action. Default (a): edit it down
  with `LegAmountForEdit()`, yellow log, normal page "TP was <x>, position <y>: resized".
- TP resting while the position is flat for more than 2 s → the PF-R4 action. Default (a): cancel it, red log,
  urgent page "TP resting with no position: cancelled".
- Once per episode, as batch 2's watchdog does. Pure seam (TP amount, position, seconds) → OrderCheck.
- The 2 s grace covers the gap between a fill echo and the position echo. Under `incremental` the TP should
  never exceed the position, so in normal running F3 never fires. It is the backstop for every path F1 and F2
  miss (manual edits on the Deribit UI, a socket gap, a missed echo).

**🚫 Do-not-touch**

| Thing | Why |
|---|---|
| The TP's missing `reduce_only` | X-1 experiment (f): a reduce-only TP makes an SL trigger cancel both legs |
| The emergency close's send order | `docs/spec-protection-batch2.md` do-not-touch list; unchanged here |
| A size cap in code | Owner ruling 2026-10-07: no code cap |
| The trailing-entry paths | Their only leg is reduce-only; out of scope |
| The nine census symbols | Any count change must be explained |

## §5 — Acceptance

1. Gate passes (`tools/checks/verify-gate.ps1`); the nine `frmMainPageV2.vb` censuses re-run (occurrence counts).
2. OrderCheck fixtures: `LegAmountForEdit()` (position, flat, placed-size override); the F3 decision (oversize,
   flat, equal, within grace); the audit's `partial` scenario as a fixture: 500 entry, 10 filled, remainder
   cancelled → no TP above 10 survives.
3. Testnet harness run, with `X2_TAKER_ENTRY` and the fix in force: a partial fill → legs at the filled size;
   remainder cancelled → legs stay at the filled size; re-anchor and Edit TP → TP stays at the position; a
   hand-edit of the TP amount up on the Deribit UI → F3 resizes it within 3 s.
4. A normal whole-fill trade at 10 USD behaves exactly as before (echo shape, ids, re-anchor, SL trigger chase).

## §6 — Sequence

1. ✅ The owner ruled PF-R1 to PF-R4 on 2026-10-12, before the absence (2026-10-14 → 2026-11-25).
2. **After the batch-2 implementer reports and its review passes:** the X-2 seat. The batch-2 implementer is
   editing `frmMainPageV2.vb` in this tree now, and X-2 touches the same placement code and the x64 bin. Its
   testnet session can share one launch with batch 2's own harness acceptance run.
3. Coordinator reviews the X-2 record and confirms or drops F1.
4. Phase 2 implementer; coordinator review; acceptance §5 item 3; remove `X2Testnet.vb`.
5. The owner then decides `N2 LIVE` and Amount above 10 USD (`docs/HANDOVER-7.md` §2 item 3).

## §6b — Not verified by this seat

- Deribit's `incremental` behaviour: documented, never observed. That is what X-2 is for.
- That a taker limit at the best ask partially fills on testnet: reasoned from matching rules, not yet tried.
- The audit's claim that `first_hit` arms full-size legs on a partial fill: X-2 (a) checks it.
- Whether `positionSizeUSD` updates before the TP's open echo on a partial fill: F3's grace assumes a gap of
  under 2 s; X-2 timestamps will show it.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **X-2 experiment seat:** Opus 5.5, high — temporary switches on the placement path, live testnet orders.
- **Phase 2 implementer:** Opus 5.5, high — order, receive and edit paths.
- **Reviews:** coordinator, Opus 5.5, high.
