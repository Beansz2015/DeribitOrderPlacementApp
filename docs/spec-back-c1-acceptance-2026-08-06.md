# Spec-back — C1 acceptances: five findings the specs and the runtime protocol do not cover

**For:** the owner + orchestrator. **From:** the C1 implementer seat (Opus 5, HIGH), 2026-08-06.
**Status:** raised AFTER the acceptances rather than before implementing — none of these blocked the
work, and none is a code defect. They are things the **docs** now get wrong, plus one harness defect
and one mistake of mine.

**Context:** all five acceptances 3–7 ran and passed
(`runtime-record-c1-feedback-emitter-2026-08-06.md`). Code is APPROVED (`review-c1-feedback-emitter.md`),
the review's D1 fixed, gate `GATE PASSED` / OrderCheck 268/268, censuses unchanged throughout. **Nothing here
changes the emitted schema, any disposition token, or any ruling.**

---

> **⚠ RENUMBERED 2026-08-06 by the coordinator: these five findings are SB1–SB5, not D1–D5.**
> `D1` was already taken **twice** in live docs — the C1 review's write-ordering defect
> (`review-c1-feedback-emitter.md` §4, fixed in `cef0b9c`) and N2b's spec-back trap (H-6 §7.4) — so
> a grep for "C1 D1" returned two different things in the same feature. **`D` = a defect found in
> code; `SB` = a spec-back finding against the docs.** References to `D1`'s race elsewhere in this
> file and in the runtime record still mean the REVIEW's D1 and are left alone.

## SB1 — 🚨 Acceptance 3 does NOT need ARM/START, and both the spec and my run sheet implied it did

**The spec says** (§Acceptance 3): *"enable the path, drive one bridge act on the harness, then
inspect the file by hand."* **"Bridge act" reads as the Live placement path**, and I wrote
impl report §7.1 accordingly — engine stop, ARM, START, the §5.10 `exec_resolution_min` patch, the
§5.1 write→START→write ordering. H-6 §2.7 and its queue line then carried *"needs ARM/START
(owner-only)"* from me for two days.

**That is wrong, and the code says so in as many words:**

- `TryStart` refuses any non-Live mode with *"mode is not Live (**log-only runs un-started by
  design**)"*.
- Gate 4.5's dual-arm interlock is guarded `If disposition Is Nothing AndAlso _mode = BridgeMode.Live`
  — in Log-only it is **skipped entirely**.
- **E5 already ruled** that `LOG_ONLY` emits and that *"the emitter never gates on mode"*.

So a payload flows the whole gate chain in Log-only and yields a `would-act` disposition — trigger
(a), `last_signal` populated — with **neither toggle touched and no order placed**. Verified this
run: `armed`/`started` were `false` in every reading.

**And the log-only route is not merely adequate, it is the one that carries the evidence.** It is
what closed **E2 at runtime** — `executor.mode` observed as `"LOG_ONLY"`, the pinned string, before
any payload. E2's whole character is that it fails *invisibly* (T8 tolerance renders an unrecognised
value verbatim and takes the conservative arm), so that observation was the single highest-value
thing left in the feature — and the over-strict reading of the acceptance is what had been deferring
it behind an owner-only gate.

**Recommendation.** Amend spec §Acceptance 3 to name the **log-only route as the default** —
*"drive one consumed payload; Log-only suffices and needs no ARM/START"* — and demote the Live
placement to an explicitly optional extension, since it is still the only way to observe
`mode: "LIVE"` and an `acted (id …)` disposition. The impl report §7.1 run sheet and H-6 are already
corrected; **the spec's own wording is not**, and that is the copy a future seat greps.

**My error, named:** I wrote a run sheet from the acceptance's wording without checking whether the
code required what the wording implied. That is H-6 §7b lesson 1 in mirror image — I verified the
*instrument* existed and never questioned the *precondition*.

---

## SB2 — the live-payload clobber is AVOIDABLE, and H-6 §5.2 presents it as unavoidable

