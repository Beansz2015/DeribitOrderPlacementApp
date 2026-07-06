# Spec-back — Triggered-SL reconciliation with manual exchange-side edits (as-built)

**What this is:** a specification reconstructed **from the committed code**, not a forward spec. It states the behavior contract the code now enforces (with anchors) so a reviewer can diff it against the commit. Written 2026-07-04. Base `2381766` (code) / `17fffee` (HEAD). File: `DeribitOrderPlacementApp/frmMainPageV2.vb`. Implements the APPROVED `docs/spec-reconcile-manual-sl-edits.md` — discriminator **4a**, policy **P1**, TP **deferred**, log-on-detect. Closes residual #1 of `spec-back-msl-emergency-baseline.md`.

**Origin (owner design, runtime-confirmed defect):** after the SL triggers, a manual SL move on the Deribit chart was not reflected in `placedStopLossPrice` (seed-if-zero), so the maker chase ran off a stale reference and — proven at runtime 2026-07-04 — never fired, forcing the taker market emergency. `emergencyBaseline` already followed the live SL (ungated echo), so the two references diverged. Fix: make `placedStopLossPrice` follow the true live SL, distinguishing a manual edit from a lagging echo of the app's own reposition via a **commanded-price set**, and keep chasing from the corrected reference (P1).

---

## The discriminator — commanded-price set

The app knows every SL price it commands. A short set of recently-commanded prices tells a **manual edit** apart from a **lagging echo of the app's own reposition** — both arrive as open `StopLossOrder` echoes with a `price`.

| Element | Anchor | Contract |
|---|---|---|
| `commandedSLPrices` + `commandedSLLock` | `~:64` | The set: `List(Of CommandedSLEntry)`, all access under the lock. Written on the receive thread (each SL-edit send), read on the UI thread (the open echo). |
| `CommandedSLEntry` | `~:69` | `{ Price As Decimal, Stamp As DateTime }`. |
| `CommandedSLWindowMs = 2000.0` | `~:66` | Retention window. Repositions are ≥333 ms apart; echoes return well under that, so the set holds a handful of entries; expired ones are purged on every access. |
| `CommandedSLMatchTol = 0.25D` | `~:67` | Half a tick (BTC-PERPETUAL tick = 0.5). A commanded/echo match within tolerance absorbs rounding without swallowing a real ≥1-tick manual move. |
| `RecordCommandedSLPrice` / `IsRecentlyCommandedSLPrice` / `ResetCommandedSLPrices` / `PurgeCommandedSLPrices` | from `~:3738` | Add / test / clear / age-evict. Purge runs at the head of record and test. |

**Contract:** a price is "recently commanded" iff some unexpired entry is within `CommandedSLMatchTol`. The set is the app's memory of what it sent; anything else on the SL is the exchange's (manual).

---

## Region 1 — recording every commanded price (the single choke point)

- `UpdateStopLossForTriggeredStopLossOrder` (`~:3333`, right after the `private/edit` send): `RecordCommandedSLPrice(newPrice)`.
- This is the **single send point for every triggered-SL edit**: the normal chase (`~:1645`), the ½-M.SL force chase (`~:1643`), and the full-M.SL emergency (`~:1615`) all route through it (the last two via `ForceStopLossUpdate → UpdateStopLossForTriggeredStopLossOrder`). One call therefore covers all paths.
- **Ordering:** the echo of an edit can only arrive after its send completes, so the record is always in place before the echo is discriminated.
- **Emergency exception is a non-exception:** when the full-M.SL emergency fires, `UpdateStopLossForTriggeredStopLossOrder` takes its market-reduce branch and `Return`s **before** the send — no SL edit, no echo, nothing to record. The position is closing. So there is no path that sends an SL edit without recording it.

---

## Region 2 — the reconciliation block (open `StopLossOrder` echo, UI thread)

