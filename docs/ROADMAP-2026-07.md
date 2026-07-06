# Roadmap — July 2026 (Fable window + post-window plan)

**Written:** 2026-07-02, by the Fable 5 coordinator conversation.
**Constraints:** Fable 5 available until **2026-07-07** (5 working days). Max 5x subscription until ~2026-08-02. Owner is part-time (trading + hostel, GMT+8) — days below are targets with explicit slack rules, not commitments.

## 0. Operating principles for the window

1. **Fable time goes to design and review; mechanical work goes to Opus/Sonnet from pre-written specs.** The scarce resource is deep-reasoning review of invariant-sensitive code, not implementation typing. Every spec written before Jul 7 is work that survives the window.
2. **One Fable coordinator (this conversation).** Its accumulated verified context — the full audit trace, every reviewed diff — is the asset. Implementers stay separate conversations per the established spec → implement → report → review loop.
3. **The owner remains the runtime gate and the only pusher.** Nothing ships untested; nothing stacks on untested work.
4. Anything invariant-sensitive that must be *implemented* should land **inside** the window so Fable can review it. Post-window implementations should be things a prescriptive spec makes safe for Opus (high) or Sonnet (mechanical).

## 1. The window — Jul 2 → Jul 7

| Day | Work | Who |
|---|---|---|
| **Jul 2 (done/in flight)** | Quickfixes 1–7 + reduce-reposition shipped & runtime-tested; push milestone. `spec-position-model.md` handed off. Roadmap (this doc). | all |
| **Jul 3** | ~~Review position-model~~ ✅ done Jul 2 (impl `d314fff..5da2e5b`, runtime-verified — all four cases honest P/L; full-close proved with a one-click 20-lot flatten). Then: **rewrite `spec-medium-decouple.md` → `spec-decouple-v2.md` (#10)** — the keystone spec: clean API on `frmMainPageV2` (`PlaceAutomatedOrder`, target setters, read-only `PositionSizeUSD`/`PositionAvgEntry`/`SessionPnLUSD`/`IsWebSocketConnected`/`CanTrade`), **thread contract** (marshal internally), **order-ack semantics** (F2 rollback half: unique request ids → accepted/rejected surfaced to the caller), execution-side gate ownership (see §3 safety), default-instance removal, **and the scoped slippage-cancel** (runtime finding 2026-07-02: the ATR-slippage guard calls `CancelOrderAsync` = `cancel_all_by_instrument`, which also kills an *existing* position's protective OCO legs → naked position; the guard must cancel only its own entry-order context via `private/cancel` on `CurrentOpenOrderId`, leaving `Position*` legs alone). | Fable + implementer + owner |
| **Jul 4** | **Integration contract v1** (`docs/integration-contract-verdictengine.md`, see §3) — drafted here, shuttled to the VerdictEngine coordinator for amendment, ratified by owner. In parallel: **#10 implementation** starts from the v2 spec (Fable-high implementer). | Fable + VE coordinator + owner |
| **Jul 5** | #10 review (Fable — this is the review the window exists for) + owner runtime tests. Write **resilience-pass spec** (`spec-resilience.md`): F7 keep-alive timeout + delete dead silence-check/monitor, F8 limiter init at connect + placement credits, F9 monitor-loop leak, F12 `UnhandledException`/unobserved-task backstop, F5 fragmentation-safe receive (port the FrmIndicators StringBuilder pattern), **restart position restore** (owner request 2026-07-02: on connect-seed with size ≠ 0, log "Open position detected: LONG/SHORT n @ avg", set status label In Position, mirror avg to the placed-price display — display-only, engine fields already correct; leg context self-heals from the next echo), **window-X close** (owner request: drop the `CS_NOCLOSE` CreateParams override, add `FormClosing` running btnClose's cleanup with a bounded wait; btnClose delegates to `Me.Close()`). | Fable + owner |
| **Jul 6** | Write **tie-in spec** (`spec-autotrade-tiein.md`): AutoTradeSettings re-code as the settings/trigger surface per the ratified contract; FrmIndicators retirement checklist (what disconnects, what must keep working until cutover). Needs VerdictEngine coordinator input on its send-side (one shuttle round). Buffer day for anything slipped. | Fable + VE coordinator |
| **Jul 7** | **Handover day:** review whatever landed; write `HANDOVER-2.md` (post-Fable continuation, Opus-oriented, pointing at this roadmap + all specs); consolidate memory files; final spec polish. Nothing new starts. | Fable |

**Slack rules:** if position-model review slips → Jul 3's #10 spec still happens (independent). If #10 *implementation* slips past Jul 5 → it moves post-window (the v2 spec is prescriptive enough for Opus-high; Fable review is preferred, not mandatory). The integration contract and the Jul 7 handover are the two items that must not slip — they are the window's irreplaceable outputs.

## 2. Post-window — rest of July (Opus/Sonnet on Max 5x)

Ordered; each from a pre-written spec, reviewed by an Opus conversation using the §4 methodology in `HANDOVER-2.md`:

1. ~~**#10 decouple**~~ ✅ landed + runtime-verified in-window.
2. ~~**Resilience pass**~~ ✅ landed + runtime-verified in-window (with restore-hardening, close-completion, M.SL baseline, SL-reconciliation follow-ons — see `spec-back-session-2026-07-04.md`).
2.5. **Entry-chase v2** (added 2026-07-06, owner-requested; **UNBLOCKED 2026-07-07** — §9 matrix passed, `reduce_only` probe closed with no code change ahead of it) — `spec-entry-chase-v2.md`, Opus/Fable high. **Immediately after the remaining commits push, BEFORE tie-in:** it is small (2 commits + optional 3rd), improves every manual fill from day one, and rewrites the same `HandleQuoteUpdates` region the §9-fix conversation already has hot — ideally the same implementer conversation takes it next. It also supersedes the four-gate `And→AndAlso` half of housekeeping item 8. Must NOT run in parallel with anything else in `HandleQuoteUpdates`.
3. **Tie-in implementation** — both repos, coordinated per the contract (§3): execution side here (Opus high), engine side ✅ **already live-ready** (A2 go-ahead 2026-07-06; emission OFF until the owner flips it). Integration testing on the test sub-account with the execution-side master switch off → log-only → small size → normal; the live-at-min-size step also waits for the engine's placed-geometry pass (contract §7 addendum). Starting tie-in right after entry-chase v2 starts the log-only soak clock early — the soak is calendar time and runs unattended.
4. **Execution-ergonomics bundle** — `spec-execution-ergonomics.md` (written Jul 3, owner-selected): Phase A (post-resilience, Opus high) = input persistence, risk-based SIZE button, journal MAE/MFE + planned-R + fees, alerts, break-even button; Phase B (post-tie-in) = USE-ENGINE-LEVELS manual button + signal columns + bridge alerts. **Rejected by owner (do not re-propose): automatic session-end flattening** — shutoff time is variable and late discretionary opportunities must stay possible.
5. **Housekeeping v2** — Sonnet medium: existing `spec-medium-housekeeping.md` + audit F14–F18 additions (dead code, unused EntityFramework/DriveInfo packages, 500 ms entry delay, edit-button raw parses, `txtLogs` trim, `DeleteMultipleTrades` success-via-error-event, AutoTradeSettings first-click, `amountValid` orphan, `positionSizeUSD` parameter shadowing, stale `SendReduceMarketOrderAsync` header comment, stale `RequestNameForId` id-2 hint). **Slippage-guard coherence (runtime-confirmed 2026-07-03):** the four reposition gates use non-short-circuit `And`, so with `chkMaxSlippageATR` OFF the predicate still runs — logging "exceeds limit" lines and resetting the slippage baseline via its internal `ResetOrderAttempt()` — it just can't cancel; AND the pre-placement checks in `ExecuteOrderAsync`/`StopLossForTrailingOrderAsync` aren't checkbox-gated at all. Fix both: `And` → `AndAlso` on the four gates, and gate the pre-placement checks on `maxSlippageATRchecked` so the checkbox is the single arm switch for the whole feature. Plus a **carefully scoped** F10 pass (blocking `Me.Invoke` → `UiInvoke` for *display-only* writes — the echo-handler lambdas that mutate state are excluded; spec must list exact call sites).
6. **Production-cutover checklist** (doc, owner-executed): rotate to production API keys via `secrets.json` only, delete the throwaway sub-account, verify `.gitignore`, first-week size limits, circuit-breaker values.

**Explicit non-goals (decided, don't re-litigate without new evidence):** #9 dispatcher (skip until after VerdictEngine integration proves a need); FrmIndicators bug-fixing (dying module — #2/#7 runtime tests stay ON HOLD until retirement); `Option Strict` migration of legacy files; multi-fill close aggregation.

**Backlog (raised in the 2026-07-07 session review — `spec-back-session-2026-07-07.md` §4/§5; unscheduled, revisit post-tie-in or bundle with housekeeping):** F1 state re-sync on a rejected SL edit (references phantom-advance today — accepted LOW); `isRepositioning`-style single-flight for the triggered-SL chase; probe whether `btnEditSLPrice`'s `trigger_price` on an already-triggered SL is rejected; P3 hybrid chase policy (owner-deferred; trigger = P1's chase-pullback proving annoying in live use); TP post-fill manual-move gap (reconcile spec §7, deferred).

## 3. Cross-app coordination (decision + rationale)

**Decision (recommended): no third coordinator conversation.** Coordination = one **written contract** + the two existing coordinators + the owner as arbiter.

- **The contract doc is the coordination mechanism, not a conversation.** `docs/integration-contract-verdictengine.md`, canonical copy in **this repo** (the API provider/callee owns the interface), versioned (v1, v2 …) with a change log. The VerdictEngine repo references it by absolute path.
- **Why not a third conversation:** it would start context-poor on both codebases, relay between two experts while knowing less than either, and add a lossy hop. The doc-driven loop already proven here (spec → report → review) extends cleanly across repos.
- **Both coordinators can READ each other's repos directly** (same machine — this conversation has already read `C:\Dev\DeribitVerdictEngine\docs\*`). Standing safety rule: **read the other repo freely; never write to it.** Each coordinator writes only in its own repo; cross-repo decisions go through the owner.
- **Shuttle protocol:** Fable drafts contract v1 → owner pastes/points the VE coordinator at it → VE coordinator amends (its send-side needs, verdict schema, cadence, readiness timing vs. its v48 re-baseline) → Fable reconciles → owner ratifies. Two rounds should converge.

**The contract must settle these (v1 agenda):**

1. **Transport — DECIDED 2026-07-03 (VE brief + our reply):** atomic single-file JSON (`verdict_signal.json`, temp + `File.Replace`), consumer = FSW + debounce + independent 10 s staleness poll; default path `C:\Dev\DeribitBridge\`, configurable both sides. The earlier named-pipe recommendation is **superseded** — at the engine's 30 s–3 min cadence a one-way file is inspectable/replayable/restart-proof, and the v2 *feedback file* (position state + signal dispositions, order app → engine) closes the ack loop with the same mechanism. See `signal-bridge-reply-orderapp.md` (our field-level diff) → freeze into `integration-contract-verdictengine.md` after VE acks.
2. **Direction & authority: the execution app is the gatekeeper.** The engine *proposes* (signal, side, targets, size hint); the execution app *disposes* — it re-validates everything against its own gates (master auto-trade switch, connection, `CanMakeAPIRequest`, position-exists, circuit breaker, time window, cooldown) and can refuse. The callee never trusts the caller; the kill switch lives on the execution side. (Consequence: circuit-breaker/cooldown/time-window move from FrmIndicators into the execution app or its re-coded AutoTradeSettings — they are execution-side risk gates, not analysis.)
3. **Message set v1:** `place` (side, order type, TP/trigger/SL offsets or absolute targets, size), `cancel_all`, `flatten` (market reduce via the position model), `status` request/response (position size/avg, session PnL, connection, gates), `ack/reject` (request id, reason), heartbeat. Every message versioned + request-id'd.
4. **Threading:** pipe handler on the execution side marshals onto the #10 API exactly like any other caller — the thread contract is in the #10 spec and restated in the contract.
5. **Failure semantics:** engine behavior on missing ack/heartbeat (alarm, stop signaling); execution behavior on malformed/stale messages (reject + log, never partial-apply).
6. **Cutover sequencing:** contract ratified → #10 landed (in-process API) → execution-side pipe host + re-coded AutoTradeSettings → engine-side client → integration test ladder (switch off → log-only → min size → normal) → FrmIndicators retirement.

## 4. Spec inventory (status at 2026-07-02)

| Spec | Status | Implementer |
|---|---|---|
| `spec-audit2-quickfixes.md` (7 fixes) | ✅ shipped + tested | done |
| `spec-reduce-reposition.md` | ✅ shipped + tested | done |
| `spec-position-model.md` | 🔨 in implementation | Fable/Opus high |
| `spec-decouple-v2.md` (#10) | ✅ written Jul 3 (supersedes `spec-medium-decouple.md`; 3 commits: scoped cancel / placement acks+rollback / public API) | Fable high (in-window preferred) |
| `integration-contract-verdictengine.md` | ✅ **FROZEN v1, Jul 3** (brief→reply→ack converged in one round; both lanes may implement) | n/a (contract) |
| `spec-resilience.md` | ✅ written Jul 3 (pulled forward; 6 commits: fragmentation+keepalive, limiter-at-connect, monitor leak, crash backstops, restart restore, X-close) | Opus high (after decouple-v2 push, before tie-in impl) |
| `spec-autotrade-tiein.md` | ✅ written Jul 3 (pulled forward; 4 commits incl. the TimedOut ack hardening; testable without the engine via hand-crafted payloads) | Opus high (post-window; after decouple-v2 push) |
| `spec-execution-ergonomics.md` | ✅ written Jul 3 (owner-selected bundle; reconciled 2026-07-04 to the new base). Phase A = input persistence, risk-based SIZE, MAE/MFE+R+fees journal, alerts, break-even — post-resilience (landed), queued behind entry-chase-v2 + tie-in per §2. Phase B = post-tie-in (USE-ENGINE-LEVELS, signal columns, bridge alerts) | Opus high |
| `spec-medium-housekeeping.md` + F14–F18 addendum | ✅ addendum written 2026-07-06 (items 6–16, all re-verified vs `fc7bb6c`; bundle turnkey) | Sonnet medium (item 15 diff gets coordinator review) |
| `spec-entry-chase-v2.md` | ✅ written 2026-07-06 (best-non-crossing target + time throttle + entry-only chase default-ON; slots at §2 item 2.5) | Opus high (after §9 push, before tie-in) |
| `spec-medium-dispatcher.md` (#9) | ⛔ skip (standing decision) | — |
| `HANDOVER-2.md` | 📝 Jul 7 | Fable |

## 5. Working notes for post-Fable conversations

- Model/effort guidance: `CODE_AUDIT_FABLE5.md` §H (table). Short form: Opus **high** for anything touching order/SL/receive paths or the pipe host; Sonnet **medium** for mechanical cleanups; review every impl report against the actual code (`git show` + build + targeted reads), never against the report's claims.
- The §5 invariants in `HANDOVER.md` remain binding; the position model adds invariant: *avg entry retains the just-closed basis on the flat echo — never zero it on flat.*
- Context discipline: don't re-read `frmMainPageV2.vb` end-to-end; the two audit docs + section banners are the map.
