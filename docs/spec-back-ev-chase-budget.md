# Spec-back — EV-aware chase budget: review request (2026-07-28)

**Why this doc:** `spec-ev-chase-budget.md` is implemented (`03bafe8` · `2ffbe1a` · `fef4371` ·
`e9f5bfc`) and ships OFF. This is the review request: what landed, the four decisions the spec did
not settle, one named residual, one finding that is arguably in the fee relay's own scope and was
missed by it, and the adversarial cases already run so the reviewer can attack them rather than
re-derive them.

**From:** the order-app Opus 5 implementer seat. **To:** the coordinator/orchestrator seat.
**Relay:** via the trader (neither seat writes cross-repo).

**State:** `origin/master = eea010c`. Local `master` = the four EV commits on top of `cd567e1`,
**19 ahead**. Tree clean apart from this doc + one comment fix (§1). **Gate EXECUTED at HEAD:
GATE PASSED, OrderCheck 124/124** (104 pre-EV; +20). Nothing pushed. **No runtime acceptance run** —
this seat placed no trades, armed no bridge, and did not launch the app.

**Read alongside:** `docs/impl-report-ev-chase-budget.md` (the unit table, the per-§ walkthrough, the
acceptance matrix, the owner runtime recipe). This doc does not repeat it.

**Contract impact: NONE**, as the spec's header predicted — no payload fields, no disposition
tokens, no change to the disposition file's format or cardinality. `SignalBridge.vb` is not in the
diff at all. **Relay to the engine seat: nothing** beyond the ack header the owner already holds.

---

## 1. Scope of the diff — what a reviewer has to check

Four commits, five files.

| Commit | Touches | Nature |
|---|---|---|
| `03bafe8` | `frmMainPageV2.vb`, `AppUserSettings.vb`, `orderapp-settings.example.json`, `OrderCheck/Program.vb` | pure seam + config + 13 fixtures. **No gate wired — zero runtime behaviour change.** |
| `2ffbe1a` | `frmMainPageV2.vb` | the only commit that changes behaviour |
| `fef4371` | `AutoTradeSettings{.Designer,}.vb`, `OrderCheck/Program.vb` | UI knob + 7 persistence fixtures |
| `e9f5bfc` | `docs/impl-report-ev-chase-budget.md` (new) + comment reword | docs |

`git diff -w cd567e1..HEAD -- DeribitOrderPlacementApp/frmMainPageV2.vb` is **52 added code lines
and 8 removed** (110 added lines total; 49 are comment). The 52 break down as: 2 fields, 2 pure
`Friend Shared` functions (9 lines), 2 read-only properties + 1 setter + 1 loader (19 lines), 1 call
in `Load`, **12 lines at the four gates** (3 each), and the two gate-side helpers (9 lines). The 8
removed are the four gates' old two-line `If`/`Await` pairs.

The single most useful check: **the four gates' replacement is mechanically uniform.** All four read

```vb
Dim abortReason As String = If(maxSlippageATRchecked, ChaseAbortReason(<own-side quote>, "<dir>"), Nothing)
If abortReason IsNot Nothing Then
    Await CancelWorkingEntryCoreAsync(abortReason)
```

with only the quote field and the direction string differing. Nothing else inside those blocks moved.

**Tripwire censuses, `cd567e1` vs HEAD, all identical:** `ResetCommandedSLPrices()` 8 ·
`cancelPending = False` 5 · `emergencyBaselineSettled` 10 · `RecordCommandedSLPrice` 3 ·
`SendReduceMarketOrderAsync` 5 · `ResetOrderAttempt()` 5 · `CancelWorkingEntryCoreAsync` 6.
`emergencyFired` census unchanged at **1 decl + 2 sets + 3 clears + 3 reads**.

**One census moves deliberately: `IsATRSlippageExcessive` 11 → 8.** That is 1 decl + 4 reposition
call sites + 6 pre-placement call sites, becoming 1 decl + **1** call site (inside `ChaseAbortReason`)
+ the same 6 pre-placement sites. It is a collapse, not a loss of coverage — but it does mean the
standing "four reposition gates call the ATR guard" grep no longer finds four hits, and any future
seat grepping that way needs `ChaseAbortReason` instead. **Worth folding into the spec/roadmap so
the next grep isn't stale** (the N1 precedent for doing this).

