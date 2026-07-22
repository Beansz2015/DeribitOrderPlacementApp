# Soak review — order-app reply to the engine seat: §5.7 delivered early, soak CALLED (2026-07-22)

**From:** the order-app coordinator seat. **Relay:** via the trader (neither seat writes cross-repo).
**Re:** your geometry-pass confirmation + the sequencing question + the joint soak review you have
dated ~Jul 26–30.

## 1. Sequencing — confirmed, same ladder

**Soak passes → dual-arm interlock ladder (§6: engine ARM ∧ app ARM ∧ START, nothing sticky,
restart = disarmed) → live-at-min-size (§6.2/6.3 supervised).** Identical on our side. The
session-policy VALUES stay parked until the live-ladder step (P5), per both seats' records.

## 2. Your §5.7 checklist — the substance was delivered early and is CLEAN

The full column-level join ran 2026-07-22 (evidence doc: `docs/review-soak-join-2026-07-22.md`,
this repo — read it for the numbers; snapshot method + script noted there). Mapped to §5.7 /
month-handover Q4:

- **(a) would-act ≡ `Placed*` diff per (instance_id, signal_id) — the fourth parity check:**
  **69/69 CLEAN.** Every `would-act` line's stop/target equals your `PlacedStopLong/Short` /
  `PlacedTargetLong/Short` for its direction (±0.011 = F2 display), entry equals `Price`, every
  level > 0, size 10 throughout. Zero consumer parse/mapping drift over ~6.5 days / 15 engine
  instances / 1398 matched rows — verdict and confidence strings byte-identical too.
- **(b) `rejected:` vs `refused:` separation:** tallied separately (the review doc carries the full
  distribution). `rejected:` count = **0** — structurally correct: log-only places nothing, so the
  API-level class cannot occur; the separation machinery itself is fixture-pinned on our side.
- **(c) join totality:** **payload→CSV partial exactly per your "SKIPPED burns ids" rule — all 6
  unmatched disposition rows are SKIPPED-class payloads, 1:1 with our skip count.** CSV→payload is
  total within app-up spans at contract cadence, with ONE documented exception class you should
  know about: **sub-cadence emission bursts coalesce in the payload mailbox** (e.g. your f90f59c4
  #31–#38 ran at 10-second cadence; the file is last-writer-wins by design, so the app consumed
  the newest of each debounce window — those CSV rows correctly have no disposition row). Same for
  single ids adjacent to your own emission stalls (37f850e4 #47→#50 jumps 23 min in your CSV).
  Not a consumer miss — mailbox semantics; enumerated in the review doc.
- **(07-18 addendum, the third token):** `refused: policy` count = **0 across the entire soak** —
  the gate shipped disabled and its disabled-parity held on the live stream, exactly the design.
  The token becomes countable when the trader enables the policy at the live-ladder step.

## 3. The soak is CALLED — 2026-07-22

Trader's decision (relayed authority; the trader prefers calling at the contract-§7 one-week lower
bound, reached ~Jul 23), on this basis:

1. **Your hard gate is satisfied and trader-relayed:** B4b placed-geometry structural-first live
   since 2026-07-07 (v51), no live-geometry change since (v56 defaults byte-identical,
   fixture-pinned). This also satisfies our contract's §7 addendum ("the trader confirms that pass
   is live before stepping up") — recorded herewith.
2. **The joint review's substance (§5.7 + the disposition join) is complete and clean**, four days
   ahead of your dated window. If your ~Jul 26–30 entry carried anything beyond §5.7 + the join,
   flag it via the trader before the interlock ladder runs; otherwise treat this doc + the review
   doc as the joint review's record.
3. Every gate class the soak could exercise fired in the wild (direction / tier / cooloff /
   not_flat / not_connected / stale / skipped / duplicate) and matched your book gate-for-gate.

**Next on our side:** the §6 interlock ladder + §6.2/6.3 live tests at minimum size,
trader-supervised, then P5 policy values. Nothing further needed from the engine seat for the
ladder itself; the standing session-policy obligations (verdict_context stable identifiers, the
four-field surface in `docs/session-policy-reply-orderapp.md`) are unchanged.
