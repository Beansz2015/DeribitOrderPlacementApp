# Spec-back — UI-marshal window-handle race hardening (as-built)

**What this is:** a specification reconstructed **from the committed code**, not from a forward spec (there wasn't one — this was an owner-runtime-found crash fixed directly). It states the behavior contract the code now enforces, with anchors, so a reviewer can diff it against the code (`git show 8bcf770`). Written 2026-07-04 against commit `8bcf770` on `master`. Files: `DeribitOrderPlacementApp/FrmIndicators.vb`, `DeribitOrderPlacementApp/frmMainPageV2.vb`. Diff: 34 insertions, 14 deletions.

**Origin:** owner runtime test — restarting into an open position during a market spike broke in the VS debugger at `FrmIndicators.ProcessMessage → Me.Invoke(Sub() UpdateSignals())` with `System.InvalidOperationException: Invoke or BeginInvoke cannot be called on a control until the window handle has been created`.

**Reconstruction method:** each section describes what the merged code does at the marshal points and startup, independent of the commit message, plus the audit outcome for every form that marshals from a background thread.

---

## The defect class

A **background thread marshals to a WinForms control via `Control.Invoke` before the target's window handle exists (or after it is torn down).** `Control.Invoke`/`BeginInvoke` require a created handle; calling them outside that window throws `InvalidOperationException` ("handle not created"). In this app the background producers are:

- **FrmIndicators** — its own WebSocket receive loop (`ConnectAndStream` → `ProcessMessage`, on a threadpool thread) and its `System.Timers.Timer` reconnect (`OnPollElapsed`, threadpool).
- **frmMainPageV2** — the Deribit receive loop (`ReceiveWebSocketMessagesAsync`, threadpool) and the logger it calls (`AppendColoredText`).

The marshal points are only *safe* if the handle is guaranteed present. This change makes that guarantee explicit (realize-the-handle) and defends the residual reconnect/teardown windows (guarded marshal).

**Severity in practice:** the failing FrmIndicators marshal sits inside a fire-and-forget `Task.Run`, so in production it becomes an *unobserved task exception* — non-fatal in .NET 9 — and the resilience backstop `My.MyApplication.OnUnobservedTask` (`ApplicationEvents.vb:34`) logs it to `crash.log` and calls `SetObserved()`. The observed symptom (a hard stop) is the **debugger** breaking on the user-unhandled exception; that tick's `UpdateSignals` is lost either way.

---

## Region 1 — FrmIndicators: realize the handle first

**Contract:** the window handle exists before any background code can marshal to the form.

- `FrmIndicators.New(host)` (`FrmIndicators.vb:53`): after `InitializeComponent()` / `_host = host`, `Dim forceHandle As IntPtr = Me.Handle`. The constructor runs on the UI thread (it is called from `frmMainPageV2_Load` → `_indicators = New FrmIndicators(Me)`, `frmMainPageV2.vb:417`), so accessing `Me.Handle` realizes the handle on the UI thread, before `_indicators.Show()` and therefore before `FrmIndicators_Load` starts `Task.Run(AddressOf ConnectAndStream)` (`FrmIndicators.vb:67`).

**Postcondition:** by the time `ConnectAndStream`'s receive loop runs, `Me.IsHandleCreated` is true. (WinForms already creates the handle during `Show()` before `Load`; this makes the invariant explicit and independent of show-timing.)

**Note (why this alone is not sufficient):** the handle can still be momentarily absent during teardown, and the `OnPollElapsed` reconnect re-enters the same marshal path later in the session. Region 2 guards those windows.

---

## Region 2 — FrmIndicators: guarded marshal helper

**Contract:** a background marshal that finds no live handle **drops the UI update** rather than throwing.

- `UiInvokeSafe(action)` (`FrmIndicators.vb:220`): `Try … If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then Me.Invoke(action) … Catch (swallow)`. The `Catch` covers the check-then-invoke race (handle destroyed between `IsHandleCreated` and `Invoke`).
- Both receive-loop marshals now route through it:
  - `Task.Run(Sub() UiInvokeSafe(Sub() UpdateSignals()))` at `FrmIndicators.vb:273` — after chart **history** is loaded.
  - `Task.Run(Sub() UiInvokeSafe(Sub() UpdateSignals()))` at `FrmIndicators.vb:315` — on each live **candle** subscription message.
  - The outer `Task.Run` is retained: it offloads `UpdateSignals` (indicator math + `lblScore` update, marshalled onto the UI thread by `Invoke`) off the receive-loop thread so the loop is not blocked by the synchronous `Invoke`.

**Dropped-update semantics:** skipping a tick is harmless — `UpdateSignals` re-runs on the next history/candle message once the handle is up. Indicator state (incl. ATR consumed by frmMainPageV2) converges on the next tick.

---

## Region 3 — frmMainPageV2: guard the receive-thread logger

**Contract:** `AppendColoredText`, called from the receive thread, does not throw when the form handle is absent.

- `AppendColoredText` (`frmMainPageV2.vb:3715`): before the `Me.Invoke` that appends to the RichTextBox, `If Not (Me.IsHandleCreated AndAlso Not Me.IsDisposed) Then Return`, and the `Me.Invoke` is wrapped in `Try … Catch` (drop the line on the check-then-invoke race). Previously a raw `Me.Invoke` — a latent instance of the same class (not observed in practice because frmMainPageV2's own handle is created at `Load`, before its receive loop connects, but unguarded against teardown / any pre-handle background log).

---

## Audit — every form that marshals from a background thread

| Form | Background producers | Verdict |
|---|---|---|
| **FrmIndicators** | receive loop (`ProcessMessage`), poll/reconnect timer (`OnPollElapsed`) | **Fixed** (Regions 1–2). Heartbeat marshal (`:269`) and backtest re-seed (`:1856`) were **already** `Try/Catch`-guarded; the `InvokeRequired`-guarded marshals (`:1439/:1567/:1600/:1629`) are safe; `UpdateSignals`' inner `Me.Invoke` (`:681`) is only reachable after `UpdateSignals` is already on the UI thread; the backtest loop marshal (`:1778`) runs only when the form is shown (user-initiated). |
| **frmMainPageV2** | Deribit receive loop, restore/close paths | `UiInvoke` (`:117`) and the marshals at `:337/:380/:4207` **already** guard `IsHandleCreated` ✅. `AppendColoredText` **fixed** (Region 3). The restore-hardening code (`HandleOpenOrdersSnapshot`, `ProcessPositionData`, `CompletePositionClose`) marshals only via `UiInvoke` / `Me.Invoke` on paths that run after connect (post-`Load`), and displays via the already-guarded `UiInvoke`. |
| **AutoTradeSettings** | none | **Clean** — no `Me.Invoke` / `Task.Run` / timer / async; nothing marshals from a background thread. |

---

## Behavior contract / invariants (post-change)

1. **Realize-then-guard.** Every form with a background marshal either (a) realizes its handle before the producer starts, or (b) guards each marshal with `IsHandleCreated AndAlso Not IsDisposed` (+ `Try/Catch` for the check-then-invoke race) — FrmIndicators does both.
2. **Drop, don't crash.** A marshal with no live handle drops that UI update; it is never fatal and never blocks the producer.
3. **No behavior change on the happy path.** Once the handle exists (the normal steady state), every guarded marshal behaves exactly as the prior raw `Me.Invoke`.

---

## Residuals / known-theoretical

1. **`forceHandle` is an intentionally-unused local** (`FrmIndicators.vb:53`) — the side effect of accessing `Me.Handle` is the point (handle realization). VB emits no unused-local warning; build is 0/0.
2. **Early-handle creation in the constructor** fires `OnHandleCreated` before `Show()`. For this form (no handle-order-sensitive designer logic observed) it is benign; the subsequent `Show()` reuses the existing handle.
3. **Heartbeat / backtest marshals left on their existing `Try/Catch`** rather than re-routed through `UiInvokeSafe` — functionally equivalent, minimal churn; a later consolidation could unify them on the helper.
4. **Dropped ticks during the (brief) pre-handle window** are not replayed; the next message re-runs `UpdateSignals`. No persistent state is lost.
5. **Not a production-fatal defect** — see the severity note above; the fix's primary value is removing the debugger break during restart testing and the silent per-tick loss.