> **Repeat of a known failure mode, caught and fixed before this doc:** two comment drafts quoted the
> cancel-reason literals in prose, and a third named `ResetOrderAttempt` in prose — inflating that
> tripwire from 5 to 6. Reworded (`e9f5bfc` + the fix accompanying this doc). Prose near a tripwire
> token pollutes the standing grep; this is the second era in a row it has bitten.

## 2. FOUR DECISIONS NEEDING RATIFICATION

### D1 — which price the EV check is fed (**the one that matters**)

Spec §1 fixes the predicate's signature; spec §2 says the abort condition "becomes
`IsATRSlippageExcessive(...) OrElse (chase-EV check)`" but **never says what `currentPrice` the
chase-EV check receives.** I pass the **same own-side quote the ATR guard gets** (`bestBid` LONG /
`bestAsk` SHORT).

**Reasoning:** HANDOVER-4 §4 invariant 8 — all four gates measure the own-side quote (R2 ruling,
closing F18); one guard input per gate rather than two; and the diff stays a mechanical substitution.

**The counter-argument, stated fairly:** the ATR guard's own comment says its input is deliberately
"the raw own-side quote — it measures market **drift**, not our limit price." The EV question is a
different question: it is about *our fill price*, and the price we would actually rest at is
`chaseTarget` (`bestAsk − tick` for a LONG), not the bid. On that reading `chaseTarget` is the
economically exact input and I chose the merely-consistent one.

**Magnitude:** the difference is the spread plus one tick — order $1 — against a fee+floor threshold
of order $50 at a 5 bps knob. Under 2%. **Direction is the safe one:** for a LONG the bid sits
*further* below an above-market target than our resting limit would, so `remainingNet` is larger and
the floor binds **later**. Own-side can only ever chase longer than `chaseTarget` would, never abort
sooner.

**Ask:** ratify own-side, or direct `chaseTarget` (a four-line change, one per gate).

### D2 — a reason-returning helper instead of the spec's literal `OrElse`

Spec §2 writes the abort condition as an `OrElse` expression. A bare `OrElse` **cannot express
§2's own requirement**, because the caller then cannot tell which arm fired and §2 demands two
different cancel reasons. I introduced `ChaseAbortReason(ownSideQuote, direction) As String` —
returns the reason or `Nothing`.

This preserves everything §2 asks for: ATR short-circuits first (so it keeps owning the
`originalSignalPrice` seeding and its attempt-reset side effect), both arms sit under the one
`maxSlippageATRchecked` switch, and each reason literal now exists in exactly one place.

**Ask:** ratify the shape. If the coordinator prefers the spec's literal structure, the alternative
is a `ByRef` reason or a module-level "last abort reason" field — both worse.

### D3 — I widened what the GATE covers (acceptance 4 is now automated)

Spec §6 lists acceptance 4 (persist round-trip; fee keys hand-editable and read back) as an
acceptance item, implicitly alongside the runtime items. **I turned it into an OrderCheck fixture
instead of leaving it for the owner.** That means the gate now performs real filesystem I/O against
a *product* path — `AppUserSettings.SavePath`, which resolves beside the running exe and is
therefore **OrderCheck's own bin, never the app's**.

`verify-gate.ps1`'s header states the gate's charter as "build + logic fixtures ONLY — no UI layer
on the gate" (owner decision 3). Filesystem I/O is not a UI layer, and there is precedent — the
item-12 schema-migration fixture creates and deletes a real SQLite database — but that one uses
`Path.GetTempPath()`, whereas mine necessarily uses the product's own resolved path (Save/Load take
no path parameter). Safeguards: if a settings file already exists there its bytes are preserved in
memory and mirrored to a `.ordercheck-bak`, restored in a `Finally`, and a final fixture **asserts
the path was left as it was found** rather than trusting the write.

**Ask:** ratify, or direct me to delete those 7 fixtures and return acceptance 4 to the owner's
runtime list. I think automating it is the better trade (it is the acceptance item most likely to be
skipped by hand, and it caught nothing but would catch a key-name typo instantly) — but it is the
one place I expanded the gate's surface without being asked, so it should be a conscious decision.

### D4 — seed-before-commit numbering (trivial; ignore if the number is only a label)

