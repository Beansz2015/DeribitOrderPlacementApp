# Impl report — the entry chase preserves the PLACED size (N2b)

**Spec:** `spec-chase-preserve-placed-size.md` (owner-ticked 2026-08-01).
**Origin:** `review-risk-sized-bridge-trades.md` §2/§3 — N2's acceptance 3b failed with the risk
size placed correctly and then reverted by the first reposition.
**Seat:** Opus HIGH, fresh conversation, own pass. **No trades placed, no bridge armed.**
**Baseline at start:** `a6e3af1`, 10 ahead of `origin/master` (`53a2375`), tree clean; gate
executed by me → **GATE PASSED, OrderCheck 173/173**; all seven §4.6 censuses matching.

## Commits

| | |
|---|---|
| `8370541` | **Spec-back** — two box-mirror sites outside §2's scope, raised BEFORE implementing. **Two rulings still outstanding**; see §7. |
| `82005d0` | §2 — `placedOrderSizeUsd`: declaration, set site, both chase reads, five clears. |
| `9b04972` | §3 — both `Position entered:` lines print the size that entered. |
| *(this doc)* | impl report |

Gate executed at each commit and at final HEAD: **GATE PASSED, OrderCheck 173/173** every time.

## 1. What changed (§2)

One field, `placedOrderSizeUsd As Decimal = 0D`, declared beside `legAnchorPrice` /
`pendingReanchorFill` in the order-context block — it has their lifecycle exactly. `0` keeps
`sizeUsdOverride`'s established meaning, *nothing retained, use the box*; no second sentinel.

| role | site | note |
|---|---|---|
| decl | `:411` | order-context field block |
| **set** | `ExecuteOrderAsync :3947` | `= amount`, the value all three payload legs carry, after the `sizeUsdOverride` fold |
| **read** | `UpdateLimitOrderWithOTOCOAsync :4237` | full-bracket re-anchor |
| **read** | `UpdateEntryOrderOnlyAsync :4322` | the arm `EntryOnlyChase` (default ON) actually takes — the one 3b ran through |
| clear | fill/`OpenPositions` transition `:3422` | |
| clear | `CancelOrderAsync :4007` | nuclear teardown |
| clear | `CancelWorkingEntryCoreAsync :4101` | scoped teardown |
| clear | `StopLossForTrailingOrderAsync :4860` | placement seed |
| clear | `CompletePositionClose :5250` | |

Both reads are the spec's exact form, `If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd`,
placed after the existing `orderAmountVal` read so the `amount <= 0D` guard below is unchanged.

**Two deliberate choices, both flagged for the reviewer:**

**(a) The set is UNCONDITIONAL, not only-when-overridden.** §2 says the field must record *"exactly
what was sent"*, and acceptance 3 sanctions either `0` **or** box-equal for a manual placement. I
took box-equal, because it makes **the placement itself a clear**: a manual order cannot inherit a
previous act's size even by a path that escaped all five teardowns. That is acceptance 4's exact
risk, and the conditional variant would have *created* it — with only-when-overridden, a manual
placement after a 310-lot act would not write the field at all, so a single missed clear anywhere
would put 310 on the manual order. See §3 for the full argument.

**(b) The clear at the fill is at the `OpenPositions` transition (`:3422`), not at the two
per-label filled echoes.** That is the single site where the working-entry id context dies —
it sits with `CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId = Nothing` — and it covers
**both** entry labels, since `EntryLimitOrder` and `EntryTrailingOrder` fills both set
`OpenPositions = True`. One line instead of two, at the place the ids it belongs to are nulled.

**(c) `StopLossForTrailingOrderAsync` CLEARS rather than sets**, even though it is a placement. That
bracket is placed at `amount = orderAmountVal` — the box — so `0` is the truthful record, it keeps
the entire trailing path byte-identical to before this change, and it makes the D3 reachability
proof below exact rather than "0 or equal to the box".

## 2. The clear set, proved against `cancelPending` and `emergencyFired` (spec §5)

§5: *"Enumerate the reset sites of `cancelPending` and `emergencyFired` and prove the new field
matches them."* Enumerated from the code at final HEAD, not from memory:

| event | `cancelPending` | `emergencyFired` | `placedOrderSizeUsd` | match? |
|---|---|---|---|---|
| `ExecuteOrderAsync` placement seed | `= False` `:3940` | `= False` `:3941` | **`= amount` `:3947`** | ✅ same seed block |
| trailing placement seed | `= False` `:4852` | `= False` `:4853` | **`= 0D` `:4860`** | ✅ same seed block |
| nuclear teardown (`CancelOrderAsync`) | `= True` `:4012` | *(deliberately not)* | **`= 0D` `:4007`** | ✅ beside `legAnchorPrice :4005` |
| scoped teardown (`CancelWorkingEntryCoreAsync`) | `= True` `:4093` | *(deliberately not)* | **`= 0D` `:4101`** | ✅ beside `legAnchorPrice :4099` |
| `CompletePositionClose` | *(via the nuclear cancel it calls)* | `= False` `:5246` | **`= 0D` `:5250`** | ✅ beside `legAnchorPrice :5248` |
| fill / `OpenPositions` transition | — | — | **`= 0D` `:3422`** | spec-named; see below |

**The three `cancelPending` reset sites I did NOT match, and why each is already covered:**

| site | why no clear is needed |
|---|---|
| `IsCancelPending()` 4-s self-clear `:495` | A timeout on the cancel *gate*. No order lifecycle event; the working entry's existence is unchanged by it. |
| raced-abort repair `:1642` | Reached only from the id-31 `not_open_order` response, i.e. *the scoped cancel lost and the entry filled*. `CancelWorkingEntryCoreAsync` already ran and cleared at `:4101`, so the field is provably `0` on arrival. |
| exchange-confirmed `cancelled` echo `:3357` | Every cancel the app issues goes through one of the two teardowns, both of which clear. The echo only re-opens the gate. |
| `TrailingStopLossOrderAsync` cancel-all `:4931` | Fires with a position open, so the fill clear at `:3422` has already run. `legAnchorPrice` is deliberately not cleared here either — the trailing transition needs its context — and this field follows it. |

**Why the fill transition has no sibling:** `cancelPending` has nothing to do at a fill, and
`emergencyFired` must *survive* the fill — that is the whole point of the N1 latch. This field is
the opposite: a fill is precisely when the working entry ceases to exist, so it is the one clear the
siblings could not have told me about. §2 named it, and it is the load-bearing one.

## 3. Acceptance 4 — why a stale bridge size cannot reach a later MANUAL order

The brief's stated risk. The argument is two-legged, and the second leg is the one that makes it
structural rather than an enumeration I could have got wrong:

**Leg 1 — every way a working entry dies clears the field.** The five clears above, matched to the
siblings, with the four non-matching sibling sites shown to be already covered.

**Leg 2 — even if leg 1 had a hole, the manual placement overwrites it before any chase can read
it.** There is no path to a manual entry order that does not run `ExecuteOrderAsync :3947`, and that
line is unconditional. So the value a chase can read is always *this* order's own size.

Leg 2 needs one ordering check, because `:3947` sits **after** `Await SendWebSocketMessageAsync`.
Could a chase edit fire in that window and read a stale value?

- The chase gate (`:2185`) requires all three of `CurrentOpenOrderId` / `CurrentTPOrderId` /
  `CurrentSLOrderId` to be non-`Nothing`.
- **Every site that installs an id** — `:2996`, `:3149`, `:3162`, `:3215`, `:3228`, `:3238` — runs
  **inside a `Me.Invoke` lambda**, and the UI thread is held by `ExecuteOrderAsync` itself for the
  whole window. So ids cannot go `Nothing` → non-`Nothing` inside it. (The one receive-thread
  assigner, the id-778 restore at `:5552`, is a connect-time snapshot.)
- Therefore either the ids were already `Nothing` — the chase cannot fire at all — or they belong to
  a **previous** working entry, whose own placement set the field to *its* size, which is the
  correct value to re-send to it.

So the window cannot produce a wrong size in either branch. Placing the set after the send (beside
its sibling seeds) rather than at the `sizeUsdOverride` fold also buys the property that matters
more: the early `Return`s between the two — bad quote, ATR-slippage abort, unsupported type — can
never leave a size retained for an order that was never placed.

**Two residuals, declared rather than papered over.** Neither can put a stale size on a *new* order,
because of leg 2:

