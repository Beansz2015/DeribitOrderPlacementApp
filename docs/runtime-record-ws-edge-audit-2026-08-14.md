# Runtime record — ws-edge audit trail, 2026-08-14

**Spec:** `docs/spec-ws-edge-audit.md`. **Impl report:** `docs/impl-report-ws-edge-audit.md`.
**Code under test:** `090d88c` + `f9a9b2d`.

> ## ✅ ALL FOUR RUNTIME ACCEPTANCES PASSED — including the real disconnect
>
> **`docs/spec-ws-edge-audit.md` §Acceptance items 2, 3 and 4 all RAN and all PASSED.** A genuine
> network drop was provoked by the owner, and the transition was captured. **The `executor.ws`
> DOWN/OK transition has been evidenced after the fact for the first time** — which is the entire
> purpose of `docs/spec-ws-edge-audit.md`.
>
> ⚠ **Read §5 of this record before citing this anywhere.** This ran with the **C1 emitter
> DISABLED**, so `executor_feedback.json` publishing `"ws": "DOWN"` is **still unobserved**. Two
> different claims; only one of them was proven here.

---

## 1. Session facts

| Fact | Value |
|---|---|
| Date | **2026-08-14 local (UTC+8)** · the rows below are **UTC**, so they read `2026-08-13T20:xx` |
| Window | connect **20:35:17Z** → graceful close ~**21:10Z** |
| Bin | `DeribitOrderPlacementApp/bin/Debug/net9.0-windows8.0` — the **harness** bin, AnyCPU Debug |
| Launched by | `tools/launch-app.ps1`, PID **5116** |
| Title | **`Deribit Order Placement App V2.2 — TESTNET`** — verified by the launcher and by screenshot |
| Exchange | `TESTNET environment — test.deribit.com` |
| C1 emitter | **DISABLED** — `Executor feedback: disabled (no feedback_output_path in bridge.json)` |
| Bridge | `consumer ready (mode Off)`, `bridge.json not found beside the exe` |
| Safety | **No trade. No ARM. No START.** Connectivity only. The only control clicked was `Connect!`, which is not on `tools/trade-buttons.txt` |
| Disconnect | Provoked **by the owner** — the seat does not change network configuration |

---

## 2. 🚨 THE ARTEFACT — `ws-edges.log`, complete and unedited

```
2026-08-13T20:35:17Z | OK | connect
2026-08-13T20:45:39Z | DOWN | WebSocket exception: The remote party closed the WebSocket connection without completing the close handshake.
2026-08-13T20:46:02Z | OK | connect
```

**Three rows. That is the whole file.** The middle two are the disconnect and the recovery, 23
seconds apart. Before this session the file **had never existed**.

The matching host-log lines, from `tools/read-log.ps1`:

```
WebSocket exception: The remote party closed the WebSocket connection without completing the close handshake.
ws edge: 2026-08-13T20:45:39Z | DOWN | WebSocket exception: The remote party closed the WebSocket connection without completing the close handshake.
Connection lost - initiating recovery sequence
Reconnection attempt 1/10
Reconnect attempt 1 failed: Unable to connect to the remote server
Reconnection attempt 2/10
Reconnect attempt 2 failed: Unable to connect to the remote server
Reconnection attempt 3/10
Reconnect attempt 3 failed: Unable to connect to the remote server
Reconnection attempt 4/10
Reconnect attempt 4 failed: Unable to connect to the remote server
Reconnection attempt 5/10
TESTNET environment — test.deribit.com
WebSocket authorized successfully
ws edge: 2026-08-13T20:46:02Z | OK | connect
Successfully reconnected
```

**Each gray line is character-identical to its file row.** That is `docs/spec-ws-edge-audit.md`
§2.2's "two readers, one decision" holding at runtime: the host log and the file cannot disagree,
because the file's row IS the line.

---

## 3. Acceptance — item by item

