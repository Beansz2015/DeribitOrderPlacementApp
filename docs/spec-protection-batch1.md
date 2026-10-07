# Spec — position protection, batch 1: the guards that do not wait for the testnet experiments

**Origin:** `docs/triage-adversarial-audit-2026-10.md`. Owner ruling 2026-10-07: split the fixes **only
where a guard does not depend on the testnet experiments**; anything that does joins the batch after
them. Rows `A*n*` are the engine report's (`docs/adversarial-audit-2026-09-24.md` §B Band A, engine repo,
branch `claude/great-keller-s5f4gp`); `G*n*` are the stop-lifecycle audit's.
**Status at writing:** open, no code written. Facts verified at HEAD `0bda3ad`, 2026-10-08.
**Scope:** `frmMainPageV2.vb`, `RemoteNotifier.vb` only if its API needs a priority it lacks; OrderCheck.
**Order path, receive path → Opus, high.**
**Run order:** implement `docs/spec-chase-anchor-reset.md` first (already written; same seat is fine,
separate commits), then this.

---

## §0 — What is in, and why each item does not depend on the experiments

| Item | Row | Why it is independent of X-1 |
|---|---|---|
| 1. Reconnect forever, page the owner, time out the auth wait | A15 (G4, G16) | Pure socket lifecycle. No exchange order semantics. |
| 2. Position-management side from the position's sign | A16 (G6) | The sign of `positionSizeUSD` is the truth whatever Deribit does with legs. |
| 3. Restore checks that an open position has a stop | A20 (G10) | Reads the order list the app already requests; no new exchange behaviour. |
| 4. The emergency close stops claiming success it has not seen | A12 (G1), **message part only** | Changes what is logged and paged, not what is sent. |

**Deferred to the post-experiment batch:** A12's **reorder** (reduce first, cancel on confirmed flat) —
depends on X-1(g), how a reduce-only market behaves while reduce-only legs rest. A13's dead-leg branch —
depends on X-1(a), what `order_state` Deribit pushes for a crossed or killed stop.

## 🚫 Do-not-touch

| Thing | Why |
|---|---|
| The emergency's **send order** (cancel-all, then reduce) | Item 4 changes messages only. The reorder waits for X-1(g). |
| Entry-direction uses of `TradeMode` (placing a NEW entry) | The toggle is the right source when flat. Item 2 touches position management only. |
| The `isClosing` shutdown path and the ws-edge / feedback publishes on connect and disconnect | Unchanged; item 1 adds around them. |
| The nine census symbols | `emergencyFired` (10) sits on the emergency lines item 2 edits — replace tokens, add no occurrence. Any count change must be explained. |

## §1 — Item 1: reconnect (A15, G4, G16)

**Today:** `HandleWebSocketDisconnect` (`frmMainPageV2.vb:1410`) tries `maxReconnectAttempts = 10`
(`:1407`), about 72 s of delays, then logs "manual intervention required" and calls only the local
`Alert("connection")` (`:1466`). `RemoteNotifier.Post` is never called on a disconnect.
`AuthorizeWebSocketConnection` (`:1245`) waits for the auth reply with `ReceiveAsync` on the main token
(`:1266`), with no timeout, so one attempt can hang the loop forever.

**Change:**
- Retry until connected or `isClosing`. Keep the existing delays for the first 10 attempts, then a
  fixed cap (**30 s**, coordinator default). Keep the single-flight guard.
- **Page** (`RemoteNotifier.Post`, urgent) when still down **60 s** after the drop, then every
  **10 min** while down. The message names whether a position is open (`positionSizeUSD`) and its size.
  On recovery after a page, post one "reconnected after N s" line. A drop that recovers inside 60 s
  pages nobody: drops are routine and self-heal (`docs/runtime-record-ws-down-emitter-2026-08-14.md`).
- Bound the auth reply wait: a linked token with a **10 s** timeout. A timeout fails the attempt,
  which the loop retries.
- Keep the local `Alert("connection")` calls.
- ⚠ `docs/HANDOVER-7.md` §6's "recovery budget ~72 s, 10 attempts" trap becomes false. The report must
  say so, and the coordinator updates the handover.

## §2 — Item 2: side from the position (A16, G6)

**Today:** 18 `If TradeMode` sites (grep). Position-management ones take the side from the Buy/Sell
toggle: one click while a long is open flips the chase and the M.SL cap to the short side (the audit's
`mode-flip` run: adverse moves ignored, then a favourable 70 USD move market-sells the long, logged
"Emergency Buy").

