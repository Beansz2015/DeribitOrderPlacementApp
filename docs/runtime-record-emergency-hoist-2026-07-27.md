# Runtime record — N1 emergency hoist, harness pre-verification (2026-07-27)

**Purpose:** the owner asked this seat to drive N1's runtime acceptance on testnet via the harness
FIRST, so the owner's own confirming run is a short re-check rather than a discovery exercise.
**Seat:** Opus 5 implementer. **Code under test:** `521d675` (N1 = `b4496b9` + `1a97054`).
**Verdict: the N1 acceptance items that are reachable all PASS.** One unexplained non-N1 anomaly is
recorded in §5 — it did not reproduce.

> **OWNER CONFIRMING RUN 2026-07-28 — PASSED on the real runtime bin (x64 Debug), trades #96–#98.
> Spec §Acceptance 2 and 3 are CLOSED.** Full result in §7. Restore of `orderapp-settings.json`
> verified by the owner. The §5 anomaly did NOT reproduce across three further placements.

**Environment (verified before any order was placed, not assumed):**

| Check | Result |
|---|---|
| Window title | `Deribit Order Placement App V2.2 — TESTNET` |
| Endpoint (app log) | `TESTNET environment — test.deribit.com` |
| Harness gate | `click-PLACES-ORDER.ps1` = TESTNET title + harness PID, printed per click |
| Build under test | `bin/Debug` dll **contains `emergencyFired`** (post-N1); built fresh from HEAD by `launch-app.ps1` |
| Account | testnet, $900.86, flat, bridge mode Off (N1 is not on the bridge path) |

**Geometry used** (Designer defaults are far too wide to exercise the cap in a session): amount 10,
Trig. P. 5, S. Loss 1, **M. SL 1 (checked)**, T.Prof 400, ATRSlip off. For a long that puts the SL
trigger at entry−5, the SL limit (and therefore the loss-cap anchor) at entry−6, and the emergency at
≈entry−7.

---

## 1. Results against spec §Acceptance

| Item | Verdict | Evidence |
|---|---|---|
| **A** — cap still fires after the hoist | **PASS ×2** | trades #6 and #7, both `Emergency Sell Market Order Executed.` |
| **B** — exactly once | **PASS ×2** | one emergency line + one reduce per trade; corroborated server-side (§3) |
| **C** — re-arms on a fresh position | **PASS** | #6 fired (latch set) → closed → #7 fired again |
| **D** — §3 normal-session parity | **PASS** | trade #8 with M.SL 200: 8 clean SL repositions, maker fill, **zero** emergency lines |
| §Acceptance 1 — gate + greps | PASS (earlier) | GATE PASSED 104/104 at HEAD; census 1 decl + 2 sets + 3 clears + 3 reads |

### The arithmetic, re-derived from the logs

Both fires land exactly where the frozen-anchor semantics predict — this is the part worth checking,
because it proves the hoisted check measures from `emergencyBaseline` and not from the chase
reference:

| Trade | Entry | SL limit = anchor | Cap = anchor − M.SL | Fired at | Verdict |
|---|---|---|---|---|---|
| #6 | 64592.5 | 64586.5 (= entry−6 ✓) | 64585.5 | 64585.02 | ✓ ≤ cap |
| #7 | 64554.5 | 64548.5 (= entry−6 ✓) | 64547.5 | 64546.94 | ✓ ≤ cap |

## 2. Trade #8 — the parity proof, in full

```
Market buy order placed For 10 starting at 64551.5.
Triggered SL placed @ $64545.5
SL repositioned: $64545.50 → $64538.50      (first reposition = where the hybrid latch freezes the anchor)
SL repositioned: $64538.50 → $64533.00
SL repositioned: $64533.00 → $64530.50
SL repositioned: $64530.50 → $64526.50
SL repositioned: $64526.50 → $64525.00
SL repositioned: $64525.00 → $64521.00
SL repositioned: $64521.00 → $64520.50
SL repositioned: $64520.50 → $64510.50
Position executed at 64510.5.   Loss of: $0.01.   Trade #8 recorded in database
```

With the cap out of reach the hoisted check is evaluated on every qualifying tick and is **invisible**:
the chase throttles, repositions and fills exactly as before, and the trade closes on the maker leg.
That is precisely what §3 asks for.

## 3. Independent corroboration — ntfy server-side poll

Not the app's own log; the server's record (handover §12 recipe; topic URL deliberately not recorded
here). Two fires ⇒ **exactly two** urgent posts, and the parity trade produced a *normal* close
instead:

```
23:24:11  p=5  Emergency Sell Market Order Executed.      <- trade #6
23:27:06  p=5  Emergency Sell Market Order Executed.      <- trade #7
23:29:02  p=4  Position closed at 64510.5: loss $0.01     <- trade #8 (parity, NOT an emergency)
```

The same poll independently confirms the owner's 23:04 launch was testnet
(`OrderApp started — TESTNET, breaker $1`).

## 4. Hygiene

- **The owner's journal was NOT touched.** The harness runs `bin/Debug`, which has its own
  `trades.db` (written 23:29); the runtime `bin/x64/Debug/…/trades.db` is untouched since 15:57.
  Trades #6–#8 are harness-DB rows — unlike the #90–#93 episode, nothing needs deleting.