| # | Item in `docs/spec-ws-edge-audit.md` §Acceptance | Result |
|---|---|---|
| 1 | The pure seam, no app running | ✅ **PASS** — OrderCheck **289/289**, proven failable |
| 2 | **A REAL disconnect, end to end** | ✅ **PASS** — §3.1 |
| 3 | No flooding, five minutes | ✅ **PASS** — §3.2 |
| 4 | Graceful close is not a `DOWN` | ✅ **PASS** — §3.3 |
| 5 | Nothing else regressed | ✅ **PASS** — `GATE PASSED`, censuses **68 across 64** unchanged |
| 6 | `.gitignore` | ✅ **PASS** |

### 3.1 Item 2 — the real disconnect ✅

All four required observations:

- **Exactly two new rows, `DOWN` then `OK`.** Row count 1 → 3; nothing else appeared.
- **Both gray lines present** in the host log (§2 of this record).
- **The app reconnected on its own.** `Successfully reconnected`, and the existing recovery path was
  never touched by this change.
- **The `DOWN` row carries a real reason** — the `WebSocketException` arm fired, and the exception
  text is in the row. `docs/spec-ws-edge-audit.md` §2.2's `<reason-if-known>` earns its place here:
  it distinguishes a server close from a transport failure without the host log, which does not
  persist.

**Two things this run established that no fixture could:**

1. **🚨 E6a's DOWN publish site IS REACHED on a real disconnect.** `PublishExecutorFeedback()`
   (`frmMainPageV2.vb:1581`) and `LogWsEdge(exitReason)` (`:1591`) are separated by comments only.
   The `DOWN` row proves control flow reached both. Until now that was an argument from reading the
   loop, and `docs/HANDOVER-6.md` item 6 could only say the edge was *specified*.
2. **The reconnect re-enters `ConnectToWebSocketDirectly`**, which is the function holding the OK
   call site — `TESTNET environment — test.deribit.com` reprints there, and the `OK` row follows.
   `docs/impl-report-ws-edge-audit.md` claimed this from `frmMainPageV2.vb:1350`; it is now observed.

**Four failed reconnect attempts produced ZERO rows — but read the mechanism carefully.** Attempts
1–4 failed before `ConnectAsync` returned, so they reached **neither** call site and started no
receive loop. They did **not** exercise the transition field. The field's suppression of a repeated
`DOWN` remains pinned by **ws-edge fixture 10 only** (pure). Do not cite this run as runtime proof of
the `docs/spec-ws-edge-audit.md` §2.1 ⚠.

### 3.2 Item 3 — no flooding ✅

`20:37:07Z → 20:42:07Z`, app connected and responding throughout: **row count 1 → 1, file
byte-identical** (md5 `2556fe8a…` both ends). Process confirmed alive and responding after the
window, so the zero is not a dead-app artefact.

⚠ **One condition of the item was NOT met, and it does not change the result.**
`docs/spec-ws-edge-audit.md` §Acceptance item 3 specifies five minutes *"with the 10 s heartbeat
running"*. That is the **C1 emitter's** heartbeat, and the emitter was **disabled** in this bin. It
does not matter: `ExecutorFeedback.OnHeartbeat` republishes its own snapshot through
`DrainQueue`/`WriteAtomic` and **has no path back into the form**, so it can never reach `LogWsEdge`.
The flooding mode that can affect this design is repeated receive-loop exits, not the heartbeat.
**An emitter-enabled re-run would be worth having, but nothing about the design depends on it.**

### 3.3 Item 4 — graceful close ✅, and it validates `SB1`

Closed with `tools/stop-app.ps1`, which calls `CloseMainWindow()` first. It reported **`PID 5116
closed.`**, **not `killed`** — so `FormClosing` genuinely ran and the test is valid. A killed process
runs no shutdown code and would have proven nothing.

**Row count 3 → 3, md5 `7de33d71…` identical before and after. No spurious `DOWN`.**

**🚨 This is the finding of the session.** `docs/spec-ws-edge-audit.md` §Acceptance item 4 states
that shutdown cannot append a `DOWN` *because the emitter latches itself disposed before the socket
closes*. **In this bin the emitter was disabled, so `ExecutorFeedback.ShutdownWithFinalWrite`
returned immediately and `_disposed` was never latched at all.** An implementation that used the
spec's stated mechanism as its guard would have appended a spurious `DOWN` **in exactly this
configuration**, and item 4 would have failed here.

