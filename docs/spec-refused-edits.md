# Spec — refused edits: the app's record follows the exchange, and a stuck stop pages

**Written 2026-10-13 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Origin:** `TN-F3` (a finding in `docs/runtime-record-testnet-2026-10-12.md` §6, disposed in
`docs/review-testnet-2026-10-12.md` §2): when Deribit refuses an edit, the app still advances its own record and logs
the move, and 58 s of refused triggered-stop edits raised no page. Extended by `PF-SB4` (item 3 in
`docs/review-pf-phase2.md` §2): a refused take-profit resize is retried only once per episode.
**Status:** open; **all four owner rulings in (2026-10-13)**; no code. Ready for an implementer seat.
**Scope:** `frmMainPageV2.vb`; OrderCheck. **Stop, entry and edit paths → Opus, high.**
**Facts verified at HEAD `e184218` on 2026-10-13** by read and grep.

IDs in this doc carry the prefix `RE-` (refused edit). Do not confuse `RE-R1`… with batch 2's `R1`… or `PF-R*`.

---

## §0 — Decisions for the owner

| # | Decision | Options | Coordinator recommendation | Status |
|---|---|---|---|---|
| RE-R1 | A refused **stop** edit (triggered-SL chase, id 223350; also 223346, 223348) | (a) reset the chase reference to the stop's price from its latest exchange echo, so the next tick re-targets from the truth · (b) keep today's optimistic reference | (a) | ✅ **RULED 2026-10-13: (a)** |
| RE-R2 | A triggered stop the app **cannot move** | (a) urgent page once per episode after 3 consecutive refused edits of the triggered stop, or 10 s with the confirmed stop price behind the chase target · (b) log only, as today | (a) | ✅ **RULED 2026-10-13: (a)** |
| RE-R3 | A refused **take-profit resize** (request id 42) | (a) the leg-size check keeps retrying on the SL backoff schedule (0.67 s → 5 s cap) until the TP matches; one page per episode · (b) one retry per episode, as built | (a) | ✅ **RULED 2026-10-13: (a)** |
| RE-R4 | A refused **entry chase** edit (id 223344) | (a) reset `placedPrice` to the entry's price from its latest echo · (b) keep today's optimistic reference | (a) | ✅ **RULED 2026-10-13: (a)** |

Owner rule: anything that changes live order behaviour needs the owner's ruling before it ships. RE-R1, RE-R3 and RE-R4
change which edits are sent; RE-R2 adds a page only. All four were ruled before the owner's absence.

**Decision-bias tripwire** (`docs/harness-runs/decision-bias-20261012T2032Z-*`, baselines written first): RE-R2
`no_richer_option`; RE-R1, RE-R3, RE-R4 `richer_option_wrong`; all stable 5/5. None flagged.

## §1 — The defect, as the code stands

**The triggered-SL chase** (quote handler, `frmMainPageV2.vb:2917`–`2978`):
- After `UpdateStopLossForTriggeredStopLossOrder` returns, the chase sets `placedStopLossPrice = newStopPrice`
  (`:2936`) and logs `SL reposition sent: …` (`:2967`). The send site swallows every exception, so this runs when
  the exchange **refuses** the edit too. The comment at `:2954` already says so.
- The next tick computes `shouldUpdate` against `currentStopPrice` = that optimistic reference. If the market comes
  back, the app thinks the stop already moved and sends nothing, while the exchange's stop sits at the old price.
- A refused SL edit is counted into the retry backoff (`docs/spec-sl-backoff-coupling.md`, hook at `:2128`), and a
  **confirmed** edit (the commanded-price echo) resets it (`:3677`). So a confirmation channel already exists.
- `10028` (rate limit) returns early to `HandleRateLimitError` (`:2028`) and is **not** counted. It is still a
  refused edit for this spec's purpose.

**The entry chase** (`:2674`–`:2728`, `:3029`, `:3069`): `placedPrice = chaseTarget` after the send, whatever the
reply. `TN-F3` saw `Order repositioned: $82757.00 → $82760.50` while the exchange kept the old price.

**The take-profit resize** (Phase 2, `docs/impl-report-pf-phase2.md` §6 `PF-SB4`): a refused F3 resize pages, but
the episode rule allows no second attempt; the TP stays wrong until the episode changes.

