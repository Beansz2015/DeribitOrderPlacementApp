# HANDOVER-4 — DeribitOrderPlacementApp coordinator seat (written 2026-07-28)

**Supersedes `HANDOVER-3.md` as the standing checkpoint** (H-3 §4–§5 invariants/methodology remain
binding and are EXTENDED here; H-2 likewise per H-3). Written at the Jul-28 Fable→Fable rotation;
**the ~Aug-2 Fable→Opus transition needs only a light delta on this doc**, not a rewrite. Memory's
`era-state-checkpoint` is the live ledger between refreshes — trust it over this snapshot.

## 1. Read-first map

1. This doc, then the memory index (auto-loaded; `era-state-checkpoint` is the state).
2. `docs/integration-contract-verdictengine.md` — FROZEN v1 + addenda (2026-07-21 session-policy;
   2026-07-22 soak-called stamp). Canonical for consumer behavior.
3. `docs/ROADMAP-2026-08.md` — the plan of record (quick wins DONE; N1 DONE; queue below).
4. Per task: spec → impl-report → review chains. This era's: session-policy
   (`spec-session-policy-gate.md` chain), quick-wins (`spec-quickwins-notifier-signalcols.md`
   chain + `spec-back-quickwins-acceptance-2026-07-25.md`), N1
   (`spec-emergency-hoist.md` chain + `spec-back-emergency-hoist-acceptance-2026-07-28.md`),
   cutover (`production-cutover-checklist.md` incl. §9 AWS migration).
