# Spec-back — `spec-sl-backoff-coupling.md` commit 2: the id range 223344–223350 is not the SL-edit set

**For:** the coordinator. **From:** the N1b implementer seat (Opus HIGH, fresh conversation, 2026-07-28).
**Status:** **commit 1 is LANDED** (`afb4bbc`, comment-only, GATE PASSED 124/124). **Commit 2 is HELD**
pending the ruling below — the owner directed escalation rather than an implementer judgment call.
**File:** `DeribitOrderPlacementApp/frmMainPageV2.vb`. **No engine code changed.**

> ⚠️ Anchors are at `afb4bbc` and will drift. Locate by symbol.

---

## 1. The ask

The spec's commit 2 says:

> In `HandleUnhandledJsonRpcError`'s **SL-edit error arm (ids 223344–223350)** … call
> `BackoffStopLossRetry(DateTime.UtcNow)` for **genuine edit failures only**

At HEAD, **`223344–223350` is the whole order-edit id space, not the SL-edit set.** Four of the seven
ids are not SL edits, and two of those fire *while the triggered-SL chase is live*. Taken literally,
commit 2 would throttle the SL chase on failures that have nothing to do with the SL — the precise
harm N1 was hoisted to remove.

**The ruling needed:** which ids increment the backoff.

## 2. Evidence — the id map at `afb4bbc`

| id | Sender (anchor) | SL edit? |
|---|---|---|
| **223344** | `SendRateLimitedUpdate` regular branch (`:4201`): entry **main** (`:4082`, `:4127`), pre-fill secondary **TP** (`:4084`), **and post-fill `ReanchorTPToFillAsync`** (`:4162`) | **no** |
| **223345** | manual **TP** edit button (`:6020`) | **no** |
| 223346 | `SendRateLimitedUpdate` trigger branch — pre-fill secondary SL (`:4086`); manual **SL** button (`:6081`) | yes (pre-trigger) |
| **223347** | `UpdateStopLossForTrailingOrder` — trailing **main** edit (`:4422`) | **no** |
| 223348 | `UpdateStopLossForTrailingOrder` — trailing SL edit (`:4443`) | yes (pre-trigger) |
| **223349** | **reduce-order** reposition (`:4219`) | **no** |
| 223350 | `UpdateStopLossForTriggeredStopLossOrder` (`:4326`) — the **triggered-SL chase** | yes — *the only path the backoff's throttle governs* |

`BackoffStopLossRetry` pushes `lastStopLossUpdate` forward, and `lastStopLossUpdate` gates exactly one
block: the triggered-SL chase/edit machinery at `:2288`. Nothing else reads it.

## 3. Why the literal range is load-bearing, not cosmetic

**(a) A failing TP re-anchor would throttle a live SL chase.** `ReanchorTPToFillAsync` sends **id
223344** and runs off the **post-fill `open` TP echo** (dispatched `:3016`) — i.e. exactly when the
position opens, the SL trigger-flip lands, and the chase begins. Its red-error class is reachable
(`order_not_found` if the TP moved/filled; the `-32602 post_only` class is the reason
`spec-back-postonly-secondary-fix.md` exists). Under the literal range that red error stamps
`lastStopLossUpdate` forward by up to 5 s and delays **the SL chase**. Same for **223349** (reduce
reposition), which likewise fires mid-position.

**(b) The counter has no reset on those paths.** `slUpdateFailures = 0` is written at exactly one site
— the *successful triggered-SL chase* (`:2353`). Pre-fill 223344/223346/223347/223348 failures
therefore leave the counter armed for the life of the position. Because `BackoffStopLossRetry` is
`Math.Min(slUpdateFailures + 1, 8)` over `Math.Min(333 * 2^n, 5000)`, four accumulated non-SL failures
put the *first* genuine SL failure straight at the 5 s cap instead of escalating from 333 ms. (The
forward *stamp* itself expires harmlessly; it is the counter that persists.)

**(c) Acceptance 4 is unsatisfiable as written.** The spec's acceptance 4 reads:

> Normal sessions byte-identical (no red SL-edit errors ⇒ counter stays 0 ⇒ flat 333 ms as today).

Under the literal range a normal session containing *any* red 223344 / 223345 / 223347 / 223349 error
does **not** keep the counter at 0. Acceptance 4 only holds under the narrow reading. That internal
inconsistency is the main reason I read `223344–223350` as shorthand carried over from the existing
benign-race arm's range (`:1564`) rather than a deliberate widening — but it is the coordinator's call,
not mine.

## 4. Options

