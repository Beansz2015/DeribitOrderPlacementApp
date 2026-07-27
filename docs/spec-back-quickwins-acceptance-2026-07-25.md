# Spec-back — quick-wins runtime acceptance (2026-07-25)

**Why this doc:** the pre-ladder quick wins (Q1 notifier · Q2 signal-tag lifecycle · Q3 ops tools)
completed **owner runtime acceptance** on testnet this session. This captures the result, the
consumer-side behaviours confirmed live, and the two stale test-recipe defects that were corrected,
so a fresh seat or the coordinator can resume without the chat. **§5 is the part to relay to the
engine/orchestrator seat.**

**From:** the order-app Opus 5 seat. **Relay:** via the trader (neither seat writes cross-repo).

**Push-state:** `origin/master = 9667dc8` (owner pushed all 12: impl 4 + rulings/specs `4262785` +
review `f49659b` + grid `57db236` + settings fix `2346fad` + 4 handover updates). Local `441c8ab`
= 1 doc commit ahead. Tree clean. **Gate re-run at HEAD: GATE PASSED, 104/104.**

---

## 1. What is now accepted

Every item of `spec-quickwins-notifier-signalcols.md` §Acceptance is closed:

| # | Item | Status |
|---|---|---|
| 1 | Gate per commit, new fixtures counted | ✅ 104/104 at HEAD (96 → 102 Q1 → 104 Q2) |
| 2 | **Q1** startup ping · `acted` · auto-STOP urgent · **disabled-parity** | ✅ all four |
| 3 | **Q2** bridge-tagged · manual NULL · **step-3 clear** · legacy rows intact | ✅ all four |
| 4 | **Q3** both scripts read-only against real files, archive opens | ✅ implementer |
| 5 | Greps (no `Await` on notifier sites; tag-clears beside teardowns; SL tripwires) | ✅ implementer + review |

Q4 (USE-ENGINE-LEVELS) remains **spec-sanctioned SKIP** on measured geometry.

### The runtime evidence (testnet, owner-driven; this seat read back)

| Trade | Proves | Evidence |
|---|---|---|
| 2 | Q1 `acted` + Q2 bridge-tag | disp `10:00:32 acted (id 109534252220)`; ntfy `p=3`; grid **#92 = Signal 1 / MEDIUM** |
| 3 | Q2 step-3 clear | disp `10:19:54 rejected: cancel pending`; next manual **#93 empty** |
| 4 | external close | log `Position closed.`; ntfy **`p=5` urgent**; **no DB row** (correct) |
| — | Q1 disabled-parity | `disabled (no ntfy_url)`, other 5 startup lines identical, **zero posts server-side**; restore → ping |

**18-message ntfy audit** matches the spec's priority table exactly: startup `p=3` ×8, auto-STOP
`p=5` ×5, `acted` `p=3`, `rejected` `p=3`, tracked close `p=4` ×2, external close `p=5`. The
disabled window is visible as a gap in the same server-side record (nothing 09:08:58 → 09:15:26
while the app ran with `ntfy_url` removed).

## 2. Consumer behaviours CONFIRMED at runtime (no code changed)

These were designed and reviewed; this session is the first time each was exercised live.

- **Freshness = `2.5 × max(exec_resolution_min, 1)`** (`SignalBridge.vb:239`) — observed at 2.5 min
  with `exec_resolution_min = 1`.
- **START's interlock consumes a *parsed* payload for BOTH freshness and engine ARM**
  (`TryStart`, `:267`). `StartWatching` (`:374`) attaches the watcher only and **never seeds from
  the file on disk**, so the bridge knows nothing until a file *event* delivers a payload.
- **Evaluation is file-change-only** (FSW → 150 ms debounce). START does not re-evaluate the payload
  already on disk, and `OnStalenessTick` (`:440`) returns early when fresh — it can only ever *stop*
  the bridge, never re-assess.
- **Auto-STOP on staleness fired 5×, all correct** (10 s poll, stand-down alert at 3 consecutive) —
  every one caused by the engine being deliberately stopped for the harness.
- **De-dupe watermark persists across restarts** (`bridge-state.json`) — restored as
  `last acted signal 1 (engine harness-d4409081adc4)` after Trade 2.
- **Session policy is live and refusing**: `refused: policy(LONDON/tier)` ×4, then MEDIUM passed.
  `size_mult 0.5 clamped to contract min 10` on every LONDON act, as designed.

## 3. Two stale test-recipe defects — CORRECTED

Both were in `handover-quickwins-runtime-acceptance.md` §7 and both cost failed attempts:

1. **`-Entry <far price>` does NOT make a bridge entry rest.** `SignalBridge.vb:734`: *entry is a
   reference only — the app enters at top-of-book under its own slippage cap.* Trade 2 filled in
   ~1 s (the chase crosses the spread), so "rest a limit → Cancel All Open" is an unwinnable race.
   `IsATRSlippageExcessive` does not use the payload entry either — it seeds `originalSignalPrice`
   lazily from the live quote at the first chase evaluation.
