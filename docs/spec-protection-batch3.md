# Spec — position protection, batch 3: a cap on the position, a dead-feed detector, stand-downs that stand down, a bounded freshness window

**Written 2026-10-14 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Origin:** the engine audit's decision items `C-16` (b) (rows `A18`, `A19`), `C-18` (row `A8`) and `C-7` (row `A7`),
read at engine branch `origin/claude/great-keller-s5f4gp`, `docs/adversarial-audit-2026-09-24.md` (read-only). The
app-side triage is `docs/triage-adversarial-audit-2026-10.md` §4, batch "2 — position-level protection".
**Status:** open; **all five owner rulings in (2026-10-14)**; no code. Ready for an implementer seat after the refused-edits seat. **Only the app's half** of each item. Anything that changes the shared
`executor_feedback.json` or the payload schema is out of scope until the owner agrees it with the engine side
(`docs/triage-adversarial-audit-2026-10.md` §5).
**Scope:** `frmMainPageV2.vb`, `SignalBridge.vb`; OrderCheck. **Order, stop, receive and bridge paths → Opus, high.**
**Facts verified at HEAD on 2026-10-14** by read and grep.

IDs carry the prefix `B3-`. Do not confuse `B3-R1`… with batch 2's `R1`…, `PF-R*` or `RE-R*`.

Covered elsewhere, so not here: the audit's `A1` (a stop on the wrong side of the fill) — the exchange rejects a
crossed OTOCO stop and batch 2's `R1` action handles it, runtime-accepted (`docs/review-testnet-2026-10-12.md`).

---

## §0 — Decisions for the owner

| # | Decision | Options | Coordinator recommendation | Status |
|---|---|---|---|---|
| B3-R1 | **Position loss cap**: what it measures from, and when it may act (§1) | (a) a **backstop** only: fires when the price is one M.SL past the position's stop **trigger** and the triggered-stop M.SL path is **not** armed (no stop, stop untriggered past its trigger, stop rejected, a restart before the snapshot); same M.SL setting; flattens with the existing emergency sequence · (b) always armed from the trigger, even while the triggered-stop M.SL path runs · (c) scaled to ATR (k × ATR) instead of the M.SL setting | (a) | ✅ **RULED 2026-10-14: (a)** |
| B3-R2 | **Position loss cap with M.SL unticked or 0** | (a) the cap is off too, and an M.SL-off position pages once ("no loss cap") · (b) a separate cap setting that stays on | (a) | ✅ **RULED 2026-10-14: (a)** |
| B3-R3 | **Dead feed** (§2): what the app does when the socket stops delivering while a position or a working entry exists | (a) probe with `public/test` after 3 s without any inbound frame; no reply within 2 s → urgent page, the bridge stands down (refuses new acts), and the app forces a reconnect · (b) page only | (a) | ✅ **RULED 2026-10-14: (a)** |
| B3-R4 | **Stand-down** (§3): a stale payload, a SKIPPED payload or ARM off, while a **bridge-placed** entry is working | (a) cancel that working entry (scoped cancel), and a stand-down payload never resizes its abort cap · (b) freeze it: stop chasing, keep the cap fixed · (c) (a) plus publishing the working entry in the feedback file — needs the engine side, deferred | (a) | ✅ **RULED 2026-10-14: (a)** |
| B3-R5 | **Freshness window** (§4) | (a) the app caps the payload's window: `2.5 × min(exec_resolution_min, N)` minutes, `N` a setting (default 1, i.e. 2.5 min) · (b) the app's own fixed window, ignoring the payload's field · (c) leave as today | (a) | ✅ **RULED 2026-10-14: (a)** |

Owner rule: anything that changes live order behaviour needs the owner's ruling before it ships. B3-R1, B3-R3, B3-R4 and
B3-R5 all change live behaviour; B3-R2 decides when a cap exists.

**Decision-bias tripwire** (`docs/harness-runs/decision-bias-20261013T1730Z-*`, baselines written first): B3-R1, B3-R2, B3-R5
`richer_option_wrong`; B3-R3 `no_richer_option`; **B3-R4 `gives_up_for_economy`, stable 5/5 — FLAGGED and shown to the owner**
with the richer option (c) (also publish the working entry in `executor_feedback.json`, a shared-schema change needing the
engine side). **The owner chose (a) knowing the flag.** (c)'s feedback half is not queued.

## §1 — Position loss cap (`A18`, `C-16` (b)) — B3-R1, B3-R2

**Today.** The M.SL emergency runs only inside the quote handler's triggered-stop branch: `IsWebSocketConnected` and
not cancel-pending **and `SLTriggered` and a `PositionSLOrderId`** (`frmMainPageV2.vb`, `HandleQuoteUpdates`). Its
anchor is `emergencyBaseline` once triggered, else `StopLossTriggerOriginal`. So **before a trigger, or with no stop at
all, nothing caps the loss.** Batch 2's watchdog pages for "no stop" but never acts (owner ruling `R2`, alarm only).
The cases this leaves open: a stop that is cancelled or never placed; an untriggered stop the price has passed (an
exchange that did not trigger, or a socket gap); a restart before the id-778 snapshot lands.

