# Impl report — protection batch 3: a position cap, a dead-feed detector, stand-downs that stand down

**Written 2026-10-14 (GMT+8) by the protection batch 3 implementer seat (Opus 5.5, high).**
**Spec:** `docs/spec-protection-batch3.md` (owner rulings `B3-R1` to `B3-R5`, all (a), 2026-10-14), with
`docs/review-refused-edits.md` §2 row `RE-SB5` folded in as `docs/spec-protection-batch3.md` §4b.
**Code commit:** `4ad3035` (on top of `74f8399`). Local only; nothing pushed.
**Scope held:** code and fixtures only. No app launch, no testnet, no trade, no bridge arm. The engine repo was read
only (`git grep` and `git show` on its `master`; nothing fetched, nothing written).

IDs in this report:
- `B3-R1`…`B3-R5` are the owner's rulings (`docs/spec-protection-batch3.md` §0). Not batch 2's `R1`…`R4`.
- `B3-E1` is this seat's escalation, raised **before** writing the code it concerns (the `E` kind).
- `B3-SB1`… are this seat's spec-back findings against `docs/spec-protection-batch3.md` (the `SB` kind).
- `RE-SB5` is the refused-edits seat's finding (`docs/impl-report-refused-edits.md` §5): a refused trailing-entry
  main edit (request id 223347) left `placedPrice` optimistic. `RE-R4` is the owner's refused-entry-edit ruling.
- `SB3` is batch 2's spec-back finding: after a rejected stop, the M.SL anchor is pinned at the dead stop's trigger.
- Mutation names (`M-B3-1` …) are this report's own labels for the mutation runs in section 4.

---

## 1. First actions (before any code)

| Brief item | Finding | Outcome |
|---|---|---|
| `docs/spec-protection-batch3.md` §4 — the `exec_resolution_min` values the engine emits | **ASIA (00–07 UTC) = 3, LONDON (08–12 UTC) = 3, NY = 1.** The emitter writes `o("exec_resolution_min") = r.ExecResolution` (engine `Core/SignalEmitter.vb:144`); `r.ExecResolution` comes from `ExecutionResolution.ResolveResolution` (`Core/ExecutionResolution.vb:55`); the shipped `settings.json:370`, `:379`, `:388` set 3, 3, 1; the seed `Core/Settings/EngineSettings.vb:703`–`:704` agrees | **STOP for this item.** With `N` = 1, every Asia and London payload (one per 3-min bar) is stale for most of its bar. **Escalated as `B3-E1` (section 6). `B3-R5` is not built.** |
| `docs/spec-protection-batch3.md` §2 — the `public/set_heartbeat` interval | **30 s** (`EnableDeribitHeartbeatEnhanced`, `frmMainPageV2.vb:1880`–`:1882`). Deribit's heartbeat test needs a reply, so it detects a dead link only on a 30-s scale. The socket's own keep-alive is 30 s with a 20-s timeout (`frmMainPageV2.vb:1640`, `:1643`) | **No conflict.** The 3-s probe and 2-s reply window are a finer, separate detector. The heartbeat is unchanged |
| `docs/spec-protection-batch3.md` §3 — how a bridge-placed working entry is identified | **No explicit handle exists today.** The scoped cancel (request id 31) is the cancel, not an identity. The nearest proxy, the signal tag (`pendingSignalId`), can stay staged after a timed-out act whose late reply was a rejection; the next MANUAL entry would then read as the bridge's | **A reliable handle exists in today's data**, so no escalation: the order id the bridge's placement ack names. Only `PlaceAutomatedOrder` pre-registers an ack (`PendingPlacement.Tcs`), and its only caller is the bridge. A manual placement can never match. Recorded as `B3-SB1` with the tripwire run |

## 2. What was built

