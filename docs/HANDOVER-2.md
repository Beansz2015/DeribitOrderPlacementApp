# HANDOVER-2 — DeribitOrderPlacementApp (written 2026-07-03, context-rich)

**For:** any fresh conversation continuing this work — a Fable seat before 2026-07-07, an Opus seat after. Auto-memory loads each session; this is the task-focused complement. **Supersedes `HANDOVER.md`** (2026-07-02, kept for history — its §5 invariants remain binding and are extended in §4 below).

## 1. Read-first map (in order, as needed)

1. `docs/ROADMAP-2026-07.md` — the plan of record: window schedule, post-window ordering, spec inventory, decisions.
2. `docs/CODE_AUDIT_FABLE5.md` — the full Fable-5 trace (findings F1–F20) + §H effort table. `CODE_AUDIT.md` is the older baseline audit.
3. `docs/integration-contract-verdictengine.md` — **FROZEN v1** cross-app contract; canonical for consumer behavior.
4. Specs + impl-reports as the task requires (inventory in ROADMAP §4). Superseded: `spec-medium-decouple.md` (→ `spec-decouple-v2.md`). Standing-skip: `spec-medium-dispatcher.md`.
5. Memory: `fable-window-plan`, `project-component-status`, `receive-loop-threading`, `ws-handler-id-guards`, `credentials-secrets-setup`, `sl-reconciliation-policy`.

## 2. State at handover-writing (2026-07-03, HEAD `fccdec5`, branch `housekeeping-now`)

- **Shipped + runtime-verified + PUSHED:** audit2 quickfixes 1–7, reduce-reposition, position-model (`39d7e82..5da2e5b`).
- **Decouple-v2 FULLY runtime-verified (tests 1–5 PASS, 2026-07-03)** + three follow-up fixes made during testing, all reviewed/approved: `19fa162` (connection guards on cancel/reduce paths), `9cb97f4` (limiter armed at connect — supersedes the connect-init half of resilience F8; spec updated), `3714171` (guard ordering). Known chain blemish, accepted: `19fa162`/`9cb97f4` don't Debug-build from a clean checkout (committed `Handles btnApiSmoke.Click` for a control that only ever existed uncommitted). **Pending: the owner's cleanup commit (working tree: parity log line re-commented + smoke handler removed + whitespace), then push `da288e6..HEAD`.**
- **Contract v1 frozen** (see §5). Both lanes may implement; neither has started.
- **Resilience pass SHIPPED (`0349b17..a42aa76`, code-reviewed 2026-07-03; owner runtime tests pending: dead-link, restore, X-close, crash-log, full-cycle regression — then push).** Receive loop is now fragment-safe with `KeepAliveTimeout` dead-link detection; crash backstops live in `ApplicationEvents.vb`; single `FormClosing` shutdown path (ergonomics config-save slot marked at its top). Optional refinement if the owner finds the "Open position detected" line noisy on normal opens: gate it with `AndAlso placedPrice = 0D` (restart discriminator).
- **Specs written, not implemented:** `spec-autotrade-tiein.md` (**written Jul 3** — bridge consumer in `SignalBridge.vb` + interlock panel + FrmIndicators trigger neutralization + the `TimedOut` ack hardening as its commit 1; implement after decouple-v2 is pushed), `spec-resilience.md` (**written Jul 3** — 6 commits; implement after decouple-v2 push, BEFORE tie-in impl: it owns the connect/receive/shutdown regions), `spec-entry-chase-v2.md` (**written Jul 6, owner-requested** — chase to best non-crossing price on a time throttle, entry-only chase default-ON; slots after the §9 push, before tie-in — ROADMAP §2 item 2.5), `spec-execution-ergonomics.md` (written; Phase A after resilience, Phase B after tie-in), `spec-medium-housekeeping.md` + ROADMAP §2.5 additions (addendum written Jul 6 — bundle turnkey).
- Build green throughout; the owner is the only pusher; local-first git discipline.

## 3. How we work (unchanged, binding)

**Git world changed 2026-07-03:** repo is now **PRIVATE**; `housekeeping-now` is merged and deleted; **`master` is the only branch** — implementers commit to master, the owner pushes `origin master` at tested milestones; **`docs/` is tracked** (commit docs alongside work; the "owner manages docs versioning" note in HANDOVER v1 is obsolete).

