# Spec-back — `post_only` on all order edits (as-built) + the `reduce_only` coordination question

**For:** the orchestrator and any pipeline spec that touches order placement/edit payloads or the SL/reduce amount-sizing. **What this is:** a specification reconstructed **from the committed code** (`45a8da5`), not a forward spec — it states the maker/taker contract the code now enforces, with anchors, and then raises **one cross-cutting open question (`reduce_only`)** that affects earlier changes and needs an orchestration decision. Written 2026-07-06. File: `DeribitOrderPlacementApp/frmMainPageV2.vb`. Build 0/0. **Not pushed.**

**Origin:** the owner's maker-first fee preference — limit orders should fill as makers; the **only** deliberate takers are the market entry (`btnMarket`) and the market reduce / M.SL emergency (`btnReduceMarket` → `SendReduceMarketOrderAsync`). Investigation (this session) found that `private/edit` never re-sent `post_only`, so the maker guarantee on any *edited* order depended on Deribit preserving the flag across an edit — not confirmable from the code. `45a8da5` closes that.

---

## 1. What shipped (`45a8da5`) — the contract

**Placement (pre-existing, unchanged):** every limit order is created `post_only: True` + `reject_post_only: False`:

| Order | Placement anchor |
|---|---|
| Entry limit | `:2861` (`If Not MarketOrderType`) |
| Take Profit (OTOCO leg) | `:2830` |
| Stop Loss (OTOCO `stop_limit` leg) | `:2841` + `reject_post_only` `:2843` |
| Reduce limit (`SendReduceOrderAsync`, `Not isMarketOrder`) | `:3082` |
| Trailing main + SL (`StopLossForTrailingOrderAsync`) | `:3627` (main), `:3620` (SL leg) |
| Entry **market** (`btnMarket`) / Reduce **market** (`btnReduceMarket`) | **no** `post_only` — deliberate takers |

**Edit (the change):** all **8** `private/edit` payloads now also send `post_only: True` + `reject_post_only: False`, matching placement:

| Path | id | Order |
|---|---|---|
| `SendRateLimitedUpdate` (trigger branch / regular branch) | 223346 / 223344 | entry-reposition SL / entry-reposition main+TP |
| `SendReduceRepositionEdit` | 223349 | reduce limit chase |
| `UpdateStopLossForTriggeredStopLossOrder` | 223350 | triggered-SL chase |
| `UpdateStopLossForTrailingOrder` (main / SL) | 223347 / 223348 | trailing repositions |
| `btnEditTPPrice_Click` / `btnEditSLPrice_Click` | 223345 / 223346 | manual TP / manual SL buttons |

**Invariant (post-change):** a limit order is a maker across its **whole lifecycle** — at placement *and* on every reposition/manual edit. A would-be-marketable edit is **repriced** to a maker price (`reject_post_only: False`), never filled as a taker. The two market orders (entry-market, reduce-market/emergency) are the only takers, untouched.

**Safety of the change:** re-sending `post_only: True` on an already-post_only order is a no-op if Deribit preserves the flag, and closes the taker hole if it doesn't. `post_only`/`reject_post_only` are valid `private/edit` params, so no rejection risk.

**Known minor interaction (documented):** on the triggered-SL chase (223350) only, if the chase price crosses when the edit lands and Deribit reprices it, the open echo returns the *repriced* price — not the value recorded in the commanded-price set — so the reconciliation (`spec-back-reconcile-manual-sl-edits.md`) logs a benign `Manual SL edit` and self-corrects `placedStopLossPrice`/`emergencyBaseline` to the true SL. Cosmetic, rare, state ends correct.

---

## 2. OPEN QUESTION for the orchestrator — `reduce_only` on edits

**The parallel gap.** `reduce_only: True` is set on the same orders at placement — SL leg (`:2842`), reduce order (`:3071`), trailing SL leg (`:3618`) — and is **likewise never re-sent on any `private/edit`**. The premise that motivated the `post_only` fix (Deribit may not preserve order flags across an edit) applies **equally** to `reduce_only`. This was left out of `45a8da5` deliberately, to keep that change scoped and because — unlike `post_only` — it is **not safe to add blindly** (see the two unknowns below).

