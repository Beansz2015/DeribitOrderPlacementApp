# Coordinator review — C1: executor feedback emitter

**Reviewed at HEAD `cfa2c61`** (eight implementer commits, `c27951b`..`125f228`).
**Spec:** `spec-c1-feedback-emitter.md` · **Schema:** contract §8 · **Report:**
`impl-report-c1-feedback-emitter.md`.

## VERDICT: code **APPROVED**, with **one defect (D1)** to fix before acceptance 7

D1 is a narrow shutdown-ordering race, one line to fix, and it does not affect anything already
landed. Everything else verifies — including the claim the report itself nominated as the one most
worth re-deriving, which I re-derived by a stricter method than the report used and which **holds**.

**Nothing here was taken on the report's word.** Each item below names what was executed or read.

## 1. Executed

- **`tools/checks/verify-gate.ps1` → `GATE PASSED`, OrderCheck `OK 264/264`.** Run by this seat at
  HEAD, including the four 8b assertions and the repo guards.
- **The nine censuses, re-run at HEAD**, occurrence-counted with `-o`: `emergencyFired` 10 ·
  `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 ·
  `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13
  · `placedOrderSizeUsd` 18 = **68 occurrences / 64 lines. All unchanged.**
- **A negative test on fixture 8/8b, because a fixture that cannot fail proves nothing.** The report
  said 8b was proven failable and the negative test reverted before commit — so that claim arrived
  unverifiable. I re-derived it: removed `"ws"` from `Serialize`'s executor block, ran OrderCheck,
  and the suite went from **264/264 to a non-zero exit**. Reverted immediately; tree clean.
  *Limit: I did not isolate which assertion fired (fixture 8's per-field `ws` case or 8b's domain
  set). Either proves the coverage is real.*

## 2. The nominated claim — RE-DERIVED, and it holds

> *"No hooked method has a `Return` between its first snapshot-field write and its tail hook."*

A unified diff cannot show this and fixture 8 cannot reach it, so it was read.

**Method:** for each of the eight tail-hook methods, walk back to the enclosing declaration, find
the first assignment to any of `positionSizeUSD` / `positionAvgEntry` / `placedStopLossPrice` /
`manualTPval`, then scan every line between that and the hook.

⚠ **My first pass was unsound and I nearly shipped it.** It anchored on `^\s*Return`, which misses
VB's inline `If … Then Return` — the commonest form, and precisely the one the claim is about. The
second pass matched `\bReturn\b` / `\bExit (Sub|Function)\b` anywhere on a non-comment line.

**Result: one candidate**, and it is not a defect —

| Method | Hook | First field write | Verdict |
|---|---|---|---|
| `SyncTradeInputsFromUi` | 532 | 518 | clean |
| `HandleOrderPositionUpdates` | 3605 | 3028 | **1 candidate — adjudicated below** |
| `ExecuteOrderAsync` | 4113 | 4068 | clean |
| `CancelOrderAsync` | 4215 | 4146 | clean |
| `CancelWorkingEntryCoreAsync` | 4285 | 4274 | clean |
| `StopLossForTrailingOrderAsync` | 5031 | 5001 | clean |
| `HandleOpenOrdersSnapshot` | 5787 | 5740 | clean |
| `ProcessPositionData` | 5901 | 5806 | clean |

**Line 3327, `If cancelPending Then Return`, sits inside `Me.Invoke(Sub() … )`** — it returns from
the lambda, not from `HandleOrderPositionUpdates`, so the method still reaches its tail hook. The
claim stands. *(`HandleBalanceUpdates`'s hook at 2143 writes none of the four fields — it is the
breaker half of trigger (c), as the report states.)*

## 3. Verified by reading

- **No control reads in the snapshot path.** `CaptureFeedbackSnapshot` (`:740`–`:757`) reads one
  `SignalBridge` reference, five of its properties, `IsWebSocketConnected`, and the four backing
  fields. No `.Text`, no control property. The spec's review-blocking absolute holds.
- **`SameContent` swallows nothing.** It compares all **12** `FeedbackSnapshot` members, and
  `SameLastSignal` all **4** of `LastSignalRef`, with `a Is b` correctly treating null-vs-populated
  as a change. This is the component most able to hide a field, and it is complete.
- **E2 honoured:** `ModeWire` is an explicit `Select Case` over the three pinned literals, with
  `Case Else → OFF` — the conservative arm, so an unmapped enum can never read as `LIVE`.
- **E3 structural:** `BuildSnapshot` derives `Direction` and `SizeUsd` from the one signed value, so
  §8.4's sign/direction consistency cannot be violated by care.
- **The flat trap (§3.2):** `BuildSnapshot` gates on `rawSizeUsd = 0D` and emits explicit zeros for
  direction, size, avg entry **and both working levels**. The retention at the position model is
  untouched — read around, not "fixed".
- **§4(d) honoured:** `OnHeartbeat` re-offers `_lastPublished` and never captures live state, with
  the reasoning in a comment at the site.