**Change:**
- One helper, e.g. `ManagedSideIsLong()`: `positionSizeUSD > 0` → long; `< 0` → short; `= 0` → fall
  back to `TradeMode` (no position, so the toggle legitimately means the next entry's side).
  Plain field read; receive-thread safe.
- Apply it at every **position-management** site: the triggered-SL chase and M.SL emergency branches in
  `HandleQuoteUpdates`, `SendReduceMarketOrderAsync`, `EditStopLossTo`, `UpdateStopLossForTrailingOrder`,
  the B.E./TS buttons, and any position-side logging. Leave **entry** sites on `TradeMode`.
- **The report carries a table of all 18 sites:** method, line, entry or position, changed or not, why.
- Log one line when the toggle disagrees with an open position's sign (informational; no block).

## §3 — Item 3: restore checks for a stop (A20, G10)

**Today:** the restore scan returns silently on an empty order list, commented "flat restart"
(`:5959`). With a position and no stop it emits at most a cyan `SL=none` line.

**Change:**
- When the restored order list is known **and** the position is known: if `positionSizeUSD <> 0` and
  no stop order is among the restored orders (the untriggered `stop_limit` leg, or the triggered limit
  the app labels as its stop), log red, `Alert`, and page urgent: "open position with NO STOP".
- ✅ **Owner ruling 2026-10-08 (escalation `E1` of this batch, raised by the implementer before the
  code): what counts as a stop.** The parenthetical above is widened. A stop is any of these, **and its
  direction must close the position** (sell for a long, buy for a short):
  - the app's `StopLossOrder` leg, untriggered or triggered (`open`);
  - the app's `TrailingStopLoss`, untriggered or `open`;
  - any untriggered `stop_limit`, `stop_market` or `trailing_stop` order of any label, for example a
    stop placed by hand in the Deribit UI.
  Why: under the literal text, every restart in trailing mode, or with a hand-placed stop, paged a false
  urgent NO STOP. A stop on the adding side protects nothing, so it does not count. Decision-bias
  tripwire: `docs/harness-runs/decision-bias-20261008T0700Z-jev.json` (`no_richer_option`, agreement 1.0).
- The id-777 position seed and the id-778 order snapshot arrive independently. Evaluate when the second
  of the two lands, whichever it is. Do not page on a flat account.
- Do not change what the restore re-adopts.

## §4 — Item 4: the emergency's messages (A12, message part)

**Today (`UpdateStopLossForTriggeredStopLossOrder`, `:4886-4908`; `HandleQuoteUpdates` also tests the M.SL gate at `:2678` — confirm whether it is a second send site, and cover it if so):** after `CancelOrderAsync` and `SendReduceMarketOrderAsync`, it logs and pages
"Emergency Sell/Buy Market Order Executed." unconditionally, even when the reduce was skipped (socket
down) or rejected.

**Change (messages only — the send order stays):**
- On send: log red and page urgent **"Emergency close SENT — awaiting fill"**.
- When the reduce's fill is confirmed flat (the existing reduce-fill echo path): log and page
  **"Emergency close CONFIRMED — flat"**.
- If the reduce was skipped (not connected), its send threw, or it was rejected: log red and page urgent
  **"Emergency close FAILED — position may have NO orders"**, with the reason.
- If no confirmation arrives within **10 s**: page "Emergency close UNCONFIRMED after 10 s".
- Keep `Alert("emergency_stop")`.

## §5 — Acceptance

1. Gate passes; OrderCheck all pass; nine censuses re-run, any change explained.
2. **OrderCheck seams with fixtures that fail against today's code:**
   - the side helper (long, short, flat → toggle; and a long with the toggle on Sell → long);
   - the reconnect schedule (attempt n → delay; never "give up"; the 30 s cap);
   - the page policy (down 59 s → no page; 60 s → page; every 10 min after; recovery line only after a page);
   - the restore check (position + no stop → alarm; position + stop → none; flat → none).
3. **The audit's proof scenarios as the acceptance list** (`docs/audits/proofs/order-app-gap/` on branch
   `claude/zen-cray-wy33a6`): `mode-flip` (item 2), `restore` (item 3), `emergency-rejected` and
   `trace-emergency-send-fails` (item 4's message, not its outcome). The harness itself only compiles
   `8232e9e`, so these are scenarios to re-express as fixtures, not a test to run.
4. **Runtime, testnet:** pull the network for > 60 s with no position → one page, then a recovery line
   (owner-driven: a seat does not change network configuration). Restart with an open testnet position
   and its stop cancelled by hand in the Deribit UI → the NO STOP alarm (owner-driven).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Receive path, order path, emergency path.
- Coordinator review: Opus, regardless of implementer.
