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
| C1 | **v2 feedback file — documentation phase CLOSED both sides; the EMITTER BUILD IS THE LIVE QUEUE ITEM.** Exchange closed (proposal → engine ACCEPT → ack → trader tick T1–T8); **contract §8 is the binding spec**, engine mirror is their §10. Remaining: two implementation builds on separate queues — **our order-app emitter** (own Opus-HIGH pass, ships OFF; **spec written AND all §0 escalations RULED 2026-08-02 (`spec-c1-feedback-emitter.md`) — ready for an implementer seat, which the owner launches; contract §8.1 amended in the same pass, engine relay owed**) and engine consumption (behind its net-EV rider). **Phase 2 (ACTIONABLE EXITS — the largest remaining P/L lever) stays FENCED** as a future signal-schema-v2 amendment with a pinned field; **never parse `hold_status`.** Gated on the phase-1 display soak. |

## §5 — Hygiene backlog (anytime, unscheduled, all on record)

- **A `PositionModelChanged` seam** (observation from `impl-report-c1-feedback-emitter.md` §6, not a
  proposal). The four position fields have **17 assignments across 10 methods on two threads**, so
  C1's emitter needs 10 hooks and its completeness argument is an *enumeration* rather than a
  structural guarantee. One seam would collapse that to a single hook. ⚠ The receive-loop dispatcher
  is NOT that seam — `HandleQuoteUpdates` / `HandleOrderPositionUpdates` are `Async Sub`, so a hook
  there fires at their first `Await`, before their post-await writes: it would look complete and be
  wrong on the two hottest handlers.
- ✅ **`Test-ElementMatch` exact-id match — CLOSED 2026-08-06** (`188bdab`, `3611d49`,
  `spec-harness-exact-match.md`). `harness-common.ps1:109` used to match by `IndexOf >= 0`,
  first match in enumeration order wins, and every drive script used it — `txtTrigger` is a
  substring of `txtTriggerOffset`, which enumerates first, so on the C1 acceptance run asking for
  `Trig. P.` silently set `Trig. O.` **and reported success**: `Set '' (id 'txtTriggerOffset', …) =
  '300'`, leaving the SL ~$6 below market for a whole live position (survived only because price
  moved the safe way). Fixed structurally: `Select-BestMatchIndex` (a pure function over plain
  data, `harness-common.ps1`) now ranks exact `AutomationId` first, exact `Name` second, substring
  third, and **any tie at the winning tier fails loudly** (exit 3, every tied candidate named)
  instead of picking arbitrarily. All five call sites (`set-textbox.ps1`, `toggle-checkbox.ps1`,
  `select-combo-item.ps1`, `click-button.ps1`, `click-PLACES-ORDER.ps1`) gather-then-decide via the
  new `Select-MatchingElement` seam. Re-verified: 28 Edit AutomationIds, `txtTrigger` ⊂
  `txtTriggerOffset` still the only collision; the regression case (`set-textbox txtTrigger`)
  changes only `txtTrigger`; a bare `Trig` pattern now refuses loudly naming all three ties instead
  of picking one. The deny check (`click-button.ps1`) and the TESTNET/harness-PID gate
  (`click-PLACES-ORDER.ps1`) are untouched. This is the harness commit-verification class
  ([[harness-commit-verification-trap]]): it reported the work it *did* do, on the wrong control.
  ⚠ **The `-Exact` switch this row used to ask for was SUPERSEDED and was the wrong shape** — a
  switch is only correct when the caller remembers to pass it, so it converts a silent
  wrong-control into a silent wrong-control-unless-you-remembered. Exact-first-by-default requires
  nothing of any caller and is behaviour-preserving wherever the pattern is unambiguous. Spec §2.3.
- **`Get-ProcessWindows` intermittently misses the OWNED `AutoTradeSettings` window** (surfaced by
  the SB3 acceptance run; `review-harness-exact-match.md` §5.2). The window is `Show(Me)`-owned, and
  `RootElement.FindAll(TreeScope.Children, …)` sometimes does not enumerate it even while Win32
  `IsWindowVisible` says it is open — it *is* found under `TreeScope.Descendants` of the main form.
  A retry immediately after opening succeeded. **Pre-existing and not an SB3 regression** (the old
  code would have been blocked identically), but it makes any settings-window drive step flaky.
- **FrmIndicators full retirement** — more attractive now: with the engine 24/7 on AWS the indicator
  fallback is nearly never exercised, so retiring it leaves payload ATR + the $70 constant only.
- **Edit T.S. post-trigger rework** — 4-trap catalog; `EditStopLossTo` is the home.
- **Item G — stale-SL-box cosmetic** — mechanism documented in
  `runtime-record-live-ladder-2026-07-23.md`.
- **Housekeeping smoke leftovers** — the 8b checkbox-OFF half, the 14g long-PnL observation.
- **A2** manual-freeze observation · **B2** regression · **F1** rejected-edit re-sync (accepted LOW).
- **TP post-fill manual-move gap.**
- ~~Harness `txtTrigger` tree-order nit~~ — same defect as the closed row above; folded in there.
  **fractional-offset loud rejection** still open.

## §6 — Deliberately NOT building

**App-side exit logic** (partial TP / trailing TP / scale-outs) — C1 v2 is the correct home, with
the engine deciding; building it app-side forks the brain R1 forbids. **Multi-instrument** — the
engine's scope call, not this app's. Anything on the owner-rejected list above.

## §7 — Sequence of record

Everything through N2b is done, **and C1 is COMPLETE** — implemented, reviewed, its D1 fixed, and
all five acceptances 3–7 passed 2026-08-06. Current: **SB3's harness fix → §5 backlog**, with
**N2 enable running independently** (owner; gated only on its three sizing knobs).

**The §8.7 reorder, recorded because it was exercised and never written down:** the contract slots
the emitter "after N2 unless the trader reorders". The trader reordered — C1 was specced, built,
and reviewed while N2 enable stayed pending — so the emitter is no longer downstream of N2 and
never was in practice. Neither blocks the other; both ship off/disabled.