- **E1 / atomic write:** `WriteAtomic` is `WriteAllText(tmp)` + `File.Move(overwrite:=True)`, the
  house pattern; no `File.Replace` anywhere in this repo (H-6 §6.10).
- **The three E6 hooks are three lines**, all the same call. (f) is in `frmMainPageV2_Shown`
  (`:1047`), at `:1088`, before `StartHeartbeat` at `:1093`. The `ws` DOWN edge is at `:1523`, after
  `End While` and **above** `If reconnectNeeded`, so it covers the server-close `Exit While`, all
  three exception arms, and the loop condition.

## 4. 🚨 D1 — the only defect: a late worker write can overwrite the final write

**`DrainQueue` takes its snapshot and id under `_gate`, then acquires `_writeGate` separately.
Nothing re-checks `_disposed` in between, and `_outputPath` is never cleared, so a worker
pre-empted in that window can write AFTER the graceful-close write, with an OLDER snapshot and a
LOWER `feedback_id`.**

Sequence (both threads, no exotic timing beyond ordinary pre-emption):

1. Worker **W**: `SyncLock _gate` → `_disposed` False → `TryTake` succeeds → `_feedbackId` → 5 →
   releases `_gate`. **Pre-empted before `WriteAtomic`.**
2. UI **U**: `ShutdownWithFinalWrite` → `SyncLock _gate` → latches `_disposed = True`, clears the
   queue, `_feedbackId` → 6 → releases `_gate` → `WriteAtomic` → takes `_writeGate` → **writes
   `feedback_id 6`, the final snapshot.**
3. **W** resumes → `WriteAtomic` → takes `_writeGate` → **writes `feedback_id 5`, the pre-shutdown
   snapshot, on top of the final.**

**Why it matters beyond tidiness:**
- The on-disk `feedback_id` goes **backwards** (6 then 5). §8.4 requires it monotonic; gaps are
  legal and "never inferred from", but regression is not a gap.
- **It defeats exactly what trigger (e) exists for.** The final write is meant to be the executor's
  last *true* state — that is what makes §8.1's *"silence = dead executor"* honest. Here the last
  state on disk is the one from before shutdown.
- **Acceptance 7.1 would fail intermittently**, and its failure would read as a mystery rather than
  as this.

**Severity: LOW–MEDIUM.** Narrow window; graceful close only; the engine marks the file stale within
35 s regardless, and phase-1 consumption records nothing (E4). But it is cheap to fix and it breaks
a stated guarantee.

**Recommended fix — one parameter, no re-spec.** Give `WriteAtomic` an `isFinal As Boolean`; on the
non-final path, after acquiring `_writeGate`, re-check `_disposed` under `_gate` and return if set.
Shutdown passes `True` and bypasses. *(Do not instead widen `_writeGate` around the shutdown
`_gate` section — that changes lock ordering for no gain.)*

**This is the class the report predicted:** its §3.3 says the single-writer discipline and the
`_gate`/`_writeGate` ordering are *"argued from the code, not measured"*, and no test drove
`Publish` from two threads. That was an accurate self-assessment, and this is what was in the gap.

## 5. Residuals — accepted, not defects

1. **`_lastPublished` is set before the write succeeds.** A failed `WriteAtomic` leaves the gate
   believing that content was published. Self-heals: the heartbeat re-offers `_lastPublished` every
   10 s, so a transient lock costs one interval. Correct as written.
2. **An exception between a field write and a tail hook skips that publish.** The next echo
   publishes, so it is transient rather than permanent — but under §4(d) it is worth knowing that
   the healing comes from the next *trigger*, not from the heartbeat.
3. **Fixture 8b pins the schema, not the call sites** — it would catch a new field with no
   assertion; it cannot catch a hook deleted from a handler. Unchanged limit, correctly stated in
   both the report and spec §5. Call-site completeness rests on §2 above.
4. **`SignalBridge.EmitDisposition` allocates one `LastSignalRef` per consumed payload regardless of
   configuration.** Flagged by the report for a ruling rather than left to be discovered.
   **Ruled: keep it.** Gating it would put a second state machine over the same fact to save one
   small allocation per payload, and per-payload is not a hot path.

## 6. Not proven by this review

Nothing here turns a runtime acceptance green. Acceptances **3–7 remain unrun and owner-driven**
(run sheet: impl report §7). In particular:

- **The `ws` transition has never been observed firing**, and **(f) has never been observed writing
  a file** — both are argued from code only.
- **Acceptance 4, the flat trap, is the one that matters** and needs a real open→close cycle;
  fixture 2 proves the mapping, not that the running app feeds it.
- **No concurrency was exercised**, by the seat or by this review. D1 was found by reading, and its
  fix will be too — neither is measured.

## 7. Disposition

- **Code APPROVED.** Eight commits stand; no rework.
- **D1 to be fixed in one follow-up commit** before acceptance 7 is attempted, so a 7.1 failure
  cannot be ambiguous.
- **Then the owner's acceptances 3–7.**
- **E4's revisit trigger remains binding and untouched** — re-open before any consumer *records* the
  `avg_entry` join rather than re-deriving it.