**Change (B3-R1 (a)).**
- A position-level check in batch 2's one-second watchdog tick **and** in the quote handler (so it is not 1 s late):
  position ≠ 0; anchor = the position's stop trigger (`StopLossTriggerOriginal`; after a manual SL edit, the edited
  trigger); distance = the M.SL setting; the own-side quote (ask for a long, bid for a short) is the distance past the
  anchor; **and** the triggered-stop M.SL path is not armed.
- Action: the existing emergency sequence (latch `emergencyFired`, cancel, `ArmEmergencyClose`, reduce-only market,
  batch 1's SENT/CONFIRMED/FAILED messages), urgent page "POSITION CAP — price $X is M.SL past the stop trigger $T:
  flattening".
- **It never pre-empts the triggered-stop path.** That path's anchor rule (owner #67, the hybrid fix) and the `SB3`
  pin stay untouched. The cap is the backstop for the states where that path does not run.
- No trigger known (0) → no cap; page once ("position has no stop trigger — no loss cap").
- B3-R2 (a): M.SL unticked or 0 → no cap; page once per position ("M.SL is off — no loss cap").
- Pure seam: (position, quote, anchor, M.SL, triggered path armed) → flatten or not. OrderCheck.

## §2 — Dead feed (`A19`) — B3-R3

**Today.** "Connected" means the socket's state is `Open`. Deribit sends `heartbeat` test requests at the
`public/set_heartbeat` interval and the app answers with `public/test`; nothing measures silence between them. There
is **no last-inbound timestamp**. The quote channel publishes only on change, so quote silence alone is normal in a
quiet market and must **not** be the signal.

**Change (B3-R3 (a)).**
- Stamp `lastInboundUtc` on **every** inbound frame (the receive loop, engine field, no control).
- In the watchdog tick, while a position or a working entry exists: 3 s with no frame → send `public/test` (a new
  request id); no reply within 2 s → a stall: urgent page "Feed STALLED — no data for N s (position …)"; the bridge
  stands down (refuses new acts, as a stale payload does); force a reconnect through batch 1's reconnect path.
- Cancel-on-disconnect is OFF on every account (owner, 2026-10-12), so a forced reconnect does not delete orders.
- Pure seam: (seconds since last frame, probe outstanding, seconds since probe) → none / probe / stall.

## §3 — Stand-down cancels the working entry (`A8`, `C-18` (a)) — B3-R4

**Today.** A stand-down (a stale payload, a SKIPPED payload, ARM off) never cancels a working bridge entry. The chase
keeps re-pricing it, and each stand-down payload resizes its abort cap; an ARM-off payload widened it (audit run:
72.12 → 70 → 90 → 70).

**Change (B3-R4 (a)).** On any stand-down while a working entry placed **by the bridge** exists: the existing scoped
entry cancel (request id 31), log and normal page "Stand-down (<reason>): working entry cancelled". A stand-down
payload never writes the abort cap. A **manual** working entry is never touched. Pure seam → OrderCheck. The feedback
half (`C-18` (c)) waits for the owner and the engine side.

## §4 — Bounded freshness window (`A7`, `C-7` (b)) — B3-R5

**Today.** `SignalBridge.vb` judges a payload fresh when its age ≤ `2.5 × max(exec_resolution_min, 1)` minutes, where
`exec_resolution_min` comes **from the payload** (`:626`, `:274`). The payload sets the window it is judged by, with no
upper bound.

**Change (B3-R5 (a)).** Window = `2.5 × min(max(exec_resolution_min, 1), N)` minutes; `N` a bridge setting, default 1.
**Before coding:** the implementer reads the engine contract (read-only) for the `exec_resolution_min` values the engine
actually emits. If any is above 1, **escalate** (an `E` finding scoped `B3-…`): a default of 1 would refuse those
payloads as stale. The data-age field (`C-7` (a)) is a schema change for the engine side, deferred.

## §5 — Acceptance

1. Gate passes; the nine `frmMainPageV2.vb` censuses re-run (occurrence counts; explain any change).
2. OrderCheck: one seam per item, each with fixtures that fail against today's code (mutation proof).
3. **Runtime — after the owner's return** (a seat cannot place testnet orders without the owner): the position cap with
   the stop cancelled by testnet API; the dead feed by a temporary testnet-only switch that drops inbound frames; a
   stand-down with a bridge entry working (testnet bridge, owner-armed); the freshness cap by a payload with a large
   `exec_resolution_min`.

## §6 — Not verified by this seat

- The `public/set_heartbeat` interval the app sets, and whether Deribit's heartbeat alone would detect a stall sooner.
- The engine's actual `exec_resolution_min` values (§4 makes this the implementer's first read).
- How the bridge marks an entry as bridge-placed today (the scoped-cancel path from `docs/spec-decouple-v2.md` is the
  presumed handle).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Implementer:** Opus 5.5, high — order, stop, receive and bridge paths. Sequence it after the refused-edits seat
  (same file).
- **Review:** coordinator, Opus 5.5, high.
