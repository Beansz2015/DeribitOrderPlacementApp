# Spec — position protection, batch 2: a stop that dies, a position with no stop, and the truths the experiments found

**Origin:** the testnet evidence in `docs/runtime-record-protection-batch1-2026-10-09.md` §2–§5 and
`docs/runtime-record-x1-testnet-2026-10-10.md`, reviewed in `docs/review-x1-testnet-experiments.md`. Audit rows
`A*n*` are the engine report's (`docs/triage-adversarial-audit-2026-10.md` names its source).
**Status at writing:** open, no code written. Facts verified at HEAD on 2026-10-10.
**Scope:** `frmMainPageV2.vb` (and `.Designer.vb` for one tooltip); OrderCheck. **Order, receive and
emergency paths → Opus, high.**
**Owner rulings:** §0 — all four RULED 2026-10-10.

---

## §0 — Decisions

| # | Decision | Options | Coordinator recommendation | Status |
|---|---|---|---|---|
| R1 | What the app does when the exchange **rejects the stop leg** (item 1) | (a) alarm only · (b) a replacement stop-market · (c) flatten at once | Coordinator recommended (c) | ✅ **RULED 2026-10-10 by the owner, sharper than (c): do what the stop and the M.SL would have done.** Price past the stop's **trigger** but not past the **M.SL distance** → place the stop's own reduce-only **limit** where the trigger would have placed it, and let the triggered-stop chase take over. Price past the **M.SL distance** → **flatten at once**. See §1 |
| R2 | What the live watchdog does when a position has **no stop for any other reason** (item 2) | (a) alarm only · (b) flatten | (a) | ✅ **RULED 2026-10-10: alarm only** |
| R3 | A minimum stop distance at placement | (a) none for now · (b) a configurable floor | (a) — the decision-bias tripwire flagged it `gives_up_for_economy` (5/5), shown to the owner | ✅ **RULED 2026-10-10: no floor for now** |
| R4 | **Reduce** buttons while a **triggered** stop is already closing the position (item 5) | (a) refuse with a message · (b) cancel, then reduce · (c) re-price the stop | (a) | ✅ **RULED 2026-10-10: refuse with a message** |

## 🚫 Do-not-touch

| Thing | Why |
|---|---|
| The emergency close's send order (cancel, then reduce) | Reduce-before-cancel with a **triggered** SL resting is still unobserved (`docs/review-x1-testnet-experiments.md` §3, row (g)). |
| The OTOCO payload | Item 6 corrects comments and a tooltip; the dropped `trigger_offset` stays in the payload (no behaviour change). |
| The nine census symbols | Any count change must be explained. |
| Batch 1's restore NO STOP check (`IsClosingStop`) | Reuse it; do not fork its rule. |

## §1 — Item 1: the rejected stop leg (row A13) — R1

**Evidence:** experiment (a). The OTO template arrives on `user.changes` as
`order_state: "rejected:price_too_high"`, `label: "StopLossOrder"`, `is_secondary_oto: true`,
`order_id: "OTO-…"`, with `primary_order_id` set (record Appendix A.1). The state chain in
`HandleOrderPositionUpdates` (`frmMainPageV2.vb:3461` `open` / `:3692` `untriggered` / `:3736` `filled` /
`:3847` `cancelled`) has no branch for it, so it falls through silently.

**Change:**
- Add a branch for `orderState` **starting with `rejected`**, any label.
  - Label `StopLossOrder` (or `TrailingStopLoss`): red log naming the reason, `Alert`, urgent page
    "Stop REJECTED by the exchange (<reason>) — <side> <size> position has NO STOP". Then the R1 action:
  - **R1 action (owner ruling 2026-10-10).** Read the rejected leg's own `trigger_price` and `price` (the
    stop limit) from the push. Compare the current own-side quote with them, for the position's side:
    - **Past the M.SL distance** — the same test the M.SL emergency uses in
      `UpdateStopLossForTriggeredStopLossOrder` (its baseline, before a trigger, is the stop's trigger price;
      `marketStopLossChecked` on and `marketStopThreshold` > 0): **flatten at once** with the reduce-only
      market path, with batch 1's honest SENT/CONFIRMED/FAILED messages.
    - **Otherwise** (the price is past the trigger, which is why it was rejected): place a **reduce-only,
      post-only limit** for the position size at the leg's `price`, label `StopLossOrder`, and set the same
      triggered-stop context a real trigger sets, so the existing triggered-SL chase and the M.SL cap take it
      over from there. A post-only limit that would cross is repriced to the top of the book by the exchange
      (experiment (c)), so it cannot be rejected for crossing.
    - **M.SL unticked or its threshold 0:** there is no "market stop" distance, so always the limit branch.
    - Page what was done: "… stop limit placed at <price>" or "… flattening".
  - Any other label: yellow log naming the label and reason; normal-priority page.
- The push can arrive **before** the entry's fill is processed. Take the side from the leg's direction
  (a `sell` stop protects a long) and act on the position as soon as it is known. Do not drop the event
  because `positionSizeUSD` is still 0.
- The executor feedback must not go on reporting the dead leg as `working.stop` (audit G2): clear what the
  feedback reads for the stop when the leg is rejected.
