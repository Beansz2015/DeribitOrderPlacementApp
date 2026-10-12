# Implementation report — partial fills follow-up: the wrong-side TP cancel, and PF-R4 seen at runtime

**Written 2026-10-13 (GMT+8) by the partial-fill follow-up implementer seat (Opus 5.5, high).**
**Spec:** `docs/review-pf-phase2.md` §3. The owner's ruling on `PF-SB3` (2026-10-13): cancel, then alert, as PF-R4.
The coordinator's ruling on `PF-SB2`: see PF-R4 work at runtime through a temporary testnet-only switch.
**Commits (local, unpushed):** `89ebf8a` (the wrong-side cancel and its fixtures) · `4f29398` (the temporary
switches) · `cdd3c2d` (pane: the run blocked by the permission check) · the commit that removes the switches and adds
this report and the pane update.
**Result:** built; gate PASSED, OrderCheck 466/466; **PF-R4 observed at runtime on testnet: the TP was cancelled by
id (request id 43) 2.65 s after the flat echo, and the urgent page came.** Switches removed (grep 0). Account flat,
no orders, cancel-on-disconnect OFF.

IDs in this report:
- `PF-…` are the partial-fill feature's IDs (`docs/spec-partial-fill-fix.md`). `PF-R4` is the owner's ruling that a
  TP resting while flat is cancelled, then alerted. `PF-SB2` and `PF-SB3` are the Phase 2 seat's spec-back findings:
  `PF-SB2` = an API flatten never reaches PF-R4, because the close path cancels all orders first; `PF-SB3` = a TP
  resting on the position's own side adds to the position if it fills.
- `PF-FU-…` are this seat's findings (section 6). `F3` is the once-a-second leg-size check
  (`docs/spec-partial-fill-fix.md` §4).

---

## 1. What changed

`frmMainPageV2.vb` line numbers are at the final tree (equal to `89ebf8a` for that file).

| Part | Change | Where |
|---|---|---|
| Ruling seam | `LegBreachCancelsTp(breach)`: True for `RestingFlat` and `WrongSide`. Was: only `RestingFlat` cancelled, inline in the tick | `:6723` |
| Page text | `TpWrongSideText` is now the constant `"TP on the position's side: cancelled"`. Was: a function, "TP (dir) would ADD to the LONG/SHORT n USD position - not touched, check it now". `TpGuardCancelText(breach)` picks the page for either cancel | `:6719`, `:6727` |
| Refusal wording | `TpGuardStillRestsText(breach)`: "a TP still rests on the position's side" or "… with no position". Used by the refused-cancel page (reply handler) and the not-sent page. Field `tpGuardCancelKind` carries the breach to the reply handler (display only) | `:6732`, `:6904`, `:6935` |
| Cancel send | `SendTpGuardCancelAsync(tpId, kind)` returns True when the cancel went out. A failed send pages `TP cancel NOT SENT (socket) — …` as before | `:6938` |
| F3 tick | `EvaluateLegSizeCheckAsync`: `If LegBreachCancelsTp(action)` → send the cancel first; when it went out, red log and urgent page with `TpGuardCancelText(action)`. Was: `RestingFlat` paged first, then sent; `WrongSide` logged red, played the `no_stop` alarm sound and paged, touching no order | `:7000` |
| Once per episode | Unchanged: `LegSizeCheckFires` and the episode fields. A refused cancel is not re-sent within an episode | — |
| OrderCheck | 3 fixtures (463 → 466), "PF follow-up fixture 1–3"; the label of F3 fixture 6 loses "(alarm only)" | `tools/OrderCheck/Program.vb:1894`, `:1918`, `:1925`, `:1932` |
| Temporary switches | `PfFuTestnet.vb` (pattern of `X2Testnet.vb` at `7cfc477`): `PFFU_SKIP_CLOSE_CANCEL` (CompletePositionClose does not send its id-30 cancel-all; every engine reset in `CancelOrderAsync` still runs) and `PFFU_RAW_ECHO`. Each ON only when `AppSecrets.IsTestnet` and its flag file exists. Added in `4f29398`, **removed** in the final commit | — |

