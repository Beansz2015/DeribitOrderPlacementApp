# Coordinator review — raced-abort repair (`a32946c` + report `dad0c65`)

**Date:** 2026-07-21 · **Reviewer:** coordinator seat (Fable) · **Verdict: APPROVED** —
owner runtime acceptance 1 (force the race) still pending, as the report states.

**Method (per the standing rule — verified code, not the report):** `git show a32946c` read in
full; every grep re-run by the reviewer; `HandleOpenOrdersSnapshot` and `ProcessPositionData`'s
announce block read end-to-end; **`tools/checks/verify-gate.ps1` EXECUTED at `a32946c`** in an
isolated worktree → Release 0/0, Debug 0/0, OrderCheck **48/48**, repo guards OK, **GATE PASSED**.
(First gate attempt failed on `SQLite.Interop.dll` — worktree path length, not code; reproduced
PASS from a short path. Not a finding.)

## Spec acceptance-4 grep assertions — re-run, all pass

- `cancelPending = False`: 5 sites = the 4 pre-existing (:333 timeout self-clear, :2963 cancelled
  echo, :3518/:4397 fresh-placement seeds) + exactly one new (:1446, the repair).
- `ResetCommandedSLPrices`: definition + the documented 7 call sites, unchanged. No new
  `emergencyBaseline = 0` / `placedStopLossPrice = 0D` sites (the diff adds none — the repair
  writes no SL context at all). No 8th SL-context reset site.
- `CurrentOpenOrderId` is not assigned in the repair, and cannot be assigned by the dispatched
  snapshot: `HandleOpenOrdersSnapshot` seeds it solely under
  `(EntryLimitOrder|EntryTrailingOrder) AndAlso order_state = "open"` (:5129), and
  `CurrentTPOrderId`/`CurrentSLOrderId` solely under `workingEntryFound` (same condition). The
  raced entry is FILLED → working-entry context structurally unreachable; only
  `PositionTPOrderId`/`PositionSLOrderId` + the TP/trigger displays repopulate. Verified, not
  assumed — matches the report word for word.

## Invariants

- **Strict gating:** the whole repair sits inside item 1's exact condition (id 31 AND
  `not_open_order`/`order_not_found`), between the gray line and the existing `Return`. Successful
  cancel, nuclear id-30, and normal fill paths are byte-identical.
- **Clear-before-dispatch:** `:5083 If cancelPending Then Return` in the snapshot handler is
  exactly why the synchronous clear must precede the `Task.Run` — and it does; both run on the
  receive thread, the id-778 response arrives strictly later. Ordering by construction, confirmed.
- **Socket-ordering safety** (the spec's coordinator addition) is reproduced in the code comment
  and correctly applied: the error is a response on the ordered socket, so no lagging pre-fill
  echo can slip through the cleared gate.
- **Receive-thread rules:** engine fields written directly on the receive thread (same thread as
  every other writer), display via `AppendColoredText`/`UiInvoke`, fire-and-forget `Task.Run`
  mirroring the rate-limiter re-init precedent. No new limiter; `GetLivePositionData` keeps its
  `isRequestingLiveData` debounce. The report's interleaving note (an in-flight id-777 from the
  fill's own block either being reused or superseded — both idempotent) is sound.

## The flagged deviation — `positionRestoreAnnounced = False` — ACCEPTED

The report's justification is correct and I verified its premise: the id-778 snapshot deliberately
never writes `txtPlacedPrice`/`placedPrice` without an open entry leg, and the announce block
(:5222) is the only writer in the position-no-working-entry state — without the re-arm, Entry Buy
stays 0 and spec acceptance 1 is unpassable. The re-armed block's side effects were read line by
line: one-shot re-latch, honest yellow announce, `SetTradeMode` to the position's own side
(idempotent here), status "In Position" (true), `txtPlacedPrice = average_price`, `placedPrice`
seeded ONLY when 0. No SL context, no commanded prices, no leg edits. Invariant-3-safe.

**Reviewer-added edge (benign, for the record):** if the id-31 tokens ever arrive because the
entry was cancelled *externally* rather than filled (cancel lost to a cancel), the re-sync finds
nothing (id-778 early-returns, id-777 size-0 skips the announce) and the re-armed flag simply
means the NEXT real position prints one extra "Open position detected" line with
`txtPlacedPrice` refreshed to `average_price` — cosmetic, self-healing, no engine-state impact.
No change requested.

## Scope

Diff = exactly one block in `HandleUnhandledJsonRpcError`, `frmMainPageV2.vb` (+33 lines, mostly
the load-bearing comment). Suspicious-nearby list in the report checks out — teardown, timeout
self-clear, snapshot/position handlers all untouched.

**Secondary finding** (price-improved raced fill won't TP-re-anchor): accepted-as-specced stands;
owner decision already recorded in the spec.

## Remaining before "done"

Owner runtime acceptance 1 on testnet (recipe in the report §Acceptance status); acceptance 2/3
are code-identical paths — spot-check optional.
