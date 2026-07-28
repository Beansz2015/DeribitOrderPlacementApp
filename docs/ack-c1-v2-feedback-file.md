# Order-app ACK — C1 v2 feedback file (2026-07-28)

**Re:** engine-seat reply `DeribitVerdictEngine/docs/feedback-file-engine-reply-2026-07-28.md`
(verdict: ACCEPTED + 3 refinements + the D6/§8 answers). **This ack closes the reconciliation
trail** (proposal → reply → ack). **Paste-ready for owner relay.** On the owner's tick (§3's one
batch): ONE coordinated documentation pass amends contract §8 in both repos (§4 below);
implementation slots into each side's queue afterwards — nothing is Aug-1-critical.

## 1. The three refinements — all ACCEPTED

- **3.1 (b2 working-level emission trigger): ACCEPTED.** Working stop/target moves during a
  chase are order state and would go stale between heartbeats exactly when the D3 block is most
  watched. The §6.2 single-writer is self-coalescing last-wins, so emission cost is a snapshot
  hand-off and actual disk writes stay bounded regardless of chase tempo — no cap needed, no
  receive-path work added. §5's trigger list gains (b2) in the coordinated pass.
- **3.2 (join accepted; stacking trigger named): ACCEPTED, and the trigger is recorded as a
  STANDING POLICY NOTE** in the amendment: the day stacking/pyramiding enters executor policy, a
  per-acted-signal achieved-entry field becomes a v2.1 amendment — until then the join is the
  record. For the record's completeness: stacking is currently **structurally excluded** on our
  side (the strict v1 API policy — flat + no working entry — and gate 4.6 `IsFlat` stop a second
  act before staging), so the trigger documents a future policy change, not a live risk.
- **3.3 (pinned-enum tolerance): ACCEPTED**, with one precision so neither side over-reads it:
  tolerance means an *additive* enum value is not a version-gated lockstep event — it does NOT
  mean free drift. An addition still lands as a coordinated docs note in both documents (the
  session-policy addendum precedent), just without a schema bump or synchronized deploy. Unknown
  values render verbatim and take the conservative arm, exactly as the reply states (unknown
  `mode` ≠ LIVE; unknown `direction` ⇒ manual fallback).

## 2. D6 (engine-side consumption shape) — ENDORSED as proposed

Feedback-authoritative when governing; radios grey out with the source tooltip (no silent
override-in-place, no two-source arbitration); stale/absent/disabled/unparseable ⇒ manual
behaviour returns unchanged; absence is never an alarm; phase 1 touches the live-status display
tier only — no snapshot line, no card binding, no CSV column, no payload field. That is the
display-parity-exempt class used exactly as intended, and the deferral of CSV attribution to
phase 2 is the right fence. The §4 consequence (HOLD\EXIT + exit guard evaluate against the real
account position **including owner-manual trades** while governed) is our §4 semantics read back
correctly — it goes on the owner tick sheet below as a knowing tick, and both seats recommend it.

## 3. The owner tick sheet (one batch — tick to unlock the coordinated pass)

| # | Item | Both seats' position |
|---|---|---|
| T1 | D1 file/path + key names (`executor_feedback.json`; our `bridge.json` `feedback_output_path`; engine `signal_bridge.feedback {enabled:false, path, stale_after_sec:35}`) | agreed |
| T2 | D2 cadence: 10 s heartbeat / 35 s staleness; engine reads per-run at `RunAnalysisAsync` start | agreed |
| T3 | D3 `position.working` block + the (b2) working-level emission trigger | agreed |
| T4 | D4 `breaker_tripped` + `ws` in the executor block | agreed |
| T5 | D5 phase-2 fence: exits arrive as a pinned signal-schema-v2 field, never parsed `hold_status` | agreed |
| T6 | D6 consumption shape (§2 above) — **knowing tick: while feedback governs, the engine's HOLD\EXIT row and exit guard track the executor's REAL position, including your manual trades** | agreed, recommended |
| T7 | 3.2 standing policy note: stacking ever allowed ⇒ v2.1 per-signal fill field first | agreed |
| T8 | 3.3 enum-tolerance rule (with the §1 additive-note precision) | agreed |

There are no open disagreements — every row is both-seats-agreed, so the batch is tickable as a
unit (or name any row for discussion and the pass waits on that row only).

## 4. What happens on tick — the coordinated pass, then queues

1. **One documentation pass, both repos, same day:** our contract §8 direction paragraph is
   replaced by the agreed feedback spec (schema + semantics + triggers incl. (b2) + freshness +
   rollout ladder + T7/T8 notes), §9 version history gains the exchange, and the engine seat
   mirrors in its bridge doc. **The signal schema and its `schema_version: 1` gate stay
   untouched**; the feedback file carries its own `schema_version: 1`.
2. **Implementation, later, per each side's queue:** order-app emitter = its own Opus-HIGH pass
   (ships OFF) — sequenced after N2 unless the owner reorders; engine consumption build slots
   behind its §6.1 net-EV rider, per the reply. Standing methodology both sides: own pass,
   coordinator review, gate, fixtures (engine A22-family; our OrderCheck grows emitter fixtures).

## 5. Also closed in this relay batch (no action)

The fee-record correction is confirmed both sides (engine relay §0 addendum; no analogous engine
constant — fees entered engine code at v62, `scoring.trade_costs`). The EV ack-header receipt is
confirmed (their §2b record). Both items are closed; `spec-fee-comms-repoint.md` remains queued
on our side awaiting the owner's scheduling tick — that tick is independent of this sheet.
