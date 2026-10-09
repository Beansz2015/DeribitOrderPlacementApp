# Spec — position protection, batch 2: a stop that dies, a position with no stop, and the truths the experiments found

**Origin:** the testnet evidence in `docs/runtime-record-protection-batch1-2026-10-09.md` §2–§5 and
`docs/runtime-record-x1-testnet-2026-10-10.md`, reviewed in `docs/review-x1-testnet-experiments.md`. Audit rows
`A*n*` are the engine report's (`docs/triage-adversarial-audit-2026-10.md` names its source).
**Status at writing:** open, no code written. Facts verified at HEAD on 2026-10-10.
**Scope:** `frmMainPageV2.vb` (and `.Designer.vb` for one tooltip); OrderCheck. **Order, receive and
emergency paths → Opus, high.**
**Owner rulings:** §0 lists the decisions; each is marked RULED or OPEN. Nothing marked OPEN is built.

---

## §0 — Decisions

| # | Decision | Options | Coordinator recommendation | Status |
|---|---|---|---|---|
| R1 | What the app does when the exchange **rejects the stop leg** (item 1) | (a) alarm only · (b) place a replacement reduce-only stop-market · (c) **flatten at once** with a reduce-only market | **(c).** A stop rejected as `price_too_high`/`price_too_low` means the price is already past the stop level, so the stop would have fired: flattening is what the stop was for. (b) can be rejected again for the same reason in a fast market | OPEN |
| R2 | What the live watchdog does when a position has **no stop for any other reason** (item 2) | (a) **alarm only** · (b) flatten | **(a).** The cause is unknown — the owner may have cancelled it by hand on purpose | OPEN |
| R3 | A minimum stop distance at placement | (a) **none for now** — R1 handles the crossed case · (b) a configurable floor that refuses placement | **(a).** The owner tests with deliberately tight stops; R1 makes a crossed stop safe | OPEN |
| R4 | **Reduce** buttons while a **triggered** stop is already closing the position (item 5) | (a) **refuse with a clear message** · (b) cancel the stop, then place the reduce (a naked window) · (c) re-price the stop to the reduce price | **(a).** The triggered stop is already chasing the top of book, and the chase would override (c) within 333 ms. (b) leaves no stop while a limit rests | OPEN |

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
    "Stop REJECTED by the exchange (<reason>) — <side> <size> position has NO STOP". Then the R1 action.
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

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Receive path, order path, emergency path.
- Coordinator review: Opus, regardless of implementer.