- Harness session stopped (it would otherwise refuse the owner's own `launch-app`); screenshots
  deleted per the standing rule; `harness.json {"enabled": true}` left in the Debug bin (untracked,
  harness-only, gates the screenshot hotkey).

## 5. NOT N1 — one unexplained anomaly, recorded because it involved real orders

**On the FIRST placement only,** a single `Mkt. BUY` click produced **three** entries: three
`Market buy order placed For 10 starting at 64592.5.` lines (identical price ⇒ same tick), three
`Triggered SL placed @ $64586.5` echoes, and a reduce of **30**. The size is corroborated
independently — the balance moved 0.00000047 BTC ≈ $0.030 (taker round-trip on 30 USD, not 10) and
session P/L −5.0e-8 BTC matches a 30 USD inverse-contract move, not 10.

**It did not reproduce.** The next two placements, same script and same click path, each produced
exactly one entry and one reduce of 10.

**It is not N1.** N1 adds a single `emergencyFired = False` assignment inside `ExecuteOrderAsync`
and touches nothing in the click path. `btnMarket_Click` has one `Handles` clause and calls
`ExecuteOrderAsync` once; `click-PLACES-ORDER.ps1` exits immediately after the first `Invoke()`.
So either the button was invoked three times (harness/UIA side) or the handler re-entered — this
seat could not determine which from the evidence available, and says so rather than guessing.

**Worth noting:** the emergency still fired **exactly once** against that 30-USD position with three
SL legs registered — an unintended but useful stress case for the latch.

Recommend this gets its own look, independent of N1; it should not gate N1's acceptance.

## 6. What this run does NOT prove

- **Throttle-independence itself is not runtime-observable**, and no attempt was made to observe it.
  `SendWebSocketMessageAsync` swallows every exception, so `BackoffStopLossRetry` has no reachable
  trigger from a rejected edit, `slUpdateFailures` stays 0, and the throttle is a flat 333 ms — never
  the ~5 s the item-16 note describes. The spec's §Acceptance 2 wording ("force a persistent SL-edit
  failure") is therefore not reproducible as written. The hoist's correctness is structural
  (the diff + the gate + the coordinator's independent re-grep). **This warrants a coordinator
  ruling** — either the missing coupling is a real gap (failed edits genuinely should back off) or
  `BackoffStopLossRetry` is dead code to retire; N1's stated justification needs restating either way
  (the real pre-N1 exposure was ≤333 ms, plus the latent double-fire race, which is the more
  valuable half).
- **The two placement-seed clears** (D1's addition) are not runtime-reachable — they only matter when
  an emergency never reaches `CompletePositionClose`. Test C proves the `CompletePositionClose` clear,
  which is the one that fires in normal operation. The other two remain inspection-only, as ruled.

---

## 7. Owner confirming run — 2026-07-28, real runtime bin (x64 Debug) — PASSED

Same three tests, driven by the owner with a mouse on their own bin (not the harness), same geometry
(Trig 5 / S.Loss 1 / M.SL 1, then M.SL 200 for parity). **Spec §Acceptance 2 and 3 are now CLOSED.**

| Trade | Test | Entry | Anchor (= entry−6) | Cap | Outcome |
|---|---|---|---|---|---|
| #96 | cap fires, exactly once | 64989.5 | 64983.5 ✓ | 64982.5 | one emergency, one reduce of 10, closed 64976.84 |
| #97 | **re-arm** | 64977 | 64971 ✓ | 64970 | fired again after #96 set the latch; closed 64967.13 |
| #98 | **parity**, M.SL 200 | 64956 | 64946.50 (latched at 1st reposition) | 64746.50 (unreachable) | **10 clean repositions → maker fill 64921, ZERO emergency lines** |

**Two readings worth recording so the log is not misinterpreted later:**

1. **Fill price ≠ trigger price.** #96 closed 6.66 below the anchor, not 1. The cap *triggers* at
   anchor − M.SL; `CancelOrderAsync` and the market reduce are then two WS round-trips, and in a
   fast fall price keeps moving through them. The taker fill is where the close executed, not where
   the emergency fired. Nothing here indicates a late cap.
2. **#96 and #97 fired with NO preceding `SL repositioned` line**, i.e. `emergencyBaselineSettled`
   was still False and the cap measured from the **pre-settle flip price**. That is precisely the
   adversarial case the 2026-07-13 re-check called out as conservative-and-bounded but unexercised
   ("pre-settle emergency measures from the flip price for ≤1 reposition"). It is now runtime-proven.

**Anomaly status (§5):** did **not** reproduce. One `Market buy order placed` line per click across
all three trades. Tally is now **5 clean placements vs 1 anomalous** (the single bad one was a
harness UIA `Invoke`; all three real mouse clicks were clean). That mildly shifts suspicion toward
the harness/UIA path over the app's handler — evidence, not proof; the item stays open.

**Housekeeping produced by this run:** trades **#96–#98 are testnet rows in the LIVE journal**
(the owner's bin, unlike §4's harness DB) — deletable via View Trades right-click, together with the
older #90–#93. Owner confirmed the `orderapp-settings.json` restore (Trig. P. reads 50 again).
