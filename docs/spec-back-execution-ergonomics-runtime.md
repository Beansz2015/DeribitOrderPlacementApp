# Spec-back — runtime findings from the ergonomics Phase A acceptance pass (2026-07-18)

**Origin:** owner testnet acceptance of ergonomics Phase A (`docs/impl-report-execution-ergonomics.md`).
While forcing item G's ATR-slippage abort (ATRSlip 0.05), the guard's cancel **raced a fill and lost**.
That exposed two defects on the **scoped-cancel path** — both PRE-EXISTING (`CancelWorkingEntryCoreAsync`
is from `spec-decouple-v2.md`; neither was introduced by Phase A).

**Item 1 is DONE** (owner ruling (a): implement now, record here). **Item 2 is the spec** — owner will
hand it to a separate implementer.

---

## The observed run (both items share this evidence)

```
Buy limit order placed For 10 at 64922.
LONG slippage $5.50 (0.13x ATR) exceeds limit $1.51
Working entry cancelled (ATR slippage) - position legs untouched
Position entered: LONG 10 @ $64922.00
API ERROR (id 31): code 11044 - not_open_order
Position executed at 64982.
Profit made: $0.01.
```

Inputs at the time: Amount 10, T.Prof 60, Trig P **30**, S.Loss **20**, T.P.Off **60**, ATRSlip **0.05**.

The entry filled before Deribit processed the `private/cancel` (id 31), so the cancel was rejected
`11044 not_open_order`. The position was real, its OTOCO legs were live, and it later closed at
**64982 = entry 64922 + T.P.Off 60** — i.e. **the TP leg was working the whole time**.

**Screenshot state while in that position:** `Take Profit: 0`, `Entry Buy: 0`, `Trig. Stop: 0`,
`Stop Loss: 64872` (= trigger 64892 − S.Loss 20 — the one box that survived), STATUS `In Position`.

**Phase A note (positive):** the `Stop Loss` box surviving is item G's guard doing its job —
`PositionSLOrderId` was non-Nothing, so item G's guarded clear correctly did NOT run. That is item G's
*"same abort with an open position → displays untouched"* acceptance, runtime-proven. Item G's other
half (clears when provably flat) is still unobserved — hard to reach on testnet, where the thin book
fills entries near-instantly.

---

## Item 1 — benign abort race logged as a red API ERROR — **DONE**

**Problem.** `11044 not_open_order` on the scoped cancel is the *expected* outcome whenever the
slippage guard races a fast fill: the fill won, nothing is wrong. Logging it as a red `API ERROR`
teaches the trader to ignore red lines — unacceptable in an execution app.

**Precedent.** `HandleUnhandledJsonRpcError` already downgrades exactly this class: order-edit ids
`223344-223350` returning `already_closed` print a gray *"benign chase race"* note. The scoped cancel
had simply never been given the same treatment.

**Implemented.** In `HandleUnhandledJsonRpcError`, above the generic red emission:

- gate: `messageId = 31` **AND** message contains `not_open_order` or `order_not_found` (ordinal,
  case-insensitive) → gray line `Entry cancel skipped (id 31): order already filled - benign abort
  race (the fill won)`, then `Return`.
- **Deliberately narrow:** only id 31 (the by-id `private/cancel` that can produce this). The nuclear
  id-30 path is NOT included — it also carries trailing-stop placement, where these tokens would mean
  something real. Every other id-31 error stays loud.
- `RequestNameForId` gained `Case 31 : " scoped entry cancel"` so any *other* id-31 error names itself.

**Acceptance:** force the race (below) → the gray line replaces the red one; no other error changes
severity. Logging-only, no engine state touched (same discipline as the existing downgrade).

---

## Item 2 — SPEC: the raced abort leaves the Placed panel lying about a live position

