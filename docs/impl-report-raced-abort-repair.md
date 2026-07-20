# Implementation report — raced-abort repair

**Spec:** `docs/spec-back-execution-ergonomics-runtime.md` item 2 (coordinator-approved `b77b724`,
including the socket-ordering addition). **Commit:** `a32946c` (one commit — the change is a single
block). **Gate:** `tools/checks/verify-gate.ps1` → Release 0/0, Debug (vbproj direct) 0/0, OrderCheck
0/0 + 48/48, **GATE PASSED**. **Implementer seat:** Fable (same conversation that authored the spec —
per the routing note in the handover index).

## Exact change

One insertion, `frmMainPageV2.vb`, inside item 1's existing id-31 downgrade branch in
`HandleUnhandledJsonRpcError` (between the gray benign-race line and the `Return`):

1. `cancelPending = False` + `cancelPendingSince = DateTime.MinValue` — the `cancelled` echo that
   normally clears the gate will never arrive (the order filled), and the still-set gate is what
   suppresses both the repopulating echoes and the id-778 snapshot handler (its body opens with
   `If cancelPending Then Return`). Cleared synchronously **before** the dispatch, so the ordering
   the snapshot handler needs is guaranteed by construction. Safety is the coordinator's
   socket-ordering argument, reproduced in the comment: the error is a response on the same ordered
   WebSocket, so every echo of the raced entry (open → filled) has already been processed — no
   lagging pre-fill echo exists to slip through the cleared gate.
2. `positionRestoreAnnounced = False` — **deviation, see below.**
3. One yellow line — `Entry filled before the cancel landed - re-syncing order/position state from
   exchange` — then a fire-and-forget `Task.Run` awaiting `RequestOpenOrdersSnapshot()` (id 778) and
   `GetLivePositionData("BTC-PERPETUAL")` (id 777), mirroring the existing rate-limiter re-init
   fire-and-forget pattern (the receive loop is never blocked; noted per the spec's threading
   invariant). No prices are hand-written at the error site.

## Deviation (flagged): `positionRestoreAnnounced = False`

The spec's design dispatches the two snapshots and "lets the shipped handlers repopulate" — but its
own acceptance 1 requires **Entry Buy** populated, and neither shipped handler writes it in the raced
state: `HandleOpenOrdersSnapshot` deliberately never seeds order-context without an `open` entry leg,
and `ProcessPositionData` writes `txtPlacedPrice`/seeds `placedPrice` (the documented
"restart-restore exception to placement-only seeding") **only inside the once-per-connection
`positionRestoreAnnounced` announce block**. The raced state is exactly the restore state that block
was built for — position, no working entry, `placedPrice = 0` — so the repair re-arms the flag and
lets the id-777 response run the shipped block, rather than duplicating its writes. Cosmetic side
effect, accepted: the log prints `Open position detected: …` for a position the app itself entered —
which in this situation is an honest description. Without the reset, acceptance 1 fails on the Entry
Buy box whenever the flag was already consumed earlier in the session.

## Invariants — how each was verified

- **Never re-seed `CurrentOpenOrderId`:** verified in code, not assumed — `HandleOpenOrdersSnapshot`
  assigns `CurrentOpenOrderId` only under `(label = Entry*) AndAlso state = "open"`, and
  `CurrentTPOrderId`/`CurrentSLOrderId` only under `workingEntryFound` (same condition). The raced
  entry is FILLED, so a snapshot taken after the race cannot contain it as `open` → the working-entry
  context (and the bridge's `HasWorkingEntryOrder` gate) is **structurally** unreachable. Only
  `PositionTPOrderId`/`PositionSLOrderId` repopulate. (If the owner had a genuinely-open app-labeled
  entry alongside — manual double-placement — restoring it would be *correct*, not a violation.)
- **Invariant 3 / no 8th SL-context reset site:** the repair writes no SL context at all; the
  snapshot's SL branch seeds `placedStopLossPrice`/`StopLossTriggerOriginal`/`emergencyBaseline`
  **only when 0** (single-writer discipline already in that handler). Grep: `ResetCommandedSLPrices`
  count unchanged (8 = definition + the documented 7 call sites); no new `emergencyBaseline = 0` or
  `placedStopLossPrice = 0D` sites.
- **Strict gating:** the block is inside item 1's exact condition — id 31 **and**
  `not_open_order`/`order_not_found`. Successful cancels (`cancelled` echo path), the nuclear id-30
  path, and the normal fill path are byte-identical. If the condition ever fires with no position
  (spurious), the re-sync is harmless: id-778 returns early on an empty result; id-777 with size 0
  skips the announce.
- **`cancelPending = False` sites:** grep = the 4 pre-existing (timeout self-clear :306, cancelled
  echo, two fresh-placement seeds) + exactly one new (:1419, the repair).
- **Rate limit:** no new limiter. `GetLivePositionData` self-guards via `isRequestingLiveData`;
  `RequestOpenOrdersSnapshot` consumes a credit as it always has. Two requests on a rare path.

## Interleaving note (for the runtime tester)

In the observed race the fill echo precedes the id-31 error, and the fill's own `OpenPositions` block
may already have an id-777 request in flight; the repair's `GetLivePositionData` call can then be
swallowed by the `isRequestingLiveData` debounce. That is fine: the in-flight request's **response**
lands after the repair has run (responses follow their requests; the repair runs on the earlier
id-31 response), so the re-armed announce block still executes with real position data. Either
request's response completes the repair; both landing is idempotent (announce is one-shot again,
snapshot writes are guarded).

## Secondary finding

Accepted as specced (owner decision recorded in the spec): a price-improved raced fill will not
TP-re-anchor (`legAnchorPrice`/`pendingReanchorFill` were zeroed by the teardown). Nothing in this
change touches that.

## Acceptance status

- **1 (force the race):** owner runtime test pending — testnet recipe unchanged (flat, ATRSlip ~0.05,
  Limit entry; expect the two gray/yellow lines then TP / Entry Buy / Trig. Stop populated within
  ~1 s, Stop Loss untouched, `Edit T.S.` working again).
- **2 (normal abort while flat) / 3 (normal fill):** code-identical paths (the repair is unreachable
  without the id-31 error) — regression risk nil; owner may spot-check.
- **4 (grep assertions):** all pass, above.

## Suspicious-nearby NOT touched

- The `already_closed` downgrade above the repair, and the generic red emission below it.
- `IsCancelPending()`'s timeout self-clear — still the backstop for the cancel-WINS-but-echo-lost
  case; the repair only handles cancel-LOSES, where the timeout could never help (no echo will come).
- The teardown in `CancelWorkingEntryCoreAsync` (including item G's guarded clear) — deliberately
  unchanged; the spec's rejected alternative (conditional teardown) stays rejected.
- `HandleOpenOrdersSnapshot` / `ProcessPositionData` — used as-is; no writes added, no gates changed.
- The `OpenPositions` block's `isRequestingLiveData` juggling in `HandleOrderPositionUpdates` —
  tangled but working; out of scope.