- The buy-side reason (`price_too_low` presumably) was not observed: match the `rejected` prefix, never
  the full string.

## §2 — Item 2: the live no-stop watchdog — R2

**Evidence:** experiments (a) and (f) each left a live position with no stop, and the app said nothing.
Batch 1's NO STOP check runs only at restore.

**Change:**
- Once a second, evaluate: `positionSizeUSD <> 0` **and** the app tracks no live closing stop (an
  untriggered SL leg, a triggered SL limit, or a trailing stop) **for longer than 5 s**. The grace covers
  the gap between the fill echo and the leg echoes.
- On the first breach of an episode: red log, `Alert`, urgent page "Position <side> <size> has had NO STOP
  for 5 s". Once per episode; a new episode starts when a stop reappears or the position goes flat.
- A triggered stop is a **new order id** linked by `trigger_order_id` (experiment (e)). The watchdog's
  notion of "a live stop" must follow that link, or it fires during every normal trigger.
- Pure seam for the decision (position, stop-known, seconds since) → OrderCheck.

## §3 — Item 3: cancel-on-disconnect check at connect

**Evidence:** cancel-on-disconnect was on for the testnet account and deleted every stop on every
disconnect. The live account must be checked by hand (`docs/production-cutover-checklist.md` §1); this
item makes the app check every time.

**Change:** after authorising, call Deribit's read-only `private/get_cancel_on_disconnect` (scope
`account`). If it reports enabled: red log, `Alert`, urgent page "Deribit cancel-on-disconnect is ON — every
disconnect deletes your stops". **Never change the setting from the app.** If the call fails, log yellow and
carry on. The implementer confirms the method name and response shape on testnet before relying on them.

## §4 — Item 4: re-anchor nothing new, but say what is true

No code change to the SL geometry in this batch (the chased bracket is already re-anchored before the fill).
Correct the three comments that say the exchange trails the SL (`frmMainPageV2.vb:3770`, `:3816`, `:4998`):
experiment (b) proved `trigger_offset` inside `otoco_config` is dropped, so the OTOCO stop never trails.
Change the `Trig.O.` tooltip to say it has no effect on OTOCO stops. An app-side trailing stop is a
separate feature, queued, not built here.

## §5 — Item 5: Reduce while a triggered stop is closing — R4

**Evidence:** the owner's Reduce Sell, with a triggered reduce-only SL limit covering the position, got
`11030 other_reject invalid_reduce_only_order` twice (`docs/runtime-record-protection-batch1-2026-10-09.md`
§5). With an **untriggered** SL resting, a reduce-only market fills (experiment (g)).

**Change (R4 (a)):** `btnReduceLimit_Click` (`:7006`): when a triggered SL is live, do not send. Log
"Reduce not sent: the triggered stop is already closing the position at the top of the book". With an
untriggered SL, or none, behave as today. Leave `btnReduceMarket_Click` unchanged.
Also: when an `11030` comes back on a reduce, log it as "reduce-only rejected — a reduce-only order may
already cover the position, or the position is already flat" (the code is not specific, experiment record §4).

## §6 — Item 6: small

- A `Mkt. BUY`/`Mkt. SELL` fill prints no "Position entered" line (no `EntryMarketOrder` case in the fill
  branch). Add it, matching the limit entry's line.

## §7 — Acceptance

1. Gate passes; OrderCheck all pass, with new fixtures that fail against today's code (the `rejected`
   prefix match; the watchdog decision; the R4 refusal decision); the nine censuses re-run.
2. **Testnet, harness or owner** (the experiment seat's procedure is the model — `docs/spec-x1-testnet-experiments.md` §0):
   a. A crossed stop (Trig.P. 5, a fast market, or a Mkt entry with the trigger already crossed) → the
      REJECTED page and the R1 action.
   b. Cancel the SL by hand in the Deribit UI with a position open → the watchdog page after 5 s, once.
   c. A normal trigger → **no** watchdog page.
   d. Turn cancel-on-disconnect on, then off, on testnet → the page on connect, then none.
   e. Reduce Sell while a triggered SL is closing → the refusal message, no `11030`.

## §8 — Spec-back folded in (coordinator review, 2026-10-12)

Source: `docs/impl-report-protection-batch2.md` §3; dispositions in `docs/review-protection-batch2.md` §2.
- **§1, "the price is past the trigger":** not always. The own-side quote can sit inside the spread on the
  near side of the trigger. The rule stands: the limit is placed, repriced to the top of the book, and chased.
- **§1, "at the leg's `price`":** a rejected `TrailingStopLoss` has no limit price. Use its `trigger_price`;
  with neither, place nothing, log red "Place a stop by hand"; the page and the watchdog still fire.
- **§1, the M.SL cap after an R1 limit — OWNER RULING 2026-10-12:** the R1 limit's M.SL baseline is the
  **dead stop's trigger**, settled (frozen). It is NOT re-anchored at the top of the book like a real trigger.
  The flatten fires when the price is the M.SL distance past the trigger. Fix specified in
  `docs/review-protection-batch2.md` §3.
- **§4, "change the `Trig.O.` tooltip":** there was none. One is added to the label.

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Receive path, order path, emergency path.
- Coordinator review: Opus, regardless of implementer.