Spec → implementer conversation → `impl-report-*.md` → **review = verify the actual code, never the report** (`git show` every commit, build, re-run the greps yourself, adversarial pass on invariants). Reports here have been excellent and still get verified. Specs are self-contained with model/effort headers (see audit §H: Opus/Fable **high** for order/SL/receive-path work; Sonnet medium for mechanical). Commit per fix, never push, 0-error/0-warning gate. Owner runtime-tests on a test sub-account under the VS debugger before pushing. Scope discipline: suspicious-nearby goes in the report, not the diff.

**STATE DELTA 2026-07-04 — read `spec-back-session-2026-07-04.md` (authoritative for the 18-commit session `0d078eb..968b26d`):** restore-hardening, close-completion (`CompletePositionClose`, `ApplyCloseFill` fields-only), handle-race guards, `Position entered` UX, `btnClose`/`btnMark` REMOVED, M.SL `emergencyBaseline`, and the **commanded-price SL-reconciliation** (`968b26d` — owner runtime test pending, then push). New invariants live in that doc's §8/§10; the binding ones: emergency baseline = `emergencyBaseline` else `StopLossTriggerOriginal`, 0 disables; close completes once per ≠0→0 transition via `CompletePositionClose`; **any new programmatic SL edit must `RecordCommandedSLPrice`** (user paths must NOT); the 7 SL-context reset sites are canonical anchors; id 778 = restore snapshot. Outstanding specs (tie-in, ergonomics) were updated 2026-07-04 for these changes.