5. `C:\Dev\DeribitVerdictEngine` — read freely, NEVER write; owner relays (recent relays:
   `session-policy-reply-orderapp.md`, `soak-review-reply-orderapp.md`, the EV spec's ack header).

## 2. State at handover (verify: `git rev-parse HEAD origin/master`)

- **HEAD `b1d46e3`, 13 ahead of `origin/master = 9667dc8`** — all coordinator-reviewed docs/rulings;
  owner pushes. Gate at HEAD: GATE PASSED, OrderCheck **104/104**.
- **The system is LIVE end-to-end and production-hardened:** contract v1 frozen · soak passed
  (clean column join, `review-soak-join-2026-07-22.md`) · interlock ladder + live-at-min-size
  executed and CSV-verified (`runtime-record-live-ladder-2026-07-23.md`) · session policy ENABLED
  (LONDON = MEDIUM only; UTC buckets) · breaker persists (`circuit_breaker_usd`) · ntfy notifier
  live (`ntfy_url` in secrets; Instant Delivery on the phone) · signal-tag columns in the trade DB ·
  N1 emergency hoist runtime-accepted (#96–#98).
- **Owner-side open:** push · relay the EV ack header · breaker off the `$1` test value · delete
  testnet journal rows #90–#93/#96–#98 · AWS §9 migration when ready · size ladder.

## 3. The queue (in order; specs all WRITTEN and owner-ticked)

1. **EV chase budget** — `spec-ev-chase-budget.md` (Aug-1 fee change; ships OFF via
   `min_net_move_pct = 0`; cancel REASON is the counterfactual instrument, never a 2nd disposition
   row). Opus HIGH. May already be in flight — check with the owner.
2. **N1b SL-backoff coupling** — `spec-sl-backoff-coupling.md` (red 223344–223350 class only,
   benign races EXCLUDED; commit 1 = the item-16 comment correction). Opus HIGH, own pass,
   before normal size.
3. **N2 risk-sized bridge trades** — `spec-risk-sized-bridge-trades.md` (ships DISABLED; formula
   parity with `ApplyRiskBasedSize`; sessionFactor exactly once). Opus HIGH.
4. **C1 v2 feedback file** — the era centerpiece (engine-managed exits). Cross-app proposal
   still TO DRAFT (was reserved for this seat; falls to the successor). Contract §8 direction;
   owner arbitrates; schema addendum through the frozen-contract amendment process.
5. Backlog: `ROADMAP-2026-08.md` §5 (note the **triple-placement WATCH + protocol** and
   `set-textbox -Exact`).

## 4. Invariants ADDED since HANDOVER-3 §4 (binding; verify against code, anchors drift)

1. **Session policy (4.4b):** evaluated AFTER the whole contract-§4.4 chain; gates the PINNED
   confidence enum (STRONG/WEAK = input aliases) + `verdict_context` as opaque stable identifiers;
   `refused: policy(SESSION/dim)`; disabled ⇒ structurally silent. Buckets UTC via
   `SessionBucketFor`; the Inclusion window stays UTC+8 LOCAL — two clocks, deliberate.
2. **size_mult / EffectiveSizeUsd: unity passes through untouched**; floor+min-10-clamp only on
   real reductions; applied EXACTLY once at the act/would-act site; `refused: size` reads raw.
   `PlaceAutomatedOrder`/`ExecuteOrderAsync` `sizeUsdOverride = 0` ⇒ byte-identical legacy paths.
3. **Disposition file cardinality is FROZEN: one row per payload, written at consumption.**
   Post-acted outcomes (chase aborts) live in the host-log cancel REASON, never a second row.
4. **`emergencyFired` latch census: 1 decl + 2 sets + 3 clears + 3 reads.** Sets are
   set-then-dispatch on the synchronous line; clears = `CompletePositionClose` + BOTH placement
   seeds; NOT an SL-context reset site; a manual re-anchor does NOT re-arm within a position.
   The old "~5 s backoff" framing is FALSE — the backoff was unreachable until N1b lands.
5. **Notifier:** fully fire-and-forget/fail-silent/self-rate-limited (urgent bypasses); inert
   without `ntfy_url`; topic URL is a credential, never logged.
6. **Signal-tag lifecycle:** pending at bridge act → promoted at entry fill → cleared in BOTH
   cancel teardowns AND on definitive placement refusals (TimedOut carve-out: tag survives) →
   recorded+cleared at close. Columns are item-C TEXT (the INTEGER bullet was stale — ratified).
7. **Persisted gate config = breaker + session policy ONLY** (+ the standing item-A set);
   cooloff/window/tiers still reset per start. Mode/ARM/Started never persist.
8. **All four reposition slippage gates measure the OWN-SIDE quote** (R2 ruling closed F18);
   ATR fallback period = 7 mirroring the engine (R3; payload-first untouched).

## 5. Runtime facts that bite (learned the hard way this era)

- **🚨 x64 rebuild clobbers the bin's `secrets.json` from project source** — source is now testnet
  (`5ef58c4`) but ALWAYS re-check the window title (`— TESTNET`/`— LIVE`) after any rebuild. The
  gate builds AnyCPU only — the owner's runtime bin needs an explicit x64 build for runtime tests.
- **🚨 `FormClosing` persists all 11 geometry fields unconditionally** — runtime tests that tighten
  geometry clobber real trading values on exit. Back up `orderapp-settings.json`, verify restore.
- **The payload must LAND while the bridge is STARTED** (file-change-only evaluation; engine-stopped
  harness = write→START→write; production unaffected).
- **Fill price ≠ trigger price** on the emergency path (two WS round-trips after the cap trips).
- **Trade-placing steps in runtime tests: OWNER mouse clicks only** (triple-placement WATCH).
- Consumer-only payload tests: the ISOLATED harness `bridge.json` protocol — never stop the engine.
- Worktree gate runs need SHORT paths (`SQLite.Interop.dll` 0x800700CE).
- Keep prose away from tripwire tokens in comments (it pollutes the standing greps).

## 6. Methodology + seat rules (unchanged, proven again this era)

Spec → fresh implementer (Opus HIGH for order/SL/receive/bridge/act paths; Sonnet medium
mechanical; subagent OK for turnkey bundles) → impl report → **coordinator review = verify actual
code + EXECUTE the gate + re-run greps + adversarial pass**. Owner is the only pusher; single
branch; one implementer at a time; docs tracked and committed with the work; spec defects escalate
to the owner BEFORE implementing (the unity-passthrough and D1-miscount catches both worked this
way); rulings get folded back into specs so future greps aren't stale. Safety boundary: implementer
seats never place trades/arm the bridge — the owner drives every trade, ARM, START. Owner-rejected
list (H-3 §6) unchanged and extended by: auto-arm-on-boot, rate-limiting the loss cap.

## 7. Seat plan

This rotation: Fable→Fable (window to ~Aug 2). At the window close: Opus-high coordinator; this doc
gets a light delta (state + queue), not a rewrite; consolidate memory then. The successor's first
acts: verify push state, check whether the EV implementer is in flight, then C1 proposal drafting.
