# Runtime record — the C1 emitter publishing `"ws": "DOWN"` on a MID-SESSION disconnect

**2026-08-14, owner-driven, x64 bin, TESTNET.**
**Result: the last open C1 runtime behaviour on the `ws` DOWN edge is CLOSED.**

> ⚠ **Read §5 of this record before citing it.** Three things did NOT close here, and one of them
> looks closed at a glance. The failed-reconnect suppression is still unproven.

---

## 1. Session facts

| Fact | Value |
|---|---|
| Date | **2026-08-14 local (UTC+8)** · rows below are **UTC**, so local 22:09 reads `14:09Z` |
| Bin | `DeribitOrderPlacementApp/bin/x64/Debug/net9.0-windows8.0` — **the owner's x64 bin**, rebuilt this day (`a3f191d`) |
| Title | **`Deribit Order Placement App V2.2 — TESTNET`** — read from the live process, not assumed |
| C1 emitter | **ENABLED** — `Executor feedback: configured - C:\Dev\DeribitBridge\executor_feedback.json` |
| ws-edge code | **PRESENT** — `ws-edges.log` was created by this session; the file did not exist in this bin before it |
| Bridge | `consumer ready (mode Off)` — `mode: "OFF"`, `armed: false`, `started: false` for the whole session |
| Safety | **No trade. No ARM. No START.** Connectivity only. |
| Disconnect | Provoked **by the owner** — the seat does not change network configuration |
| Instrument | `tools/sample-feedback.ps1`, started **before** the drop. 42 distinct publishes archived |

---

## 2. 🚨 THE JOIN — the whole point of this session

`ws-edges.log`, complete and unedited, from the x64 bin:

```
2026-08-14T14:07:20Z | OK | connect
2026-08-14T14:09:24Z | DOWN | WebSocket exception: The remote party closed the WebSocket connection without completing the close handshake.
2026-08-14T14:10:17Z | OK | connect
```

Joined against the sampled `executor_feedback.json` snapshots:

| UTC | `ws-edges.log` row | Sampled feedback file | Match |
|---|---|---|---|
| `14:07:20Z` | `OK \| connect` | id **7**, `ws: "OK"` | ✅ same second |
| **`14:09:24Z`** | **`DOWN \| WebSocket exception…`** | **id 21, `ws: "DOWN"`** | ✅ **same second** |
| `14:10:17Z` | `OK \| connect` | id **27**, `ws: "OK"` | ✅ same second |

**All three join to the second.** The middle row is the one this record exists for.

The id-21 snapshot, verbatim from `verify/feedback-samples/feedback-20260814-220924-856.json`:

```json
{
  "schema_version": 1,
  "feedback_id": 21,
  "generated_at_utc": "2026-08-14T14:09:24Z",
  "executor": {
    "instance_id": "85d7a53e-a511-4c6f-a85f-ace9d785bb84",
    "app": "DeribitOrderPlacementApp",
    "mode": "OFF",
    "armed": false,
    "started": false,
    "breaker_tripped": false,
    "ws": "DOWN"
  },
  "instrument": "BTC-PERPETUAL",
  "position": { "direction": "FLAT", "size_usd": 0.0, "avg_entry": 0.0,
                "working": { "stop": 0.0, "target": 0.0 } },
  "last_signal": null
}
```

### 2.1 Why this is a MID-SESSION drop and not the shutdown write

**The discriminator is the trailing `OK`, and it is satisfied.** A graceful-close `DOWN` is by
definition the **last** publish of its session. This one is not:

- `DOWN` at **id 21**, then `OK` at **id 27** — a **higher** `feedback_id`, 53 s later.
- `instance_id` is **`85d7a53e-a511-4c6f-a85f-ace9d785bb84` from id 1 through id 41 and beyond**, so
  every row above is **one process**. The app kept running straight through the drop.
- Confirmed independently: the process was still alive after the session at **id 64**, same GUID.

**This is exactly the ambiguity that `HANDOVER-6.md` §2 item 7 was created to prevent, and it
resolved the right way.**

---

## 3. What this CLOSES

✅ **The C1 emitter publishes `"ws": "DOWN"` into `executor_feedback.json` on a real mid-session
disconnect.** Previously the last unproven claim on this edge. Now joined to `ws-edges.log` on the
second, inside a single proven process lifetime.

Together with `runtime-record-ws-edge-audit-2026-08-14.md`, all three claims on this edge now hold:

| Claim | Status |
|---|---|
| The DOWN/OK **transition** can be evidenced after the fact | ✅ proven 2026-08-14 (the earlier record) |
| E6a's DOWN **call site** is reached on a real disconnect | ✅ proven 2026-08-14 (the earlier record) |
| The **emitter publishes** `"ws": "DOWN"` mid-session | ✅ **proven here** |

---

## 4. Also established, and new — none of it was the goal

1. **The emitter heartbeat is ~10 s, and it republishes UNCHANGED content by design.** ids 7→20 are
   all `ws: "OK"` with identical content, ~10 s apart. This is not a content-gate failure: contract
   §8.5's ruling is that **a fresh `generated_at_utc` proves the process is ALIVE**, not that any
   field changed. First time the cadence has been measured.