**H-6 §5.2 frames it as an accepted footgun:** with the engine stopped, `write-payload.ps1` defaults
to the LIVE payload path and clobbers `C:\Dev\DeribitBridge\verdict_signal.json`; the first-write
backup plus `restore-payload.ps1` is *"the only safety net: restore is mandatory, not cleanup."*

**It is not the only net.** `write-payload.ps1` resolves the payload path **from the running bin's
`bridge.json` `path` key** — so pointing that key at a scratch file means the live payload is never
opened at all:

```json
"path": "C:\\Dev\\DeribitBridge\\harness-signal.json"
```

**Verified this run.** `verdict_signal.json` was byte- and timestamp-identical before and after —
**1562 bytes, 11:03:07 PM** — and `restore-payload.ps1` correctly reported *"nothing to restore"*
because nothing had been backed up. There was nothing to forget to restore.

**This touches no ruling.** The engine-stopped refusal still applied and was honoured: it gates on
the **process**, not the path, per the 2026-08-01 ruling that rejected a path-aware variant. Stopping
and restarting the engine remains mandatory and remains the half that bites.

**Recommendation.** H-6 §5.2 gains the scratch-path variant as the **preferred** form, with the
backup/restore net kept as the fallback for anyone running against the default path. It removes the
larger of the two footguns that section names.

---

## SB3 — 🔧 harness defect: `Test-ElementMatch` is substring-only, and `txtTrigger` ⊂ `txtTriggerOffset`

**`harness-common.ps1:109`** matches on `Name` or `AutomationId` by
`IndexOf(Pattern, OrdinalIgnoreCase) >= 0` — pure substring, first match in enumeration order wins.
Every drive script uses it (`set-textbox`, `click-button`, `toggle-checkbox`, `select-combo-item`).

**It bit during acceptance 4.** I asked for `txtTrigger` (**Trig. P.**, the stop trigger distance)
and got **`txtTriggerOffset`** (**Trig.O.**), which is enumerated first. The script reported success:

```
Set '' (id 'txtTriggerOffset', window '…— TESTNET') = '300'
Committed via blur (focus -> '' (id 'txtTopAsk'); verified off the target)
```

**Consequence, stated honestly:** the stop trigger stayed at **5** instead of the 300 I intended, so
the SL sat ~6 below market for the whole acceptance-4 position. **The run was unaffected only because
price moved up** (64806.5 → 64825.62). Had it ticked down six dollars, the stop would have triggered
and the position would have closed itself mid-test — and the flat-trap reading would have been taken
against a close I had not driven, which reads as a mystery rather than as this.

**This is the harness commit-verification class:** a script that reports work it did not do is worse
than one that errors. It reported the work it *did* do, on the wrong control.

**Scope, measured rather than asserted.** I enumerated the 28 `Edit` ids on both forms; the `txt`
prefix protects most near-collisions (`txtStopLoss` is *not* a substring of `txtMarketStopLoss` or
`txtPlacedStopLossPrice`). **`txtTrigger` ⊂ `txtTriggerOffset` is the only collision among the text
boxes** — and it is the one that bit on first use. Buttons and combos are clean today, but nothing
prevents the next one.

**Recommendation.** In `Test-ElementMatch`, try an **exact** `AutomationId` match across all
candidates first, and fall back to substring only when no exact match exists. Behaviour-preserving
for every existing call site, and it makes the ambiguous case impossible rather than unlikely.
Alternatively — cheaper, weaker — make a script that matched more than one candidate say so.
**Sonnet, medium: it is `tools/` only and touches no trading path.**

---

## SB4 — the harness bin's GATE CONFIG diverges from the owner's x64 bin, and nothing says so

**H-6 §4 and §5.11 name the divergence as "separate settings file, DB and journal"** and call the
AnyCPU bin's own `bridge-dispositions.log` *"accidental but useful isolation"*. What neither says is
that **the gate configuration diverges too**, which is what decides whether a bridge payload is acted
on at all.