| | Option | Assessment |
|---|---|---|
| **(a)** | **SL ids only: 223346 / 223348 / 223350.** Genuine SL edits — pre-fill secondary SL + manual SL button, trailing SL, triggered-SL chase. | **Recommended.** Matches the spec's stated intent ("genuine edit failures"), satisfies acceptance 4 verbatim, and keeps non-SL edits off the SL hot path. Costs a one-line spec amendment. |
| (b) | Literal `223344–223350`. | Implementable today, but accepts §3(a)/(b) and needs acceptance 4 reworded. I would flag both in the impl report. |
| (c) | **223350 only.** | Tightest failure↔mechanism match (the chase is the only thing `lastStopLossUpdate` throttles), but leaves pre-trigger SL edit failures uncoupled — arguably under-delivers the ruling. |

Under (a) or (c) the benign exclusions in the spec still apply unchanged: the `already_closed`
downgrade (`:1564`–`:1569`) and the id-31 abort race (`:1579`) both `Return` before the red emission,
so hanging the coupling on the **red emission path** (`:1622`) automatically excludes them — no extra
condition needed. That satisfies "only errors that survive to a RED emission on those ids couple in".

## 5. Re-verification items from the spec — both resolved

**The `:4246` (now `:4355`) `too_many_requests` arm is UNREACHABLE — spec-back confirmed**, derived
independently of the original claim:

1. `SendWebSocketMessageAsync` (`:1123`) catches `WebSocketException`, `OperationCanceledException`,
   and `Exception`, and swallows all three. No send propagates.
2. No local `Throw` anywhere in the project carries `too_many_requests` or `10028`. The only
   occurrences of those tokens are response-side: `HandleRateLimitError`'s parse (`:1517`) and the
   ownership skip in `HandleUnhandledJsonRpcError` (`:1555`).
3. ⇒ no exception matching that `ex.Message.Contains` gate can reach the `Catch` at `:4351`.

The chase `Catch` (`:2358`) is likewise unreachable-for-this-purpose: its awaited body
(`ForceStopLossUpdate` → `UpdateStopLossForTriggeredStopLossOrder`) is fully wrapped in its own
`Try/Catch`, and `UiInvoke` is non-blocking `BeginInvoke`. **Both existing call sites confirmed dead**,
so this coupling is genuinely the backoff's first reachable trigger. No design change either way.

**Thread discipline — confirmed, with one qualification the spec should absorb.** The spec says
`BackoffStopLossRetry`'s writers are "engine fields, same-thread class as their existing writers".
That is true, but the class is broader than "receive thread":

- `HandleUnhandledJsonRpcError` is called **synchronously** at `:1357` in the receive loop body — raw
  receive thread. A new call from there is the *cleanest* of the three writers.
- `HandleQuoteUpdates` is `Private Async Sub` (`:1994`), invoked fire-and-forget at `:1337`. Its
  writes (`lastStopLossUpdate` / `slUpdateFailures` at `:2352`–`:2353`, `BackoffStopLossRetry` at
  `:2362`) all sit **after** awaits, so they run on threadpool continuations, not the receive thread.
- `:4358` and `ForceStopLossUpdate`'s `lastStopLossUpdate = DateTime.MinValue` (`:4759`) are likewise
  post-await / button-path writers.

So the pair is **already** a lock-free field shared across the receive loop and its continuations —
the same accepted torn-write class documented for `emergencyBaseline` (`:150`). The new writer adds no
class that is not already present, and adds the only writer that is *not* on a continuation. **No new
synchronisation is warranted**; I raise it only because the spec asked me to confirm rather than
assume, and "same-thread" overstates what is actually true.

## 6. What is landed, and what I need

- **Landed `afb4bbc`** — commit 1, comment-only, zero IL (`git diff -w`: no non-comment changed line).
  Corrects both the item-16 throttle-gate comment and the N1-era hoist comment to the truth in §5.
  GATE PASSED, OrderCheck **124/124**. Census unchanged: `BackoffStopLossRetry` call sites = **2**
  (`:2362`, `:4358`); `emergencyFired` = **1 decl + 2 sets + 3 clears + 3 reads**.
- **Held:** commit 2, its gate run, and acceptance 2/3 (the runtime pass is owner-driven anyway —
  trade-placing steps are OWNER mouse clicks only, per the triple-placement WATCH protocol).
- **Needed:** the §4 ruling. On (a) or (c) I also need the spec's acceptance-1 grep amended to name the
  ids, and acceptance 4's parenthetical is then correct as written.

Not pushed — the owner is the only pusher.

Related: `spec-sl-backoff-coupling.md` (the spec), `spec-back-emergency-hoist-acceptance-2026-07-28.md`
§2 (the ruling this implements), `spec-back-fill-reanchor-bug.md` (the 223344 red-error class),
`spec-back-postonly-secondary-fix.md` (the second 223344 red class), `HANDOVER-4.md` §4.4.
