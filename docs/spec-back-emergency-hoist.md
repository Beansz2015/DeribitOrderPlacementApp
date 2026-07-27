# Spec-back — N1 emergency-check hoist: review request (2026-07-27)

**Why this doc:** `spec-emergency-hoist.md` is implemented (`b4496b9` + correction `1a97054`) and the
owner wants a **coordinator review before runtime acceptance**. This is the review request: what
landed, the three decisions I made that the spec did not settle, one named residual, and the
adversarial cases I already ran so the reviewer can attack them rather than re-derive them.

**From:** the order-app Opus 5 implementer seat. **To:** the coordinator/orchestrator seat.
**Relay:** via the trader (neither seat writes cross-repo).

**State:** `origin/master = 9667dc8`. Local `master = 1a97054`, **3 commits ahead**
(`eea010c` docs, then the two N1 commits). Tree clean. **Gate EXECUTED at HEAD: GATE PASSED,
OrderCheck 104/104.** Nothing pushed. **No runtime acceptance run** — this seat placed no trades and
armed no bridge.

**Read alongside:** `docs/impl-report-emergency-hoist.md` (the full before→after, the latch's six
sites quoted, the §3 gating table ticked line by line). This doc does not repeat it.

---

## 1. Scope of the diff — what a reviewer has to check

Two commits, one code file plus its impl report.

| Commit | Touches | Nature |
|---|---|---|
| `b4496b9` | `frmMainPageV2.vb`, `docs/impl-report-emergency-hoist.md` (new) | the change |
| `1a97054` | same two | **comment + doc only** — a correction I found while writing this doc (§4) |

`git diff -w 5ef58c4..HEAD -- DeribitOrderPlacementApp/frmMainPageV2.vb`, non-comment lines, is
**eleven lines**: 1 field declaration, 2 `If` re-nestings (the block-structure move), 2
`Not emergencyFired` conjuncts, 2 latch sets, 3 latch clears, 1 closing `End If`. Everything else in
the 87/44 diffstat is comment.

The single most useful check: **`git diff -w` shows the emergency locals (`emergencyThreshold`,
`emergencyThresholdValid`, `emgBaseline`, `baselineKnown`) and the `priceMovement` computation as
unchanged.** That is the mechanical proof that the comparison was *moved*, not re-derived — the
thing spec §"byte-identical" cares most about. The chase's 50 %-force branch reads those same four
locals from the enclosing scope now, unchanged.

Tripwires, `5ef58c4` vs HEAD, all identical: `ResetCommandedSLPrices()` 8 · baseline-zero sites 11 ·
`cancelPending = False` 5 · `emergencyBaselineSettled` 10 · `RecordCommandedSLPrice` 3 ·
`SendReduceMarketOrderAsync` 5. (Two comment drafts briefly inflated two of these by containing the
literal token in prose; reworded. Flagging the failure mode — prose near a tripwire token pollutes
the standing grep.)

## 2. THREE DECISIONS NEEDING RATIFICATION

These are the reason the review is worth the coordinator's time. All three are recorded in the impl
report; none was deviated around silently.

### D1 — I implemented **3 clears**, not the acceptance line's "exactly 2"

The spec body says clear at "(1) `CompletePositionClose`; (2) the placement seeds where a fresh
order re-establishes a clean context (the `cancelPending = False` 'fresh order' sites)" — plural.
There are **exactly two** such seeds in the file: `ExecuteOrderAsync:3638` and
`StopLossForTrailingOrderAsync:4535`. The acceptance grep line then says "exactly 2 clears". Both
cannot hold.

**Implemented:** 3 (`CompletePositionClose` + both seeds). **Reasoning:** clearing only one seed is
an arbitrary asymmetry with a real hole behind it — a bracket placed via
`StopLossForTrailingOrderAsync` after an emergency that never reached `CompletePositionClose` would
run with the loss cap latched off. I read the acceptance line as a miscount of the body, not a
design intent.

**Ask:** ratify 3, or name which single seat the spec meant and why the other is safe to skip.

### D2 — spec §5's `CanMakeRequest` premise does not match HEAD

