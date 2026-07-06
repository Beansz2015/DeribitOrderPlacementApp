# Implementation report — spec-reconcile-manual-sl-edits.md (4a + P1)

**Date:** 2026-07-04
**Implementer:** Opus 4.8 (high)
**Base:** `17fffee` on `master` (code base = `2381766`; `17fffee` added only the handover/spec-back docs).
**Commit:** 1 code commit (this report + the spec-back committed alongside). Build `dotnet build DeribitOrderPlacementApp.sln -c Debug` = **0 errors / 0 warnings** (before and after). Committed locally, **not pushed** (owner is the only pusher; runtime test pending).
**Spec honored:** `docs/spec-reconcile-manual-sl-edits.md` — APPROVED, decisions locked: discriminator **4a (commanded-price set)**, policy **P1 (keep chasing from the corrected reference)**, TP **deferred**, **log** the detected edit (cyan). Implemented exactly to those; nothing re-opened.

All code in `DeribitOrderPlacementApp/frmMainPageV2.vb`. Diff: **120 insertions, 13 deletions** (mostly comments).

---

## The bug (recap)

After the SL triggers, `placedStopLossPrice` was **seed-if-zero** from the open echo, so a **manual exchange-side SL move** (which arrives as an open `StopLossOrder` echo with the new `price`) was dropped. The chase (`bestAsk/bestBid` vs `placedStopLossPrice ± $5`) then ran off the stale placement value; runtime-confirmed to leave `shouldUpdate = False` on every tick so the maker chase never fired and the **taker market emergency fired instead**. `emergencyBaseline` already followed the live SL (via an ungated echo update) — so the two references diverged. This closes residual #1 of `spec-back-msl-emergency-baseline.md`.

## The fix — commanded-price discriminator (4a)

The crux is telling a **manual edit** apart from a **lagging echo of the app's own reposition** — both arrive as open `StopLossOrder` echoes with a `price`. The app now remembers every SL price it commands; an echo price it did **not** command (and that differs from the current reference) is a manual edit.

### New state (near `:53`, after the `emergencyBaseline` field)

- `commandedSLPrices : List(Of CommandedSLEntry)` + `commandedSLLock` — the commanded-price set, all access under the lock (written on the receive thread, read on the UI thread).
- `CommandedSLEntry` — `{ Price As Decimal, Stamp As DateTime }` (private structure).
- `CommandedSLWindowMs = 2000.0` — keep commanded prices ~2s. Repositions are ≥333 ms apart (`MinStopLossUpdateInterval`) and echoes return well under that, so the set holds a handful of entries; expired entries are purged on every access.
- `CommandedSLMatchTol = 0.25D` — half a tick (BTC-PERPETUAL tick = 0.5). Absorbs any echo rounding without swallowing a real ≥1-tick manual move.

### Helpers (from `:3738`, right after `ForceStopLossUpdate`)

- `RecordCommandedSLPrice(price)` — purge-expired, add, size-backstop (32). 
- `IsRecentlyCommandedSLPrice(price) As Boolean` — purge-expired, then any entry within `CommandedSLMatchTol`.
- `ResetCommandedSLPrices()` — clear (SL-context reset).
- `PurgeCommandedSLPrices(nowUtc)` — drop entries older than the window; caller holds the lock.

### Recording — the single choke point (`:3333`)

`RecordCommandedSLPrice(newPrice)` is called **inside `UpdateStopLossForTriggeredStopLossOrder`**, right after the `private/edit` send. That function is the **single send point for every triggered-SL edit** — the normal chase (`:1645` direct) and the emergency `ForceStopLossUpdate` (`:1643`/`:1615`) both route through it — so one call covers all paths. The echo can only arrive after the send completes, so the record is always in place before it. (Recording only at `:1652` would have **missed** the emergency-force price and misread its echo as manual — see "Why the choke point" below.)

### The reconciliation block (`:2109–2134`, open `StopLossOrder` echo, UI thread)

Replaced the old seed-if-zero block. Under the existing `Not cancelPending` gate:

1. **Seed emergency baseline at the trigger moment** — `If emergencyBaseline = 0D Then emergencyBaseline = If(price, emergencyBaseline)`. It starts 0 while `placedStopLossPrice` carries over non-zero from the untriggered leg (same order, same limit price), so `placedStopLossPrice`'s own seed won't fire at trigger — the baseline needs its own seed.
2. **`If placedStopLossPrice = 0D`** — trigger-moment seed for the price (unchanged single-writer semantics; defensive).
3. **`ElseIf price.HasValue AndAlso price.Value <> placedStopLossPrice AndAlso Not IsRecentlyCommandedSLPrice(price.Value)`** — a price that **differs** from our reference and that we **didn't command** = a **manual edit**. P1: set `placedStopLossPrice`, `emergencyBaseline`, `txtPlacedStopLossPrice` to the true value and log `Manual SL edit: $X` (cyan). The chase then runs off the corrected reference — no policy code beyond making the reference correct.
4. **else** (in the set, or unchanged) — the app's own reposition or a lagging echo → **ignore** (preserves the runaway/transition-race protection).

### `emergencyBaseline` brought under the same discriminator

- Removed the **ungated** `emergencyBaseline = If(price, emergencyBaseline)` at the old `:2062` — it swallowed lagging/out-of-order echoes of the app's own reposition and could walk the baseline backward.
- `emergencyBaseline` now moves with `placedStopLossPrice`: **seeded** at the trigger moment (step 1), **advanced with the app's own chase** at the reposition (`emergencyBaseline = newStopPrice`, `:1653`), and **set to the true value on a manual edit** (step 3). Net: it still follows the live SL (owner-confirmed behavior preserved) but no longer via a race-exposed ungated write.

### Reset sites (7, mirroring `emergencyBaseline = 0`)

`ResetCommandedSLPrices()` added at all seven SL-context resets: the **4 SL-placement** paths, **nuclear cancel** (`CancelOrderAsync`), **market-reduce** (`isMarketOrder`), and **`CompletePositionClose`**. A new trade cannot inherit a stale commanded price.

---

## Why the choke point (not `:1652`)

| Path | Sends SL edit? | Advances `placedStopLossPrice`/`emergencyBaseline`? | Recorded? |
|---|---|---|---|
| Chase, normal (`:1645`) | yes (`newStopPrice`) | yes, at `:1652–1653` | ✅ in the function |
| Chase, ½-M.SL force (`:1643`) | yes (`newStopPrice`) | yes, at `:1652–1653` | ✅ in the function |
| Full-M.SL emergency (`:1615`) | **no** — routes to the market-reduce + `Return` (position closes) | n/a (position closing) | n/a (no SL edit → no echo) |

Recording in the function captures the two chase paths; the emergency path closes the position rather than editing the SL (verified: the emergency market-reduce condition that fires at `:1615` is exactly the one re-checked at the top of `UpdateStopLossForTriggeredStopLossOrder`, which then market-reduces and returns before the send). So there is **no** path that sends an SL edit without either recording it or advancing the references — no stale-reference window.

---

## Idempotence & edge cases (spec §8)

