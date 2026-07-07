# Spec-back — Fill re-anchor (`ReanchorLegsAsync`) targets stale leg IDs and fires too early

**For:** the orchestrator (to turn into a forward fix spec) and any spec touching `HandleOrderPositionUpdates` (the open/untriggered/filled echo handler) or the OTOCO leg-ID model. **What this is:** a bug reconstructed **from the committed code** (`843b05a`) + one live runtime observation (owner trade #47). It states the mechanism with anchors and raises **one genuine design decision** (whether/how to re-anchor the SL leg) that is the orchestrator's/owner's call. Written 2026-07-08. File: `DeribitOrderPlacementApp/frmMainPageV2.vb`. **No code changed** — owner asked for the spec-back first. Entry-chase v2 is already **pushed** (`origin/master = 44cd51e`); this is a follow-up defect on top of it.

> ⚠️ Anchors are at `843b05a` and will drift. Locate by symbol.

**Origin:** owner runtime test, trade #47 (SHORT 10). The entry **chased before filling** (`63126 → 63125.50 → 63112.50`), so for the first time in a live test `legAnchorPrice (63126) ≠ fillPrice (63112.50)` and the entry-chase-v2 §4 fill re-anchor (`ReanchorLegsAsync`) actually fired. It logged success, then threw two red API errors:

```
Position entered: SHORT 10 @ $63112.50
Legs re-anchored to fill $63112.50: TP $63052.50, SL trigger $63172.50 / limit $63202.50
API ERROR (id 223344 order edit): code 10004 - order_not_found
API ERROR (id 223346 order edit): code 10004 - order_not_found
Triggered SL placed @ $63160.5           ← note: AFTER the re-anchor
```

Trade #46 filled without chasing (`legAnchorPrice == fillPrice`), so the hook was a no-op and the bug stayed hidden through the entry-chase-v2 runtime sign-off.

---

## 1. Symptom

Two `10004 order_not_found` errors on **every entry that chased before filling** (and the trailing variant would throw one — id 223346 only). The ids map exactly to the two edits `ReanchorLegsAsync` sends via `SendRateLimitedUpdate`: **223344** = the TP edit (regular-limit branch), **223346** = the SL edit (trigger branch). The `Legs re-anchored to fill …` line is **optimistic logging** — printed before the edits are acknowledged — so it reads as success immediately above the failures.

**Runtime impact: harmless, but real.** The edits fail and no-op; the TP/SL legs stay at exchange/placement geometry; the trade closed normally. The costs are (a) the intended fill-anchored tightening **silently doesn't happen**, (b) red `API ERROR` noise on every chased entry, (c) a misleading success log. No money bug — the entry-only drift bound (`legReanchorDriftMax = Min(tpOffset, triggerDistance)/2`) already guarantees a chased fill can't overrun its own TP, which was the safety net §4 relied on.

## 2. Root cause — the OTOCO fill retires the legs and re-creates them with new IDs

The original author documented this at `:2160` and `:2192`:

```
'Step 1. Placed order : EntryLimitOrder = Open / TakeLimitProfit + StopLossOrder = Untriggered
'Step 2. In position : EntryLimitOrder = Filled / TakeLimitProfit + StopLossOrder = Triggered AND new TakeLimitProfit + StopLossOrder = Open
'When order is executed, TakeLimitProfit/StopLossOrder becomes 2 orders each
'- 1 with triggered state (The order before execution) and 1 with open state (Triggered by execution)
```

So the app tracks leg IDs in **two field pairs**, deliberately:

| Field pair | Set where | Holds |
|---|---|---|
| `CurrentTPOrderId` / `CurrentSLOrderId` | `untriggered` echo (`:2348` / `:2361`) | the **pre-fill** working-entry legs |
| `PositionTPOrderId` / `PositionSLOrderId` | `open` echo (`:2195` / `:2202`) | the **post-fill** live position legs |

The restore snapshot handler corroborates the split: `Position*` is set unconditionally, `Current*` only `If workingEntryFound` (`:4486`–`:4495`).

**`ReanchorLegsAsync` edits the wrong pair.** It sends to `CurrentTPOrderId` (`:3444`) and `CurrentSLOrderId` (`:3447`) — the **pre-fill** IDs, which the exchange retired the instant the entry filled ⇒ `order_not_found`. The live post-fill legs are the `Position*` IDs, which it never touches.

## 3. Second, independent problem — it fires before the new IDs exist

Even retargeting `Current* → Position*` is **not sufficient**, because the re-anchor runs on the bare `filled` EntryLimitOrder echo (`:2395`), which is processed **before** the new `open` TP/SL echoes populate `Position*OrderId`. The log order proves it: `Position entered` → `Legs re-anchored` (+errors) → *then* `Triggered SL placed` (the `open` SL echo, which sets `PositionSLOrderId` at `:2202` and flips `SLTriggered` at `:2213`). At the fill echo instant, **both** ID pairs are stale/unset for the new legs. The re-anchor must be **deferred** to when the post-fill `open` echoes arrive.

## 4. The design decision for the orchestrator — does the SL leg get re-anchored, and how?

The TP side is unambiguous: TP is a plain limit that neither trails nor gets chased, so a deferred re-anchor to `PositionTPOrderId` (fill-anchored price) is pure win. The **SL side is a genuine decision** because by the time the fix could run, the SL is already:

- flipped `SLTriggered = True` (`:2213`),
- **adopted** by the three-way echo classifier — `placedStopLossPrice`/`emergencyBaseline` set from the exchange's authoritative triggered price (`:2233`–`:2245`),
- and owned by the SL-chase (`spec-sl-chase-v2.md`), which begins chasing it immediately.

Two defensible designs:

- **(a) Keep the SL re-anchor (retargeted + deferred + interleaved).** Enforces the trader's *intended* stop distance from the actual fill (`fill ± triggerDistance`). This matters: the SL-chase only moves the stop **adversely** (it will never loosen an exchange-trailed stop back out to the intended distance), and in #47 the exchange-created SL (`63160.5` limit) landed **tighter** than the intended `63202.50` — a tighter-than-structural stop, which cuts against the owner's structural-stop philosophy. Cost: the re-anchor and the trigger-flip **adopt** both write `placedStopLossPrice`/`emergencyBaseline`, and the re-anchor's `RecordCommandedSLPrice` must land so the discriminator recognises the edit as ours (no spurious `Manual SL edit`). Sequencing these in the `open` StopLossOrder branch is the delicate part.
- **(b) Drop the SL re-anchor; TP-only.** Let adopt + SL-chase own the SL entirely. Simplest and lowest-risk, but concedes the tighter-than-intended stop, and reverts `ReanchorLegsAsync` to a non-SL-context path ⇒ **reset-site count 8 → 7** (doc churn — see §6).

**Recommendation:** keep the TP re-anchor; **lean (a) for the SL** to preserve the owner's intended stop distance — but this is explicitly the owner's risk-model call, and the adopt/chase interleave is what makes it worth a careful forward spec rather than an inline patch.

## 5. Recommended fix shape (for the forward spec)

1. **Defer, don't edit at the fill echo.** In the `filled` EntryLimitOrder / EntryTrailingOrder branches (`:2395` / `:2426`), replace the direct `ReanchorLegsAsync` call with setting an order-context flag + captured fill: e.g. `pendingReanchorFill As Decimal` (0 = none). Reset it wherever `legAnchorPrice` resets (the same order-context death sites — nuclear cancel `:2519`-area, scoped cancel, close completion), so a stale pending re-anchor can't fire on the next position.
2. **TP re-anchor** in the `open` **TakeLimitProfit** branch (`:2194`, after `PositionTPOrderId = orderId`): if `pendingReanchorFill > 0`, edit `PositionTPOrderId` to the fill-anchored TP (honoring `manualTPval`), then clear the pending flag for the TP side.
3. **SL re-anchor** (if design (a)) in the `open` **StopLossOrder** branch (`:2201`), sequenced against the existing trigger-flip adopt (`:2233`): edit `PositionSLOrderId` to the fill-anchored trigger/limit, and drive the reference writes + `RecordCommandedSLPrice(newSLprice)` in an order that keeps the discriminator consistent (the adopt and the re-anchor must not fight over `placedStopLossPrice`/`emergencyBaseline`). This is the part that needs the spec's care.
4. **Honest logging:** move the `Legs re-anchored …` line to after a confirmed send (or reword to "requesting"), so it never prints above a failure again.
5. **Credit/headroom + single-flight:** the deferred edits now run inside the echo handler (UI-thread `Me.Invoke` region) rather than the receive-thread fill branch — the spec must place them thread-correctly (the `open` branch is inside `Me.Invoke`; leg edits are async sends — mirror how the existing chase paths marshal).

**Minimal-scope alternative (if the owner wants the errors gone with least risk):** drop the §4 fill-re-anchor hooks entirely (`:2395` / `:2426`) and accept placement-anchored geometry — safe (drift-bounded), reverts reset-site count to 7, loses the tightening. This is the "disable" fallback; (1)–(4) is the real fix.

## 6. Coordination warnings

- **Touches the hottest region.** The fix lives in `HandleOrderPositionUpdates`' `open`/`filled` echo branches — the same code that holds the **three-way SL echo classification** (`spec-back-session-2026-07-07.md`) and the trigger-flip adopt. Any SL re-anchor here interleaves with that discriminator; coordinate with anything else in this handler.
- **`RecordCommandedSLPrice` invariant still binds.** If design (a), the SL re-anchor is a programmatic SL edit ⇒ it **must** record (HANDOVER-2 §3), or its echo is misread as a manual edit.
- **Reset-site count may move.** (a) keeps `ReanchorLegsAsync` (or its successor) as the **8th** SL-context reset site — no count change. (b) removes it ⇒ **8 → 7**, requiring the reverse of entry-chase v2's doc edits: `spec-back-session-2026-07-04.md` §1 (×2) + §10 bullet 4, `HANDOVER-2.md` §3, and the two code comments (the commanded-set header near `:61` and the `ResetCommandedSLPrices` header near `:4064`, both currently reading "8 … sites").
- **Entry-chase v2 is pushed.** `origin/master = 44cd51e` already contains the buggy `ReanchorLegsAsync`. This is a new fix commit on top, not an amend.
- **`legReanchorDriftMax` fallback interaction:** in manual-targets mode the re-anchor re-sends absolute manual prices — the deferred fix should keep the same "no pointless edit" behavior (skip if the fill-anchored price equals the resting price).

## 7. Owner runtime evidence + test plan for the forward fix

- **Evidence (trade #47):** the two `order_not_found` ids (223344 TP, 223346 SL), the `Legs re-anchored` line printing above them, and `Triggered SL placed @ 63160.5` arriving after — all consistent with §2/§3. Exchange-created SL limit `63160.5` vs intended `63202.50` is the §4 tighter-stop evidence.
- **Tests for the fix:** (1) chased entry → TP re-anchors to the fill on the exchange UI, **no** `order_not_found`; (2) chased entry → SL ends at the intended fill-anchored distance (design (a)) with **no** spurious `Manual SL edit`; (3) non-chased entry (fill == anchor) → no re-anchor, no edits; (4) manual-TP/SL mode → legs stay at the absolute prices; (5) cancel mid-fill → no deferred re-anchor fires on the next position (pending flag cleared at context death); (6) trailing-entry chased fill → SL-only re-anchor, no error.

Related: `impl-report-entry-chase-v2.md` (§4, the origin of `ReanchorLegsAsync`), `spec-back-session-2026-07-07.md` §1/§4 (the three-way echo classification the SL re-anchor interleaves with), `spec-sl-chase-v2.md` (the chase that owns the SL post-fill).