## §2 — 🚫 Do-not-touch: the M.SL anchor latch

`:2941`–`:2944` settles `emergencyBaseline` at the **first** post-trigger reposition target, on the send, even when
the edit is later refused. **Keep it.** The anchor is a loss bound, not a record of the order's price. The intended
anchor is the top-of-book stop; settling there gives the tightest bound. Settling only on a confirmed edit would leave
the anchor at the flip price, which for a long sits **below** the top of book, so the M.SL flatten would fire later.
That widens the bound, which the owner's standing ruling forbids (memory `standing-rulings-in-force`: never beyond one
M.SL past the intended anchor). Also untouched: the `SB3` pin and its restart re-pin; the emergency send order.

## §3 — The change (per the rulings)

**RE-1 — the stop's reference follows the exchange (RE-R1).**
- Track the stop's last **confirmed** price from its echo. Batch 2's live-order map already holds each order's latest
  echo (`ApplyOrderToLiveMap`).
- On a refusal of 223350, 223346 or 223348 — an error reply, **including `10028`**, but **not** the benign
  `already_closed` / `order_not_found` races that already `Return` early — set `placedStopLossPrice` to the confirmed
  price. The next tick re-targets from it, still governed by the backoff and the single-flight flag.
- Log: `SL edit REFUSED (<code>) — stop still at $X; chase resumes from there` (yellow). The existing `SL reposition
  sent` line stays (it says "sent", not "moved").
- Pure seam: (reference, confirmed price, refusal kind) → new reference.

**RE-2 — a stuck triggered stop pages (RE-R2).**
- While `SLTriggered`: count consecutive refused edits of the triggered stop (reset on a confirmed echo). At 3, or when
  the confirmed price has trailed the chase target for > 10 s, send one urgent page per episode:
  `Stop cannot be moved: <n> edits refused (<last code>) — stop at $X, market $Y. M.SL cap at $Z still armed`.
- Use batch 2's one-second watchdog tick for the 10-s test; do not fork it.
- Pure seam: (refusals, seconds behind, already paged) → page or not.

**RE-3 — the take-profit resize retries (RE-R3).** In F3 (`EvaluateLegSizeCheckAsync`): after a refused id-42
resize, re-arm the episode's action after `NextSlBackoff`'s delay (0.67 s doubling, 5 s cap). Page once per episode
(the existing `TP resize REFUSED …` page). Never more than one id-42 edit in flight (the Phase 2 gate).

**RE-4 — the entry chase reference follows the exchange (RE-R4).** On a refusal of 223344 for the **entry** (not the
TP re-anchor, which shares the id), reset `placedPrice` to the entry's echoed price; log
`Entry edit REFUSED (<code>) — order still at $X`. The 223344 id is shared: decide by the order id in the request,
kept with the send.

## §4 — Acceptance

1. Gate passes; the nine `frmMainPageV2.vb` censuses re-run (occurrence counts; at `e184218`: 71 across 67 lines).
2. OrderCheck: each seam, with fixtures that fail against today's code (mutation proof): RE-1 reference reset
   (refused, 10028, benign race unchanged); RE-2 page decision (2 refusals no page; 3 → page; 10 s behind → page;
   once per episode; a confirmed echo resets); RE-3 retry schedule; RE-4 entry reset.
3. **Runtime — needs the owner** (auto-mode refuses a seat's testnet order placement; the owner is away until
   2026-11-25). On testnet: a refused triggered-SL edit is hard to cause under `first_hit`. A temporary testnet-only
   switch that sends the chase edit with an invalid field (so Deribit refuses it) is the candidate; the spec for that
   switch is written with the runtime seat. Until then: fixtures and code reading.

## §5 — Not verified by this seat

- How often refusals happen under `first_hit` in practice. Seen so far: `already_closed` races (benign) and `11050`
  (only under `incremental`, now dropped). `10028` has not been seen on an SL edit.
- That the confirmed price in the live-order map is current when the refusal reply lands (reply and echo ordering).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Implementer:** Opus 5.5, high — the triggered-SL chase and the entry chase.
- **Review:** coordinator, Opus 5.5, high.