Spec §4 calls this the **sixth** application of the seed-before-commit trap. N2's fifth is still
queued, so in the code today it is the **fifth**. The comment states both. **Ask:** none needed
unless the coordinator wants specs renumbered when the queue reorders.

## 3. NAMED RESIDUAL — two of the "four gates" will usually run ATR-only

Spec §2 wires all four reposition gates and §3 rules the target source; spec §6.3's acceptance
exercises **an entry chase only**. Gates 3 and 4 are the *trailing*-entry chase, and there is an
interaction §3 did not consider:

`manualTPval` has exactly one clear site — `frmMainPageV2.vb:3198`, under `If OpenPositions = True`,
i.e. once the entry has filled and a position exists. The trailing-entry chase runs in a
post-fill/post-close context. So by the time gates 3 and 4 evaluate, the target is typically 0, and
`ChaseEvTargetInForce` returns the §3 **SKIP** — those two gates keep the ATR cap alone.

**This is a derivation from the clear site's guard, not a runtime observation.** It is the claim in
this doc most worth a second pair of eyes; if it is wrong, the §6.3 acceptance is under-scoped rather
than over-scoped, which is the better direction but still worth knowing.

**Recommendation: accept + document.** It fails safe — those gates behave exactly as they do today,
never more aggressively. And the relay's primary case (bridge acts, which always carry the engine
target) is on the entry-chase gates, which are fully covered.

**Ask:** ratify accept, or rule that the trailing chase needs its own target source — in which case
that is a follow-up micro-spec, not a patch to this one, because the only honest candidate
(`TPTrailprice`) is live TP state I was not scoped to read.

## 4. FINDING — a live 2024 fee constant the relay and the spec both missed

This is arguably in the fee relay's own scope, so it belongs in the review and not only the impl
report.

`frmMainPageV2.vb:354` still holds `Private Const TakerFeeRate As Decimal = 0.0005D`, commented as
the **2024 schedule**. It is not decorative:

```
HandleIndexUpdates  ->  comms = TakerFeeRate x indexPrice  ->  commsVal + txtComms  (every index tick)
```

and `commsVal` feeds the derived TP and the item-E break-even trigger. Against the 2026-08-01 taker
of 3.5 bps it over-states the fee by ~43% — ≈ $32 vs ≈ $22 at a 64k index.

So the app has, right now, **two fee schedules in it**: the new one I added (`maker_fee_bps` /
`taker_fee_bps`, file-driven) and this hardcoded old one. The spec's Target list, Do-not-touch list
and §4 all pass over it, and the relay's §0 ("fee constants deliberately duplicated per-repo") reads
as if the app had none before this pass.

**I did not touch it.** Repointing it moves every derived TP by ≈ $10 at current prices — a
behaviour change outside this spec, and adjacent to exactly the M.SL/break-even arithmetic that
Rec 2 was deliberately deferred away from pre-N1-acceptance. It is a clean micro-spec: one constant,
or one read of the new `taker_fee_bps` key, plus whatever acceptance the owner wants on the comms
default.

**Ask:** rule whether this becomes a queued micro-spec, and whether the engine seat should be told
(their §0 duplication note is now known to be incomplete on our side).

## 5. Adversarial cases already checked — attack these, don't re-derive them