Two behaviour points beyond the letter of `docs/review-pf-phase2.md` §3, both on the cancel path (see `PF-FU-SB1`):
- **Both** cancels (flat and wrong side) now send first, and page "cancelled" only when the send went out. The
  ruling's option text reads "cancel …, then log red and send an urgent page saying it was cancelled".
- The wrong-side case no longer plays the local `no_stop` alarm sound. The ruling says "the same action" as the flat
  case, which has none.

## 2. Verified, and how

| Claim | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 466/466** at `89ebf8a`, at `4f29398`, and on the final tree | `tools/checks/verify-gate.ps1`, executed three times; output read from file |
| Nine `frmMainPageV2.vb` censuses | **71 occurrences across 67 lines** (12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 19) before the change, at both commits, and on the final tree. No change: no census symbol was touched | `Select-String -AllMatches -CaseSensitive` per symbol, match counts summed, lines de-duplicated. Executed four times |
| The new fixtures fail against `fd73951` | **Yes.** Mutation `M-fd73951` (`LegBreachCancelsTp` → RestingFlat only; the old wrong-side text; the refusal tail always "with no position"): **FAILED 2/466** — follow-up fixtures 1 and 2. Follow-up fixture 3 passes on both: it guards the once-per-episode rule, which did not change | Executed: copy, mutate by script, build OrderCheck, run; restored by byte copy **and touch**; SHA-256 equal to before (`438bc09f…`); no `MUTATION` text left; rebuilt → `OK 466/466` |
| Switches removed | **0** references to `PfFuTestnet`, `PFFU_`, `skipSend` or `pffu-raw` in `*.vb` / `*.vbproj`. `git diff 89ebf8a -- DeribitOrderPlacementApp/` is empty | grep; git |
| x64 bin after | Rebuilt from the final tree (dll 2026-10-12 20:03:55 UTC): carries `TP on the position's side: cancelled`, not `PFFU_SKIP_CLOSE_CANCEL` | `dotnet build … -p:Platform=x64`; UTF-16 string scan at both byte alignments |
| Line endings | `frmMainPageV2.vb` CRLF 8906 of 8906; `Program.vb` CRLF throughout | Python byte count (see `PF-FU-SB3`) |
| PF-R4 at runtime | section 3 | raw echo, app log, ntfy, API |

## 3. Runtime record — PF-R4 on testnet (2026-10-12 20:01–20:03 UTC)

### 3.1 Set-up and safety

