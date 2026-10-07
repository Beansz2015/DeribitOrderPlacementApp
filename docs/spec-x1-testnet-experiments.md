# Plan — X-1 testnet experiments: how Deribit actually treats our OTOCO legs

**Status:** ✅ **APPROVED by the owner 2026-10-08 — all of it, including (f)'s testnet-only reduce-only-TP switch and (g)'s reduce-only market.** The §2 raw-echo diagnostic, if one proves necessary, still goes to the owner first.
**Origin:** X-1 of the engine report (`docs/adversarial-audit-2026-09-24.md` §B row A13 and §C C-16,
engine repo, branch `claude/great-keller-s5f4gp`), and `docs/triage-adversarial-audit-2026-10.md` §4.
Owner ruling 2026-10-07: **a seat runs them via the harness.** The audit's Deribit facts came from
web-search summaries; nothing here has been observed.

## §0 — Safety rules (from `CLAUDE.md`, not negotiable)

- **TESTNET only.** The harness refuses a window title without `— TESTNET` and a non-harness-launched
  process (`tools/click-PLACES-ORDER.ps1`'s gates). Re-check `secrets.json` reads `testnet` and the
  title before the first placement (memory `gate-does-not-build-x64-bin`).
- **Every placement through `tools/place-and-verify.ps1`.** Settings boxes through `tools/set-textbox.ps1`.
- **The bridge stays Off and is never armed.** No experiment needs the bridge.
- **The owner's own app instance must be closed** before the harness launches its own.
- Screenshots of the app only (`tools/screenshot-mainform.ps1`), deleted after use.
- Amount **10 USD** (one contract) except experiment (d), which needs a partial fill.
- Every experiment ends **flat with no open orders**, checked in the log and the order snapshot.

## §1 — The experiments

| # | Question | Decides | How (UI only unless marked) |
|---|---|---|---|
| (a) | An OTOCO buy whose sell-stop trigger is **already above the market** at fill: triggered, rejected or left resting? What `order_state` does `user.changes` push? | A13's dead-leg branch; C-1's app-side check | Set a **negative** `Trig. P.` so the trigger computes above the bid (confirm the app accepts it, and what it sends), then `Mkt. BUY`. |
| (b) | Does `trigger_offset` 30 trail a `stop_limit`'s trigger, and is the new trigger pushed? | A9 | `Mkt. BUY` with `Trig.O.` 30. Watch the stop leg's echoed `trigger_price` while the price rises. Passive; may need a wait. |
| (c) | Where does a triggered post-only stop rest when its limit would cross, and is it repriced by the exchange? | A2; C-16(c) | Continue (b) until the stop triggers, or set a tight `Trig. P.`. Watch the echoes before the app's chase edits it. |
| (d) | With `first_hit`, are the TP and SL placed at the **full** primary amount on a partial fill? | **A14 (the queued partial-fill fix)** | `Limit BUY` at an Amount larger than testnet's top-of-book (e.g. 500 USD), so it partially fills. Watch the legs' amounts. Cancel the remainder and flatten at once. |
| (e) | Does a triggered stop keep its `order_id`? | the chase's id tracking | Observed in (c). |
| (f) | **With `reduce_only` on the TP**, does an SL trigger cancel **both** legs? (`frmMainPageV2.vb:4312` says so) | **A14's fix design** (a reduce-only TP) | ⚠ **Needs a code change:** a testnet-only switch that adds `reduce_only` to the TP leg, honoured only when `AppSecrets.IsTestnet`, default off, removed after. **Separate owner approval.** |
| (g) | A reduce-only **market** sell for the full size while reduce-only legs rest: does it fill? What happens to the resting legs? | **A12's reorder** (reduce before cancel) | Open 10 USD with `Mkt. BUY`, then `Mkt. Rdc. Sell` with the OTOCO legs still resting. Watch the fill and the legs' states. |

## §1b — Riding along: the trade slippage-fields testnet check (owner, 2026-10-08)

The owner put this check on hold and asked for it here, since the harness is already running. It is
acceptance §3 item 4 of `docs/spec-trade-slippage-fields.md`; details in
`docs/review-trade-slippage-fields.md` §5.

| # | Check | How | Pass |
|---|---|---|---|
| (h) | A chased limit entry that fills records its slippage | ATRSlip ticked at 0.6. `Limit BUY` 10 USD; let it chase at least once and fill; flatten. | Its `Trades` row: `RequoteCount` > 0, `SignalPrice` = the placement price, a plausible `SlippageATR` |
| (i) | A forced abort is recorded, and adds no trade | ATRSlip 0.05. `Limit BUY` 10 USD; let the chase trip the cap. | One `AbortedEntries` row, reason `ATR slippage`; **no** new `Trades` row |

- `Mkt. BUY` entries (most of §1) never run the chase guard, so their rows carry anchor 0 and
  `SlippageATR` 0 — correct, and not a pass for (h).
- A `Trades` row is written only at close, so read after flattening.
- Read a **copy** of `trades.db` from the x64 bin, never the live file. Back the file up before the run.
- Record (h) and (i) in the same runtime record as the experiments.

**(j) — the chase-anchor reset check** (owner, 2026-10-08): acceptance §3 item 3 of
`docs/spec-chase-anchor-reset.md`; the step-by-step recipe is `docs/impl-report-chase-anchor-reset.md` §6.
ATRSlip 0.05; place a **1-USD** `Limit BUY` (the exchange rejects it: not a multiple of the contract size);
set Amount back to 10; after the bid moves more than ATR × 0.05, `Limit BUY` again. **Pass:** the order is
placed, with no "slippage exceeds limit" line and no new `AbortedEntries` row. Cancel it afterwards.

## §2 — Observation

- The app log (`tools/read-log.ps1`) shows order-state lines, but may not show raw `user.changes`
  payloads. **First task:** check what the log carries for each needed field (`order_state`,
  `trigger_price`, `amount`, `order_id`). If a field is missing, propose a testnet-only raw-echo log
  line — a code change needing owner approval, like (f).
- Record each result as a short table in `docs/runtime-record-x1-testnet-<date>.md`: the request sent,
  the echoes received (verbatim), and the answer to the question.

## §3 — What the owner approves

1. The UI-only experiments (a)–(e) and (g), under §0.
2. Separately: (f)'s testnet-only switch, and any raw-echo diagnostic from §2.

## §Model and effort

- **Model:** Opus. **Effort:** high. It places real (testnet) orders through the order path.