| Item | Built | Where (`frmMainPageV2.vb` unless named; lines at `4ad3035`) |
|---|---|---|
| `B3-R1` (a) — position loss cap, a backstop | Fires when the own-side quote (ask for a long, bid for a short) is one M.SL past the stop **trigger** (`StopLossTriggerOriginal`) **and** the triggered-stop M.SL path is not armed. Runs the existing emergency sequence: latch, nuclear cancel, `ArmEmergencyClose`, reduce-only market, `ReportEmergencyCloseSend` (batch 1's SENT/CONFIRMED/FAILED). Urgent page `POSITION CAP — price $X is M.SL past the stop trigger $T: flattening` | quote-handler call `:2640`; seams `:7043`–`:7092`; `TryFirePositionCapAsync` `:7106`; watchdog half `EvaluatePositionCapAsync` `:7131`, called at `:7014` |
| `B3-R2` (a) — M.SL off | No cap. One page per position: `M.SL is off — no loss cap`. No trigger known: no cap, one page `position has no stop trigger — no loss cap` | `PositionCapNoticeFor`, `PositionCapNoticePages`, `PositionCapNoticeText`; state under `positionCapGate` |
| `B3-R3` (a) — dead feed | Every inbound frame is stamped (`lastInboundTicks`). While a position or a working entry exists: 3 s with no frame → `public/test` on a new request id **44**; no frame within 2 s of it → urgent page `Feed STALLED — no data for N s (position …)`, the bridge auto-STOPs, the socket is aborted and handed to batch 1's reconnect path | stamps `:1746`, `:1755`; connect reset `:1649`; seams `:7174`–`:7210`; `EvaluateFeedWatch` `:7222`, called at `:7015`; ws-edge reason `:1834`; id name `:2170`; `SignalBridge.vb:567` `NotifyFeedStalled` |
| `B3-R4` (a) — stand-down cancels the bridge entry | A stale payload (or 10-s stale tick), a SKIPPED payload, engine ARM off, or local ARM unchecked sends the existing scoped entry cancel (id 31) when the working entry is the bridge's. Log and normal page `Stand-down (<reason>): working entry cancelled`. A manual entry is never touched. While a bridge entry works, a stand-down never changes the engine ATR that the abort cap multiplies | handle `:2200`, `:2237`, `:7271`–`:7296`; `StandDownBridgeEntry` `:7299`; `SignalBridge.vb` ARM setter `:245`, stale tick `:520`–`:543`, payload `:691`, `:714`; seams `SignalBridge.vb:575`, `:586` |
| `B3-R5` (a) — freshness cap | **Not built** — `B3-E1` | — |
| `RE-SB5` (§4b) — refused 223347 | The trailing main edit's send is kept (order id, entry) before the send, in its own list, and its reply runs through the refused-edits seams exactly as 223344's does: a refused 223347 sets `placedPrice` to the entry's echoed price | send `:5524`–`:5541`; seams `:6626`–`:6641`; handler `:6844`, `:6852` |

**Pure `Friend Shared` seams (every decision):** `TriggeredMslPathArmed`, `PositionCapFires`, `PositionCapText`,
`PositionCapNoticeFor`, `PositionCapNoticePages`, `PositionCapNoticeText`, `FeedCheck`, `FeedProbeOutstanding`,
`FeedWatchActive`, `FeedStallText`, `IsBridgeEntry`, `StandDownCancelsEntry`, `StandDownCancelText`, `KeepsEditSend`
(all `frmMainPageV2.vb`); `PayloadStandDownReason`, `AtrAfterPayload` (`SignalBridge.vb`).

**Reused, not forked:** batch 2's one-second watchdog tick (`RunProtectionWatchdogAsync`); the emergency sequence
(the same calls in the same order as batch 2's R1 flatten); batch 2's no-stop grace (`NoStopGraceSeconds`) and its
"wait for the id-778 snapshot" rule; batch 1's reconnect path (`HandleWebSocketDisconnect`); the scoped cancel
(`CancelWorkingEntryCoreAsync`); the bridge's `ForceStop`; the refused-edits seams (`TakeEditSend`,
`EntryRefusalResets`, `ReferenceAfterEditReply`).

## 3. How each piece decides

- **"The triggered-stop path is armed"** is the exact gate of the hoisted M.SL check in `HandleQuoteUpdates`: socket
  open, no cancel pending, `SLTriggered`, a `PositionSLOrderId`, and a chase reference > 0. While it holds, the cap
  returns False before it looks at the price. So the hybrid #67 anchor latch and the `SB3` pin are never pre-empted.
- **The cap also stands down** while disconnected (nothing can be sent) and while a cancel is pending (the
  2026-07-08 ruling that gates the M.SL during the cancel window).
- **One emergency per position.** The cap claims the single-fire latch under its own gate (`positionCapGate`), so its
  two call sites (the receive thread, the watchdog thread) cannot both fire. A latch already set by the triggered path
  or batch 2's R1 flatten stops it.
- **The anchor** is `StopLossTriggerOriginal`. It is set at placement, kept in sync by the untriggered stop's echo
  (`frmMainPageV2.vb:3822` — so a manual SL edit moves it), and zeroed by the nuclear cancel, the position close and a market reduce.
  A cancelled-by-hand stop leaves it in place, which is what lets the cap act on "a stop that is cancelled".
- **The two notices** wait until this connection's id-778 snapshot has seeded the order map (batch 2's rule), so a
  restart does not page "no stop trigger" before the snapshot. "No trigger" also waits batch 2's 5-s no-stop grace.
  Both are priority `high`; both re-arm when the position goes flat.
