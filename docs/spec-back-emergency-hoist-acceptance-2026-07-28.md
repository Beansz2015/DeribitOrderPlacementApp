# Spec-back — N1 emergency hoist: acceptance closed, two items for ruling (2026-07-28)

**Why this doc:** N1 is **done** — implemented, coordinator-reviewed/APPROVED with all four asks
ruled, and now **owner runtime-accepted on the real runtime bin**. This closes the loop and puts
**two items in front of the coordinator**: one that needs a ruling (§2) and one that needs an owner,
not a ruling (§3). Nothing in N1 itself is re-opened.

**From:** the order-app Opus 5 implementer seat. **To:** the coordinator/orchestrator seat.
**Relay:** via the trader (neither seat writes cross-repo).

**State:** `origin/master = 9667dc8`; local `master = a89bdf1`, **11 commits ahead, unpushed**, tree
clean. Gate at HEAD: **GATE PASSED, OrderCheck 104/104**.

**Supersedes as status:** `spec-back-emergency-hoist.md` (the review request — all four of its asks
are ruled and closed). **Read alongside:** `review-emergency-hoist.md` (the rulings),
`runtime-record-emergency-hoist-2026-07-27.md` (both runtime sessions, §7 = the owner's).

---

## 1. N1 is closed

| Stage | Status |
|---|---|
| Implementation | `b4496b9` + scoping correction `1a97054` |
| Coordinator review | **APPROVED**, four asks ruled (`978ef90`) |
| Rulings folded back into the spec | `521d675` — §Acceptance grep now says **3 clears**; §5's rate-limiter premise annotated false + do-not-re-raise |
| Harness pre-verification (this seat) | `4db09ae` — testnet, all reachable items pass |
| **Owner runtime acceptance** | **`a89bdf1` — spec §Acceptance 2 + 3 CLOSED** |

The owner's run (trades **#96–#98**, real x64 Debug bin, mouse clicks — a different bin *and* a
different input path from the harness run):

| Trade | Test | Anchor (entry−6) | Cap | Result |
|---|---|---|---|---|
| #96 | fires, exactly once | 64983.5 ✓ | 64982.5 | one emergency, one reduce of 10 |
| #97 | **re-arm** | 64971 ✓ | 64970 | fired again after #96 set the latch |
| #98 | **parity** (M.SL 200) | 64946.50 | 64746.50 | 10 clean repositions → maker fill, **zero** emergency lines |

**Bonus coverage, unplanned:** #96/#97 fired with **no preceding reposition**, so
`emergencyBaselineSettled` was still False and the cap measured from the **pre-settle flip price** —
the exact case the 2026-07-13 re-check listed as conservative-and-bounded but *unexercised*. Now
runtime-proven.

**Log-reading trap worth a line in the handover:** #96 closed **6.66** below the anchor, not 1. The
cap *triggers* at anchor − M.SL; `CancelOrderAsync` + the market reduce are then two WS round-trips
and price keeps moving through them. **Fill price ≠ trigger price** — that log does not show a late
cap.

---

## 2. THE ASK — `BackoffStopLossRetry` is unreachable, and it changes N1's justification

Found while working out how the owner could reproduce spec §Acceptance 2's "force a persistent
SL-edit failure". **They can't, and neither can anyone else.**

### The evidence chain (all at HEAD)

1. `SendWebSocketMessageAsync` (`:1056`) catches `WebSocketException`, `OperationCanceledException`
   **and bare `Exception`**. It never propagates. A rejected edit therefore never throws at the
   send site.
2. `UpdateStopLossForTriggeredStopLossOrder` additionally wraps its **entire** body in its own
   `Try`/`Catch`, so nothing inside it can reach the chase's `Catch` either.
3. Both `BackoffStopLossRetry` call sites therefore have no reachable trigger from a rejected edit:
   the chase `Catch` (`:2279`) and the `too_many_requests`/`10028` arm (`:4246`).
4. ⇒ `slUpdateFailures` (`:1896`) is **always 0**, and `lastStopLossUpdate` is only ever set to
   *now* (success) or `MinValue` (force). **The throttle is a flat 333 ms. It has never been 5 s.**

**A cheap empirical check the coordinator can run:** the `SL update rate limited: {N}s remaining`
line (`:2292`) is gated on `remainingMs > 1000`, which requires `lastStopLossUpdate` to be *in the
future* — something only `BackoffStopLossRetry` ever does. **If that line has never appeared in any
archived session log across the whole soak, that is independent empirical confirmation.** I have no
persisted host log to grep (txtLogs is a RichTextBox, and the dispositions log is bridge-only), so I
could not run it myself.

Note this is **not** "failed edits are invisible": Audit2 F2's `HandleUnhandledJsonRpcError`
(`:1471`) does surface edit ids 223344–223350. The failure is **visible but not coupled** to the
retry throttle.

### What this changes

- The item-16 trade-off note, ROADMAP N1's framing, and §4 of `review-emergency-hoist.md` all cite
  "**up to ~5 s**" of emergency-detection delay. The real pre-N1 exposure was **≤333 ms**, plus the
  latent double-fire race. **The race is the more valuable half of N1** — that part is unaffected
  and stands exactly as reviewed.
