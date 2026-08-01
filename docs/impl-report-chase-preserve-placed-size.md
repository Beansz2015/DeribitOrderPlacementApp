# Impl report — the entry chase preserves the PLACED size (N2b)

> ## AMENDED 2026-08-01 after the coordinator rulings + owner veto
> (`review-chase-preserve-placed-size.md`; spec amended in place). Corrected **in place** below, so
> nothing here reads as current when it is not. Three changes:
>
> - **OWNER VETO — the set is now value-conditional** (`e2c66b9`). §1(a)'s unconditional
>   `= amount` is gone. See §1(a) as rewritten; the acceptance-4 argument in §3 is **unaffected**,
>   because the *write* stays unconditional and that is the half it rests on.
> - **D1 REJECTED — my error.** `ReanchorTPToFillAsync` cannot run on a bridge entry: the staging
>   gate's fourth conjunct is `manualTPval <= 0D` and a bridge act always sets a manual TP. I quoted
>   the full four-conjunct gate, proved **one** conjunct's reachability, and generalised it to the
>   whole gate. §7's "N2 must NOT be enabled until this is ruled" is **void**, and §8's TP-leg
>   observation is **dropped**. Nothing to fix, no commit.
> - **D2 UPHELD and implemented** (`5fc5b16`); **D3 accepted** as argued.
>
> New commits: `e2c66b9` (veto) · `5fc5b16` (D2 restore seed). Gate at each: **GATE PASSED,
> OrderCheck 173/173**. Censuses re-run — §6 below carries the amended new line.

**Spec:** `spec-chase-preserve-placed-size.md` (owner-ticked 2026-08-01).
**Origin:** `review-risk-sized-bridge-trades.md` §2/§3 — N2's acceptance 3b failed with the risk
size placed correctly and then reverted by the first reposition.
**Seat:** Opus HIGH, fresh conversation, own pass. **No trades placed, no bridge armed.**
**Baseline at start:** `a6e3af1`, 10 ahead of `origin/master` (`53a2375`), tree clean; gate
executed by me → **GATE PASSED, OrderCheck 173/173**; all seven §4.6 censuses matching.

## Commits

| | |
|---|---|
| `8370541` | **Spec-back** — two box-mirror sites outside §2's scope, raised BEFORE implementing. **All three now ruled**; see §7. |
| `82005d0` | §2 — `placedOrderSizeUsd`: declaration, set site, both chase reads, five clears. |
| `9b04972` | §3 — both `Position entered:` lines print the size that entered. |
| `fdb6160` | impl report (this doc; amended below after the rulings) |
| `e2c66b9` | **OWNER VETO** — the retained size is value-conditional; the write is not. |
| `5fc5b16` | **D2 UPHELD** — the id-778 restore seeds the placed size. |

Gate executed at each commit and at final HEAD: **GATE PASSED, OrderCheck 173/173** every time.

## 1. What changed (§2)

One field, `placedOrderSizeUsd As Decimal = 0D`, declared beside `legAnchorPrice` /
`pendingReanchorFill` in the order-context block — it has their lifecycle exactly. `0` keeps
`sizeUsdOverride`'s established meaning, *nothing retained, use the box*; no second sentinel.

| role | site | note |
|---|---|---|
| decl | `:411` | order-context field block |
| **set** | `ExecuteOrderAsync :3957` | `If(sizeUsdOverride > 0D, amount, 0D)` — **amended by the veto**, see (a) |
| **read** | `UpdateLimitOrderWithOTOCOAsync :4247` | full-bracket re-anchor |
| **read** | `UpdateEntryOrderOnlyAsync :4332` | the arm `EntryOnlyChase` (default ON) actually takes — the one 3b ran through |
| **restore seed** | `HandleOpenOrdersSnapshot :5576` | **D2**, single-writer (`If placedOrderSizeUsd = 0D`) |
| clear | fill/`OpenPositions` transition `:3423` | |
| clear | `CancelOrderAsync :4017` | nuclear teardown |
| clear | `CancelWorkingEntryCoreAsync :4111` | scoped teardown |
| clear | `StopLossForTrailingOrderAsync :4870` | placement seed |
| clear | `CompletePositionClose :5260` | |

