# Coordinator review — session policy gate + two side repairs (`0afe06d..b8d4308`)

**Date:** 2026-07-22 · **Reviewer:** coordinator seat (Fable — not the Opus implementer seat) ·
**Verdict: ALL THREE SURFACES APPROVED.** All nine spec acceptances were already green (owner ran
2/3/8; the implementer drove 4/5/6/7 on testnet); nothing runtime remains.

**Method:** every code diff read in full (`0afe06d`, `893d3a0`, `d32674e`, `8a31fd6`, `8be8ea5`,
`eaa801b`); `SessionPolicy.vb` read end-to-end at HEAD; all §7 greps re-run by the reviewer;
**`tools/checks/verify-gate.ps1` EXECUTED at HEAD `b8d4308`** → Release 0/0, Debug 0/0, OrderCheck
**96/96**, repo guards OK, **GATE PASSED**.

## Surface A — the specced session-policy work: APPROVED

- **4.4b placement verified in the diff:** after the entire contract-§4.4 `ElseIf` chain, before
  §4.5 — every contract refusal keeps its token; only engine-actionable signals can be
  policy-refused. `PolicyRefusalFor` returns `Nothing` whenever disabled, making disabled-parity
  structural. Tier-before-context matches first-failing-gate convention (fixture-pinned).
- **The reviewer's prime adversarial target — DateTimeKind — is closed:** `ParsePayload` uses
  `AssumeUniversal Or AdjustToUniversal` (SignalBridge.vb:833), so `GeneratedUtc.Kind = Utc`
  always; `SessionBucketForPayload`'s conversion is genuinely defensive-only, and the fixture
  `23:30Z = NY (a local misread would say ASIA)` pins it through the real parse path on this
  UTC+8 machine. Exactly the right fixture.
- **size_mult:** `EffectiveSizeUsd` implements the corrected §4 (unity passthrough; floor+clamp on
  real reductions only); applied once at the act/would-act site; `refused: size` and all other
  gates read raw `SizeUsd`; the override is passed only when `effective <> rawSize`, so the common
  path reaches `PlaceAutomatedOrder` byte-identically. `ExecuteOrderAsync` validates the Amount box
  BEFORE applying the override; `txtAmount` is never written.
- **Config model (`SessionPolicy.vb`):** genuinely immutable (backing fields, `AsReadOnly`,
  `WithEnabled` returns new); parse rejects duplicates/empty-tier-sets/ambiguous-empty-contexts/
  out-of-range mult, invariant culture throughout; fail-closed `AllowsContext`; `FromJson` tolerant
  per spec. Reviewer note: an `N/A`-confidence payload can never reach 4.4b (refused at
  `direction` or the global tier gate first), so `CanonicalTier`'s passthrough of unknown tokens is
  safe.
- **UI (`d32674e` + `8a31fd6`):** seed-before-commit handled identically to the risk-sizing
  pattern, same position, commented both sides; the Load ordering was re-verified at HEAD in the
  risk-sizing review and is unchanged. D5 relocation is Designer-complete — `GroupBox1` survives
  only in explanatory comments (the build proves it: no declaration remains), the window tooltip
  migrated to both time boxes, `&&` escape verified on-screen.
- **Greps re-run, all match the report:** `PlaceAutomatedOrder(` = definition + the single bridge
  caller; `ExecuteOrderAsync(` = definition + pass-through + 6 unchanged manual sites;
  `txtAmount.Text =` = 4 pre-existing writers; `ResetCommandedSLPrices()` = definition + 7 call
  sites, identical to the raced-abort review's count — no new SL-context sites anywhere in the
  stack. The report's method note (naive `emergencyBaseline = 0` counting ≠ the paired-site
  invariant) is correct and worth the ink.

### Deviations — all ACCEPTED

1. `enabled` lives inside `SessionPolicyConfig` (one snapshot object, not parallel fields) — better
   than the spec's sketch; it is what every read site wants.
2. `ToJson` writes only configured sessions — required for the box/file/box round-trip to be honest.
3. Geometry fine-tuning (§5 permitted), including the `8a31fd6` WordWrap finding — **that one was a
   real defect the spec's own geometry table would have shipped**: a wrapped line silently hid the
   ASIA session. WordWrap=False is load-bearing and correctly commented as such. The method lesson
   (inspect-tree measures bounds, not text fit; screenshots are part of geometry sign-off) is
   recorded in the report and should be treated as standing.
4. §5.2 individual wiring dropping `SelectAllOnEnter` as well as `CommitOnEnterKey` — correct; the
   click-bound select-all would make a multiline box uneditable line-by-line.
5. `FromJson` clamps an out-of-range `size_mult` to 1.0 where the text parser rejects the line —
   asymmetric but conservative in the right direction (never increases size, never disables
   neighbours); documented.

### Reviewer-observed edge, accepted (for the record)

`PolicySizeMultFor` re-reads the snapshot at the act site, so a config commit landing in the
microseconds between the 4.4b check and the size computation could apply the newer config's mult to
a signal admitted under the older one. Both are owner-committed configs an instant apart, no
invariant is touched, and the trade was admitted by a valid policy — benign, not worth a
lock/plumb-through. Noted so a future reader doesn't rediscover it as a bug.

## Surface B — `8be8ea5` gate-config warning repair: APPROVED

Owner-authorised out-of-scope; judged on evidence per the report's index. The fault was real and
runtime-proven in both directions (never-cleared + clobbered-blind). The repair is the right shape:
`ApplyBridgeStatusLine` as the label's single writer, warning outranks status, `Nothing` clears.
All callers are UI-thread (commit handlers + the already-marshalled `RefreshBridgePanel` path);
no gate semantics touched — display only. The three-step runtime proof (§6b) covers exactly the
direction that was broken.

## Surface C — `eaa801b` harness `-CommitViaBlur` repair: APPROVED

Tools-only. Root cause correctly identified (blur-to-window hands focus straight back to the
last-active child; nothing verified the outcome), and the fix follows the harness's own safety
philosophy: focus-first-or-write-nothing, blur onto an Edit-only sink (never a deny-listed button),
verify via RuntimeIds and refuse loudly. The regression case re-proves the exact acceptance that
was previously polluted. **Standing lesson, endorsed: the harness reported success it had not
achieved — always verify what is IN FORCE, and suspect the harness before the product.**

## Backlog items surfaced (report §7, endorsed for housekeeping — none blocking)

- `orderapp-settings.json` non-atomic write (`File.WriteAllText`) — apply the F-2 temp+move
  discipline; the policy block now rides this path, raising the cost of a mid-save crash.
- `lblBridgeStatus` truncates long warnings on screen (full text intact in UIA).
- `AutoTradeSettings` TabIndex gap after `GroupBox1` — harmless.

## Process notes

- The pre-commit anchor pass catching the §4 unity-passthrough defect *before any code was written*
  is the spec-defect escalation rule working exactly as intended.
- The §6b isolated-payload protocol (harness bin gets its own `bridge.json` on a scratch path; the
  engine and the real payload are never touched) is **superior to the engine-stopped protocol** for
  consumer-only tests and should be the default going forward — the soak ran uninterrupted through
  the entire acceptance pass.

## State

The policy ships DISABLED; the soak's disposition stream is untouched (acceptance 2, owner-run,
against the live engine). Enablement is the owner's action at the live-ladder step, post-soak
review, starting from the engine doc's P5 conservative policy. Remaining for this stack: the
owner's push.