| Item | Value | How checked |
|---|---|---|
| Bin | x64 Debug built from `4f29398` (dll 19:54 UTC); carries the switch and the wrong-side text | `dotnet build … -p:Platform=x64`; string scan |
| Environment | `secrets.json` → `testnet`; hash unchanged after | read before launch; the launcher and the API helper refuse any other value; SHA-256 |
| Window title | `Deribit Order Placement App V2.2 — TESTNET` (PID 35452) | the launcher's title check; `place-and-verify.ps1` re-checks |
| Owner's instance | not running | no `DeribitOrder*` process before launch |
| Flags | `PFFU_SKIP_CLOSE_CANCEL`, `PFFU_RAW_ECHO` created before launch, deleted after | listing |
| Placements | 1: `Mkt. BUY` 10 through `tools/place-and-verify.ps1` (exit 0, "Effect verified … x1"). Boxes by `tools/set-textbox.ps1 -CommitViaBlur`: Amount 10, `Trig. P.` 150 (each exit 0) | exit codes |
| Bridge | mode Off; never armed | startup line `[BRIDGE] consumer ready (mode Off)` |
| Testnet API calls | 14, all in Appendix B. State-changing: 1 × `private/close_position`. Host hard-coded to `test.deribit.com`; allow list; no key or token printed; `user_id` redacted | the helper script |
| A first attempt | At 19:55 UTC the placement was refused by Claude Code's auto-mode permission check, before any script ran. Nothing was sent. I stopped the app, removed the flags, read the account back (flat, no orders, COD OFF; Appendix B, 19:55:44–19:55:46) and committed the pane as blocked (`cdd3c2d`). The owner then allowed testnet placements through `tools/place-and-verify.ps1` | API |
| Start | flat, no orders, cancel-on-disconnect OFF | API, 19:54:28–19:54:30 |
| **End** | **flat, no orders, cancel-on-disconnect OFF** | API, 20:03:18–20:03:20 |
| Bin after | `orderapp-settings.json` restored from the backup (the Amount and `Trig. P.` writes had changed it; hash now equal to the backup `8FACB47A…`); no `PFFU_*`, `X1_*`, `X2_*` flag; no `r1-anchor.json`; the raw-echo log moved to `%LOCALAPPDATA%\DeribitOrderApp\pffu-backup-20261013\` (it holds account-summary lines, so it is not committed) | hash, listing |
| Non-zero exits | None | exit codes logged |

### 3.2 Results

| Step | Observed | Verdict |
|---|---|---|
| Open | Payload `first_hit`, OTOCO, full-size legs, as before (Appendix A). Whole fill LONG 10 @ 82785. Post-fill TP `122923006765` open, amount 10, price 83184.5; SL `SLTS-11032708` untriggered, amount 10. API read: size 10, both orders open | ✅ |
| Flatten | Testnet API `private/close_position` market at 20:02:22.241. Flat echo at **20:02:22.455** (fill 82783) | ✅ |
| The switch | App log `PF follow-up testnet switch: close cancel-all NOT sent`. **No id-30 request** in the raw echo between the flat echo and the F3 cancel | ✅ |
| **PF-R4** | **OUT id 43 `private/cancel` on `122923006765` at 20:02:25.109 (2.65 s after the flat echo).** Reply at 20:02:25.285: `order_state cancelled`, `cancel_reason user_request`. App log (red) `TP resting with no position: cancelled`. **Page at 20:02:25, priority 5 (urgent), same text** | ✅ **PASS** (Appendix A; Appendix C) |
| The SL | `SLTS-11032708` cancelled at 20:02:25.292, `cancel_reason oco_other_closed`: **a user cancel of the TP leg takes the OCO-linked SL with it** (see `PF-FU-SB2`) | observed |
| Other page | The existing `Position closed with NO tracked fill (external / liquidation?)`, urgent, 20:02:22 — the app's external-close path, as in the Phase 2 run | as before |

The latency fits the design: F3 waits out a 2-s grace (`LegSizeGraceSeconds`) and ticks once a second, so 2–3 s.

## 4. Not verified

- **The wrong-side cancel at runtime.** A wrong-side TP needs a position flip or a manual mismatch; I did not
  produce one. Fixture-covered only (follow-up fixtures 1–3), and the tick shares the cancel path that section 3 saw work.
- The refused-cancel and not-sent pages (their new wording): not exercised at runtime.
- The SHORT side; any real partial fill (`docs/spec-partial-fill-fix.md` §5 item 5 is still owed).
- That the OCO-linked SL is also cancelled when the cancelled TP is **wrong side** (`PF-FU-SB2`): seen only for the
  flat case.

## 5. Harnesses

- **Doc re-ranker:** not fired. Every document I needed was named in the brief; I hunted no decision.
- **Decision-bias tripwire:** not fired. I made no recommendation between options. The two behaviour points in
  section 1 follow the ruling's text; they are listed for the coordinator in `PF-FU-SB1`, not decided as options.

## 6. Findings (scoped `PF-FU-…`)

| ID | Kind | Meaning | State |
|---|---|---|---|
| `PF-FU-SB1` | Spec-back (an implementation reading of the ruling) | The flat-case cancel now also sends before it pages, and pages "cancelled" only when the send went out; the wrong-side case lost its local `no_stop` alarm sound. Both follow the ruling's option text ("cancel …, then log red and send an urgent page"; "the same action") | Built in `89ebf8a`; for the coordinator to confirm |
| `PF-FU-SB2` | Spec-back (an exchange behaviour the ruling did not name) | A user `private/cancel` of the post-fill TP also cancels its OCO-linked SL (`oco_other_closed`, 7 ms later; section 3.2). For the flat case this is what we want. For the wrong-side case it means the cancel also removes the old SL. **Reasoning, not observed:** the app's SL leg has the same direction as its TP and is reduce-only, so when the TP is on the position's side the SL is too, and it cannot reduce the position anyway. The position is then without a stop, and batch 2's no-stop watchdog (alarm only, owner ruling R2) pages after 5 s | Not acted on. For the coordinator: confirm that the watchdog page is the intended follow-on, or rule a re-placed stop |
| `PF-FU-SB3` | Method | The Git Bash line-ending trap hit again: one `sed -i` turned `frmMainPageV2.vb` to LF, and Git Bash `grep -c $'\r$'` reported CRLF falsely. Restored to CRLF before any build that counted; counted with Python bytes | Closed |

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Review of this seat:** coordinator, Opus 5.5, high — the order and close paths.
- **`PF-FU-SB2` follow-up, if the coordinator rules a change:** Opus 5.5, high.

---

## Appendix A — raw echoes, verbatim

From `pffu-raw-echo.log` (the `PFFU_RAW_ECHO` switch). Multi-line outbound JSON is joined to one line. The only
change: `user_id` → `<redacted>`.

```text
2026-10-12T20:01:57.928Z OUT { "jsonrpc": "2.0", "id": 600001, "method": "private/buy", "params": { "instrument_name": "BTC-PERPETUAL", "amount": 10.0, "type": "market", "label": "EntryMarketOrder", "time_in_force": "good_til_cancelled", "linked_order_type": "one_triggers_one_cancels_other", "trigger_fill_condition": "first_hit", "reject_post_only": false, "otoco_config": [ { "amount": 10.0, "direction": "sell", "type": "limit", "label": "TakeLimitProfit", "price": 83184.5, "time_in_force": "good_til_cancelled", "post_only": true }, { "amount": 10.0, "direction": "sell", "type": "stop_limit", "trigger_price": 82634.5, "price": 82631.5, "label": "StopLossOrder", "time_in_force": "good_til_cancelled", "trigger_offset": 30.0, "post_only": true, "reduce_only": true, "reject_post_only": false, "trigger": "last_price" } ] } }
2026-10-12T20:01:58.113Z IN  {"jsonrpc":"2.0","id":600001,"result":{"order":{"label":"EntryMarketOrder","price":85267.5,"user_id":"<redacted>","direction":"buy","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006761","creation_timestamp":1791835318317,"starbase_order_id":235492210919866368,"filled_amount":10.0,"average_price":82785.0,"mmp":false,"post_only":false,"replaced":false,"reduce_only":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318317,"contracts":1.0,"is_primary_otoco":true,"order_state":"filled","order_type":"market","starbase_last_update_timestamp":1791835318317285623,"is_liquidation":false,"risk_reducing":false,"oto_order_ids":["OTO-11032706","OTO-11032707"]},"trades":[{"label":"EntryMarketOrder","timestamp":1791835318317,"state":"filled","price":82785.0,"user_id":"<redacted>","direction":"buy","index_price":82760.38,"instrument_name":"BTC-PERPETUAL","trade_seq":142668328,"api":true,"amount":10.0,"mark_price":82783.98,"order_id":"122923006761","matching_id":null,"tick_direction":0,"profit_loss":0.0,"starbase_order_id":235492210919866368,"mmp":false,"post_only":false,"reduce_only":false,"starbase_match_id":235492210919874560,"trade_id":"273013749","self_trade":false,"contracts":1.0,"fee_currency":"BTC","starbase_timestamp":1791835318317285623,"fee":6.0e-8,"order_type":"market","risk_reducing":false,"liquidity":"T"}]},"usIn":1791835318315038,"usOut":1791835318320254,"usDiff":5216,"testnet":true}
2026-10-12T20:01:58.129Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"EntryMarketOrder","price":85267.5,"user_id":"<redacted>","direction":"buy","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006761","creation_timestamp":1791835318317,"starbase_order_id":235492210919866368,"filled_amount":10.0,"average_price":82785.0,"mmp":false,"post_only":false,"replaced":false,"reduce_only":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318317,"contracts":1.0,"is_primary_otoco":true,"order_state":"filled","order_type":"market","starbase_last_update_timestamp":1791835318317285623,"is_liquidation":false,"risk_reducing":false,"oto_order_ids":["OTO-11032706","OTO-11032707"]}],"instrument_name":"BTC-PERPETUAL","trades":[{"label":"EntryMarketOrder","timestamp":1791835318317,"state":"filled","price":82785.0,"user_id":"<redacted>","direction":"buy","index_price":82760.38,"instrument_name":"BTC-PERPETUAL","trade_seq":142668328,"api":true,"amount":10.0,"mark_price":82783.98,"order_id":"122923006761","matching_id":null,"tick_direction":0,"profit_loss":0.0,"starbase_order_id":235492210919866368,"mmp":false,"post_only":false,"reduce_only":false,"starbase_match_id":235492210919874560,"trade_id":"273013749","self_trade":false,"contracts":1.0,"fee_currency":"BTC","starbase_timestamp":1791835318317285623,"fee":6.0e-8,"order_type":"market","risk_reducing":false,"liquidity":"T"}],"positions":[{"size":10.0,"kind":"future","user_id":"<redacted>","maintenance_margin":1.611e-6,"initial_margin":2.416e-6,"direction":"buy","index_price":82760.38,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82783.98,"leverage":50.0,"interest_value":0.0011241349360185716,"delta":1.20796e-4,"average_price":82785.0,"floating_profit_loss":-1.0e-9,"realized_profit_loss":6.2e-8,"total_profit_loss":-1.0e-9,"realized_funding":0.0,"size_currency":1.20796e-4}]}}}
2026-10-12T20:01:58.159Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"OTO-11032706","creation_timestamp":1791835318315,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"is_secondary_oto":true,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318315,"order_state":"untriggered","order_type":"limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:01:58.161Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"StopLossOrder","price":82631.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"OTO-11032707","creation_timestamp":1791835318315,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"trigger":"last_price","trigger_price":82634.5,"mmp":false,"post_only":true,"replaced":false,"reduce_only":true,"oco_ref":50157,"is_secondary_oto":true,"trigger_offset":null,"triggered":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318315,"order_state":"untriggered","order_type":"stop_limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:01:58.161Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006765","creation_timestamp":1791835318315,"starbase_order_id":235492210928254980,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318319,"contracts":1.0,"order_state":"open","order_type":"limit","starbase_last_update_timestamp":1791835318319996297,"is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:01:58.165Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"OTO-11032706","creation_timestamp":1791835318315,"filled_amount":10.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"is_secondary_oto":true,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318315,"trigger_order_id":122923006765,"order_state":"triggered","order_type":"limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:01:58.165Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"StopLossOrder","price":82631.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"SLTS-11032708","creation_timestamp":1791835318321,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"trigger":"last_price","trigger_price":82634.5,"mmp":false,"post_only":true,"replaced":false,"reduce_only":true,"oco_ref":50157,"trigger_offset":null,"triggered":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318321,"order_state":"untriggered","order_type":"stop_limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:01:58.166Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"StopLossOrder","price":82631.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"OTO-11032707","creation_timestamp":1791835318315,"filled_amount":10.0,"average_price":0.0,"reject_post_only":false,"trigger":"last_price","trigger_price":82634.5,"mmp":false,"post_only":true,"replaced":false,"reduce_only":true,"oco_ref":50157,"is_secondary_oto":true,"trigger_offset":null,"triggered":true,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318315,"trigger_order_id":"SLTS-11032708","order_state":"triggered","order_type":"stop_limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:02:22.455Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"","price":8.03e4,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923046139","creation_timestamp":1791835342659,"starbase_order_id":235492313017614340,"filled_amount":10.0,"average_price":82783.0,"mmp":false,"post_only":false,"replaced":false,"reduce_only":true,"last_update_timestamp":1791835342659,"contracts":1.0,"order_state":"filled","order_type":"market","starbase_last_update_timestamp":1791835342659658804,"is_liquidation":false,"risk_reducing":false}],"instrument_name":"BTC-PERPETUAL","trades":[{"timestamp":1791835342659,"state":"filled","price":82783.0,"user_id":"<redacted>","direction":"sell","index_price":82760.57,"instrument_name":"BTC-PERPETUAL","trade_seq":142668348,"api":true,"amount":10.0,"mark_price":82783.62,"order_id":"122923046139","matching_id":null,"tick_direction":0,"profit_loss":0.0,"starbase_order_id":235492313017614340,"mmp":false,"post_only":false,"reduce_only":true,"starbase_match_id":235492313017622528,"trade_id":"273013795","self_trade":false,"contracts":1.0,"fee_currency":"BTC","starbase_timestamp":1791835342659658804,"fee":6.0e-8,"order_type":"market","risk_reducing":false,"liquidity":"T"}],"positions":[{"size":0.0,"kind":"future","user_id":"<redacted>","maintenance_margin":0.0,"initial_margin":0.0,"direction":"zero","index_price":82760.57,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82783.62,"leverage":50,"interest_value":0.0011241349360185716,"delta":0.0,"average_price":0.0,"floating_profit_loss":0.0,"realized_profit_loss":5.9e-8,"total_profit_loss":0.0,"realized_funding":0.0,"size_currency":0.0}]}}}
2026-10-12T20:02:25.109Z OUT { "jsonrpc": "2.0", "id": 43, "method": "private/cancel", "params": { "order_id": "122923006765" } }
2026-10-12T20:02:25.285Z IN  {"jsonrpc":"2.0","id":43,"result":{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006765","creation_timestamp":1791835318315,"starbase_order_id":235492210928254980,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835345495,"cancel_reason":"user_request","contracts":1.0,"order_state":"cancelled","order_type":"limit","starbase_last_update_timestamp":1791835345495406862,"is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761},"usIn":1791835345494719,"usOut":1791835345495949,"usDiff":1230,"testnet":true}
2026-10-12T20:02:25.287Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006765","creation_timestamp":1791835318315,"starbase_order_id":235492210928254980,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835345495,"cancel_reason":"user_request","contracts":1.0,"order_state":"cancelled","order_type":"limit","starbase_last_update_timestamp":1791835345495406862,"is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
2026-10-12T20:02:25.292Z IN  {"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.changes.BTC-PERPETUAL.raw","data":{"orders":[{"label":"StopLossOrder","price":82631.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"SLTS-11032708","creation_timestamp":1791835318321,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"trigger":"last_price","trigger_price":82634.5,"mmp":false,"post_only":true,"replaced":false,"reduce_only":true,"oco_ref":50157,"trigger_offset":null,"triggered":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835345495,"cancel_reason":"oco_other_closed","order_state":"cancelled","order_type":"stop_limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"instrument_name":"BTC-PERPETUAL","trades":[],"positions":[]}}}
```

## Appendix B — testnet API calls, verbatim

The helper (this seat's scratchpad `tn-api.ps1`; copied from the Phase 2 seat; host hard-coded to
`test.deribit.com`) prints the request line and the reply. The only change to a reply: `user_id` → `<redacted>`.

```text
2026-10-12T19:54:28.221Z REQUEST GET /api/v2/private/get_cancel_on_disconnect?scope=account
2026-10-12T19:54:28.481Z REPLY {"jsonrpc":"2.0","result":{"enabled":false,"scope":"account"},"usIn":1791834868651181,"usOut":1791834868651455,"usDiff":274,"testnet":true}
2026-10-12T19:54:29.157Z REQUEST GET /api/v2/private/get_position?instrument_name=BTC-PERPETUAL
2026-10-12T19:54:29.364Z REPLY {"jsonrpc":"2.0","result":{"size":0.0,"kind":"future","user_id":"<redacted>","maintenance_margin":0.0,"initial_margin":0.0,"open_orders_margin":0.0,"direction":"zero","index_price":82400.47,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82430.01,"leverage":50,"interest_value":0.0011241349360185716,"delta":0.0,"average_price":0.0,"floating_profit_loss":0.0,"realized_profit_loss":6.2e-8,"total_profit_loss":0.0,"realized_funding":0.0,"size_currency":0.0,"estimated_liquidation_price":null},"usIn":1791834869564395,"usOut":1791834869565301,"usDiff":906,"testnet":true}
2026-10-12T19:54:30.046Z REQUEST GET /api/v2/private/get_open_orders_by_instrument?instrument_name=BTC-PERPETUAL
2026-10-12T19:54:30.271Z REPLY {"jsonrpc":"2.0","result":[],"usIn":1791834870466571,"usOut":1791834870467026,"usDiff":455,"testnet":true}
2026-10-12T19:55:44.214Z REQUEST GET /api/v2/private/get_position?instrument_name=BTC-PERPETUAL
2026-10-12T19:55:44.428Z REPLY {"jsonrpc":"2.0","result":{"size":0.0,"kind":"future","user_id":"<redacted>","maintenance_margin":0.0,"initial_margin":0.0,"open_orders_margin":0.0,"direction":"zero","index_price":82713.27,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82735.83,"leverage":50,"interest_value":0.0011241349360185716,"delta":0.0,"average_price":0.0,"floating_profit_loss":0.0,"realized_profit_loss":6.2e-8,"total_profit_loss":0.0,"realized_funding":0.0,"size_currency":0.0,"estimated_liquidation_price":null},"usIn":1791834944618530,"usOut":1791834944619480,"usDiff":950,"testnet":true}
2026-10-12T19:55:45.067Z REQUEST GET /api/v2/private/get_open_orders_by_instrument?instrument_name=BTC-PERPETUAL
2026-10-12T19:55:45.284Z REPLY {"jsonrpc":"2.0","result":[],"usIn":1791834945479449,"usOut":1791834945479923,"usDiff":474,"testnet":true}
2026-10-12T19:55:45.943Z REQUEST GET /api/v2/private/get_cancel_on_disconnect?scope=account
2026-10-12T19:55:46.152Z REPLY {"jsonrpc":"2.0","result":{"enabled":false,"scope":"account"},"usIn":1791834946350295,"usOut":1791834946350601,"usDiff":306,"testnet":true}
2026-10-12T20:02:11.617Z REQUEST GET /api/v2/private/get_position?instrument_name=BTC-PERPETUAL
2026-10-12T20:02:11.822Z REPLY {"jsonrpc":"2.0","result":{"size":10.0,"kind":"future","user_id":"<redacted>","maintenance_margin":1.611e-6,"initial_margin":2.416e-6,"open_orders_margin":0.0,"direction":"buy","index_price":82760.82,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82784.22,"leverage":50.0,"interest_value":0.0011241349360185716,"delta":1.20796e-4,"average_price":82785.0,"floating_profit_loss":-1.0e-9,"realized_profit_loss":6.2e-8,"total_profit_loss":-1.0e-9,"realized_funding":0.0,"size_currency":1.20796e-4,"estimated_liquidation_price":null},"usIn":1791835332015071,"usOut":1791835332015934,"usDiff":863,"testnet":true}
2026-10-12T20:02:12.432Z REQUEST GET /api/v2/private/get_open_orders_by_instrument?instrument_name=BTC-PERPETUAL
2026-10-12T20:02:12.650Z REPLY {"jsonrpc":"2.0","result":[{"label":"TakeLimitProfit","price":83184.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923006765","creation_timestamp":1791835318315,"starbase_order_id":235492210928254980,"filled_amount":0.0,"average_price":0.0,"reject_post_only":false,"mmp":false,"post_only":true,"replaced":false,"reduce_only":false,"oco_ref":50157,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318319,"contracts":1.0,"order_state":"open","order_type":"limit","starbase_last_update_timestamp":1791835318319996297,"is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761},{"label":"StopLossOrder","price":82631.5,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"SLTS-11032708","creation_timestamp":1791835318321,"reject_post_only":false,"trigger":"last_price","trigger_price":82634.5,"mmp":false,"post_only":true,"replaced":false,"reduce_only":true,"oco_ref":50157,"triggered":false,"trigger_fill_condition":"first_hit","last_update_timestamp":1791835318321,"order_state":"untriggered","order_type":"stop_limit","is_liquidation":false,"risk_reducing":false,"primary_order_id":122923006761}],"usIn":1791835332834598,"usOut":1791835332839942,"usDiff":5344,"testnet":true}
2026-10-12T20:02:22.241Z REQUEST GET /api/v2/private/close_position?instrument_name=BTC-PERPETUAL&type=market
2026-10-12T20:02:22.469Z REPLY {"jsonrpc":"2.0","result":{"order":{"label":"","price":8.03e4,"user_id":"<redacted>","direction":"sell","time_in_force":"good_til_cancelled","instrument_name":"BTC-PERPETUAL","web":false,"api":true,"amount":10.0,"order_id":"122923046139","creation_timestamp":1791835342659,"starbase_order_id":235492313017614340,"filled_amount":10.0,"average_price":82783.0,"mmp":false,"post_only":false,"replaced":false,"reduce_only":true,"last_update_timestamp":1791835342659,"contracts":1.0,"order_state":"filled","order_type":"market","starbase_last_update_timestamp":1791835342659658804,"is_liquidation":false,"risk_reducing":false},"trades":[{"timestamp":1791835342659,"state":"filled","price":82783.0,"user_id":"<redacted>","direction":"sell","index_price":82760.57,"instrument_name":"BTC-PERPETUAL","trade_seq":142668348,"api":true,"amount":10.0,"mark_price":82783.62,"order_id":"122923046139","matching_id":null,"tick_direction":0,"profit_loss":0.0,"starbase_order_id":235492313017614340,"mmp":false,"post_only":false,"reduce_only":true,"starbase_match_id":235492313017622528,"trade_id":"273013795","self_trade":false,"contracts":1.0,"fee_currency":"BTC","starbase_timestamp":1791835342659658804,"fee":6.0e-8,"order_type":"market","risk_reducing":false,"liquidity":"T"}]},"usIn":1791835342658392,"usOut":1791835342661561,"usDiff":3169,"testnet":true}
2026-10-12T20:02:40.779Z REQUEST GET /api/v2/private/get_position?instrument_name=BTC-PERPETUAL
2026-10-12T20:02:40.998Z REPLY {"jsonrpc":"2.0","result":{"size":0.0,"kind":"future","user_id":"<redacted>","maintenance_margin":0.0,"initial_margin":0.0,"open_orders_margin":0.0,"direction":"zero","index_price":82762.57,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82786.48,"leverage":50,"interest_value":0.0011241349360185716,"delta":0.0,"average_price":0.0,"floating_profit_loss":0.0,"realized_profit_loss":5.9e-8,"total_profit_loss":0.0,"realized_funding":0.0,"size_currency":0.0,"estimated_liquidation_price":null},"usIn":1791835361181975,"usOut":1791835361182767,"usDiff":792,"testnet":true}
2026-10-12T20:02:41.683Z REQUEST GET /api/v2/private/get_open_orders_by_instrument?instrument_name=BTC-PERPETUAL
2026-10-12T20:02:41.899Z REPLY {"jsonrpc":"2.0","result":[],"usIn":1791835362092502,"usOut":1791835362093121,"usDiff":619,"testnet":true}
2026-10-12T20:03:18.418Z REQUEST GET /api/v2/private/get_position?instrument_name=BTC-PERPETUAL
2026-10-12T20:03:18.628Z REPLY {"jsonrpc":"2.0","result":{"size":0.0,"kind":"future","user_id":"<redacted>","maintenance_margin":0.0,"initial_margin":0.0,"open_orders_margin":0.0,"direction":"zero","index_price":82786.41,"instrument_name":"BTC-PERPETUAL","settlement_price":83495.91,"mark_price":82812.0,"leverage":50,"interest_value":0.0011241349360185716,"delta":0.0,"average_price":0.0,"floating_profit_loss":0.0,"realized_profit_loss":5.9e-8,"total_profit_loss":0.0,"realized_funding":0.0,"size_currency":0.0,"estimated_liquidation_price":null},"usIn":1791835398821870,"usOut":1791835398824187,"usDiff":2317,"testnet":true}
2026-10-12T20:03:19.216Z REQUEST GET /api/v2/private/get_open_orders_by_instrument?instrument_name=BTC-PERPETUAL
2026-10-12T20:03:19.428Z REPLY {"jsonrpc":"2.0","result":[],"usIn":1791835399615641,"usOut":1791835399616105,"usDiff":464,"testnet":true}
2026-10-12T20:03:20.058Z REQUEST GET /api/v2/private/get_cancel_on_disconnect?scope=account
2026-10-12T20:03:20.283Z REPLY {"jsonrpc":"2.0","result":{"enabled":false,"scope":"account"},"usIn":1791835400475346,"usOut":1791835400475747,"usDiff":401,"testnet":true}
```

## Appendix C — pages, as the ntfy server holds them (since the second launch)

Read-only poll of the app's topic (the URL is a credential and is not printed). `p3` = default, `p5` = urgent.

```text
2026-10-12T20:01:25Z p3 [OrderApp] OrderApp started — TESTNET, breaker $10
2026-10-12T20:02:22Z p5 [OrderApp] Position closed with NO tracked fill (external / liquidation?)
2026-10-12T20:02:25Z p5 [OrderApp] TP resting with no position: cancelled
```
