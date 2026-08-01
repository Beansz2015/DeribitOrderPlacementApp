# ROADMAP — August 2026 era (plan of record; trimmed 2026-08-02)

**What this doc is for now: §4 (the centerpiece), §5 (the live backlog) and §6 (what we deliberately
do NOT build).** Everything else has closed. Closed milestones are indexed in
`ARCHIVE-closed-milestones.md`, which points at each one's real spec → review chain — they are no
longer narrated here. Current state, open items and the queue live in `HANDOVER-6.md`.

**Standing constraints (binding):** the owner-rejected list stays rejected (session-end
auto-flattening; app-side re-gating of engine logic; P2 SL policy; P3 hybrid chase; the emergency
clamp; auto-arm-on-boot; rate-limiting the loss cap). **R1: the engine decides signal logic — the
app never grows a second brain.** One implementer at a time; reviews execute the gate; the owner is
the only pusher.

## §1 — Owner track (no code)

1. ~~Phase 3: enable the session policy~~ — **DONE**, verified `session_policy.enabled = True`.
   ⚠ NY is unconfigured and therefore *unrestricted at full size* — see `HANDOVER-6.md` §2.3.
2. **Size ladder** at FIXED size: 10 → 20–30 → normal. *(open)*
3. **AWS London migration** — `production-cutover-checklist.md` §9; the key choreography kills the
   two-executor window. *(open)*
4. Rulings as they arise; matrix/policy revisions on the engine seat's cadence (evidence discipline:
   revise at regenerations, not week-to-week).

## §2 — Pre-ladder quick wins — **ALL DONE 2026-07-25** (Q1 ntfy · Q2 signal columns · Q3 ops tooling)

Owner-accepted; see the archive. **One optional item was never specced and remains available:**

| # | Item | Notes |
|---|---|---|
| Q4 | *(optional)* **Phase B item F — USE-ENGINE-LEVELS button** (manual trade borrows the latest payload's levels) | Modest value; the tie-in shape is stable, so the old "needs a fresh look" caveat is cleared. Take it if a pass has room. |

## §3 — Pre-normal-size — **ALL CLOSED except N2's owner enable**

N1 · N1b · N1c · EV · SF/SF2 · N2b are closed; **N2 is code-approved, unblocked and ships DISABLED,
awaiting the owner's tick.** Verdicts and doc chains: `ARCHIVE-closed-milestones.md`. The three
sizing knobs that must be set together before that tick: `HANDOVER-6.md` §2.

## §4 — The next era's centerpiece

| # | Item | Notes |
|---|---|---|
| C1 | **v2 feedback file — documentation phase CLOSED both sides; the EMITTER BUILD IS THE LIVE QUEUE ITEM.** Exchange closed (proposal → engine ACCEPT → ack → trader tick T1–T8); **contract §8 is the binding spec**, engine mirror is their §10. Remaining: two implementation builds on separate queues — **our order-app emitter** (own Opus-HIGH pass, ships OFF) and engine consumption (behind its net-EV rider). **Phase 2 (ACTIONABLE EXITS — the largest remaining P/L lever) stays FENCED** as a future signal-schema-v2 amendment with a pinned field; **never parse `hold_status`.** Gated on the phase-1 display soak. |

## §5 — Hygiene backlog (anytime, unscheduled, all on record)

- **`set-textbox -Exact` switch** — `set-textbox` matches by SUBSTRING, so `txtTrigger` can never
  address `txtTrigger` while `txtTriggerOffset` exists; it silently sets the wrong box and reports
  success. *(Verified still absent 2026-08-02.)*
- **FrmIndicators full retirement** — more attractive now: with the engine 24/7 on AWS the indicator
  fallback is nearly never exercised, so retiring it leaves payload ATR + the $70 constant only.
- **Edit T.S. post-trigger rework** — 4-trap catalog; `EditStopLossTo` is the home.
- **Item G — stale-SL-box cosmetic** — mechanism documented in
  `runtime-record-live-ladder-2026-07-23.md`.
- **Housekeeping smoke leftovers** — the 8b checkbox-OFF half, the 14g long-PnL observation.
- **A2** manual-freeze observation · **B2** regression · **F1** rejected-edit re-sync (accepted LOW).
- **TP post-fill manual-move gap.**
- **Harness `txtTrigger` tree-order nit** · **fractional-offset loud rejection.**

## §6 — Deliberately NOT building

**App-side exit logic** (partial TP / trailing TP / scale-outs) — C1 v2 is the correct home, with
the engine deciding; building it app-side forks the brain R1 forbids. **Multi-instrument** — the
engine's scope call, not this app's. Anything on the owner-rejected list above.

## §7 — Sequence of record

Everything through N2b is done. Current: **N2 enable (owner) → C1 emitter → §5 backlog.**