`SB1` in `docs/impl-report-ws-edge-audit.md` §5 predicted this from code and guarded on `isClosing`
instead. **The prediction is now confirmed by the passing result rather than only by argument.**

---

## 4. What was NOT provable this way

Carried from `docs/impl-report-ws-edge-audit.md` §4, corrected against what actually ran:

1. **The concurrency is still argued, not demonstrated.** Two threads in `NoteState` at once was not
   reproduced. Unchanged.
2. **The `While`-condition exit arm was not exercised.** The disconnect took the
   `WebSocketException` arm. `server closed connection`, `receive cancelled` and
   `socket no longer open` have **never appeared in a real row**.
3. **A failed-reconnect `DOWN` suppression was not exercised at runtime** — see §3.1 of this record.
   ✅ **SETTLED 2026-10-06 by code reading:** a failed reconnect never calls `NoteState`, so there is
   nothing to exercise on that path. Evidence: `runtime-record-ws-down-emitter-2026-08-14.md` §5 item 1.
4. **No `DOWN` was observed while the emitter was ENABLED.** See §5 of this record.
5. **The owner's x64 bin still does not carry this code.** `tools/checks/verify-gate.ps1` is
   AnyCPU-only. This ran in the harness Debug bin.
6. **The manual-re-click residual** (`docs/impl-report-ws-edge-audit.md` §4 item 7) was not tested —
   `Connect!` was clicked once, from a disconnected state.

---

## 5. 🚨 What this does NOT close — read before updating any status line

> ✅ **SUPERSEDED IN PART, 2026-08-14 (same day, later session):
> `runtime-record-ws-down-emitter-2026-08-14.md`.** The third row of the table below — *the emitter
> publishes `"ws": "DOWN"`* — is now **PROVEN** on a mid-session drop in the rebuilt x64 bin, joined
> to a `ws-edges.log` `DOWN` row to the second. **This section's status table is therefore stale for
> that row only; the rest of it stands.** ⚠ `§4 item 3` of THIS record — the failed-reconnect `DOWN`
> suppression — is **still open** and was NOT closed by that session either, though it superficially
> looks like it was. Read that record's §5 before citing either document.

**`executor_feedback.json` carrying `"ws": "DOWN"` is STILL UNOBSERVED.** The C1 emitter was
**disabled** for this whole session, so it published nothing at all. Two distinct claims, and only
the first was proven:

| Claim | Status after this session |
|---|---|
| The ws DOWN/OK **transition** can be evidenced after the fact | ✅ **PROVEN** — the three rows in §2 |
| E6a's DOWN **call site** is reached on a real disconnect | ✅ **PROVEN** — §3.1 |
| The **emitter publishes** `"ws": "DOWN"` into `executor_feedback.json` | 🚫 **STILL UNOBSERVED** |

So the `ws` DOWN edge stays on C1's still-unobserved list in `docs/HANDOVER-6.md` item 7 and in
`docs/ROADMAP-2026-08.md` §5 — **but it is no longer un-evidenceable.** The next log-only session in
the owner's emitter-enabled x64 bin will produce both artefacts, and they can be joined on the
timestamp.

**To close it properly:** rebuild the x64 bin so it carries `WsEdgeLog`, run a log-only session with
`feedback_output_path` set, and provoke a drop. Then `ws-edges.log` gives the `DOWN` row and
`executor_feedback.json` can be sampled at that moment.

---

## 6. Housekeeping

- `tools/stop-app.ps1` removed `verify/app.pid`. The screenshot taken during setup was deleted per
  the standing rule (`docs/HANDOVER-6.md` §5 item 13).
- `ws-edges.log` is left in place in the harness bin **as the evidence**. It is git-ignored.
- The engine (`DeribitVerdictEngine.exe`) was running throughout on the owner's side and was **not
  touched** — that repo is READ-ONLY from here.