Anchor `~:2109–2134`, inside the open-echo `Case "StopLossOrder"`, under the existing `Not cancelPending` gate (scoped/nuclear cancel semantics, invariant #3). Replaced the old seed-if-zero block. In order:

1. **Emergency-baseline trigger seed** — `If emergencyBaseline = 0D Then emergencyBaseline = If(price, emergencyBaseline)`. `emergencyBaseline` starts 0 at trigger while `placedStopLossPrice` carried over non-zero from the untriggered leg, so it needs its own seed.
2. **`If placedStopLossPrice = 0D`** — trigger-moment single-writer seed for the price (mirror + engine), unchanged semantics.
3. **`ElseIf price.HasValue AndAlso price.Value <> placedStopLossPrice AndAlso Not IsRecentlyCommandedSLPrice(price.Value)`** — the **manual-edit** discriminator. A price that **differs** from our reference **and** was **not commanded** by us. Action (P1): `placedStopLossPrice = price`, `emergencyBaseline = price`, mirror `txtPlacedStopLossPrice`, and `AppendColoredText(txtLogs, $"Manual SL edit: ${price:F2}", Color.Cyan)`.
4. **else** — in the commanded set, or unchanged: the app's own reposition or a lagging echo → **ignore** (both references were already advanced at the reposition, Region 3).

**Two-part discriminator, by design:**
- `price <> placedStopLossPrice` — a manual edit changes the price. Suppresses the trigger-moment echo (equal to the carried-over price) and any repeated identical echo → **no spurious "Manual SL edit"** and idempotent on re-broadcast.
- `Not IsRecentlyCommandedSLPrice(price)` — a lagging/out-of-order echo of the app's own reposition carries a price we commanded → ignored → **no backward walk** (the runaway/transition-race protection the old seed-if-zero provided).

Both are required: the first alone would let a lagging own-echo reset the reference backward; the second alone would fire a spurious "manual" at the trigger moment (empty set).

---

## Region 3 — `emergencyBaseline` moved under the same discriminator

- **Removed** the ungated `emergencyBaseline = If(price, emergencyBaseline)` at the old `:2062` (it updated on *every* echo, including lagging/out-of-order own-echoes → could walk the baseline backward).
- `emergencyBaseline` now changes in lockstep with `placedStopLossPrice`, at exactly three places:
  - **Trigger seed** — Region 2 step 1.
  - **App's own chase** — `emergencyBaseline = newStopPrice` at the reposition (`~:1653`), added next to the existing `placedStopLossPrice = newStopPrice` (`~:1652`). The emergency follows the app's own chase synchronously, no longer via the echo.
  - **Manual edit** — Region 2 step 3.
- **Net:** `emergencyBaseline` still follows the live SL (owner-confirmed behavior, `f42a6a7`), but without the race-exposed ungated write. The two references stay equal post-trigger.

---

## Region 4 — reset sites (7, mirroring `emergencyBaseline = 0`)

`ResetCommandedSLPrices()` is called at every site that resets `emergencyBaseline` to 0 — the canonical "SL context reset" anchors:

| Site | Why |
|---|---|
| SL placement ×4 (limit OTOCO ×2, trailing/edit ×2) | new SL is pre-trigger — commanded history is stale |
| Nuclear cancel (`CancelOrderAsync`) | context torn down |
| Market-reduce (`isMarketOrder`) | position closing by market |
| `CompletePositionClose` | position flat |

A new trade cannot inherit a stale commanded price. (Scoped cancel — `CancelWorkingEntryCoreAsync` — deliberately touches none of these, invariant #3.)

---

## Behavior contract / invariants (post-change)

1. **Manual edit is followed:** post-trigger, an open SL echo whose price differs from `placedStopLossPrice` and was not commanded by the app updates `placedStopLossPrice`, `emergencyBaseline`, the display, and logs `Manual SL edit: $X` (cyan). The chase then runs off the corrected reference (P1 — no suspension).
2. **App's own echo is ignored:** an echo whose price is in the commanded set (or equals the current reference) does not move `placedStopLossPrice`/`emergencyBaseline` — no backward blip. Runaway/transition-race protection preserved.
3. **No spurious detection:** the trigger-moment echo and repeated identical echoes are no-ops (`price <> placedStopLossPrice` guard). No "Manual SL edit" line unless the SL actually moved to a value the app didn't send.
4. **References stay equal:** `placedStopLossPrice` and `emergencyBaseline` are written together everywhere post-trigger (reposition, manual, seed), so the emergency always measures from the live SL.
5. **Recorded before echoed:** every commanded SL price is recorded at the send, before its echo can be discriminated. The emergency path market-reduces (no SL edit) rather than editing, so it needs no record.
6. **Clean per-trade:** the commanded set is cleared at all 7 SL-context resets and self-expires after ~2s.
7. **Pre-trigger unchanged:** the untriggered `StopLossOrder` echo (unconditional `placedStopLossPrice`/display mirror + `StopLossTriggerOriginal` sync) is untouched.
8. **Threading:** the set is lock-guarded (receive-thread writes, UI-thread reads); no `Await` inside any `SyncLock`. `placedStopLossPrice`'s cross-thread Decimal read is unchanged (accepted torn-read class, invariant #6).

---

## Residuals / known-theoretical

1. **Coincidental match (accepted, spec §4a):** a manual move within half a tick of a price the app commanded in the last ~2s is missed (falls back to today's behavior). Rare; self-corrects on the next non-matching echo or reposition.
2. **Take-Profit parallel — deferred (spec §7, decision 3):** a post-fill manual TP move is still not reflected (the TP open echo sets only `PositionTPOrderId`; no price update, no chase, no emergency). Same class of gap, display-only. Flagged for a follow-up change.
3. **Rapid app repositions vs a simultaneous manual move:** last write wins and converges; not separately arbitrated (accepted, spec §8).
4. **Reconnect / id-778 restore into a triggered SL:** the commanded set is empty; the first post-restore echo equals the restored `placedStopLossPrice` ⇒ no detection. If the SL was moved *while disconnected*, the first echo differs ⇒ followed as a manual edit (correct). No special-casing.

---

## Owner runtime tests to run (spec §9)

1. **Manual SL move reflected:** trigger, then drag the SL on the chart → the "Stop Loss" box and engine value update; `Manual SL edit: $X` (cyan) logs; the M.SL emergency arms at `newSL − M.SL`.
2. **App doesn't fight its own echoes:** let the app chase with no manual touch → no "Manual SL edit" lines, `SL repositioned` behaves as today, no backward blips.
3. **P1 policy:** after a manual move, the app re-chases from the corrected reference toward the market (maker fill).
4. **Stale/out-of-order:** under a brief reconnect, a late echo doesn't reset the SL backward.
5. **Regression:** full trigger → chase → fill; restart-into-triggered restore; flat/new-trade start (commanded set clean).

Build 0/0. Not pushed — owner is the only pusher, after these pass.

---

## Amendment 1 — trigger-flip is adopt-only, not discriminated (2026-07-06)

Runtime test surfaced a false-positive `Manual SL edit` when the owner moved the SL **trigger** (not the limit) before it triggered: the discriminator ran on the trigger-flip echo, where the flip `price` differed from the stale pre-move `placedStopLossPrice` and wasn't commanded, so the *transition* was misread as a manual edit.

**Contract change (supersedes Region 2 steps 1–2):** the open-echo handler captures `wasTriggered = SLTriggered` **before** flipping `SLTriggered = True`. Then:

- **`Not wasTriggered OrElse emergencyBaseline = 0D`** ⇒ **adopt**: set `placedStopLossPrice` + `emergencyBaseline` + display to the exchange's authoritative triggered `price`, **silently** (no discriminator, no log). This is the one-time trigger flip (or a not-yet-adopted state, e.g. a null-price flip). It correctly handles a **trigger-only move** — the flip price may differ from the last untriggered mirror, and that is not a manual edit. The `emergencyBaseline = 0D` arm also guarantees the emergency baseline is seeded (a 0 baseline disables the M.SL emergency).
- **else (already triggered)** ⇒ the manual-edit discriminator (`price <> placedStopLossPrice AND not-commanded`) and the app's-own-echo ignore, as before. Post-trigger the trigger no longer exists, so a discriminated change can only be a **limit** move (manual) or the app's own chase — exactly the cases the discriminator is for.

Net: manual-edit detection is now scoped to **post-flip** echoes. Invariant 3 ("no spurious detection") is restored — the trigger flip and a trigger-only move no longer log `Manual SL edit`. The seed-if-zero pair that used to live inside the block is replaced by the adopt branch.

Build 0/0. Not pushed.