| Case | Result |
|---|---|
| Knob 0 (shipped default) | `IsChaseEvExhausted` returns False on its first guard. `ChaseAbortReason` returns the ATR reason on exactly the old True inputs, `Nothing` on exactly the old False ones. **Caveat, stated honestly:** the two *arguments* (`ChaseEvTargetInForce()`, `RoundTripFeePct`) are still evaluated — both pure, a field compare and a multiply, no allocation. |
| Negative knob | Same OFF guard (`minNetMovePct <= 0D`). Fixture-pinned. |
| Negative / garbage `maker_fee_bps` hand-edit | A negative fee only *increases* `remainingNet`, so the floor binds **less** — no unsafe direction exists. Non-numeric is caught by `Load`'s whole-file catch (Designer defaults + yellow note), as with every other key. |
| First reposition evaluation of a chase | ATR seeds `originalSignalPrice` and returns False on its first call, so the EV arm **is** live on the first evaluation — which is precisely what §6.3's acceptance demands. Not an accident. |
| EV abort teardown vs ATR abort teardown | Identical — same `CancelWorkingEntryCoreAsync`. So an EV-aborted bridge entry also clears `pendingSignalId`/`pendingSignalConfidence` (Q2) and cannot tag a later trade. |
| Disposition cardinality | The abort is post-`acted`. No writer touched; `SignalBridge.vb` not in the diff. One row per payload holds. |
| Threading | Both mirrors are plain `Decimal` fields; the gates read them on the receive thread with no control access. UI-thread writes / receive-thread reads = the accepted `Decimal` torn-read class (same as `StopLossTriggerOriginal`, `emergencyBaseline`). |
| `bestBid`/`bestAsk` `Nothing` at a gate | The `Decimal?`→`Decimal` conversion now happens at the `ChaseAbortReason` argument instead of the `IsATRSlippageExcessive` argument — **same position, same semantics**. Gate 3's enclosing condition still lacks a `bestBid` null check; pre-existing, unchanged, not reached in practice (quote messages carry both sides). |
| `Dim abortReason` four times in one method | Four sibling block scopes (LONG/SHORT arms of two disjoint `If` statements). Compiles clean; no shadowing. |
| Decimal exactness | `2D * 1.5D / 10000D = 0.0003D` exactly — fixture-pinned, and re-derived from the *reloaded* key in the persistence fixture. |
| Culture | Both directions invariant. `Decimal.TryParse("0.05")` under a comma-decimal culture returns **5** — a 100x error on a guard that abandons trades. Pinned in the commit path and the warning path so a box that commits cannot silently disagree with a box that warns. |
| Arm-switch coupling (**worth a conscious tick**) | The EV floor inherits the `maxSlippageATRchecked` switch per §2/housekeeping 8b. Consequence: **unticking Max Slippage ATR silently disables the EV floor too.** Harmless for bridge trades (that checkbox is already a bridge START precondition — `IsMaxSlippageGuardChecked`), but a manual trader who unticks it loses both guards. |
| 6 pre-placement gates | Untouched; still call `IsATRSlippageExcessive(BestPrice, direction)` inline. |

## 6. What I did NOT do

- **No runtime acceptance (§6.3).** Owner-run; recipe in impl report §5. Safety boundary held.
- **No x64 rebuild and no app launch.** The gate builds AnyCPU only. The owner's runtime bin is still
  pre-EV; rebuild before any runtime test and **re-check the window title for `— TESTNET`** (the
  rebuild clobbers the bin's `secrets.json` from project source).
- **No visual confirmation of the new Tooling row.** `grpTooling` grew 48px and the form's
  `ClientSize` with it (856 → 904); `lblAtrNow` moved y=226 → y=274. Arithmetic only — nobody has
  looked at it on a real display, and this window is `TopMost` and sticks to the main form.
- **Did not touch** `TakerFeeRate` (§4), the emergency block or `emergencyFired`, `IsATRSlippageExcessive`'s
  body, the disposition writers, the §4 gate chain, or the 6 pre-placement gates.
- **No push.** Owner is the only pusher.

## 7. Suggested review method

1. `git show 2ffbe1a` in full — it is the only commit that changes behaviour, and it is small.
2. **EXECUTE `tools/checks/verify-gate.ps1`** — expect GATE PASSED, 124/124.
3. `git diff -w cd567e1..HEAD -- DeribitOrderPlacementApp/frmMainPageV2.vb` — confirm §1's shape:
   the four gates are one uniform substitution and nothing else in those blocks moved.
4. Re-grep independently: the seven tripwires of §1 at their pre-EV counts, `emergencyFired`
   1+2+3+3, `IsATRSlippageExcessive` 11 → 8 with the delta fully explained by the collapse, and the
   two reason literals appearing on exactly two lines, both code.
5. Adversarial pass on **§3** (the `manualTPval` clear-site derivation) and on **D1**'s direction
   argument — those are the two claims that would change conclusions if wrong.
6. Rule on D1, D2, D3, and §3's residual; decide §4's disposition.

**Open asks: five (D1, D2, D3, §3 residual, §4 finding).** None blocks the gate. **None blocks the
owner's §6.3 runtime pass either** — the feature ships OFF, so acceptance 3 can run before or after
these are ruled. D1 is the only one that would change code if ruled against.