2. **"Open a position first → `rejected: position open`" is UNREACHABLE.** Bridge gate 4.6 tests
   `IsFlat`, which reads the *same* `positionSizeUSD` field as `PlaceAutomatedOrder:658`, so it
   stops at `refused: not_flat` **before** `SetPendingSignalTag`. Nothing staged ⇒ nothing cleared
   ⇒ the test proves nothing.

**The deterministic route (use this next time):** `cancel pending` is the ONLY rejection reachable
*after* staging — the bridge deliberately leaves it to `PlaceAutomatedOrder` (`SignalBridge.vb:679`).
Cancel All Open sets `cancelPending = True` with a **4-second** self-clearing timeout
(`frmMainPageV2.vb:321`); the only early clear is the raced-abort repair (id 31), which cannot fire
while flat. Bridge STARTED + flat → click **Cancel All Open** → land the payload within 4 s →
`rejected: cancel pending` → `ClearPendingSignalTag`. Worked first try.

## 4. Harness protocol (engine stopped) — and why it is NOT production

With the engine stopped the payload must be written **twice**: write #1 (satisfies START's
freshness/engine-ARM precondition; dispositions `refused: interlock`, expected) → **START** → write
#2 (lands while started → acts).

**This double write is a harness artifact.** With the engine running it rewrites the payload every
run interval, so START just works, the next emission acts, and none of the staleness auto-STOPs seen
this session occur. The only residual in production is a possible wait of up to one run-interval
after switching to Live before START will succeed. None of it applies in **Log-only** (gate 4.5 is
Live-only; log-only runs un-started by design).

---

## 5. FOR THE ENGINE / ORCHESTRATOR SEAT

**No contract change. Contract v1 stays FROZEN and untouched — everything above is consumer-side.**
Five things worth knowing:

1. **The `refused: policy(SESSION/dimension)` token class is now COUNTABLE.** It was **0 across the
   entire soak** (the gate shipped disabled, and that disabled-parity is what the clean join proved).
   The trader has now enabled the policy, so disposition joins from here on will contain
   `refused: policy(LONDON/tier)`-style rows. These are precisely the "engine-actionable, declined by
   my policy" counterfactual the feature exists to produce, joinable on the session named in the
   token. **Buckets are UTC** (`ASIA <08:00`, `LONDON 08:00–12:59`, `NY ≥13:00`) and describe which
   market session *analysed* the signal — deliberately a different clock from the trader's UTC+8
   Inclusion window.
2. **The `rejected:` class is non-zero for the first time.** During the soak it was structurally 0
   (log-only places nothing). Live mode now produces both classes, first instance
   `rejected: cancel pending`. Keep `rejected:` and `refused:` tallied separately, as agreed.
3. **Emission cadence is load-bearing for ARMING, not just for staleness.** The app cannot START at
   all without a recently parsed payload — freshness *and* engine ARM are learned only on
   consumption. An emission stall therefore prevents arming and, if already started, auto-STOPs the
   bridge after 3 × 10 s checks. Design intent, but worth stating plainly: **please keep the run
   interval comfortably inside `2.5 × exec_resolution_min`.** If a future `exec_resolution_min` and
   run interval ever converge, spurious stand-downs become possible — that ratio is the thing to
   watch.
4. **App trades are now joinable to engine signals.** Q2 records `SignalId` + `SignalConfidence` per
   bridge-placed trade in the app's trade DB (and shows them in the View Trades grid). Per-signal P/L
   attribution against the engine matrix is now a query rather than a manual join — a direct enabler
   for the evidence reviews and for **C1 (v2 feedback file)**.
5. **Deribit TESTNET returned `11094 internal_server_error` for roughly a day** (their server-side
   500s on authenticated snapshot queries — position/orders/account) and recovered on 2026-07-25.
   Nothing to do with either app; flagged in case the engine seat saw the same window.

**Asks: none blocking.** The single optional confirmation is item 3's cadence ratio.

## 6. State at close

- `origin/master = 9667dc8`; local `441c8ab` (docs) ahead by 1; tree clean; gate 104/104.
- Dispositions log **1930 rows** (x64 bin). Soak join review stands down per the soak call.
- Payload restored to the engine's own (`restore-payload.ps1`): `signal_id=2`, direction `NONE`,
  instance `72bf33d9…`, `.harness-backup` consumed. Note that backup captured an engine payload from
  ~09:39 today (the engine ran briefly then — the two `NO TRADE [WEAK LONG] | refused: direction`
  rows), not the 07-24 one.
- App up, x64, TESTNET, connected, flat, **disarmed**, notifier configured.
- **Circuit breaker still persisted at `$1`** — a test value from this session's harness runs. It
  must be reset before the size ladder continues (see the task list).
- Testnet rows #90–#93 pollute the live journal; deletable via View Trades right-click.

**Safety boundary held:** this seat launched the app, read logs/DB/grid, ran read-only tools and
config-only edits. It placed **no trades** and never armed the bridge — the owner drove every trade,
every ARM, and every START.
