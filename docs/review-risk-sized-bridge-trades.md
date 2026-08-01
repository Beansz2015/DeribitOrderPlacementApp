# Coordinator review — N2 risk-sized bridge trades

**Reviewed:** `6850c7a` (seam + fixtures + SIZE-button repoint) · `a1262f0` (act-site integration) ·
`7bc8daa` (SIGNAL BRIDGE checkbox + persistence) · `c40e039` (impl report), against
`spec-risk-sized-bridge-trades.md` as amended 2026-08-01.
**Reviewer gate at HEAD: GATE PASSED, OrderCheck 173/173.** Censuses re-run independently.
Runtime legs driven by the reviewer on the harness (TESTNET, trades #68/#69 in the harness DB;
owner drove Mode/ARM/START).

## VERDICT: **code APPROVED — but the FEATURE does not work end-to-end. Acceptance 3b FAILED, on a
defect OUTSIDE N2's scope that N2 is the first thing to expose.**

The risk size is computed correctly, logged correctly, and **placed** correctly. It is then
**silently reverted to the Amount box by the entry chase**, so the position that actually results is
the old size. N2 must not be enabled until that is fixed.

## 1. What PASSED

| Acceptance | Result |
|---|---|
| 1 — gate per commit, fixtures counted | **PASS** — 153 → 173, re-executed at HEAD by the reviewer |
| 3 — log-only sizing | **PASS** — `would-act … size 310`, matching `floor(1 × 63000 / 200 / 10) × 10` with the Amount box at 10 |
| 3 (composed) — sessionFactor exactly once | **PASS** — with a `0.5` policy line the same payload gives **150**, i.e. 310 halved-then-floored **once**. Applied twice it would read 70. |
| 4 — persistence round-trip | **PASS, both directions**, through the FORM (not just the file layer the report tested): tick ON → file `true` on close → reads back `On` after restart; and the reverse |
| 5 — censuses / greps | **PASS** — `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `TakerFeeRate` 0 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `txtAmount.Text =` 3 · `refused: size` unchanged · **`EffectiveSizeUsd` exactly ONE call site in the act path** |
| §2 — screenshot | **PASS** — `Risk-size` on the Tiers row in SIGNAL BRIDGE, caption legible, no wrap, no clip, clear of the group edge |

Code verified at the sites, not from the report: the seam body is the button's two arithmetic lines
verbatim including expression association; the button repoint is exactly two lines → one call with
its guards and all three refusal messages untouched; the act site keeps `rawSize = SizeUsd` and
compares the override against it; `sizeMult` never touches the risk base. The §3 fail-safe is
handled by the seam's own guard line — `p.Entry = 0` ⇒ `refPrice <= 0` ⇒ `-1` — so all three
conditions collapse into one check. Cleaner than the spec asked for.

## 2. Acceptance 3b — FAILED. The chase resizes the order back to the Amount box.

Live act, flag ON, session policy OFF (unity — the default), Amount box **10**, computed size
**310**:

```
Buy limit order placed For 310 at 63074.                 <- correct: the override WAS honoured
[BRIDGE] signal #8100 ... -> acted (id 110990389746)
Order repositioned: $63074.00 → $63076.00                <- the chase edits the resting order
Position entered: LONG 10 @ $63075.96
Reduce-only MARKET sell 10  order sent.                  <- the exchange's own answer: 10
```

**Placed 310, held 10.** The reduce is exchange-derived (`positionSizeUSD`, the position model), so
it is the authority — and it says the position was the Amount box size all along after the first
reposition.

**Cause, confirmed at both sites:** the entry-chase edit re-sends the amount from the Amount-box
mirror.

```vb
' UpdateEntryOrderOnlyAsync  :4275
' UpdateLimitOrderWithOTOCOAsync  :4193
Dim amount As Decimal = orderAmountVal
```

Nothing retains the placed size — `sizeUsdOverride` is consumed inside `ExecuteOrderAsync`
(`If sizeUsdOverride > 0D Then amount = sizeUsdOverride`) and never stored — so the chase has
nothing to re-send but the box. The first reposition therefore rewrites the order to the box size,
and the app enters at top-of-book, so **almost every bridge entry is chased at least once**.

**A second, smaller reporting defect on the same evidence:** `Position entered: LONG 10 …`
(`:3245`) prints `orderAmountVal`, the Amount-box mirror, not the filled size. Even once the chase
is fixed, that line will lie whenever the placed size differs from the box. It is the line a future
acceptance would read.

## 3. Attribution — this is NOT an N2 code defect, and that matters for the fix

N2 did not touch either chase path, and the spec did not mention them. The defect is **pre-existing
and shipped with the session-policy `size_mult` feature**: any bridge entry placed at a reduced size
has been silently restored to the full Amount box by the first reposition ever since.

It was never caught because it was **structurally invisible until now**. Everything to date ran at
live-at-min-size, where the Amount box is 10 and `EffectiveSizeUsd` clamps every reduction back up
to the contract minimum of 10 — so the override was `0` or the sizes were equal, and there was
nothing to revert. N2 is the first feature that makes the placed size differ from the box by a wide
margin, which is why it surfaced here.

So the session-policy `size_mult` is also, today, **not in force on any chased bridge entry.**

## 4. What I am NOT claiming

- The partial-fill hypothesis is not excluded by direct observation — I did not query the exchange
  order book for a residual 300. But it is excluded by the code: the chase edit sends
  `amount = orderAmountVal` on the order id it is repositioning, which *is* a resize to 10, and the
  reduce found exactly 10 with no leftover working order after `Cancel All`.
- I did not re-run 3b after the fix, because there is no fix yet.

## 5. Disposition

- **N2's three code commits: APPROVED and KEEP.** They are correct, well-argued, and the
  spec-back that preceded them caught the defect that would have made this far worse. Nothing here
  needs reverting.
- **N2 must ship DISABLED and stay disabled** until the chase preserves the placed size. It already
  ships disabled, so no action — but the checkbox must not be ticked in the owner's bin.
- **`spec-chase-preserve-placed-size.md` (N2b) is the blocker** — written alongside this review.
  Its acceptance is a re-run of 3b: place 310 with the box at 10, let it chase, and the reduce must
  report **310**.
- Minor, for N2b or its own pass: the `Position entered:` line should print the size actually
  filled, not the box mirror.

## 6. Two notes on the implementer's asks

- **`TabIndex = 7`** (reaching the new checkbox last in the group) — **leave it.** Renumbering two
  unrelated controls to gain a marginally better tab order is churn on a mouse-driven form.
- **The parity-sweep probe left out of the repo** — **agreed, correct call.** It compared the seam
  against the button's *pre-repoint* inline arithmetic, so it was a one-time migration check; once
  the button calls the seam there is nothing left to compare against, and keeping it would mean a
  second copy of the formula living in the harness forever. That is the thing this item exists to
  prevent. The 3024-combination result is recorded here as evidence and does not need to be re-runnable.