1. **Exchange rejection.** `HandlePlacementResponse`'s rollback restores `placedPrice` /
   `placedStopLossPrice` but not this field — exactly as it does not restore `legAnchorPrice`. The
   rejected order does not exist, so no chase can fire on it (no ids were installed), and the next
   placement overwrites.
2. **A working entry cancelled externally** (Deribit web UI). The `cancelled` echo branch has no
   `EntryLimitOrder` case, so `CurrentOpenOrderId` is not nulled and the field is not cleared —
   pre-existing behaviour, shared with `placedPrice` and `legAnchorPrice`. A chase edit on the dead
   id returns `already_closed`, which `:1609` already downgrades to a gray benign line.

## 4. §3 — which value `Position entered:` now prints, and why

**Chosen: the filled echo's own `amount`**, falling back to the retained `placedOrderSizeUsd`, then
to the box. §3 asked me to choose between *the retained placed size* and *the position model's own
size* and to say why; I took a third that is strictly better than either, and here is the reasoning:

- **Not `positionSizeUSD` (the position model).** It is the **total** position, so it would
  misreport an add as if the whole book had just entered; and it is seeded on the receive thread
  from the position channel (`:2908`) and the id-777 snapshot (`:5637`), neither of which is ordered
  against *this* order echo — so it can legitimately still be `0` at this line.
- **Not the retained placed size alone.** It is what was *sent*, which is the very thing the review
  warned against treating as evidence of what was *held*. It is right for the chase (that is what
  the chase is re-sending) but it is not the fill.
- **The echo's own `amount` is the fill's own size.** It comes from the same message
  `entryShownPrice` is already read from two lines above, it is the token `ApplyCloseFill` already
  uses for the *close's* size (`:5212`), and on an `order_state = "filled"` echo it is by definition
  the amount that filled. Same read shape as the existing `average_price` pair, so the two lines
  read as one idiom.

The `EntryTrailingOrder` sibling gets identical treatment. In practice it reads the same as before —
a trailing bracket is always placed at the box — and it is changed so the two lines cannot drift.

**Fallback ordering verified:** the `OpenPositions` clear at `:3422` runs *after* the per-order loop
(`Next` at `:3372`), so `placedOrderSizeUsd` is still live at both log lines.

## 5. Behaviour deltas beyond the defect

1. **Editing the Amount box while an order rests no longer resizes that resting order mid-chase.**
   Before, every reposition re-read the box, so typing a new number silently resized the live order.
   Now the resting order keeps the size it was placed at until it fills or is cancelled. This is a
   real change to *manual* behaviour and is a direct consequence of §2's design, not an addition to
   it. I judge it strictly better — the box is the input for the *next* order — but it is the
   owner's to veto, and acceptance 3 does not exercise it.
2. **Nothing else.** With `placedOrderSizeUsd = 0` every touched expression is character-for-character
   its previous self. The trailing path is byte-identical by construction (§1c).

## 6. Censuses (re-run by me, scoped to `frmMainPageV2.vb`)

§4.6's seven, all **unchanged** from the `a6e3af1` baseline I measured myself:

`emergencyFired` **10** · `IsATRSlippageExcessive` **8** · `NextSlBackoff` **2** ·
`RecordCommandedSLPrice` **3** · `slUpdateFailures = 0` **1** · `isPlacingOrder` **13** ·
`lastPlacementAdmittedUtc` **13**

§4.6's new line: **`placedOrderSizeUsd` = 12 raw** = 11 code + 1 comment mention, i.e.
**1 decl + 1 set + 2 chase reads + 5 clears + 2 display fallback reads (§3)**. The spec predicted
"1 decl + 1 set + 2 chase reads + the clear sites"; the two extras are §3's fallbacks, which the
spec's census line was written before §3's value was chosen.

⚠️ **Grep trap, flagged so a reviewer is not misled:** `orderAmountVal` reads **26**, up from **24**
at `a6e3af1` — but **no read was added or removed**. The two log lines gave up their `orderAmountVal`
and the two new §3 fallback lines took it back (net zero); the `+2` is entirely **comment**
mentions added by the §2 commit. Same comment-only-movement trap the handover flags. A repo-wide
grep inflates it further (the docs discuss it); the number above is `frmMainPageV2.vb`-scoped.