**Recommended implementer:** Opus at **high** — the fix lives on the receive/echo path and must not
disturb the cancel lifecycle or invariant 3. One conversation, one or two commits.
**Target:** `frmMainPageV2.vb`. **Anchors by symbol; verify against HEAD** — several specs have landed.
**Ground rules:** the standing ones — build 0/0 per commit (`tools/checks/verify-gate.ps1`, GATE PASSED
required), local commits, never push, receive thread = engine fields only + `UiInvoke`/`AppendColoredText`
for display, scope discipline, impl report at the end.

### Problem

After the raced abort, **`Take Profit`, `Entry Buy` and `Trig. Stop` all read 0 while a real position
with live legs exists** (evidence above — the TP leg the panel showed as `0` is what closed the trade).
The panel is not merely stale, it actively denies live protective orders. Concrete consequences:

- The trader cannot see the working TP/stop geometry of a position they are actually in.
- **`Edit T.S.` is disabled**: `btnEditSLPrice_Click` gates on `Decimal.Parse(txtPlacedTrigStopPrice.Text) > 0`,
  so with `0` it refuses with *"S.L. textbox is 0"*. (`B.E.` still works — it reads `positionAvgEntry`,
  not the box. That is the current workaround.)

### Root cause (verified by code reading — implementer should re-confirm)

1. `CancelWorkingEntryCoreAsync` optimistically tears the working-entry context down *before* the
   exchange answers: engine `cancelPending = True`, `CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId
   = Nothing`, `placedPrice = 0D`, `legAnchorPrice = 0D`, `pendingReanchorFill = 0D`; display
   `txtPlacedPrice`/`txtPlacedTakeProfitPrice`/`txtPlacedTrigStopPrice` → `"0"`. Correct when the
   cancel wins; wrong when it loses.
