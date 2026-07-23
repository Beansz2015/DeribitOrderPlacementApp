# Production cutover checklist (owner-executed)

**Written 2026-07-24 (coordinator), after live-at-min-size passed (`runtime-record-live-ladder-2026-07-23.md`).**
Scope: everything between "the ladder works at size 10" and "this runs at normal size as my daily
setup". All items are yours to execute; nothing here changes code. Work top to bottom; §1 before
any size-up.

## §1 — Credentials & account hygiene (one-time, BEFORE size-up)

- [ ] **Rotate the live API key.** The current key dates from the throwaway/exposed-key era. On
  Deribit: create a NEW key scoped to trade+read only (no withdrawal scope), update `secrets.json`
  (`client_id`/`client_secret`), restart the app, verify "WebSocket authorized successfully", THEN
  delete the old key. Never paste keys anywhere but `secrets.json` (it is gitignored and
  repo-guarded — the gate checks it stays untracked).
- [ ] **Delete the throwaway sub-account** (the exposed-key one) once nothing references it.
- [ ] **Testnet key stays separate** and only in the testnet flip of `secrets.json`. After ANY
  harness/testnet session: restore `Environment` to `"live"` and re-verify the file (valid JSON,
  all keys — the harness passes used a byte-count sanity check; keep that habit).
- [ ] Confirm account-level safety on Deribit itself: no withdrawal scope on the trading key,
  sensible account-level limits if offered.

## §2 — Session-start ritual (EVERY live session — pin this)

The bridge gate config deliberately does NOT persist (restart = Designer defaults). On every start:

- [ ] **Circuit breaker: the default is `-1` = OFF.** Type your real max-session-loss value FIRST,
  before arming. (See §6 — this is the one standing footgun.)
- [ ] Cooloff (default 5 min) and Inclusion Time Range (default blank = 24 h) — set if wanted.
- [ ] Tiers box: `HIGH,MEDIUM` (default is correct; policy refines per-session on top).
- [ ] Session policy: verify the P5 lines + checkbox state survived restart (these DO persist).
- [ ] Amount = your intended size; **Max Slippage ATR checkbox ON** (START requires it, and it is
  the whole guard's arm switch).
- [ ] Panel line reads `… | Engine ARM: … | Payload: FRESH` before you arm; flat + no working orders.
- [ ] Arm discipline: engine ARM → app ARM → START; **uncheck app ARM whenever you leave the desk**
  (instant force-stop; re-arming is 3 clicks by design).

## §3 — First-week size ladder (suggested; adjust to comfort)

- [ ] Days 1–2: stay at **10** (min size). Watch: fills vs engine levels, slippage-abort rate,
  policy refusals (post-P5) look right per session.
- [ ] Days 3–4: **20–30**. The session-policy multipliers start being real here (LONDON 0.5× of 20
  = 10, no longer clamped). Confirm the clamp line disappears at sizes where the mult is
  expressible.
- [ ] Day 5+: step toward normal size. Decide fixed-amount vs risk-sized (`SIZE` button): risk
  sizing binds only when `stop distance ≥ risk × price ÷ max_size` — at risk 25/max 500 that is
  roughly a $3.2k stop at current prices, so for structural stops the `max_size_usd` cap is the
  operative limit. Tune `risk_per_trade_usd`/`max_size_usd` in Tooling deliberately, not by feel.
- [ ] At each step: session P/L and the disposition log get a end-of-session glance (the join
  machinery stays available if anything ever looks off).

## §4 — Loss caps at real size

- [ ] **Circuit breaker value:** set it to the number that, if hit, means "stop trading today" —
  and remember it force-STOPs the bridge when session PnL breaches it (re-arm = full sequence,
  deliberately). It is a session cap, not a per-trade cap.
- [ ] **M. SL (max loss per trade)** stays your per-trade emergency cap — the value persists;
  verify it scales with your size step (a $70 cap at size 10 means something different at 50).
- [ ] First week: keep both caps TIGHT relative to size. Loosen consciously, later.

## §5 — Monitoring & records

- [ ] Alerts: right-click alert config — confirm `acted`/order-rejected/external-close/connection
  alerts are ON for live running (external_close is deliberately its own toggle with an adverse
  tone — keep it audible).
- [ ] Trade DB (`Results`): R-multiples become meaningful at real size (they read 0.00 on min-size
  scratches — known display property, not a bug). Skim weekly.
- [ ] Disposition log: end-of-session glance for unexpected tokens (`rejected:` = API-level —
  investigate; `refused: policy` = policy working). Row count keeps growing ~1/min while the
  engine runs — normal.
- [ ] Keep the engine seat informed via the trader when you change policy VALUES (their matrix
  reviews consume the same cells — evidence discipline §3 of the proposal).

## §6 — Kill switches & incident playbook (know these cold)

1. **Uncheck app ARM** — instant force-stop of automation (the primary kill).
2. **STOP button / mode → Off** — same effect, coarser.
3. Position management stays manual-first: `Cancel All Open`, `Mkt. Rdc.`, Edit T.S., B.E. all
   work on bridge-placed positions exactly as on manual ones.
4. If the DISPLAY looks wrong while flat (e.g. a stale Stop Loss box after a slippage abort —
   known cosmetic, `runtime-record-live-ladder-2026-07-23.md`): trust the exchange UI over the
   panel, do not debug live. Disarm, snapshot the log, bring it to the coordinator.
5. If anything REAL misbehaves: disarm, flatten manually if needed, save `txtLogs` + the
   disposition log tail, report. Never keep trading through an anomaly to "see if it recurs".

## §7 — Standing hygiene

- [ ] Harness/testnet work never touches the live session (the PID/title tiers enforce it; keep
  using the isolated-bridge.json protocol for payload tests — the soak-era standard).
- [ ] `orderapp-settings.json` now writes atomically (housekeeping 18) — but it lives beside the
  exe; if you ever clean bin folders, know that standing inputs + the session policy live there.
- [ ] Push cadence: the repo is the system of record — keep pushing after each reviewed batch.

## §8 — Decisions this checklist surfaces (owner rulings, before/at size-up)

1. **Persist the circuit breaker (and cooloff/window)?** Today's non-persistence is deliberate
   (restart = clean defaults), but at production size a forgotten `-1` breaker is the biggest
   operational hole in this list. Options: (a) keep the ritual (§2) and accept it; (b) small spec:
   persist gate config like the risk keys; (c) smallest spec: change the Designer default from
   `-1` to a conservative positive value. Coordinator recommends **(b) or (c)** before normal size.
2. **Item-8 drift** (trailing-LONG slippage gate reads `bestAsk` where entry-LONG reads `bestBid`)
   — standing flag from housekeeping; decision, one-line change if ruled.
3. **ATR period 7 vs 14** (payload vs app indicator on source flips) — standing cross-app decision;
   affects only the slippage limit's step on fallback transitions.
