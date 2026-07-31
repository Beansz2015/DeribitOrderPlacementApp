# HANDOVER-5 — DeribitOrderPlacementApp coordinator seat (written 2026-07-30, Fable→Opus)

**Supersedes `HANDOVER-4.md` as the standing checkpoint.** H-4 §1 (read-first map), §4 (invariants,
incl. the emergencyFired census + disposition-cardinality freeze), §5 (runtime bite-list), §6
(methodology + seat rules) and its §8 delta **remain binding and are NOT restated here** — read
them first; they are current through 2026-07-30 except where §2 below moves the state. Memory's
`era-state-checkpoint` is the live ledger between refreshes — trust it over this snapshot.
**The outgoing Fable seat remains reachable until ~Aug 2 for the reserve list in §5 ONLY** — the
owner is deliberately conserving Fable budget for an engine-seat task.

## 1. State at handover (verify: `git rev-parse HEAD origin/master`; never trust the text)

- **origin/master = `3018c1d` · HEAD is 9 ahead** — 3 N1c commits, 3 Tooling-visual commits, the
  N1c review, this doc, and this correction. Owner pushes. **Do not read a HEAD sha off this
  bullet**: the original said `cf48686`/7-ahead, which was already one short the moment this doc
  committed itself, and any later docs commit shifts it again. The §1 header's rule applies to
  this line first — `git rev-parse` it. (Last verified `64861bf` = 8 ahead, Opus seat, 2026-07-30,
  before this correction.) **Gate at HEAD: GATE PASSED, OrderCheck 153/153** — execute it yourself
  before believing anything.
- Landed + reviewed since H-4 §8 was written: **N1c confirmed-reset** (`a960f07`+`9d3b9f5`,
  review in `spec-sl-backoff-confirmed-reset.md`'s header block — the SL backoff now genuinely
  escalates 666 ms → 5 s and resets only on the exchange-confirmed commanded-price echo; only id
  223350 feeds the set; two accepted residuals: un-reset on lost echo, over-reset on lagging
  older echo — both bounded) and the **EV Tooling UI closure** (visual check passed; 48 px
  reclaimed, form back to 512x856; caption renamed `Min Net Profit:` — DISPLAY TEXT ONLY, the
  identifiers and `min_net_move_pct` key deliberately still say "move": grep BOTH spellings).
- Current grep traps (supersede older notes): `emergencyFired` raw 10 = 9 code + 1 comment ·
  `IsATRSlippageExcessive` 8 (the four reposition gates grep as `ChaseAbortReason`) ·
  `slUpdateFailures = 0` exactly 1, at the echo-recognition branch (NOT the chase Try) ·
  `NextSlBackoff` 2 · `TakerFeeRate` 0 · `RecordCommandedSLPrice` 3 (ONE recording site).

## 2. Open items (owner-side runtime; no code)

1. **SL-backoff runtime record** (N1c acceptance 3 — closes the whole N1b/N1c arc). Owner runs:
   settings backup → x64 rebuild → **`— TESTNET` title check** → size-10 trade, Trig.P 5,
   S.Loss 1, M.SL ~30–50 checked → SL triggers, chase runs → cancel the SL on the testnet WEB UI
   → record the gray `SL-edit failure #N - backoff` climb (0.7→1.3→2.7→5.0 s) + the first
   genuine `SL update rate limited` lines → M.SL cap fires EXACTLY once despite the throttle →
   teardown (restore settings, verify; delete testnet journal rows). Owner sends the log
   excerpt; the coordinator writes `runtime-record-sl-backoff-<date>.md`.
2. **EV §6.3 runtime pass** (the only EV item left): knob 0.5% + near-target bridge act ⇒
   `Working entry cancelled (EV floor)` on the FIRST reposition evaluation; knob 0 ⇒ chases to
   the ATR cap as today. Same rebuild/title/backup discipline. Then EV closes end-to-end.

## 3. The queue (specs WRITTEN; one implementer at a time; owner launches seats)

1. **N2 risk-sized bridge trades** — `spec-risk-sized-bridge-trades.md`, Opus HIGH, ships
   DISABLED. Review anchors: formula parity with `ApplyRiskBasedSize`, sessionFactor applied
   EXACTLY once, floored to the contract step, knob-off byte-identity.
2. **C1 emitter build** — contract §8 (`integration-contract-verdictengine.md`) is the binding
   spec (schema, pinned enums, T7/T8 notes, (b2) trigger, never-on-receive-path, ships OFF).
   Engine consumption build runs on the engine's own queue; phase-2 actionable exits stay fenced
   (signal-schema-v2, never parse `hold_status`).
3. Backlog: `ROADMAP-2026-08.md` §5 (triple-placement WATCH protocol still in force:
   trade-placing runtime steps are OWNER-mouse-only).

## 4. Standing rules (unchanged, binding — H-4 §6 is the full text)

Review = verify actual code AND execute `tools/checks/verify-gate.ps1` AND re-run the censuses
yourself (the era's repeated lesson: comment-only commits move censuses; acceptances can name
unobservable observables — verify what is IN FORCE, including that an acceptance's instrument
can exist). Owner is the only pusher; engine repo read-only; owner-rejected list stays rejected;
spec defects escalate BEFORE implementing; rulings get folded back into specs.

## 5. FABLE-RESERVE LIST — **DISSOLVED 2026-08-01: Fable is off the table (98% usage).**

**Nothing routes to Fable. The Opus seat handles everything below without escalation, including
the two classes it was told to reserve.** One such item has already been handled on that basis:
the 2026-08-01 triple-placement investigation overturned the Fable-era "leans harness-side"
premise (reserve class 1) — see `investigation-triple-placement-2026-08-01.md`. The list is kept
below only as a record of what the classes WERE, since the rationale for treating them carefully
(they touch settled rulings) survives the seat's disappearance — escalate them to the OWNER instead.

<details><summary>The original list (historical)</summary>

1. **Any spec-back that CONTRADICTS recorded state** — a claim that a prior Fable ruling,
   review premise, or acceptance is wrong (the escalation-defect class). Fable holds the
   full verification context of every 2026-07-28→30 ruling and can self-assess fastest.
2. **Any proposal to reverse a standing ruling** on the emergency block, the SL hot path, the
   backoff/reset design (incl. the two accepted residuals), the EV own-side input pin, or the
   C1 phase-2 fence.
3. **The C1 emitter review, ONLY if it lands before Aug 2** (unlikely — it queues after N2).
   Otherwise review it against contract §8 clause-by-clause; the ack + engine reply carry the
   agreed rationale for every field.
Everything else — N2 review, runtime-record write-ups, routine spec-backs, relays — the Opus
seat handles without escalation.

</details>

## 6. First acts for the incoming Opus seat

1. `git rev-parse HEAD origin/master` + execute the gate at HEAD (expect 153/153).
2. Read H-4 §4–§6 + the memory checkpoint; then this doc's §2 to see what the owner has run.
3. Write the two runtime-record docs as the owner's excerpts arrive (§2).
4. Launch N2 on the owner's go; review per §4.
5. Consolidate memory (the checkpoint has accreted superseded sections — fold them; keep the
   traps, rulings-in-force, and reserve list).