2. The echoes that would repopulate those boxes arrive **inside the `cancelPending` window and are
   dropped by design**: the `untriggered` branch's marshalled lambda opens with `If cancelPending Then
   Return` (it is the only writer of `txtPlacedTakeProfitPrice`/`txtPlacedTrigStopPrice`), and the open
   `StopLossOrder` reconcile block is likewise inside `If Not cancelPending`.
3. `cancelPending` is cleared by the **`cancelled` echo** — which never arrives, because the order
   *filled* rather than cancelled. So it stays set for the full `cancelPendingTimeout` (4 s).
4. Deribit pushes **on change only**. By the time the timeout lapses the leg echoes are long gone and
   nothing re-sends them → the boxes stay `0` for the life of the position.

That gating is correct and load-bearing for the normal cancel (it is the transition-race fix — lagging
echoes must not re-arm a dead context). The defect is that **nothing repairs the state when the cancel
turns out to have lost.**

### Design

**Repair on the authoritative signal, reusing the existing restore machinery.** The id-31
`not_open_order`/`order_not_found` response IS the exchange telling us the cancel lost — item 1 has
already isolated that exact condition in `HandleUnhandledJsonRpcError`. Hang the repair there.

On that signal (and *only* that signal):

1. **Clear the cancel gate immediately** — `cancelPending = False` (and `cancelPendingSince =
   DateTime.MinValue`). There will never be a `cancelled` echo for this order, so the remaining
   timeout is dead weight that is actively suppressing the echoes we need.
2. **Re-sync from the exchange rather than guessing** — dispatch the existing
   `RequestOpenOrdersSnapshot()` (id 778 → `HandleOpenOrdersSnapshot`, built by the restore-hardening
   spec for precisely this "re-establish display/context from the exchange" job) and
   `GetLivePositionData("BTC-PERPETUAL")` (id 777). Let the shipped handlers repopulate; do not
   hand-write leg prices at the error site.
3. **One honest log line**, e.g. `Entry filled before the cancel landed - re-syncing order/position
   state from exchange` (gray/yellow), immediately after item 1's gray line.

**Rejected alternative (do not implement):** making the teardown conditional on "still flat" at cancel
time. At that instant the positions echo may not have arrived — that *is* the race — so the check is
unreliable. Repairing on an authoritative response is deterministic.

**Socket-ordering argument (coordinator addition, 2026-07-20 — why clearing `cancelPending` here cannot
re-arm a dead context):** the id-31 error is a RESPONSE on the same WebSocket as the order echoes, and
Deribit delivers in order — so by the time the repair runs, every echo of the raced entry (open → filled)
has already been delivered and processed. There is no "lagging pre-fill open echo" left to slip through
the cleared gate; the observed evidence shows exactly this ordering (`Position entered` printed BEFORE
the id-31 error). The transition-race protection was built for the cancel-WINS case, whose lagging
`cancelled`-adjacent echoes genuinely do trail; in the cancel-LOSES case the error response is
structurally the LAST message of the sequence. This is why the repair site is safe where a timer-based
early-clear would not be.

### Invariants (load-bearing — a reviewer will check each)

- **Never re-seed `CurrentOpenOrderId`.** The entry is FILLED, not working. Restoring working-entry
  context would let the entry chase edit a filled order and would make `HasWorkingEntryOrder` lie to
  the bridge gate chain. Only the POSITION legs (`PositionTPOrderId`/`PositionSLOrderId`) may be
  repopulated. Verify `HandleOpenOrdersSnapshot` cannot set the working-entry context here (there is
  no open entry left to report, but confirm rather than assume).
- **Invariant 3 (HANDOVER-2 §4) is preserved by construction:** the repair only *writes real exchange
  values*; it must never zero SL context. **Do not add an 8th SL-context reset site.**
- **Receive-thread rules:** `HandleUnhandledJsonRpcError` is on the receive thread and is a `Sub` —
  engine fields written directly, display via `UiInvoke`/`AppendColoredText`, and the two requests
  dispatched without blocking the receive loop (fire-and-forget `Task.Run`, mirroring the existing
  rate-limiter re-init pattern). Note the choice in the report.
- **Strict gating:** the repair runs ONLY on id 31 + `not_open_order`/`order_not_found`. The successful
  cancel path, the nuclear cancel, and the normal fill path must be **byte-identical to today**.
- **Rate limit:** two extra requests on a rare path. Reuse the existing `isRequestingLiveData` debounce
  for the position request; do not add a new limiter.

### Secondary finding (owner decision — recommend ACCEPT in v1)

The cancel also zeroed `legAnchorPrice` and `pendingReanchorFill`, so a **price-improved** raced fill
will not re-anchor the TP to its true fill (the `legAnchorPrice <> 0D` gate in the filled-entry branch
fails). In the observed run the fill was exactly at the order price, so nothing was lost.
**Recommendation: accept and document in v1.** Re-arming the re-anchor from a repair path risks editing
legs on stale geometry — a worse failure than a TP sitting on pre-fill geometry. Revisit only if a
raced *and* price-improved fill is observed in practice.

### Acceptance

1. **Force the race** on testnet: flat, ATRSlip ~`0.05`, place a **Limit** entry, let the guard trip
   while the thin book fills the entry. Expect, in order: `Working entry cancelled (ATR slippage)`,
   `Position entered: …`, item 1's **gray** benign-race line, the re-sync line, and then — within ~1 s —
   **`Take Profit`, `Entry Buy` and `Trig. Stop` populated with the real live values**, `Stop Loss`
   unchanged, STATUS `In Position`. `Edit T.S.` must work again on that position.
2. **Normal (non-raced) slippage abort while flat:** byte-identical to today — the three boxes read 0,
   item G's guarded clear runs, no gray line, no re-sync.
3. **Normal fill with no cancel:** untouched.
4. **Grep assertions:** `cancelPending = False` appears only at its existing sites plus the one new
   repair site; no new `emergencyBaseline = 0` / `placedStopLossPrice = 0D` / `ResetCommandedSLPrices()`
   sites; `CurrentOpenOrderId` is not assigned anywhere in the repair.

### Commits

1. `Raced abort repair: re-sync order/position display when the entry cancel loses to a fill`
2. (if separated) `Docs: impl report - raced abort repair`

**Implementation report:** `docs/impl-report-raced-abort-repair.md`, standard format — exact changes,
build results, deviations with justification, the runtime evidence for acceptance 1 and 2, and
suspicious-nearby not touched.
