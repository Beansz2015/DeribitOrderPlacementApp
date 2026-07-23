# Runtime record — interlock ladder + live-at-min-size (2026-07-23)

**Owner-driven, coordinator-verified.** Contract §7 ladder step EXECUTED: the first live
bridge-placed orders on the real exchange (engine instance `46544566`, signals #19/#20/#23/#24).

## Ladder (§6.2) — PASS, every branch

START refused with ARM unchecked → refused with engine ARM off (named cause) → full sequence
STARTED → engine-ARM-off payload auto-STOPPED with named cause → re-sequence required and worked →
app restart mid-armed came back fully disarmed (restart = disarmed, proven live).

## Live placement (§6.3) — PASS, levels verified against the engine CSV by the coordinator

| Sig | Verdict | Engine Price | App entry | Outcome |
|---|---|---|---|---|
| #19 | SHORT (MEDIUM) | 64793.50 | 64793 | chased ×3, ATR-slippage abort (guard correct) |
| #20 | SHORT (MEDIUM) | 64794.50 | 64789 | chased ×7, ATR-slippage abort |
| #23 | SHORT (MEDIUM) | 64719.50 | 64717 | chased ×7, ATR-slippage abort |
| #24 | STRONG SHORT (HIGH) | 64735.00 | 64735 → filled 64734.98 | POSITION; exited via SL, −$0.01 |

**#24 level fidelity (R2 on the live exchange):** engine `PlacedStopShort` = **64789.60** →
`DeriveManualSl(SHORT)` = RoundToTick(64789.60 + StopLimitOffset 2) = **64791.5 — exactly the
observed SL limit/execution price**; the trigger = 64789.5 = the engine stop tick-rounded, within
the quarter-tick guarantee. Engine `PlacedTargetShort` = 64675.28 → TP leg at **64675.5**
(RoundToTick; never filled — SL exited first). ATR repoint live: the slippage lines priced off the
payload ATR (~33) as designed. De-dupe watermark advanced per acted; **cooloff anchored on the
position CLOSE** (log line) — no actionable signal arrived inside the 5-min window so the
`refused: cooloff` line itself wasn't exercised this session (proven in the soak, 5 occurrences).

## Item G's "clears-when-flat" half — OBSERVED, mechanism explained, cosmetic

After a flat slippage abort the Stop Loss box kept its last leg value (screenshot, 64867). Root
mechanism, verified in code: the pre-fill OTOCO SL leg's echo registers `PositionSLOrderId`
(`:2694`/`:2872`), so at the moment `CancelWorkingEntryCoreAsync` runs item G's guarded clear
(`positionSizeUSD = 0 ∧ Not SLTriggered ∧ PositionSLOrderId Is Nothing`, `:3711`), the leg id is
still registered — the guard **conservatively refuses to clear**, and the cascade-cancel echoes
that would prove flat arrive only afterwards. For bracketed entries the flat-clear branch is
therefore structurally near-unreachable — which is why it was never observed. **Fails safe in the
dangerous direction (never clears over a live position); the residue is a stale display box while
flat, overwritten by the next placement.** Accepted as-is; optional future follow-up (owner's
call, low value): re-run the guarded clear when the leg-cancel echoes confirm flat.

## Restart panel question — answered: by design

After a restart in mode **Off** the SIGNAL BRIDGE line reads `Off | stopped | Engine ARM: off |
Payload: stale/none` even while the engine runs armed: in Off the bridge is idle — it does not
watch the payload file, and it learns engine-ARM state **only from payloads it consumes**. The
panel shows the bridge's knowledge, not the engine's actual state. On switching to Log-only/Live
the watcher starts and the next payload (≤1 min) updates the line to `Engine ARM: ON | Payload:
FRESH` — exactly what the owner observed. Correct behaviour, no change needed.

## Standing after this session

Live-at-min-size is EXECUTED and verified. Remaining on the ladder: the owner enables the session
policy (P5 conservative values — at size 10 the LONDON/ASIA multipliers clamp to the contract
minimum with a yellow line, by design), then the step-up from minimum size is a pure owner
decision (production-cutover checklist still to be written). Housekeeping smoke riders: 8b's
guard-armed half observed live (slippage lines with the checkbox ON); the checkbox-OFF zero-lines
half and 14g's long-PnL-marks-to-bid remain opportunistic.