Spec §5 says "Rate-limiter guard on the fire path as today (`CanMakeRequest`)". **There is no such
guard**, and there never was on this path: the emergency branch of
`UpdateStopLossForTriggeredStopLossOrder` returns *before* that function's limiter section, and
neither `CancelOrderAsync` nor `SendReduceMarketOrderAsync` consults `rateLimiter`. I changed
nothing on account of it. The *derived* requirement — set the latch only where a send is genuinely
dispatched — is met: the set sits after every gate, on the same synchronous straight line as the
dispatch.

**Ask:** confirm this is a stale spec description and not a missing guard the coordinator intended
N1 to add. (If a limiter guard on the emergency taker close is actually wanted, that is a separate
spec — and I'd argue against it: rate-limiting the loss cap is the wrong direction.)

### D3 — I added one latch READ the spec did not ask for

At the hoisted check (`:2186`): `If marketStopLossChecked AndAlso Not emergencyFired Then`. The spec
specifies the latch's set and clear sites but not its reads. This one is load-bearing; §4 derives
the exact window. Without it, a tick in that window re-enters `ForceStopLossUpdate`, finds the fire
branch already latched out, and falls through to **editing the resting SL to the own-side touch** —
a defect the hoist would have introduced.

**Ask:** ratify the extra read.

## 3. NAMED RESIDUAL — latch set, send silently not dispatched

The one behaviour change I cannot argue away. `emergencyFired` is set immediately before
`Await CancelOrderAsync()`, but `SendReduceMarketOrderAsync` — two frames later — has two early
returns of its own: socket not connected, and empty position model with `orderAmountVal <= 0`. If
either fires, the latch is set and no reduce was sent; the cap then stays latched off for that
position until `CompletePositionClose` or a fresh placement, where **pre-N1 the next tick would have
retried**.

**Recommendation: accept + document.** Both paths are narrow (path 1 needs the socket to drop
between this block's `IsWebSocketConnected` gate and the send, across `CancelOrderAsync` — and a
down socket could not have sent the retry either; path 2 needs `positionSizeUSD = 0` in a
`SLTriggered` context with a live `PositionSLOrderId`). And the alternatives are worse:

- **Set the latch after the send returns** → reopens the §4 race. Trades a rare missed retry for a
  rare **duplicate taker close**. Wrong direction: the duplicate close is the more expensive error.
- **Clear the latch inside `SendReduceMarketOrderAsync`'s guard-returns** → wrong seam;
  `FlattenPositionAsync` and `btnReduceMarket_Click` share that function.

**Ask:** ratify accept, or direct a different trade-off.

## 4. FINDING — the latch closes a PRE-EXISTING double-fire race

This changes what N1 *is*, so it belongs in the review rather than only the impl report. The
double-fire the latch prevents **was already reachable before the hoist.**

`CancelOrderAsync` is the first statement of both fire branches, and every one of its state resets —
`placedStopLossPrice = 0` (`:3695`), `cancelPending = True` (`:3702`), `SLTriggered = False`
(`:3731`), `emergencyBaseline = 0` (`:3733`) — executes **after** its own
`Await SendWebSocketMessageAsync` (`:3691`). For the width of that one send-await, every gate that
would stop a re-entrant quote tick is still open:

| Gate | State during the window | Pre-N1? |
|---|---|---|
| `SLTriggered` / `Not IsCancelPending()` / socket | all still passing | same |
| throttle `MinStopLossUpdateInterval` | `ForceStopLossUpdate` set `lastStopLossUpdate = MinValue` ⇒ passes unconditionally | **same — this is not a hoist artefact** |
| `currentStopPrice > 0` | `placedStopLossPrice` not yet zeroed | same |
| emergency comparison | `emergencyBaseline` not yet zeroed, price still beyond cap | same |

`HandleQuoteUpdates` is `Async Sub` (fire-and-forget), so a tick landing there runs concurrently
rather than queueing. Pre-N1 it would have dispatched a **second `CancelOrderAsync` +
`SendReduceMarketOrderAsync`**. Narrow — one WS send — but real, and on the taker-close path.

Two consequences for the review:

1. The honest characterisation is **"hoist + close a latent double-fire race"**, not "hoist, plus a
   latch to pay for the hoist". Worth a line in whatever supersedes the item-16 trade-off note.
2. It sharpens what set-then-send has to be. It is not merely conventional here: the set is
   provably ahead of the first yield (an async body runs synchronously to its first incomplete
   await; the first on this path is the `CancelOrderAsync` *below* the assignment), so the latch is
   already visible to any tick that arrives during the window above.

**Correction on the record:** `b4496b9`'s code comment and impl-report §4(c) said a post-fire tick
would edit the SL to the touch "every tick while the close settles". That over-scoped it — the
window is the one send-await above, not the whole close. Corrected in `1a97054`; the conclusion
(D3 is load-bearing) is unchanged.

## 5. Adversarial cases already checked — attack these, don't re-derive them

| Case | Result |
|---|---|
| Re-entrant tick during the emergency's `CancelOrderAsync` | Latched. Set is synchronous, ahead of the first await (§4). |
| Post-fire ticks after `CancelOrderAsync` returns | Block gate already closed three ways (`SLTriggered` False, `cancelPending` True, baseline 0). Latch is belt-and-suspenders there. |
| Fire from the **chase** path (`:2240` 50 %-force → internal re-check), not the hoisted check | Same two branches, same latch. The historical "#68 quote / #71 send" pair is **not** two dispatch sites — #68 is the quote handler *detecting* and routing in; #71 is the internal re-check firing off a chase edit. Both converge on `:4139`/`:4152`. |
| Other `SendReduceMarketOrderAsync` callers | 5 occurrences: declaration, `FlattenPositionAsync`, `btnReduceMarket_Click`, and the 2 fire branches. Only the latter two are M.SL emergencies; the latch touches only those. |
| Throttled-out ticks now evaluate the emergency locals | Pure reads of engine fields + the tick's `bestBid`/`bestAsk`. No side effects, no allocation. On a throttle-passing tick, inputs and results are identical to pre-N1. |
| `bestBid`/`bestAsk` `Nothing` | Unchanged nullable arithmetic, copied verbatim; `priceMovement` lands 0 exactly as before. |
| Restart / restore-seeded baseline | `emergencyFired` defaults False; restore seeds the baseline; cap armed. No interaction. |
| Manual SL edit re-anchors the baseline after a fire | Latch deliberately **not** cleared (spec: not an SL-edit path). Intentional; worth a conscious tick from the reviewer since it means a manual re-anchor cannot re-arm the cap within the same position. |
| Latch vs the `emergencyBaselineSettled` hybrid latch | Independent. 10 sites, untouched, unchanged count. |
| Is it an 8th SL-context reset site? | No. Zeroes no price, no commanded set, no baseline. `ResetCommandedSLPrices()` stays 8. |

## 6. What I did NOT do

- **No runtime acceptance.** Spec §Acceptance 2 (testnet, isolated harness: persistent SL-edit
  failure ⇒ emergency fires without waiting for the throttle, exactly once; re-arm on a fresh
  position) and §3 (normal-session parity) are **open**. Safety boundary held.
- **No x64 rebuild.** The gate builds AnyCPU + Release only. The owner's runtime bin is still
  pre-N1 — it must be rebuilt before any runtime test, and the window title re-checked for
  `— TESTNET` afterwards.
- **No push.** Owner is the only pusher.
- **Nothing outside `frmMainPageV2.vb`** and the two docs.

## 7. Suggested review method (HANDOVER-3 §5)

1. `git show b4496b9` and `git show 1a97054` in full.
2. **EXECUTE `tools/checks/verify-gate.ps1`** — expect GATE PASSED, 104/104.
3. `git diff -w 5ef58c4..HEAD -- DeribitOrderPlacementApp/frmMainPageV2.vb` — confirm the eleven
   non-comment lines of §1 and that the emergency locals show as unchanged.
4. Re-grep independently: `emergencyFired` = 1 decl + 2 sets + 3 clears + 3 reads; the six tripwires
   of §1.
5. Adversarial pass on §4 — the pre-existing race is the claim most worth a second pair of eyes,
   because if it is wrong, D3 and the residual trade-off in §3 both need revisiting.
6. Rule on D1, D2, D3 and the §3 residual.

**Open asks: four (D1, D2, D3, §3).** None blocks the gate; all four block runtime acceptance, since
D1 in particular changes what "re-arm on a fresh position" is supposed to prove.