- **Trigger moment / repeated identical echoes:** `price == placedStopLossPrice` ⇒ the manual `ElseIf` is skipped (nothing changed) ⇒ **no spurious "Manual SL edit"** log and no reset. The exchange re-broadcasting the open SL order is a no-op.
- **App's own reposition echo (in-order or lagging):** in the commanded set ⇒ ignored. Even if it aged out, the `price <> placedStopLossPrice` guard catches the in-order case (already advanced at `:1652`). Runaway protection intact.
- **Reconnect / id-778 restore into a triggered SL:** commanded set is empty; the first post-restore echo equals the restored `placedStopLossPrice` ⇒ `price <> placedStopLossPrice` false ⇒ no spurious detection. If the user moved the SL *while disconnected*, the first echo differs ⇒ correctly followed as a manual edit. No special-casing needed.
- **`cancelPending`:** whole block gated on `Not cancelPending` (scoped/nuclear cancel semantics, invariant #3) — unchanged.
- **Rapid app repositions vs a simultaneous manual move:** last write wins and converges (accepted per spec §8).
- **Threading:** the set is lock-guarded (receive-thread writes, UI-thread reads); `placedStopLossPrice`'s cross-thread Decimal read is unchanged (the accepted torn-read class). No `Await` inside any `SyncLock`.
- **Null `price`:** `price.HasValue` short-circuits both the `<>` and the set check; `If(price, …)` fallbacks unchanged.

---

## Pre-trigger unchanged

The untriggered `StopLossOrder` echo (its unconditional `placedStopLossPrice`/`txtPlacedStopLossPrice` mirror + `StopLossTriggerOriginal` sync) is **untouched** — requirement 5. Only the triggered/open echo changed.

---

## Deviations / residuals

1. **Coincidental match (accepted, spec §4a):** a manual move that lands within half a tick of a price the app commanded in the last ~2s falls back to today's behavior (missed). Rare and self-corrects on the next non-matching echo or reposition.
2. **Take-Profit parallel — deferred (spec §7 / decision 3):** a post-fill manual TP move is still not reflected (the TP open echo sets only `PositionTPOrderId`). Same class of gap, display-only (no chase, no emergency). Flagged for a follow-up.
3. **DIAG not re-added.** The temporary `[DIAG]` from `04d708a` was optional ("if you want to confirm during your own reasoning"). The paths were verified by reasoning against the code; the owner can re-add it from `04d708a` to observe the chase/manual decision at runtime, then revert.

---

## Acceptance summary (implementer-verifiable)

| Assertion | Result |
|---|---|
| Build 0/0 (before + after) | ✅ |
| 7 `ResetCommandedSLPrices()` sites = the 7 `emergencyBaseline = 0` anchors | ✅ grep-verified |
| Commanded price recorded at the single SL-edit send point | ✅ `:3333` |
| Manual edit ⇒ `placedStopLossPrice` + `emergencyBaseline` + display + cyan log | ✅ `:2121–2129` |
| App's own / lagging echo ⇒ ignored (no backward blip) | ✅ commanded-set + `price <> placedStopLossPrice` guards |
| No spurious "Manual SL edit" at trigger / on repeat echoes | ✅ `price <> placedStopLossPrice` guard |
| Ungated `emergencyBaseline` echo write removed; follows via discriminator | ✅ old `:2062` gone |
| Pre-trigger echo untouched | ✅ |

**Owner runtime tests remain to be run** on the test sub-account — spec §9 (manual SL move reflected; app doesn't fight its own echoes; P1 re-chase after a manual move; stale/out-of-order under reconnect; full trigger→chase→fill regression; restart-into-triggered). Do **not** push until those pass.

---

## Amendment 1 — trigger-flip false positive (2026-07-06 runtime test)

**Symptom:** owner entered a long, moved the SL **trigger** once (not the limit), and on trigger the log showed a spurious `Manual SL edit: $61510.00` — where 61510 *was* the SL price. Close was correct; the log was wrong.

**Root cause:** the discriminator ran on the **trigger-flip echo** (untriggered → triggered). Moving the trigger left `placedStopLossPrice` holding a pre-move value ≠ the flip price, and the flip price wasn't in the commanded set, so `price <> placedStopLossPrice AND not-commanded` misread the *transition* as a manual edit. The original `price <> placedStopLossPrice` guard was **not** sufficient at the flip (contra the acceptance row above — corrected here): it only suppresses the flip when the pre-trigger mirror already equals the flip price, which a trigger move breaks.

**Fix:** capture `wasTriggered = SLTriggered` **before** setting `SLTriggered = True`. In the reconciliation block, `If Not wasTriggered OrElse emergencyBaseline = 0D` ⇒ this is the trigger flip (or the reference isn't adopted yet, e.g. a null-price flip) ⇒ **adopt** `price` into `placedStopLossPrice` + `emergencyBaseline` + display **silently** (no discriminator, no log). The manual-edit `ElseIf` now runs **only** on echoes that arrive while *already* triggered — which post-trigger can only be a limit move (the trigger no longer exists once triggered) or the app's own chase. The `emergencyBaseline = 0D` half also guarantees the emergency baseline is seeded even if the flip echo carried a null price (a 0 baseline disables the M.SL emergency). This replaces the old seed-if-zero pair inside the block.

Build 0/0. Still not pushed.