Both reads are the spec's exact form, `If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd`,
placed after the existing `orderAmountVal` read so the `amount <= 0D` guard below is unchanged.

**Three deliberate choices, all flagged for the reviewer:**

**(a) The WRITE is unconditional; the VALUE is conditional.** ⚠️ **Amended by the owner veto.** My
first version wrote `= amount` unconditionally, and that was wrong for a trading reason I did not
know: a mid-chase Amount-box edit is a *deliberate control*. The chase can walk the entry closer to
the TP, shrinking the entry→TP distance, and the owner resizes in that moment. My unconditional set
removed it — and both my §5 and the review's first pass had it as inseparable from the fix. **It is
not.** The distinction the fix actually needs is **where the placed size came from**, not whether
one was retained:

```vb
placedOrderSizeUsd = If(sizeUsdOverride > 0D, amount, 0D)
```

- **override > 0** (bridge / risk-sized) ⇒ retained; the chase re-sends it. The defect stays fixed.
- **override = 0** (manual) ⇒ `0`; the chase falls back to the box, so a typed Amount still resizes
  the resting order.

**The write staying unconditional is the load-bearing half**, and it is why §3's acceptance-4
argument survives the veto verbatim: a manual placement is *still* itself a clear, so a stale size
from a previous act cannot survive into one. Only the value changes. The site comment says so
explicitly, so a later reader does not "simplify" it into a conditional write.

Edge case, confirmed with the reviewer, no code needed: when a risk-sized size happens to **equal**
the Amount box, N2's act site already passes `sizeUsdOverride = 0`, so the field is `0` and the
chase reads the box — that same number. And the consequence the owner should hold: **a resting
*bridge* entry deliberately does NOT follow a box edit.** Cancel it to intervene.

**(b) The clear at the fill is at the `OpenPositions` transition (`:3423`), not at the two
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
| `ExecuteOrderAsync` placement seed | `= False` `:3941` | `= False` `:3942` | **`If(override>0, amount, 0D)` `:3957`** | ✅ same seed block |
| trailing placement seed | `= False` `:4862` | `= False` `:4863` | **`= 0D` `:4870`** | ✅ same seed block |
| nuclear teardown (`CancelOrderAsync`) | `= True` `:4022` | *(deliberately not)* | **`= 0D` `:4017`** | ✅ beside `legAnchorPrice :4015` |
| scoped teardown (`CancelWorkingEntryCoreAsync`) | `= True` `:4103` | *(deliberately not)* | **`= 0D` `:4111`** | ✅ beside `legAnchorPrice :4109` |
| `CompletePositionClose` | *(via the nuclear cancel it calls)* | `= False` `:5256` | **`= 0D` `:5260`** | ✅ beside `legAnchorPrice :5258` |
| fill / `OpenPositions` transition | — | — | **`= 0D` `:3423`** | spec-named; see below |

The veto did not disturb any of this: it changed the **value** written at the one set site, not the
site list. Every clear is still a clear.

**The three `cancelPending` reset sites I did NOT match, and why each is already covered:**

| site | why no clear is needed |
|---|---|
| `IsCancelPending()` 4-s self-clear `:496` | A timeout on the cancel *gate*. No order lifecycle event; the working entry's existence is unchanged by it. |
| raced-abort repair `:1643` | Reached only from the id-31 `not_open_order` response, i.e. *the scoped cancel lost and the entry filled*. `CancelWorkingEntryCoreAsync` already ran and cleared at `:4111`, so the field is provably `0` on arrival. |
| exchange-confirmed `cancelled` echo `:3358` | Every cancel the app issues goes through one of the two teardowns, both of which clear. The echo only re-opens the gate. |
| `TrailingStopLossOrderAsync` cancel-all `:4941` | Fires with a position open, so the fill clear at `:3423` has already run. `legAnchorPrice` is deliberately not cleared here either — the trailing transition needs its context — and this field follows it. |

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
it.** There is no path to a manual entry order that does not run `ExecuteOrderAsync :3957`, and that
**write** is unconditional — the veto changed only the value written, so this leg is untouched. A
manual placement writes `0`, which is the strongest possible overwrite: the chase then reads the
box. So the value a chase can read is always *this* order's own size.

