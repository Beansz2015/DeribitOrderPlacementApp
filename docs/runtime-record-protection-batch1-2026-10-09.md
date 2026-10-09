# Runtime record — protection batch 1 testnet checks, and two hazards they exposed (2026-10-09)

**Run by:** the owner, on the x64 bin rebuilt 2026-10-08 01:47 (testnet). **Recorded by:** coordinator seat,
Opus 5.5, from the owner's pasted logs. Spec: `docs/spec-protection-batch1.md`; review:
`docs/review-protection-batch1.md`.

## 1. The batch-1 checks

| Check | Result | Evidence (owner's log) |
|---|---|---|
| 1. Outage > 60 s, flat | ✅ | `WebSocket DOWN for 60 s, still reconnecting - no position open - last error: Unable to connect to the remote server`; attempts continued past 10; `WebSocket reconnected after 103 s` |
| 1. Outage > 60 s, position open (market and limit entries) | ✅ page | `… position OPEN: LONG 10 USD - last error: …`; recovery lines after 102 s and 63 s |
| 2. 20–30 s pull, no page | ⚪ **no log supplied** | — |
| 3. Restart with the stop cancelled by hand → NO STOP alarm | ✅ | `Open position with NO STOP: LONG 10 USD - place a stop now` |
| 4. Restart with the stop resting → no alarm | ⛔ **cannot be run** | The owner found SL **and** TP deleted on **every** app exit (§2) |

**The NO STOP alarms after each network drop were TRUE alarms**, not false ones: the orders were gone
(§2). Batch 1 caught a real hazard on its first run.

## 2. Hazard 1 — every disconnect deletes the position's SL and TP

- Observed: after every app exit (repeatable, every try), and after each network drop, the position had
  no orders.
- **Not app code.** `frmMainPageV2_FormClosing` closes the socket and cancels no orders. No code in the
  app calls `private/enable_cancel_on_disconnect` or its siblings (grep, 0 hits).
- **Likely cause: Deribit's "cancel on disconnect" is enabled on the account or the API key.** Deribit
  then cancels all open orders when the connection drops. Not yet confirmed: the owner checks the testnet
  and live keys' settings.
- **Consequence:** every routine socket drop leaves an open position with **no exchange-side stop**, and
  the app does not re-place one. In live, drops happen most days
  (`docs/runtime-record-ws-down-emitter-2026-08-14.md`).

## 3. Hazard 2 — limit entries in a fast market fill with a TP and NO SL

- Observed: sometimes, only in fast markets, a limit entry fills with its TP leg and no SL leg. Both
  sides. The sell case logged:
  `Position entered: SHORT 10 @ $82934.00` → `TP re-anchored …` →
  `API ERROR (id 223344 order edit): code 11044 - not_open_order` →
  `API ERROR (id 223344 order edit): code 10004 - order_not_found` →
  `API ERROR (id 223346 order edit): code 10004 - order_not_found` → `SL-edit failure #1`.
  The buy case showed no errors but the same missing SL.
- **Most likely the audit's S0** (rows A1 + A13 of `docs/triage-adversarial-audit-2026-10.md`): the
  stop trigger is the **placement** price ± `Trig. P.` (`frmMainPageV2.vb:4260`, `:4303`, `:4346`,
  `:4388`). The owner's box reads **5 USD**. In a fast market, the price is already through a trigger
  5 USD away when the entry fills. Deribit then rejects or kills the stop leg, the app has no `rejected`
  branch (row A13), and nothing alerts. The edit errors are the app chasing legs that no longer exist.
- **Not yet confirmed.** Deribit's order history for the missing SL (its status and reason) settles it,
  and answers part of experiment X-1(a).

## 4. Consequences for the plan

- Row A13 (alert on a dead stop leg) no longer waits on X-1(a) once the order history confirms it.
- The restore-only NO STOP check is not enough: Hazard 2 happens **live**, with no restart. A live
  "position with no stop" watchdog is needed.

## 5. Follow-up, 2026-10-10 (owner)

**Hazard 1 — CONFIRMED and fixed on testnet.** The owner confirmed Deribit's cancel-on-disconnect was on,
and disabled it. Re-run of check 4 with the stop resting:
`Restored order context: entry=none, TP=122489230852@83201.00, SL=SLTS-11031556@trig 82751.00 (untriggered)`,
and **no NO STOP line**. ✅ Check 4 passes. The live account still needs the same check before any live
use; added to `docs/production-cutover-checklist.md` §1.

**Check 2 (short drop, no page)** — the owner's point stands: the first 60 s of each check-1 log show eight
attempts and no page. That covers "no page under 60 s" at runtime. "No recovery line after a drop that
never paged" is pinned by OrderCheck (`ShouldPostReconnectLine(0)` false), not observed. Accepted.

**Hazard 2 — the missing SL never existed on the exchange.** The owner reproduced it: Deribit's order and
trade history show only the filled entry. No SL order appears at all — not rejected, not cancelled, not
triggered. It happens only with the stop very close to the entry (per the owner: the trigger 5 USD and
the stop's limit about 3 USD from the entry); never with wider settings.
**Reading:** this answers the core of experiment X-1(a). A crossed stop leg at OTOCO activation is dropped
**silently**: there is no order to see, chase or cap, and nothing in history. Whether `user.changes`
pushes anything for it is still open (the app logs nothing, but it has no `rejected` branch to log with).

**New: a manual reduce is rejected while the triggered SL rests.** The owner's log:
`Triggered SL placed @ $82743` (the 5-USD stop triggered right after the fill) →
`Reduce-only LIMIT sell 10 @ 82733.5 order sent.` →
`API ERROR (id 1 subscribe/reduce order): code 11030 - other_reject invalid_reduce_only_order`, twice.
The triggered SL chase then closed the position. **Reading:** with a reduce-only order already covering
the position, Deribit refuses a second reduce-only order. That answers the limit half of experiment
X-1(g), and it matters for the audit's proposed emergency reorder (reduce **before** cancel), which would
hit the same rejection while the SL rests. The market half of X-1(g) is still to test.
It is also a manual-close bug: the Reduce Sell / Reduce Buy buttons cannot work while a reduce-only stop
covers the position.