2. **🚨 A STARTUP `DOWN` WINDOW EXISTS AND `ws-edges.log` CANNOT SEE IT.** ids **1–6**
   (`14:06:21Z`–`14:07:11Z`) published `ws: "DOWN"` **before the first connect**, and
   `ws-edges.log`'s first row is the `OK` at `14:07:20Z`. There is **no DOWN row for that window.**
   The two instruments have different coverage by construction: the emitter reports **state on every
   heartbeat**, `WsEdgeLog` reports **transitions at the two E6a publish sites only**, and
   "never connected yet" is a state, not a transition.
   **Consequence to hold on to: an app that starts and never connects publishes `DOWN` and writes no
   `ws-edges.log` at all.** So `ws-edges.log` is not a complete history of `ws` — it is a history of
   transitions. Do not use its absence as evidence the socket was up.
   *(This is also the "never-connected initial state" reading that `HANDOVER-6.md` §2 item 7 listed
   as hypothesis (a). It is now a directly observed phenomenon, which is why keeping the readings
   apart was worth doing.)*

3. **Contract §8.4's restart semantics are CONFIRMED at runtime for the first time.** The previous
   session's file was `instance_id 09910f76…` at `feedback_id: 2`; this process opened at
   `feedback_id: 1` under a **new GUID** `85d7a53e…`, with `armed: false` / `started: false`.
   §8.4 specifies exactly this — *"monotonic per executor process; identity = (`executor.instance_id`,
   `feedback_id`) … Restart ⇒ new GUID + `armed`/`started` false by construction."*
   ⚠ **So the on-disk `feedback_id` going 2 → 1 is CORRECT, not a monotonicity violation.** It would
   only be a defect if the GUID had stayed the same. Check the GUID before ever reporting an id
   regression — this is a trap that would otherwise read as C1's `D1` returning.

4. **Recovery took 8 of 10 attempts and 53 s** (`14:09:24Z` → `14:10:17Z`), against a budget of
   ~72 s of backoff plus per-attempt connect time (`frmMainPageV2.vb:1319`, `:1340`, `:1370`). The
   outage was held longer than the 20–30 s recommended. It recovered on its own; no `Connect!` click
   was needed. **Two attempts of margin is thinner than it looks** — hold the next one shorter.

---

## 5. 🚨 What this does NOT close — read before updating any status line

1. **THE FAILED-RECONNECT `DOWN` SUPPRESSION IS STILL UNPROVEN, AND IT LOOKS PROVEN.**
   Eight reconnect attempts failed between the `DOWN` and the recovery, and `ws-edges.log` carries
   **exactly one `DOWN` row**. That is tempting to read as the no-flood suppression working
   (`OrderCheck` ws-edge fixture 5, `DOWN -> DOWN is SUPPRESSED`). **It does not prove it.**
   A failed `ConnectToWebSocketDirectly()` never establishes a socket, so it never enters a receive
   loop and never exits one — and the exit is where the DOWN publish site lives. **One row is
   equally consistent with "suppressed" and with "NoteState was never called again."** Nothing in
   this session's artefacts separates them.
   ⇒ `runtime-record-ws-edge-audit-2026-08-14.md` §4 item 3 **stays open**.

   ✅ **SETTLED 2026-10-06 by code reading (coordinator seat, Opus 5.5), at HEAD `dbb43c8`. The
   answer is "`NoteState` was never called again". The suppression was NOT exercised, and on this
   path it cannot be.**
   - `NoteState` has exactly **one** caller, `LogWsEdge` (`frmMainPageV2.vb:858`). `LogWsEdge` has
     exactly **two** call sites: `:1530` `LogWsEdge("connect")`, and `:1649` at the receive loop's exit.
   - `:1530` sits **after** `ConnectAsync`, authorize and every subscribe in
     `ConnectToWebSocketDirectly` (`:1448`). A failed attempt throws before it. The receive loop is
     started only at `:1534`, after it. The reconnect loop in `HandleWebSocketDisconnect` (`:1380`)
     catches that throw and calls neither site.
   - So a failed reconnect makes **zero** `NoteState` calls. The eight failures in this session made
     zero, and the single `DOWN` row is fully explained without any suppression.
   - **The suppression rule itself is already proven at unit level:** `OrderCheck` ws-edge fixtures 5
     (`DOWN -> DOWN` suppressed) and 10 (a run of repeated `DOWN`s logs one row). What runtime could
     add is only a real caller that reaches `DOWN -> DOWN`. This read found **none**. I did not prove
     that none exists. A `DOWN -> DOWN` would need two loop exits with no successful connect between them.
   - **Fixture 5's label was wrong** ("a failed reconnect re-enters the loop exit"). Corrected in
     `tools/OrderCheck/Program.vb` with a note; the assertion is unchanged.
   - **No provoked outage is needed for this item.** Items 2 and 3 below are unchanged.

2. **The `While`-condition exit arm was again not exercised.** This drop took the
   `WebSocketException` arm, the same as the previous session. `server closed connection`,
   `receive cancelled` and `socket no longer open` have **still never appeared in a real row**.

3. **`NoteState` concurrency remains argued, not demonstrated.** Two threads in it at once was not
   reproduced. Unchanged from the earlier record.

4. **Nothing here says anything about LIVE.** `mode` was `"OFF"` throughout. `mode: "LIVE"` with an
   `acted (id …)` disposition, a `breaker_tripped` flip and a SHORT `size_usd` are all still
   unobserved, and all still gated on owner ARM/START.

---

## 6. Housekeeping

- `verify/feedback-samples/` holds all 42 snapshots plus `index.txt`. `verify/` is git-ignored, so
  the evidence is local — the rows quoted above are the durable copy.
- `ws-edges.log` is left in place in the x64 bin as the evidence. It is git-ignored.
- The app was still running at the end of this record (`— TESTNET`, `mode OFF`, id 64). Closing it
  will write one more graceful-close `DOWN`. **That is expected and is not a new observation.**
- Two journal rows were deleted from the x64 `trades.db` in the same session — separate work, see
  `HANDOVER-6.md` §2 item 4.