Read off the harness form this run versus H-6 §2.3's record of the owner's x64 config:

| | harness Debug bin | owner x64 bin (H-6 §2.3) |
|---|---|---|
| Session policy | **`chkSessionPolicyOn = Off`** | **ENABLED** |
| Rules | `NY = HIGH,MEDIUM \| any \| 0.5` | `LONDON = MEDIUM \| CONFIRMED \| 0.5`, `ASIA = HIGH,MEDIUM \| any \| 0.75`, NY absent → `DefaultRule` |
| Risk-size | `chkRiskSizeBridge = Off` | Off (pending §2.1–2.3) |

**The consequence is a silently different disposition.** This run produced
`would-act: … size 10` with **no clamp line**, because a disabled policy makes `PolicySizeMultFor`
return unity and `EffectiveSizeUsd` the identity — the documented *"disabled ⇒ structurally silent"*
invariant working correctly. **The same payload in the owner's x64 bin, with the default
`-Confidence HIGH`, between local 16:00–20:59 (UTC 08:00–12:59, the LONDON bucket) would have been
`refused: policy(LONDON/tier)`** — no `would-act` at all, and an acceptance that reads as a failure.

**Recommendation.** One line in H-6 §4 (or §5.11, wherever the bin divergence lives): **the harness
bin's gate config — session policy, tiers, inclusion window, breaker — is separate and is NOT the
owner's. Read it off the form before interpreting any bridge disposition from a harness run.** Two
practical riders worth carrying with it: `-Confidence MEDIUM` is the only value that passes all three
buckets under the owner's config, and the policy's *enabled* state is a checkbox, not the text box —
reading `txtSessionPolicy` alone tells you nothing about whether it is in force.

---

## SB5 — housekeeping: I deleted a file that was not mine

Teardown after the first session ran `Remove-Item verify\out\*.png`, which took
**`n2-settings.png` (204 KB)** along with this run's own two screenshots. It pre-dated this seat and
belonged to the closed N2 work. `verify/` is git-ignored, so it is **not recoverable**.

The standing *"delete after use"* rule covers screenshots a harness run **creates**. A wildcard over
a shared scratch directory is not that rule — it is a different and worse one. Recorded in the
runtime record §12 as well, rather than left to be discovered.

**Recommendation.** If anything, a one-line convention: harness cleanup deletes **named** artefacts
it created, never a glob over `verify/out`. Not worth a script change; worth not repeating.

---

## Still binding, and untouched by any of the above

**E4's revisit trigger.** Nothing in the implementation or the acceptances went near it. It re-opens
the moment ANY consumer **records** the `avg_entry` join rather than re-deriving it — a CSV column, a
card binding, a stored achieved-entry, or the T7 / v2.1 per-signal field. The mitigation then is an
immutable snapshot published by reference, not a lock. Phase-1 engine consumption still records
nothing, which is what makes the accepted class safe today.

## Worth carrying — the reviewer's rule about negative tests

Raised at review and it generalises: **a negative test reverted before commit can only be taken on
trust.** I proved fixture 8b could fail by dropping `executor.ws` from the expected set, then
reverted it, which left the claim unverifiable downstream — the coordinator re-derived it rather than
believe it, which was correct. The fix adopted: impl report §8.3 now carries the **recipe** so the
next reader redoes it in a minute. Either leave a negative test behind a flag, or write down how to
reproduce it.

## What this spec-back does NOT claim

- **No code defect is alleged.** SB3 is the only defect and it is in `tools/`, not the app.
- **`mode: "LIVE"` and an `acted (id …)` disposition remain unobserved** — only the Live arm reaches
  the placement path. So do the `ws` DOWN edge, a `breaker_tripped` flip, a SHORT `size_usd`, and
  D1's race (which needs two threads and a pre-emption).
- The acceptance-3 payload was **hand-crafted by the harness**. It exercises the consumer and the
  emitter; it does not re-prove the engine's own emitter output.