- **N1 was still the right thing to land first, and the ordering matters:** if the coupling in
  option (a) below is ever implemented, the backoff becomes reachable and the ~5 s exposure becomes
  *real for the first time*. N1 has already removed it pre-emptively. Fixing the coupling after N1
  is safe; fixing it before N1 would have introduced the very latency item-16 warned about.

### Options, with a recommendation

| | Option | Assessment |
|---|---|---|
| **(a)** | Couple real edit failures into the backoff — cheapest seam is `HandleUnhandledJsonRpcError`, which already parses the error and knows the edit ids; increment `slUpdateFailures` there rather than touching the send seam | **Recommended**, as its **own small spec** — a new coupling on the SL hot path deserves its own review, and it is squarely a "before normal size" item |
| **(b)** | Retire `BackoffStopLossRetry` as dead code | **Not recommended** — the #4 retry-amplifier concern was real; deleting the mechanism leaves nothing if edits ever do fail persistently |
| **(c)** | Leave as-is, document only | Acceptable if the queue is full, but then the "5 s" language must still be corrected |

**Either way, one thing should happen now regardless of the option chosen:** correct the "~5 s"
framing wherever it appears, so nobody re-derives a threat model from it.

**Explicitly NOT bundled with N1.** N1 is closed and should not be re-opened for this.

---

## 3. Second open item — a triple placement, needs an owner not a ruling

Recorded because it involved **real orders**, and it should not be buried in a runtime record.

On **one** placement (the harness seat's first), a single `Mkt. BUY` click produced **three** entries:
three `Market buy order placed` lines at an identical price (⇒ same tick), three `Triggered SL placed`
echoes, and a reduce of **30**. Size corroborated independently of the log — balance moved
0.00000047 BTC ≈ $0.030 (a 30-USD taker round trip, not 10) and session P/L matched 30 USD.

**It has not reproduced. Tally is now 5 clean placements vs 1.** The single anomalous one was a
harness **UIA `Invoke`**; all three of the owner's **real mouse clicks** were clean. That leans
toward the harness/UIA path over the app's handler — **a lean, not a finding.**

**It is not N1:** N1 adds one boolean assignment inside `ExecuteOrderAsync` and touches nothing in
the click path. `btnMarket_Click` has a single `Handles` clause and calls `ExecuteOrderAsync` once;
`click-PLACES-ORDER.ps1` exits immediately after the first `Invoke()`.

**Ask:** none — this needs an investigation owner and a slot, not a ruling. It should not gate N1,
the push, or the queue.

---

## 4. Runtime facts worth carrying into HANDOVER-3

1. **Fill price ≠ trigger price** on the emergency path (§1) — the single most likely misreading of
   an emergency log.
2. **🚨 The app persists all 11 geometry fields on `FormClosing`** (`SaveUserSettings`, unconditional,
   at the top of the handler): amount / TP / trigger / stopLoss / triggerOffset / tpOffset / comms /
   marketStopLoss / atrMult + the two checkboxes. **Any runtime test that tightens the geometry
   clobbers the owner's real trading values on exit** — in the same file that holds the session
   policy and the breaker. Back up `orderapp-settings.json` first, restore after, and *verify the
   restore by reading a box back*. (Done for the owner's run; restore verified.)
3. **Harness quirk, same family as the commit-on-blur trap:** `tools/set-textbox.ps1` matches by
   **substring**, so `txtTrigger` can never address `txtTrigger` while `txtTriggerOffset` exists — it
   silently sets the **wrong box** and reports success. Caught only because a screenshot was taken
   after setting. Worth either an `-Exact` switch on the script or a line in the quirks list.
4. **The circuit breaker gates the bridge path only** (`SignalBridge` 4.6 `breakerBreached`), so a
   `$1` breaker never blocks a manual test placement. It still needs resetting before the ladder.
5. **Harness runs cannot pollute the live journal** — `bin/Debug` carries its own `trades.db`. The
   owner's own runs do: **#96–#98 are testnet rows in the live journal**, deletable alongside the
   older #90–#93.

## 5. Engine / orchestrator relay

**None. No contract change, no bridge surface touched.** N1 is entirely consumer-side on the manual
triggered-SL path; the bridge was in mode Off for every test. Contract v1 stays frozen and untouched.

## 6. State and what is left

- N1: **complete**. Push is the only remaining N1 action.
- Open: §2 ruling · §3 investigation owner · breaker off `$1` · delete testnet journal rows.
- Queue after the push (per the reordering for the Aug-1 fee change): **EV chase budget**
  (`spec-ev-chase-budget.md`) → **N2 risk-sized bridge trades** → **C1 v2 feedback file**.
- **Safety boundary:** the harness seat placed testnet orders only, under the TESTNET-title +
  harness-PID gate, verifying the endpoint and the post-N1 build before the first order. It never
  armed the bridge, never touched the live environment, and stopped its session so it could not
  block the owner's.