**Why it's cross-cutting (affects earlier changes).** The triggered-SL edit sizes its amount from the **position model**, not the input box (restore-hardening, `c6a893c`):

```
' UpdateStopLossForTriggeredStopLossOrder  (:3317)
Dim amount As Decimal = If(positionSizeUSD <> 0D, Math.Abs(positionSizeUSD), orderAmountVal)
```

So every SL chase edit sends an amount equal to the **current modelled position size**, and `reduce_only` is the cap that keeps that from ever *increasing/flipping* the position. If `reduce_only` is dropped on an edit **and** the real position has shrunk below `positionSizeUSD` (a partial fill the model hasn't caught up to), an edit could fill beyond the remaining position and **open the opposite side**. `reduce_only` is the guard. This ties the question directly to:
- **`c6a893c`** (SL amount from position) — the amount that `reduce_only` caps.
- **`63bb149`** (position-model close completion) — assumes reduce legs stay reduce-only through their lifecycle.
- the reduce chase (`SendReduceRepositionEdit`) and both manual SL edits.

**Two unknowns to resolve before acting (empirical — owner/orchestrator to confirm on Deribit):**
1. **Is `reduce_only` an accepted `private/edit` parameter?** If it is *not* editable (set-at-creation, immutable), then it is always preserved and there is **nothing to do** — and blindly adding it to an edit could get the edit **rejected**. (This is the key difference from `post_only`, which is a known-valid edit param — hence `post_only` was safe to add and `reduce_only` was not.)
2. **If it is editable, does Deribit preserve it when the edit omits it?** If yes → no action. If no → it should be re-sent, exactly like `post_only`.

**Recommended decision path:**
- Confirm (1) and (2) on the test sub-account (edit a triggered SL / reduce order, then inspect whether it still reports `reduce_only`).
- **If** `reduce_only` is editable **and** droppable → add `{"reduce_only", True}` to the SL edits (**223346 / 223348 / 223350** + the manual SL button) and the reduce edit (**223349**).
- **Do NOT** add it to the **entry** edit (223344 — the entry opens the position, must not be reduce-only) or the **TP** edit (223345). The TP is deliberately **not** `reduce_only`: the code note at `:2855` records that `reduce_only` on the take-profit **cancels both the TP and SL legs when the SL triggers, leaving the position unprotected**. That is a landmine — flag it explicitly so no one "makes it consistent" by adding `reduce_only` to the TP.

---

## 3. Coordination warnings for parallel specs

- **Order flags now live in two places per order — placement AND edit.** Any *new* edit payload must carry `post_only: True` + `reject_post_only: False` for a limit order (grep the 8 edits above for the pattern). Any *new* limit placement must carry them too. Do **not** add `post_only` to the two market paths (`btnMarket` entry, `SendReduceMarketOrderAsync`) — they are the intended takers.
- **The `reduce_only` decision (§2) touches the same 8 edit payloads** as `45a8da5`. If the orchestrator schedules a `reduce_only` follow-up, it edits the same lines — sequence it against anything else touching these payloads to avoid collisions.
- **TP is intentionally non-`reduce_only`** (`:2855`). Do not "normalise" it.
- **Amount sizing (`c6a893c`, `:3317`) and `reduce_only` are a pair** — the position-sized amount is only safe *because* `reduce_only` caps it. Any change to either should consider the other.

---

## 4. Owner runtime tests

1. **`post_only` sticks on edit:** chase a triggered SL, then inspect the order on Deribit — it should still show `post_only` after the reposition. (Confirms the whole premise; if it showed `post_only` off before, this is the fix.)
2. **Manual SL button to a marketable price** → the order rests as a maker (repriced), not a taker fill.
3. **`reduce_only` probe (for §2):** edit a triggered SL / reduce order and inspect whether it still reports `reduce_only`, and whether Deribit accepts `reduce_only` as an edit param at all. Feeds the orchestrator's decision.
4. **Regression:** entry → trigger → chase → fill with no unexpected taker fills; the rare repriced-chase may log a benign `Manual SL edit` (§1).

Related: `impl-report-post-only-edits.md` (this change), `spec-back-reconcile-manual-sl-edits.md` (the reconciliation the chase interacts with), `spec-back-session-2026-07-04.md` (`c6a893c` amount-sizing, `63bb149` close completion).

---

## 5. Orchestrator review + decision (2026-07-07, Fable coordinator)

**Review — verified against the code (anchors re-read, build 0/0): APPROVED.** Two evidence upgrades:

1. §1's "no rejection risk" claim is *stronger* than stated: the OTOCO **stop_limit** SL leg has carried `post_only: True` since the original placement code (`:2841` — pre-dates all recent work), so Deribit demonstrably accepts the flag on trigger orders at placement; rejection on the trigger-branch **edits** is correspondingly unlikely. Runtime test #1 stays as the confirmation. (The `:2861` "valid only for limit orders" comment contrasts **market** orders — no price field — not trigger orders.)
2. The §1 placement table omits `TrailingStopLossOrderAsync`'s trailing-stop placement (id 30; `reduce_only` at ~`:3738`) — out of scope for the edit question, but include it in any future flag sweep.

**Decision on §2 (`reduce_only`): adopt the recommended path, empirically gated — the probe joins the CURRENT §9 runtime checklist.**

- **Probe (owner, this test session):** after a chase or manual edit of a triggered SL, and after one reduce-limit chase edit, inspect the order on Deribit — does it still report `reduce_only: true`? Optionally attempt one edit that explicitly sends `reduce_only: true` to learn whether the param is accepted at all.
- **Preserved →** no code change; record the finding here; question closed.
- **Dropped (or explicitly editable) →** one micro-commit adding `{"reduce_only", True}` to exactly the **223346 / 223348 / 223349 / 223350** payloads + the manual SL button — **never 223344 (entry), never 223345 (TP — the `:2855` landmine: reduce_only on the TP cancels BOTH legs at SL trigger, leaving the position unprotected)**. Sequence it **before** `spec-entry-chase-v2.md` (same payload lines).
- The §2 pairing argument is confirmed and is stronger than stated: `btnReduceLimit_Click` (`:4714`) also sizes to the **full model** (`Math.Abs(positionSizeUSD)`, direction from the position sign) and its comment says outright "reduce_only caps there anyway". So **three** paths — the triggered-SL chase (`c6a893c`), reduce limit, reduce market — deliberately over-ask at full modelled size and lean on `reduce_only` as the cap. The flag is load-bearing; this question was right to raise.

---

## 6. PROBE RESULT — `reduce_only` preserved; question CLOSED (2026-07-07, owner runtime test)

Owner ran the §5 probe on the test sub-account (trade #45: SHORT 10, SL triggered @ 63686, **five** chase repositions 63686→63729.5, maker fill, no spurious `Manual SL edit`, no backward blips): after the chase edits, the live order on Deribit **still reported BOTH `post_only` and `reduce_only`**.

- **Outcome per the §5 decision: preserved → NO code change.** No `reduce_only` micro-commit; nothing blocks `spec-entry-chase-v2.md` on this question.
- Unknown (2) is answered directly (Deribit preserves `reduce_only` across a `private/edit` that omits it — observed across five consecutive edits); unknown (1) is moot.
- The `post_only` observation confirms the edit payloads work end-to-end (runtime test §4.1 ✅). Note it does not isolate preservation-when-omitted for `post_only` — the edits now always re-send it — but that distinction no longer matters.
- Standing rules unchanged: `reduce_only` stays placement-only; **never** add it to the entry (223344) or TP (223345 — the `:2855` landmine); it remains load-bearing as the cap for the three over-ask paths (§5).

Full session context + the code-review findings: `spec-back-session-2026-07-07.md`.