Leg 2 needs one ordering check, because `:3957` sits **after** `Await SendWebSocketMessageAsync`.
Could a chase edit fire in that window and read a stale value?

- The chase gate (`:2186`) requires all three of `CurrentOpenOrderId` / `CurrentTPOrderId` /
  `CurrentSLOrderId` to be non-`Nothing`.
- **Every site that installs an id** — `:2997`, `:3150`, `:3163`, `:3216`, `:3229`, `:3239` — runs
  **inside a `Me.Invoke` lambda**, and the UI thread is held by `ExecuteOrderAsync` itself for the
  whole window. So ids cannot go `Nothing` → non-`Nothing` inside it. (The one receive-thread
  assigner, the id-778 restore at `:5562`, is a connect-time snapshot — and it is now also where D2's
  seed lives, under the same single-writer rule.)
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
   id returns `already_closed`, which `:1610` already downgrades to a gray benign line.

**A third residual, new with the veto and accepted by the reviewer:** on a *manual* entry the
resting order now keeps its placed size, while `ReanchorTPToFillAsync` still reads the live box. If
the owner edits the box mid-chase, entry and re-anchored TP leg can differ. Manual-only,
self-inflicted, and strictly smaller than the pre-N2b behaviour it replaces. Recorded, not fixed —
if it ever matters it is its own micro-spec.

## 4. §3 — which value `Position entered:` now prints, and why

**Chosen: the filled echo's own `amount`**, falling back to the retained `placedOrderSizeUsd`, then
to the box. §3 asked me to choose between *the retained placed size* and *the position model's own
size* and to say why; I took a third that is strictly better than either, and here is the reasoning:

- **Not `positionSizeUSD` (the position model).** It is the **total** position, so it would
  misreport an add as if the whole book had just entered; and it is seeded on the receive thread
  from the position channel (`:2909`) and the id-777 snapshot (`:5657`), neither of which is ordered
  against *this* order echo — so it can legitimately still be `0` at this line.
- **Not the retained placed size alone.** It is what was *sent*, which is the very thing the review
  warned against treating as evidence of what was *held*. It is right for the chase (that is what
  the chase is re-sending) but it is not the fill.
- **The echo's own `amount` is the fill's own size.** It comes from the same message
  `entryShownPrice` is already read from two lines above, it is the token `ApplyCloseFill` already
  uses for the *close's* size (`:5222`), and on an `order_state = "filled"` echo it is by definition
  the amount that filled. Same read shape as the existing `average_price` pair, so the two lines
  read as one idiom.

The `EntryTrailingOrder` sibling gets identical treatment. In practice it reads the same as before —
a trailing bracket is always placed at the box — and it is changed so the two lines cannot drift.

**Fallback ordering verified:** the `OpenPositions` clear at `:3423` runs *after* the per-order loop
(`Next` at `:3373`), so `placedOrderSizeUsd` is still live at both log lines.

## 5. Behaviour deltas beyond the defect — **NONE on the manual path (veto applied)**

My first version had one: *editing the Amount box while an order rests no longer resizes that
resting order.* **The owner vetoed it and was right** — that edit is a deliberate control, used when
the chase walks the entry closer to the TP. The value-conditional set (§1a) restores it in full:
a manual placement leaves the field `0`, so every manual chase edit reads the live box exactly as
before. New acceptance **3b** pins it.

So the manual path is now **byte-identical** to pre-N2b behaviour, and the trailing path is
byte-identical by construction (§1c). The only intended change is on the bridge path: a resting
**bridge** entry keeps its placed size and does not follow a box edit. Cancel it to intervene.

**What I got wrong here, since it is the reusable part:** I reasoned about the delta purely from the
code ("the box is the input for the *next* order") and judged it an improvement. It was a control
the owner uses on purpose. I did flag it rather than bury it, which is what let the veto happen —
but a behaviour change on a hot manual path deserved to be asked about, not assessed.

