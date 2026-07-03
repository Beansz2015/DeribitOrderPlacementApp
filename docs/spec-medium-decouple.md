# Implementation Spec — MEDIUM #10: decouple the analysis form via a clean `frmMainPageV2` API

**Severity:** 🟡 Medium (structure) — but **highest-value of the MEDIUM set**: this defines the contract the new analysis module will target, and fixes a real latent default-instance bug.
**Source:** `docs/CODE_AUDIT.md` #10. Reframed for the current reality: `FrmIndicators` is being **replaced** by a new analysis engine, so this spec focuses on the **durable `frmMainPageV2` side** (the API + removing default-instance coupling), not on rewiring the doomed `FrmIndicators`.
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET. `Option Strict Off` on legacy files (new files: `Option Strict On`).
**Start point:** `housekeeping-now` tip (after HIGH + cross-thread + transition-race, all landed). Build green.

> ⚠️ Locate code by symbol; line numbers drift.

---

## 1. Why this one matters most now

The new analysis module will need to drive `frmMainPageV2` (place orders, read ATR-target inputs, check state). Today that coupling is done the worst way:

- **Auto-trade fires by faking button clicks:** `FrmIndicators.ExecuteAutomatedTrade` calls `mainForm.btnBuy.PerformClick()` + `mainForm.btnLimit.PerformClick()` (and `btnSell`). Meanwhile `frmMainPageV2.ExecuteAutomatedOrder(orderType)` **already exists for exactly this and is unused.**
- **It writes into the other form's controls:** `FrmIndicators.btnATR_Click` sets `frmMainPageV2.txtTakeProfit`/`txtTrigger`; it reads `txtPlacedPrice`.
- **It mixes the passed instance with the VB default instance:** it uses `CType(_host, frmMainPageV2)` in some places but `frmMainPageV2.USDPublicSession` (the **default instance**) in others — they're the same object only by the luck of how the app starts. This is a real latent bug.

If we define a clean API on `frmMainPageV2` **now**, the new module targets it from day one instead of inheriting the `PerformClick`/default-instance hacks. That's the point of doing this before the replacement lands.

## 2. Scope (do the durable side; skip the throwaway side)

**In scope — `frmMainPageV2`:**
1. Add an explicit public API (below), routing existing internal logic through it.
2. Remove **all** reliance on the VB default instance for cross-form data — expose it as API instead.

**Out of scope / minimal:**
- **Do not invest in rewiring `FrmIndicators`** to the new API beyond what's needed to keep it compiling and functch (it's being replaced). It may keep using `PerformClick` for now, or you may do a light switch to prove the API — your call, but don't polish it. The **new module** is the intended first real consumer.
- Don't touch the receive-loop internals (that's #9) or order/SL logic (leave the HIGH/cross-thread/transition-race fixes intact).

## 3. Required outcome

1. **A documented public surface on `frmMainPageV2`** sufficient for an external module to run the existing automated-trade flow without touching controls or the default instance. At minimum:
   - `Public Async Function PlaceAutomatedOrder(side As String) As Task` — wraps the existing entry path (what `btnBuy/btnSell` + `btnLimit` do today). Reuse/formalise the existing `ExecuteAutomatedOrder`; set direction (`TradeMode`) + place in one call. No `PerformClick` from callers.
   - `Public Sub SetAtrTargets(takeProfit As Decimal, stopLoss As Decimal)` — replaces external writes to `txtTakeProfit`/`txtTrigger` (updates the mirror fields + display via the existing marshalling).
   - Read-only state: `Public ReadOnly Property PlacedPrice As Decimal`, `Public ReadOnly Property SessionPnLUSD As Decimal` (expose `USDPublicSession`), plus the existing `IsWebSocketConnected` and `CanMakeAPIRequest`. Add `Public ReadOnly Property CurrentAtrTargetsInEffect` only if useful.
   - Keep it minimal and honest — expose what a signal engine actually needs (place order, set ATR targets, read placed price / session PnL / connection + rate-limit readiness).
2. **No default-instance access for cross-form data** anywhere. Everything goes through an instance reference (the host passed in) + these properties.
3. **Behaviour preserved:** the existing manual buttons still work unchanged; automated placement through `PlaceAutomatedOrder` produces the **same order payloads** as the button path.
4. **Preserve the recent fixes:** the engine fields (`placedPrice`, etc.), `UiInvoke` marshalling, `cancelPending` logic, and the reposition/SL behaviour must be untouched. The API is a thin public wrapper over existing internals, not a rewrite.

## 4. Recommended approach

- Audit every `frmMainPageV2.<member>` access from `FrmIndicators` (grep `frmMainPageV2\.` and `_host`/`mainForm` in `FrmIndicators.vb`) — that list *is* the required API surface.
- Implement `PlaceAutomatedOrder` by calling the same internal path the button handlers call (`ExecuteOrderAsync`/`ExecuteAutomatedOrder`), setting `TradeMode` for side. Confirm the payload matches the manual path exactly.
- `SetAtrTargets` writes the mirror fields (`takeProfitOffset`/`triggerDistance` or whatever the cross-thread fix named them) and the display via `UiInvoke`.
- Replace `USDPublicSession` default-instance reads with the `SessionPnLUSD` property on the host instance.
- Document the surface in a short comment block (or a `docs/` note) so the new-module spec can reference it.

## 5. Acceptance / test plan (owner, test sub-account)

- Manual Buy/Sell/Limit/Market buttons behave exactly as before.
- Driving `PlaceAutomatedOrder` (a temporary test button or the light FrmIndicators switch) places entry+TP+SL with the **same payload** as the manual path — verify against the log/exchange.
- `SetAtrTargets` updates the TP/Trigger inputs and the engine uses them.
- No `frmMainPageV2.<default-instance>` access remains (grep). No cross-thread errors. Build green.

## 6. Ground rules

Implement directly; local commits per logical change; **do NOT push**; 0-error build per commit; preserve HIGH/cross-thread/transition-race behaviour; new files `Option Strict On`. Produce the standard **implementation report** (approach, per-member API list + before→after, default-instance removals, payload-parity evidence, build/commits/deviations, test steps, confirmation).

**Suggested model/effort: Opus 4.8, high.** It's API design over live-order internals — must keep payload parity and not disturb the recent fixes.
