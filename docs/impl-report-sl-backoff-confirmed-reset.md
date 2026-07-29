# Implementation report — SL-backoff confirmed reset (N1c)

**Spec:** `docs/spec-sl-backoff-confirmed-reset.md` (owner-ticked 2026-07-30).
**Commits:** `a960f07` (commit 1 — the reset move) · `9d3b9f5` (commit 2 — log honesty + the
3b(ii) diagnostic) · this report.
**Gate:** executed at each commit and at final HEAD — **GATE PASSED, OrderCheck 146 → 153.**
**No trades, no bridge arm, nothing pushed.**

## 1. What changed

### Commit 1 — the reset moves to the confirmed-success signal

`slUpdateFailures = 0` is **gone from the chase `Try`** (was `frmMainPageV2.vb:2401`, in
`HandleQuoteUpdates`'s triggered-SL reposition block). It now lives at **`:3044`**, inside the
`open` / `StopLossOrder` echo branch of `HandleOrderPositionUpdates`:

```vb
If price.HasValue AndAlso IsRecentlyCommandedSLPrice(price.Value) Then
    slUpdateFailures = 0   ' confirmed success clears the backoff
End If
```

It sits inside the existing `If Not cancelPending Then` gate, immediately after the reconcile-4a
discriminator's `If … ElseIf …` chain, and **the chain itself is untouched**.

**Why a positive test rather than the chain's else-arm.** The else-arm is the branch the spec
names, but it is *not* how the ordinary success gets there. The chase advances
`placedStopLossPrice = newStopPrice` optimistically at the send, so the confirming echo arrives
with `price.Value = placedStopLossPrice`; the `ElseIf`'s second conjunct short-circuits and
`IsRecentlyCommandedSLPrice` is **never consulted** on the common success path. Hanging the reset
on the else-arm would therefore also have fired on "unchanged price for a reason that isn't our
edit". An explicit positive test of the same commanded set is the narrower reading of "a
recognized commanded-price `open` echo IS the confirmation", and it leaves the discriminator's own
evaluation byte-identical (the spec's "do not widen the discriminator itself").

The extra `IsRecentlyCommandedSLPrice` call is behaviour-neutral for the discriminator: it takes
`commandedSLLock` and purges expired entries, which every `Record`/`IsRecently` call already does
before it reads, and it runs *after* the chain has finished.

### Which of the three coupled ids gain a confirmed reset

Verified at the senders at HEAD:

| id | sender | routes through the commanded set? | confirmed reset |
|---|---|---|---|
| **223350** | `UpdateStopLossForTriggeredStopLossOrder` (`:4443`) — the triggered-SL chase, and the emergency `ForceStopLossUpdate` which awaits the same function | **yes** — this is the single 4a send point | **yes** |
| 223346 | the trigger-branch edit at `:4299` (pre-fill secondary SL) and `EditStopLossTo` at `:6149` (manual SL button) | no — `EditStopLossTo` is *forbidden* from recording (its echo must read as a manual edit) | no |
| 223348 | `UpdateStopLossForTrailingOrder` (`:4541`) — the trailing SL leg | no | no |

So **the chase is the one coupled id that gains a confirmed reset**, which is what the spec
requires. 223346/223348 keep exactly the shared counter they had before this pass — they had no
reset of their own before N1c and they have none now; the chase's confirmation clears the shared
counter for all three, as it did before the move. The discriminator was not widened to cover them.

The `open`/`StopLossOrder` echo branch I edited is the only confirmation site: the other four
`Case "StopLossOrder"` blocks are the `untriggered` state (`:3145`, `:3215`), `cancelled`
(`:3278`) and `HandleOpenOrdersSnapshot` (`:5475`).

### Do-not-touch items — verified untouched

- **`lastStopLossUpdate = currentTime` stays on the attempt** (`:2425`), with a comment stating it
  is the anti-duplicate throttle stamp, not a success signal.
- **`placedStopLossPrice = newStopPrice` stays optimistic** (`:2406`) — chase reference,
  divergence-on-reject accepted and now documented at the new reset site.
- The emergency block, `emergencyFired`, the N1b coupling condition and the benign-race `Return`s
  are all unchanged (`git diff -w` shows no non-comment line in any of them).

### Comment corrections carried in commit 1 (judgment call — flagged)

Two comment blocks that N1b commit 1 *wrote* assert a fact that **this commit falsifies**: the
N1-hoist note (`:2303`ff) and the item-16 throttle-gate note (`:2340`ff) both say the failure
counter stays at zero so the gate is "a flat 333 ms". That was true pre-N1b and was still
effectively true post-N1b because of the defect; after this commit a persistently failing chase
really does walk 666 ms → 5 s. I corrected both in the same commit that makes them false, kept the
edits surgical, and re-ran every census afterwards (the standing prose-near-tripwire lesson). The
N1b coupling comment's pointer to the reset site (`:1651`) was repointed at the same time; the
searchable marker is still the substring `success clears the backoff`.

### Commit 2 — log honesty + the coupling diagnostic (3b(ii))

- The chase's completion log is now **`SL reposition sent: $X → $Y`** (`:2432`).
- One gray line after the coupling call (`:1660`–`:1668`):
  `SL-edit failure #3 - backoff 2.7s`.

The diagnostic's delay is **read back out of the stamp `BackoffStopLossRetry` just wrote**
(`(lastStopLossUpdate - failedAt).TotalMilliseconds + MinStopLossUpdateInterval`) rather than
re-derived from the formula, so it cannot drift from the gate it describes. This is the same
recovery the OrderCheck fixtures use. `BackoffStopLossRetry`'s signature is unchanged — the only
edit at the site is capturing `DateTime.UtcNow` into a local so the stamp can be inverted.
`AppendColoredText` only, no UI touch: receive-thread-safe like the red API ERROR line directly
above it.

## 2. IL surface

Commit 1, app file: **one removed line and three added lines**; everything else in the diff is
comments (`git diff -U0 … | grep -v "^[+-]\s*'"` shows exactly those four).
Commit 2, app file: the reworded log string plus the five diagnostic lines. No other file in the
app changed in either commit.

## 3. Fixtures (OrderCheck 146 → 153)

Per the spec, the 3b(i) group **extended**; nothing was rewritten.

- The oscillation group's three checks keep their assertions and **flip role**: they now guard
  against the defect's return ("a per-attempt reset never exceeds 1 failure / 666 ms") instead of
  describing current behaviour.
- The no-reset group's comment now says that sequence *is* the chase between confirmations.
- **7 new checks** model the post-N1c state machine — the counter's only two signals are a red
  rejection (`NextSlBackoff`) and a confirmed echo (clear):
  1. the chase **alone** escalates 666 → 1332 → 2664 → 5000;
  2. it reaches 2 failures, so `SL update rate limited` is reachable **from the chase path** — the
     observable the corrected acceptance 2 needs;
  3. a confirmed echo clears the counter;
  4. the next failure restarts at 666, not at the cap;
  5. the accepted residual: an un-reset counter starts the next failure escalated (2664);
  6. and one confirmation heals it back to the 666 ms first step;
  7. the one-line delta: same four rejections, 666 ms under the old per-attempt reset vs the 5 s
     cap now.

## 4. Censuses (acceptance 2) — run at final HEAD, not assumed

| token | count | composition |
|---|---|---|
| `slUpdateFailures = 0` | **1** | the new reset at `:3044`; **0 at the chase `Try`** |
| `BackoffStopLossRetry` | 7 raw | 1 decl + 3 comments + **3 call sites** (`:1660`, `:2439`, `:4456`) — unchanged |
| `emergencyFired` | 10 raw | 1+2+3+3 code + 1 comment — unchanged |
| `IsATRSlippageExcessive` | 8 | unchanged |
| `RecordCommandedSLPrice` | 3 | unchanged (new prose deliberately avoids the token) |
| `isSLRepositioning` | 3 | unchanged |
| `NextSlBackoff` | 2 | unchanged |
| `IsRecentlyCommandedSLPrice` | 2 → **3** | decl + the discriminator's call + the new reset test |

## 5. Acceptance status

1. **Gate per commit — PASS.** 146 → 153, executed at `a960f07`, `9d3b9f5` and final HEAD.
2. **Censuses — PASS** (table above).
3. **Owner runtime — OPEN** (owner-driven, owner-mouse-only). Recipe unchanged from the corrected
   N1b acceptance 2 except that the manual-click pump is no longer needed:
   - x64 rebuild first, then **read the window title for `— TESTNET`** (the rebuild clobbers the
     bin's `secrets.json` from project source), and back up `orderapp-settings.json`.
   - Take a position, let the SL trigger, then **cancel the SL order outside the app** — the
     `cancelled` echo does not clear `SLTriggered`/`PositionSLOrderId`, so every tick edits a dead
     order and earns `10004 order_not_found`.
   - Expect: gray `SL-edit failure #1 - backoff 0.7s`, `#2 - backoff 1.3s`, `#3 - backoff 2.7s`,
     `#4 - backoff 5.0s` and then the cap; `SL update rate limited: …s remaining` printing from the
     chase path from the second failure on; `SL reposition sent:` where `SL repositioned:` used to
     be; and the M.SL emergency still firing pre-throttle exactly once.
4. **Normal sessions — PASS on mechanism, with one deliberate exception.** With no red SL-edit
   failure the counter never leaves 0, so the gate is a flat 333 ms exactly as before; the only
   moved code runs on the recognition branch (writing 0 over 0) and on the failure path.
   **The exception is spec-mandated commit 2:** every normal session's log now reads `SL reposition
   sent:` instead of `SL repositioned:`, so "byte-identical" holds for behaviour and timing, not
   for the log text. Flagging it because acceptance 4 is worded absolutely.

## 6. For the reviewer

1. **The positive-test choice** (§1) is the one place I read the spec narrowly rather than
   literally — the else-arm the spec names is not reached by the ordinary success. If the
   coordinator prefers the literal else-arm, it is a two-line change, but it would also reset on
   "price unchanged, not ours".
2. **The two comment corrections in commit 1** are scope I added. They were written by N1b commit 1
   and are falsified by N1c commit 1; leaving them would have left the file asserting a flat
   333 ms gate. Censuses re-run after the prose edits.
3. **A dead string survives:** `pendingLocalMsg = $"CRITICAL SL repositioned to: …"` (`:4446`)
   still says "repositioned". Its only consumer is a commented-out `AppendColoredText` (`:2980`),
   so it reaches no log; I left it alone as out of scope rather than reword a dead line.
4. **Thread class of the new write:** the reset runs on the **UI thread** — the echo `Select Case`
   is inside `Me.Invoke` off the receive loop — writing a field the receive loop (the coupling) and
   the chase's post-await continuations already write lock-free. Same accepted torn-write class as
   N1b; no new synchronisation. Worth an adversarial look since N1b's own note said
   "receive-thread", and this site is one `Invoke` removed from that.