## 6. Censuses (re-run by me, scoped to `frmMainPageV2.vb`)

§4.6's seven, all **unchanged** from the `a6e3af1` baseline I measured myself:

`emergencyFired` **10** · `IsATRSlippageExcessive` **8** · `NextSlBackoff` **2** ·
`RecordCommandedSLPrice` **3** · `slUpdateFailures = 0` **1** · `isPlacingOrder` **13** ·
`lastPlacementAdmittedUtc` **13**

§4.6's new line **as amended** — `1 decl + 1 set + 2 chase reads + 1 restore seed + the clear sites
+ 2 display reads` — measured: **`placedOrderSizeUsd` = 15 raw grep** = **14 code-carrying lines +
1 comment mention**, being **12 constructs**:

| construct | count | lines |
|---|---|---|
| decl | 1 | `:412` |
| set | 1 | `:3957` |
| chase reads | **2** (not 3 — D3) | `:4247`, `:4332` |
| restore seed (D2) | 1 | `:5576`–`:5577` *(a 2-line `If` block — this is why 12 constructs occupy 14 lines)* |
| clears | 5 | `:3423`, `:4017`, `:4111`, `:4870`, `:5260` |
| display fallback reads (§3) | 2 | `:3266`, `:3313` |

⚠️ **Grep trap, flagged so a reviewer is not misled:** `orderAmountVal` reads **26**, up from **24**
at `a6e3af1` — but **no read was added or removed**. The two log lines gave up their `orderAmountVal`
and the two §3 fallback lines took it back (net zero); the `+2` is entirely **comment** mentions.
**Independently confirmed by the reviewer**: code-carrying lines are **16 before and 16 after**;
comment lines went 8 → 10. Still 16 code / 10 comment after the veto and D2 commits. Same
comment-only-movement trap the handover flags. A repo-wide grep inflates it further (the docs
discuss it); the number above is `frmMainPageV2.vb`-scoped.

## 7. The three escalated items — all RULED, none outstanding

Raised in `spec-back-chase-preserve-placed-size.md` (`8370541`) before any code was written, per the
standing rule. Rulings in `review-chase-preserve-placed-size.md`; spec amended in place.

- **D1 — REJECTED. My error, and the reasoning trap is the reusable part.** I claimed
  `ReanchorTPToFillAsync` fires on essentially every chased bridge entry. It fires on **none** of
  them: the staging gate's fourth conjunct is `manualTPval <= 0D` — its own comment reads *"only
  auto-offset TPs re-anchor"* — and a bridge act always sets a manual TP
  (`SetTradeTargets(manualTP:=RoundToTick(p.Target))`, with gate 4.4 refusing `Target <= 0`).
  **I quoted the full four-conjunct gate, proved ONE conjunct's reachability
  (`entryFillPrice <> legAnchorPrice` under `EntryOnlyChase`, which is correct), and generalised
  that to the whole gate.** On the manual path where the re-anchor *does* run, the position size IS
  the box, so `orderAmountVal` is already right. **No fix, no commit.** My "N2 must NOT be enabled
  until this is ruled" is **void** — it rested entirely on the wrong premise — and the unverified
  OTOCO `first_hit` concern is moot, since no mismatched TP leg is ever created.
- **D2 — UPHELD, option A. Implemented** (`5fc5b16`): the id-778 restore now seeds the field from
  the snapshot order's `amount`, single-writer (`If placedOrderSizeUsd = 0D`), so a snapshot can
  never overwrite a live in-process value. The restart hole is closed.
- **D3 — accepted, and the reviewer made it stronger than I argued.** I gave
  `UpdateStopLossForTrailingOrder` no read, on a reachability argument. The reviewer's form is
  better: the `placedOrderSizeUsd = 0D` at `:4870` sits in the same placement seed, a few lines
  ahead of **`:4879` — the file's only writer of `isTrailingStopLossPlaced`**, which is the sole
  gate (`:2539`) on the trailing reposition block. So the field is `0` **by construction** on every
  path reaching that third edit site (`:4599`), not merely by essay. **§4.6 stays at 2 chase
  reads.**

**Net on the escalation:** two of three were right and one was wrong. D2 would have shipped a hole
only a restart could expose. D1 was wrong but flagged rather than acted on, with the unverified
exchange behaviour explicitly not claimed.

## 8. Runtime acceptances — NOT driven by this seat (I placed no trades, armed no bridge)

Acceptance 1 (gate per commit) and 6 (censuses) are **done and reported above**. 2, 3, 3b, 4 and 5
are runtime.

⚠️ **Correction to my own first draft:** it said these "require an owner-driven placement under the
triple-placement WATCH protocol". **That is stale.** The WATCH protocol's restriction on
harness-driven placement was **lifted 2026-08-01** (`investigation-triple-placement-2026-08-01.md`
§5/§7.2 — the "leans harness-side" premise was overturned; the harness places one order per Invoke
and the exposure was app-side, since fixed by the single-flight + debounce guards). It is replaced
by a **mandatory effect assertion**: any harness-driven placement goes through
`tools/place-and-verify.ps1`, never `click-PLACES-ORDER.ps1` directly. So **all five legs are
harness-drivable** — see §9. What stops *me* is the seat boundary, not the protocol.

**Preamble applies to the OWNER'S bin only.** Back up settings → **rebuild x64** (the gate is
AnyCPU-only) → **re-read the window title for `— TESTNET`** (the x64 rebuild clobbers the bin's
testnet `secrets.json`) → teardown and restore afterwards. **A harness run needs none of it**: the
harness bin has its own settings file and journal DB, so no backup, no x64 rebuild, and no rows to
delete from the owner's live journal.

| # | setup | the observation that decides it |
|---|---|---|
| **2** — THE acceptance | TESTNET, bridge Live, N2 checkbox **ON**, session policy **OFF**, Amount box **10**, `risk_per_trade_usd = 1`, payload `entry 63000 / stop 62800` ⇒ computed **310**. Let it chase (an `Order repositioned:` line must appear), then flatten. | **The reduce must report 310, not 10.** Exchange-derived, and the authority. **Do not accept the `Buy limit order placed For 310` line as proof** — that line was already correct when 3b failed. |
| **3** — manual regression | N2 checkbox OFF. Manual Limit BUY, box **10**, chased at least once. | Position **10**. Here `placedOrderSizeUsd` is **`0`** (the amended value-conditional set). |
| **3b** — ⭐ **NEW, the vetoed behaviour must still work** | Manual Limit BUY, box **10**; **while it rests, type `20` into the Amount box**; let it be chased at least once. | The resting order must **resize to 20** and the position must be **20**. This is the leg that proves the set is value-conditional rather than unconditional — i.e. that the veto is actually in force. |
| **4** — the stale-size case | A bridge act at a non-box size → flatten → then a **MANUAL** placement at the box size. | The manual order is the **box** size. This is what proves the lifecycle clears. |
| **5** — session-policy path | A `0.5` policy line, box **ABOVE the clamp — e.g. 40, not 10** (see below), N2 checkbox OFF so the multiplier is the only sizing input. Chase at least once. | The position holds the **reduced** size (20), not the box. This is the pre-existing half of the defect. |

⚠️ **Acceptance 5's box must be above the clamp.** At box 10 a `0.5` mult clamps straight back up to
10 and the divergence vanishes — that clamp is *exactly* what hid this defect for weeks, so running
5 at box 10 would produce a confident-looking pass that proves nothing.

**D1's TP-leg observation is DROPPED** from acceptance 2 — the re-anchor never runs on a bridge
entry, so there is nothing to see. Do not spend a runtime leg on it.

Teardown (owner's bin only): restore settings, verify, delete the testnet journal rows.

## 9. What the harness can drive — verified against the scripts, not assumed

Checked at the sites in `tools/`, because the answer changes who has to do what.

**3 and 3b need no bridge at all and are fully harness-drivable**, with no engine interaction, no
payload file, and no ARM/START:

```bash
powershell -NoProfile -File tools/launch-app.ps1
powershell -NoProfile -File tools/set-textbox.ps1 txtAmount 10
powershell -NoProfile -File tools/place-and-verify.ps1 btnLimit "Buy limit order placed"
# 3b only — while the entry rests:
powershell -NoProfile -File tools/set-textbox.ps1 txtAmount 20
# then let a reposition land, and flatten:
powershell -NoProfile -File tools/place-and-verify.ps1 btnReduceMarket "Reduce-only MARKET"
powershell -NoProfile -File tools/read-log.ps1
```

**The harness is strictly BETTER than a hand for 3b.** `txtAmount.TextChanged` is wired to
`SyncTradeInputsFromUi` (`:532`), so a bare UIA `SetValue` updates `orderAmountVal` immediately —
**no `-CommitViaBlur` needed** (that switch exists for the AutoTradeSettings gate boxes, which
commit on blur). The edit therefore lands at a known instant rather than at typing speed, which
matters because the entry rests one tick inside the far side and can fill quickly. Any reposition
*after* the edit satisfies the leg; there is no need to beat the first one (the chase throttle is
350 ms, so several are expected).

**2, 4 and 5 need a bridge act.** Every control is scriptable — `select-combo-item.ps1
cboBridgeMode Live` and `toggle-checkbox.ps1 ARM -State on` are both TESTNET-gated rather than
forbidden, `chkRiskSizeBridge` / `chkSessionPolicyOn` carry harness `AccessibleName`s, and
`btnBridgeStartStop` is reachable via `click-PLACES-ORDER.ps1` (the one privileged script; the
generic `click-button.ps1` refuses it via `trade-buttons.txt`). The extra cost is the payload
choreography, and it is the part that bites:

1. **ATRSlip must be CHECKED**, or `TryStart` refuses outright: `SignalBridge.vb:279`–`:299` gates
   START on mode = Live **and** local ARM **and** a fresh payload **and** engine ARM **and** the
   slippage-cap checkbox. An unchecked box also means neither chase-abort arm is evaluated.
2. **STOP the engine first** — `write-payload.ps1` refuses while a `DeribitVerdictEngine` process
   is running (deliberately blunt, owner-ruled; do not re-propose a path-aware variant).
3. `write-payload.ps1 -Direction LONG -Entry 63000 -Stop 62800 -Target …`
4. **Write the payload TWICE: write → START → write.** Both halves are forced by the code, which is
   why this is easy to get wrong: `TryStart` refuses unless `IsFreshNow` (`:286`), so a payload must
   *already* be on disk for START to succeed — and evaluation is **file-change-only**, so START does
   not act on what is already there. The first write unblocks START; the second one is the act.
5. `restore-payload.ps1` afterwards, then **restart the engine**. Treat the restore as mandatory,
   not cleanup — with the engine stopped, `write-payload` defaults to the LIVE payload path.

**Who drives what.** `HANDOVER-6.md` §3.2 settles it and matches what I found in the scripts: the
reviewing seat runs these on the harness itself, and **the owner is needed only for bridge
Mode/ARM/START on the bridge legs**. Precedent: the coordinator drove all four SF2 acceptances and
the EV §6.3 manual arm on the harness; for N2's runtime legs the owner drove Mode/ARM/START while
the coordinator drove the rest. **This seat drives none of it** — implementer seats place no trades
and arm no bridge, which is a seat rule and is unaffected by the WATCH amendment.

Related: `spec-chase-preserve-placed-size.md` (as amended) · `review-chase-preserve-placed-size.md`
(the rulings + the veto) · `spec-back-chase-preserve-placed-size.md` (the escalation, now all
ruled) · `review-risk-sized-bridge-trades.md` §2/§3 (the runtime evidence and the pre-existing
attribution) · `spec-risk-sized-bridge-trades.md` (N2 — still gated on N2b's acceptance 2, on N2b's
own merits, not D1's) · `spec-session-policy-gate.md` §4 (`sizeUsdOverride` and the
`0`-means-the-box convention this field reuses) · `spec-entry-chase-v2.md` §4 (`EntryOnlyChase`,
`legAnchorPrice`).