## 7. ⚠️ Outstanding — two rulings, and what they mean for N2

Raised in `spec-back-chase-preserve-placed-size.md` (`8370541`) before any code was written, per the
standing rule. **Neither is implemented.** Both are the same defect class as N2b, at sites §2's
scope fence excludes.

- **D1 — `ReanchorTPToFillAsync :4352` resizes the POSITION's take-profit leg to the Amount box.**
  Fires on essentially every chased bridge entry (its `:3285` gate is effectively always true under
  `EntryOnlyChase`, which deliberately does not advance `legAnchorPrice`). A 310-lot position would
  carry a 10-lot TP leg. **My recommendation: N2 must NOT be enabled until this is ruled**, because
  a risk-sized position with a box-sized TP leg is a worse outcome than the defect N2b fixes.
  Proposed fix is one line copying the restore-hardening precedent at `:4507`; it does **not** use
  the new field, which §2's own fill-transition clear kills before that echo arrives.
- **D2 — the id-778 restore (`:5552`) does not restore the entry's amount**, so the defect survives
  a restart: the first post-restart reposition resizes a restored 310-lot entry to the box.
  Proposed fix is a two-line single-writer seed. **Known hole until ruled.**
- **D3 (informational, no ruling needed) — `UpdateStopLossForTrailingOrder :4589` is a THIRD
  working-entry edit site** the spec does not name. I deliberately gave it no read, with a proof it
  can never observe a non-zero field: its block is gated on `isTrailingStopLossPlaced` (`:2538`),
  which is written at exactly one site (`:4869`) — the trailing placement seed that clears the field
  (§1c) — and the entry and trailing chase blocks cannot both own the order context. **This is why
  §4.6's "2 chase reads" is 2 and not 3.** If the reviewer rejects the proof, the number is 3.

## 8. Runtime acceptances — for the OWNER to drive (I placed no trades)

Acceptance 1 (gate per commit) and 6 (censuses) are **done and reported above**. 2–5 are runtime and
require an owner-driven placement under the triple-placement WATCH protocol.

**Before any of them:** back up settings → **rebuild x64** (the gate is AnyCPU-only) → **re-read the
window title for `— TESTNET`** (the x64 rebuild clobbers the bin's testnet `secrets.json`).

| # | setup | the observation that decides it |
|---|---|---|
| **2** — THE acceptance | TESTNET, bridge Live, N2 checkbox **ON**, session policy **OFF**, Amount box **10**, `risk_per_trade_usd = 1`, payload `entry 63000 / stop 62800` ⇒ computed **310**. Let it chase (an `Order repositioned:` line must appear), then flatten. | **The reduce must report 310, not 10.** Exchange-derived, and the authority. **Do not accept the `Buy limit order placed For 310` line as proof** — that line was already correct when 3b failed. |
| **3** — manual regression | N2 checkbox OFF. Manual Limit BUY, box **10**, chased at least once. | Position **10**. Here `placedOrderSizeUsd` equals the box. |
| **4** — the stale-size case | A bridge act at a non-box size → flatten → then a **MANUAL** placement at the box size. | The manual order is the **box** size. This is what proves the lifecycle clears. |
| **5** — session-policy path | A `0.5` policy line, box **above** the clamp (e.g. 40), N2 checkbox OFF so the multiplier is the only sizing input. Chase at least once. | The position holds the **reduced** size, not the box. This is the pre-existing half of the defect. |

**Acceptance 2 additionally gives D1 its evidence for free:** with the position at 310, note what
the TP leg's size is on the testnet web UI after the `TP re-anchored to fill` line appears. If it
reads 10, D1 is confirmed at runtime and N2 stays disabled pending that ruling.

Teardown as usual: restore settings, verify, delete the testnet journal rows.

Related: `spec-chase-preserve-placed-size.md` · `spec-back-chase-preserve-placed-size.md` (the two
open rulings) · `review-risk-sized-bridge-trades.md` §2/§3 (the runtime evidence and the
pre-existing attribution) · `spec-risk-sized-bridge-trades.md` (N2, which this blocks) ·
`spec-session-policy-gate.md` §4 (`sizeUsdOverride` and the `0`-means-the-box convention this
field reuses) · `spec-entry-chase-v2.md` §4 (`EntryOnlyChase`, `legAnchorPrice`).