- **The probe is answered by any frame** that lands after it. The reply is a frame; any other frame proves the feed
  alive as well. The probe is stamped before its send, so a fast reply always counts.
- **The watchdog never awaits the probe or the stall's sends.** A send on a dead link could block the whole tick
  (the cap's tick half and the no-stop watchdog with it).
- **The forced reconnect** aborts the socket (never blocks; a close handshake on a dead link could) and calls
  `HandleWebSocketDisconnect`, which is single-flight with the receive loop's own call. The aborted socket skips the
  close in `ConnectToWebSocketDirectly`. The ws-edge audit row names the cause: `feed stalled (no data; probe unanswered)`.
- **The bridge stand-down on a stall** is `ForceStop("feed stalled (no data from the exchange)")`: STOPPED, as for a
  stale payload. New acts need the owner's full START sequence.
- **The bridge-entry handle** is recorded from the placement ack's `result.order.order_id`, on a timely accept and on a
  late accept after a timeout (identification only; the late branch still restores nothing). The stand-down compares it
  with `CurrentOpenOrderId`. The host side is single-flight and re-checks before it sends.
- **The abort cap.** The slippage-abort cap is ATR × ATRSlip, read live from the engine ATR the bridge holds. A
  stand-down payload (stale, SKIPPED, engine ARM off) or a stale tick used to zero it or overwrite it — the audit run
  `72.12 → 70 → 90 → 70`. Now, while a bridge entry works, it leaves the ATR as it is (`AtrAfterPayload`). With no
  bridge entry working, the rule is byte-for-byte today's (fixture 17).

## 4. Verified, and how

| Claim | Result | How |
|---|---|---|
| Gate | **GATE PASSED**, OrderCheck **510/510** (489 + 21) | `tools/checks/verify-gate.ps1`, executed on the final tree before the commit |
| Censuses | Before (`74f8399`): **73 across 69 lines** (13 · 8 · 3 · 3 · 1 · 0 · 13 · 13 · 19). After (`4ad3035`): **75 across 71 lines** (**15** · 8 · 3 · 3 · 1 · 0 · 13 · 13 · 19) | `Select-String -AllMatches -CaseSensitive` per symbol, match counts summed, lines de-duplicated. Executed before and after |
| Census change | `emergencyFired` 13 → 15: the cap's latch **read** (`:7116`) and **set** (`:7117`) in `TryFirePositionCapAsync`. No clear site added or changed. New comments avoid the symbol, so they add no count | read the lines; `git diff -U0` filtered on the symbol |
| Do-not-touch | `git diff 74f8399 4ad3035`: the 21 removed lines are the bridge's ATR block (replaced by the seam with the same rule), the 223347 send (now kept), the edit-send list helpers (generalised) and the refused-edit id gate. **None** touches the M.SL anchor latch, the `SB3` pin or its re-pin, `ArmEmergencyClose`, the reduce send, or a set/clear site of the emergency latch | the removed lines, read one by one |
| The fixtures fail against the pre-change behaviour | **Yes — six mutations, each restoring today's (or a rejected option's) behaviour in one seam:** | Executed: mutate by script, build OrderCheck, run; restore by byte copy **and touch**; SHA-256 equal before and after (`frmMainPageV2.vb` `b28ba9891e79f8c6…`, `SignalBridge.vb` `e9a555bda9215b02…`); 0 `MUTATION` strings left; rebuilt → `OK 510/510` |
| ↳ `M-B3-1` (`PositionCapFires` returns False: today, no cap) | **FAILED 2/510** — `B3-R1` fixtures 1, 2 | as above |
| ↳ `M-B3-1b` (the cap ignores an armed triggered path: option (b), which the owner rejected) | **FAILED 1/510** — `B3-R1` fixture 3 | as above |
| ↳ `M-B3-3` (`FeedCheck` never acts: today, nothing measures silence) | **FAILED 1/510** — `B3-R3` fixture 10 | as above |
| ↳ `M-B3-4` (`StandDownCancelsEntry` returns False: today, no cancel) | **FAILED 1/510** — `B3-R4` fixture 15 | as above |
| ↳ `M-B3-4b` (`AtrAfterPayload` without the bridge-entry hold: today's ATR rule) | **FAILED 1/510** — `B3-R4` fixture 18 | as above |
| ↳ `M-RE-SB5` (`KeepsEditSend` keeps 223344 only: `ddb2984`) | **FAILED 2/510** — `RE-SB5` fixtures 20, 21 | as above |
| Fixtures that pass under every mutation | `B3-R1` fixtures 4–9; `B3-R3` fixtures 11–13; `B3-R4` fixtures 14, 16, 17, 19. They pin the gates, texts, once-per-position rules and today's ATR rule | read |
| Line endings | `frmMainPageV2.vb` CRLF 9668 of 9668, BOM kept; `tools/OrderCheck/Program.vb` CRLF 2205 of 2205, no BOM; `SignalBridge.vb` LF 1351, no BOM — LF in the working copy and the index before this seat too (`git ls-files --eol`: `i/lf w/lf`) | Python byte count; `git ls-files --eol` |
| Both connect paths reset the feed watch | The button and the reconnect both call `ConnectToWebSocketDirectly` (`:1572`, `:8563`) | grep |
| Engine values (section 1) | ASIA 3, LONDON 3, NY 1 | `git grep` / `git show` on the engine's `master` (`ec1cc87`), read-only |
| Decision-bias tripwire | `B3-E1`: **`gives_up_for_economy`, agreement 0.8 (4/5, unstable)**; my baseline (written first) was `richer_option_wrong`. `B3-SB1`: `richer_option_wrong`, agreement 1.0 (5/5), matching my baseline | `docs/harness-runs/decision-bias-20261013T1824Z-{population,baseline,jev}.json` |
| Doc re-ranker | Not fired: every doc this seat read was named in the brief | — |

## 5. Not verified

- **Runtime — `docs/spec-protection-batch3.md` §5 item 3. OWED, needs the owner** (auto-mode refuses a seat's testnet
  order placement; the owner is away until 2026-11-25): the cap with the stop cancelled by testnet API; the dead feed
  by a temporary testnet-only switch that drops inbound frames; a stand-down with a bridge entry working (owner-armed);
  the freshness cap after `B3-E1` is ruled. **Nothing in this report has run in the app.**
- **What `Abort()` makes the pending `ReceiveAsync` throw** on .NET 9 (an `OperationCanceledException` or a
  `WebSocketException`). Both arms are handled: the stall calls the reconnect itself, and the reconnect is single-flight.
  Reasoned from the code, not observed.
- **Probe rate in a quiet book.** A probe goes out only after 3 s with no frame at all. How often that happens with a
  position open is not measured.
- **The SHORT side** of every path; the trailing-stop label (`TrailingStopLoss`), whose trailing moves do not update
  `StopLossTriggerOriginal`, so the cap would anchor at its placement trigger (further away, so later, never earlier).
- **The x64 bin.** Not rebuilt: no runtime test in this seat. The runtime seat rebuilds it.
- **`CLAUDE.md`'s census line** still reads 73 across 69 at `ddb2984`. Left for the coordinator, who updated it last time.

## 6. Findings (scoped `B3-…`)

| ID | Kind | Meaning | State |
|---|---|---|---|
| `B3-E1` | **Escalation (before code)** against `docs/spec-protection-batch3.md` §4 / `B3-R5` (a) | The ruled default `N` = 1 caps every window at 2.5 min. The engine emits `exec_resolution_min` = 3 in ASIA and LONDON and writes one payload per 3-min bar, so those payloads read as stale for most of each bar: **the bridge would stand down for most of Asia and London**, an effect the ruling was not made with. Options: **(a)** default `N` = 3 — every value the engine emits keeps today's window; anything larger is capped at 7.5 min (the audit's `60 → 150 min` case closes); **(b)** keep `N` = 1 (bridge NY-only in practice); **(c)** a per-session cap copying the engine's session table (ASIA/LONDON 3, NY 1) — tighter, but a hand-kept copy that can drift, as the engine's own session seed once did; **(d)** build nothing until the engine adds a data-age field. **Seat recommends (a).** Tripwire: **`gives_up_for_economy` 4/5** (unstable) — the richer option is (c). Per `CLAUDE.md`, the decision goes to the owner with (c) shown | **Not built. Waits for the owner (away until 2026-11-25).** On a ruling, the build is one setting, one seam and its fixtures |
| `B3-SB1` | Spec-back against `docs/spec-protection-batch3.md` §6 and §3 ("the scoped-cancel path … is the presumed handle") | The scoped cancel is the cancel, not an identity; no bridge mark existed. Built the handle from the bridge's placement ack (section 1). Every residual falls on the safe side: an ack that never arrives, or an app restart, leaves a bridge entry unmarked, so a stand-down does not cancel it (today's behaviour). The signal tag was rejected: it can mark a manual entry. Tripwire `richer_option_wrong` 5/5 | Built; for the coordinator to accept |
| `B3-SB2` | Implementer choices inside `B3-R1` / `B3-R2` | (1) The cap also stands down while a cancel is pending and while disconnected (the M.SL's own gating). (2) "No trigger" waits the id-778 snapshot and the 5-s no-stop grace; without them a restart pages falsely. (3) Both notices are priority `high`; the spec names none | Built; for the coordinator to accept |
| `B3-SB3` | Implementer choices inside `B3-R3` | (1) Any frame after the probe answers it, not only the id-44 reply. (2) "The bridge stands down" = `ForceStop`: STOPPED until the owner STARTs again — the same as a stale payload. (3) The forced reconnect aborts the socket rather than closing it. (4) A feed stall is not a `B3-R4` stand-down: a cancel cannot reach a dead socket | Built; for the coordinator to accept |
| `B3-SB4` | Scope of `B3-R4` | Stand-downs that cancel: stale (payload or 10-s tick), SKIPPED, engine ARM off, local ARM unchecked. Not: a mode change, STOP pressed, the circuit breaker (it is gated behind "no working entry"), a feed stall. Mode Off evaluates no payload stand-downs (it is the read-only ATR tap), but ARM unchecked cancels in any mode, and the ATR hold applies in Off too | Built; for the coordinator to accept or widen |
| `B3-SB5` | Gap outside the rulings | A stand-down cancel writes **no `AbortedEntries` row**. The slippage-fields ruling covers chase and placement aborts only, so the soak data will not show stand-down cancels except in the host log | Not built; a question for the coordinator |
| `B3-SB6` | Residual | The cap claims the latch under its own gate, but batch 2's R1 flatten and the triggered-path M.SL set it without that gate. A double claim needs two threads to pass within microseconds. The second order is reduce-only, so the position cannot flip; at worst one false `Emergency close FAILED` page. Not closed: it would change the R1 and M.SL latch code | Accepted by reasoning; for review |
| `B3-SB7` | Residual, by design of `B3-R1` (a) | The cap reads the own-side quote; Deribit triggers on the stop's trigger-price type. A long whose ask dips one M.SL below the trigger while the trigger price stays above it is flattened though the stop never triggered. In a gap, the cap can also fire before the trigger echo arrives. Both flatten at exactly one M.SL past the trigger — inside the owner's standing M.SL tolerance | For the owner's information |
| `B3-SB8` | Residual | Socket sends are still not serialised (`docs/review-protection-batch2.md` §4 item 1, queued). The probe is one more sender, fire-and-forget from the watchdog. A collision makes the send path call the reconnect. It only goes out after 3 s with no frame, when the quote-driven sends are idle | Accepted by reasoning; the queued hygiene item closes it |

## 7. Files

- `DeribitOrderPlacementApp/frmMainPageV2.vb` — the cap, the feed watch, the bridge-entry handle and stand-down
  cancel, the 223347 kept send.
- `DeribitOrderPlacementApp/SignalBridge.vb` — the stand-down calls, `PayloadStandDownReason`, `AtrAfterPayload`,
  `NotifyFeedStalled`.
- `tools/OrderCheck/Program.vb` — 21 fixtures, block "Protection batch 3" (from line 2087).
- `docs/harness-runs/decision-bias-20261013T1824Z-{population,baseline,jev}.json` — the tripwire run (UTC stamp;
  2026-10-14 GMT+8).
- `docs/outstanding.json` — this seat's row removed; the review row, the `B3-E1` ruling row and the push count updated.

## Model and effort (`docs/HANDOVER-6.md` §7b)

- **Review of `4ad3035`:** coordinator, **Opus 5.5, high** — the emergency path, the receive loop, the reconnect and
  the bridge act path.
- **Runtime (`docs/spec-protection-batch3.md` §5 item 3), after the owner returns:** **Opus 5.5, high**.
- **`B3-E1` build, once ruled:** **Opus 5.5, high** (bridge freshness gate; small).