**STATE DELTA 2026-07-07 — read `spec-back-session-2026-07-07.md` (authoritative for `968b26d`/`a1d7b58`/`da25365`/`45a8da5`):** the SL-reconciliation is **LIVE — owner runtime matrix ALL PASSED** (its §2). The open-`StopLossOrder` echo is now a **three-way classification**: trigger flip = silent ADOPT of the exchange price into both references; already-triggered + uncommanded + changed = manual edit (follow + cyan log); else ignore. **Maker-lifecycle invariant:** `post_only: True` + `reject_post_only: False` on every limit placement AND all 8 `private/edit` payloads — any new edit/placement must carry them; never on the two market paths (deliberate takers); **`reduce_only` stays placement-only and NEVER goes on the entry (223344) or TP (223345 — both-legs-cancelled landmine)**; Deribit preserves both flags across edits (trade-#45 probe — question CLOSED). `already_closed` on edit ids logs gray, not red. Review findings F1 (accepted: references phantom-advance on a swallowed edit rejection), F2 (docs nuance), F3 (cosmetic) + the backlog candidates live in that doc's §4–§5. **Push state: origin/master = `a1d7b58`; everything after it is tested + reviewed, awaiting the owner's push.** Owner decisions on record: P1 confirmed live, P2 rejected, P3 deferred, emergency clamp rejected. `spec-entry-chase-v2.md` (written Jul 6–7) is **UNBLOCKED and next** (ROADMAP §2 item 2.5), then tie-in (A2 go-ahead recorded; live step gated on the engine geometry pass). Fable window correction: available until ~**Jul 8 afternoon MYT** (Jul 7 23:59 PDT, owner's estimate); the implementer conversation is also on Fable now.

## 4. Invariants (original §5 of HANDOVER.md still binding; these EXTEND it)

1. **Receive thread touches engine fields only** — never controls. `UiInvoke`/`AppendColoredText` self-marshal. (Original rule; every new subsystem below obeys it.)
2. **Position model:** `positionSizeUSD` (signed USD) / `positionAvgEntry` update from every positions echo + id-777; **avg entry updates only while size ≠ 0 and RETAINS the just-closed basis on the flat echo** (close-P/L reads it in the same message). Never zero it on flat.
3. **Cancel scopes are deliberately asymmetric.** Nuclear `CancelOrderAsync` (button, position-close cleanup, emergency) kills everything and resets all context. Scoped `CancelWorkingEntryCoreAsync` (slippage guard, API) cancels the OTOCO primary only (children follow — Deribit-verified 2026-07-03) and must NOT touch `placedStopLossPrice`/`SLTriggered`/`Position*` legs/reduce context. Both use the same `cancelPending` 4-s window.
4. **Placement acks:** every entry placement (manual + API) gets a unique id ≥ 600000 + a `pendingPlacements` registry entry snapshotting `placedPrice`/`placedStopLossPrice` pre-seed. Rejection ⇒ **restore snapshots** (never zero — a zero can stall an actively-trailing SL) + one red line. `HandlePlacementResponse` owns the id range (generic logger skips it). Success deliberately does NOT seed order context — echoes stay the single writer. **Timeout removes the registry entry (no rollback)** — known residual: a >5s-late rejection is silent; planned fix = `TimedOut` flag (log-only late handling) in the tie-in spec.
5. **JSON-RPC id map:** 1 subscribe/reduce · 2 auth · 3 token refresh · 4 portfolio-sub + heartbeat-test · 6 quote-sub · 20 orders-sub · 30 cancel-all/trailing-stop · 31 scoped cancel · 777 get_position · 778 get_open_orders_by_instrument (restore snapshot) · 890 margin-est (vestigial) · 999 acct summary · 1001 set_heartbeat · 223344–223350 edits (223349 = reduce-reposition) · ≥600000 placements.
6. Reduce-reposition context (`ReduceOrderId`/price/amount/`reduceOrderIsBuy`) keys off the ORDER, chase direction from `reduceOrderIsBuy` never `TradeMode`; single-flight shares `isRepositioning`; gate order `IsCancelPending()` BEFORE the `Interlocked` acquire (leak-wedge otherwise).
7. Null-safe id guards (`HasValue AndAlso`), seed-only-when-zero echo writers, `BackoffStopLossRetry` — all original rules stand.

## 5. The frozen contract — 60-second version (full doc is canonical)

One atomic JSON file `C:\Dev\DeribitBridge\verdict_signal.json`, engine-written after EVERY run. Consumer: FSW + debounce + independent 10-s staleness poll; age gate `2.5 × max(exec_resolution_min,1)` min; de-dupe on (`engine.instance_id`, `signal_id`) persisted across restarts; gate chain in contract §4 (tier default HIGH+MEDIUM; **WEAK carries direction with LOW confidence — the tier gate refuses it, never infer from `direction`**; `BELOW_MIN_MOVE` is the only pinned context value; `health.ws="REST"` is OK, only `DOWN` blocks). Levels: `stop` = exit-trigger level, `target` = TP limit as-is (R2), `entry` = reference for the 0.6×`atr` slippage cap (`atr` guaranteed non-zero on actionable signals — sole ATR source post-FrmIndicators). **Dual-arm interlock:** engine toggle (emitted flag) + app toggle + START on this app; all default OFF, nothing persists, START not sticky, gates live mode only. Rollout: off → log-only → min-size → normal (**2026-07-06:** engine emitter LIVE-READY at engine `23fd8b9`, emission OFF until the owner flips it for the soak; the min-size step now ALSO waits for the owner to confirm the engine's placed-geometry pass — log-only doesn't wait; contract §3 gained informational emitter notes, §7 the rollout addendum — v1 schema unchanged). **Trader-rejected, do not re-propose:** auto session-end flattening; re-gating engine signals with app-side signal logic.

## 6. Remaining window plan (if a Fable seat picks this up before Jul 7)

**All window specs are now written** (tie-in + resilience landed Jul 3, ahead of schedule). What remains:

1. Reviews of whatever the owner's implementers land, per §3 methodology.
2. ~~Decouple-v2 runtime tests 2–5 results → push clearance.~~ ✅ Passed + pushed Jul 3.
3. Jul 7: refresh this handover with final state + consolidate memory. ~~Optional if idle time remains: extend `spec-medium-housekeeping.md` with the ROADMAP §2.5 addenda so the Sonnet bundle is turnkey.~~ ✅ Done 2026-07-06 — addendum items 6–16, every site re-verified against `fc7bb6c`; F10 pass scoped with an explicit exclusion list.

## 7. Post-window ordering

ROADMAP §2: decouple-v2 runtime tests → push; resilience; tie-in implementation + rollout ladder; ergonomics Phase A then B; housekeeping v2 (includes the slippage-guard coherence fix — `And`→`AndAlso` on the four gates + checkbox-gate the pre-placement checks); production-cutover checklist. #9 dispatcher stays skipped. FrmIndicators/#2/#7 testing stays ON HOLD until retirement.

## 8. Watch-outs for a fresh seat

- Verify against code, not memory or this doc — line anchors drift; symbols + code anchors are in every spec.
- The VerdictEngine repo (`C:\Dev\DeribitVerdictEngine`) is read-only territory: read freely for coordination, **never write there**; cross-app decisions go through the owner. Its docs use its own conventions.
- `USDPublicSession` stays a public field until FrmIndicators retires (API property `SessionPnLUSD` wraps it).
- `engine.settings_version` in payloads is informational — only `schema_version` gates.
- The owner runtime-tests everything; don't declare anything done that hasn't passed their pass.
