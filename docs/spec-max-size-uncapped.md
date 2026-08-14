# Spec — `Max Size = 0` must mean NO CAP from the form, and an uncapped state must ANNOUNCE ITSELF

**Origin:** owner request, 2026-08-14, during N2 configuration. *"Possible to make it uncapped via
the form instead of hand-editing it? … No cap being visible is a good addition."*
**Status at writing:** open. Every fact below verified in code on 2026-08-14 at HEAD `af86a3e`.
**Scope:** two files — `DeribitOrderPlacementApp/AutoTradeSettings.vb` and
`DeribitOrderPlacementApp/frmMainPageV2.vb`. **No `tools/` change. No contract change.**
**Ships:** ON. It changes what a form entry means; it changes no default. An existing
`max_size_usd = 2000` keeps behaving exactly as today.

**One-line statement of the job:** the sizing engine already treats `maxSizeUsd <= 0` as **no cap**,
but the form cannot express it — a typed `0` is silently discarded and the previous cap survives.
Make an explicitly typed `0` commit, and make the resulting uncapped state visible instead of silent.

---

## 🚫 Do-not-touch

| Thing | Where | Why |
|---|---|---|
| `RiskSizedBase` | `SignalBridge.vb:1123-1129` | **It is already correct.** `If maxSizeUsd > 0D AndAlso …` — it has always understood `<= 0` as uncapped. This spec changes only what can *reach* it. **Do not touch the arithmetic.** |
| The **Risk / Trade** box | everywhere | Deliberately excluded — see §2.3. |
| The circuit-breaker commit path | `AutoTradeSettings.vb:284-289` | It is the **model** you are copying. Read it; do not modify it. |
| `EffectiveSizeUsd`, the min-10 clamp, the session multiplier | `SignalBridge.vb` | Downstream of the cap and unaffected. |

**Standing rules** (`HANDOVER-6.md` §7): a seat never places a trade and never arms the bridge ·
**escalate a spec defect to the owner BEFORE implementing** · `E`/`D`/`SB` convention (§7bb) · the
owner is the only pusher.

---

## §1 — The gap, precisely

### 1.1 The engine already supports it

`SignalBridge.vb:1109-1110`, in `RiskSizedBase`'s own comment:

> `maxSizeUsd <= 0` means **NO CAP** (the legitimate way to spell "uncapped" in the hand-edited
> settings file).

So uncapped is a supported, documented state. **It is simply unreachable from the UI.**

### 1.2 Why a typed `0` disappears

Two guards, in series:

1. `AutoTradeSettings.vb:384-386` — `If Not Decimal.TryParse(txtMaxSize.Text, maxSize) Then maxSize = 0D`.
   **A parse failure and an explicit `0` become the same value.**
2. `frmMainPageV2.vb:96-99` — `If maxSize > 0D Then userSettings.MaxSizeUsd = maxSize`, under the
   comment *"non-positive (= blank/garbage box) keeps the last good value."*

The convention is deliberate and sound **as long as `0` cannot be typed on purpose**. The owner now
wants to type it on purpose.

### 1.3 🔑 The distinction already exists — the risk keys throw it away

`Decimal.TryParse("")` **fails**. `Decimal.TryParse("0")` **succeeds with `0`**. Blank and
explicit-zero are already perfectly distinguishable. Step 1 above destroys that information before
step 2 can use it. **You are not adding a capability; you are stopping the code from discarding one.**

### 1.4 The house already does this, twice

| Setting | Commit rule | Warns when |
|---|---|---|
| **Circuit breaker** (`AutoTradeSettings.vb:284-289`) | pushes **any** parsed value — *"`<= 0` is a deliberate, persistable disable; only a parse failure keeps last good"* | parse failure only (`:412`) |
| **EV chase budget** (`:418`) | *"a PARSE failure only — 0 and negatives are valid ways to spell OFF"* | parse failure only |
| **Max Size** (today) | parse failure **and** `0` both keep last good | parse failure **or** `<= 0` (`:417`) |

**Max Size is the odd one out. This spec makes it match the breaker.** The breaker's behaviour is
ruled — `spec-breaker-persist-atr7-item8.md` R1.

### 1.5 🚨 The invariant that makes this three edits, not one

`AutoTradeSettings.vb:418` states it outright:

> *"a box that commits must not warn, and a box that warns must not commit"*

Change the commit without the warning and you get a box that stores a value while its own validation
calls that value a problem. **That is the way to get this wrong.**

---

## §2 — The fix

### 2.1 Three coordinated edits

1. **`AutoTradeSettings.vb:385`** — stop collapsing a parse failure to `0` for Max Size. Follow the
   breaker's shape at `:284-289`: commit only on a successful parse, and commit **whatever** parsed.
2. **`frmMainPageV2.vb:99`** — accept any parsed value for `MaxSizeUsd`. Update the `:95` comment,
   which will no longer describe what the line does. ⚠ **Leave line `:98` (risk) exactly as it is**,
   and make the comment say why the two now differ.
3. **`AutoTradeSettings.vb:417`** — Max Size must no longer be flagged for `<= 0`; a parse failure
   alone is the problem. Keep `:416` (risk) unchanged.

### 2.2 The visibility requirement — an uncapped state must announce itself

**Today an uncapped cap is the quietest thing in the app.** The existing lines only fire when
something binds — `SignalBridge.vb:1069` (clamped up to 10) and `:1072` (capped by max_size_usd).
With no cap, neither fires. **Silence.** Removing a size ceiling must not be silent.

Required, and deliberately bounded so it cannot flood:

- **(a) One line at commit**, when the setting changes to uncapped — the owner sees it the moment
  they do it.
- **(b) One line at startup**, so a restart cannot hide it. **Model it on the existing family**, which
  is exactly one line, present either way:
  `frmMainPageV2.vb:959-960` (`Remote notifier: configured` / `disabled (no ntfy_url)`) and `:966`
  (`ExecutorFeedback.StartupLine()`). Put it beside `:984`'s *"Trade defaults restored…"*, which is
  the settings-derived neighbour.
- Suggested wording — state the value either way, so the line is informative when capped too:
  `Bridge max size: NO CAP (max_size_usd = 0)` · `Bridge max size: 2000`
- 🚫 **Do NOT log per-signal.** A line on every sizing decision floods the host log and buries the
  disposition stream. **(a) + (b) is the whole requirement.**

### 2.3 Why Risk / Trade is deliberately NOT changed

They look symmetrical. They are not:

- `max_size_usd <= 0` ⇒ **no cap.** A coherent, useful state.
- `risk_per_trade_usd <= 0` ⇒ `RiskSizedBase` returns **`-1`** ⇒ `RiskSizedOrFallback` falls back to
  the **raw Amount box** (`SignalBridge.vb:1092-1095`). So a persistable "risk 0" would **silently
  disable risk sizing while the `Risk-size` checkbox still reads ON** — the box and the behaviour
  would disagree, with nothing to tell the owner.

**The existing guard is protecting the owner there. Keep it.** If an implementer thinks the two
should be symmetrical, that is a spec defect — **escalate, do not unify them.**

### 2.4 What must NOT change

- **Defaults.** No default becomes 0. `frmMainPageV2.vb:76`'s `500D` fallback stays.
- **An existing configured cap.** `max_size_usd = 2000` must behave character-for-character as today.
- **Seeding.** `SeedRiskSizingFromHost` (`AutoTradeSettings.vb:368-369`) must round-trip `0` back into
  the box as `0`, not as blank — otherwise reopening the form silently re-caps.

---

## §Acceptance

**Test the defect, not the theory** (`HANDOVER-6.md` §7 lesson 2), and **prove every conjunct**
(lesson 4).

**1 — A typed `0` commits and survives a restart.** Open Auto Settings, type `0` in Max Size, commit,
close the app cleanly, and assert `"max_size_usd": 0` in
`bin/x64/Debug/net9.0-windows8.0/orderapp-settings.json`. Relaunch and assert the box reads `0`
(§2.4's round-trip).
⚠ `HANDOVER-6.md` §5.4 first — `FormClosing` persists all 11 standing fields. Restore anything else
you touch and read it back.

**2 — A BLANK box still keeps the last good value.** This is the behaviour the old guard existed for
and it must survive. Set Max Size to `2000`, commit, then **clear the box**, commit, and assert the
stored value is **still 2000**. **If this regresses, the change is wrong** — blank and `0` must now
mean different things.

**3 — Garbage still keeps the last good value.** Type `abc`. Same assertion as 2.

**4 — Commit and warning agree (§1.5's invariant).** With `0` in the box, assert the validation does
**not** flag Max Size. With `abc`, assert it **does**. Then the same two checks for Risk / Trade,
which must still flag both `0` and `abc`.

**5 — Uncapped actually removes the cap, end to end.** With `max_size_usd = 0` and
`risk_per_trade_usd = 1.0`, run **log-only** and capture a `would-act`. Recompute by hand:
`floor(entry ÷ dist ÷ 10) × 10` with **no cap applied**, and assert the logged size matches.
- ⚠ Against the payloads captured so far (`ROADMAP-2026-08.md` §1.4) the 2000 cap never bound, so
  **an uncapped run may look identical.** To prove the cap is gone you need a payload whose base
  exceeds 2000 — i.e. a stop distance under about **$32**. **If you cannot capture one, say so and
  claim only what you observed.** Do not report acceptance 5 as proven on a payload that would have
  passed either way.

**6 — Both new lines appear, and only those.** Assert the commit line fires when the value changes to
uncapped, and the startup line appears on relaunch and states the value in **both** states (capped
and uncapped). Then run log-only for **five minutes** and assert **no per-signal cap line** was
added.

**7 — Nothing else regressed.** `tools/checks/verify-gate.ps1` → `GATE PASSED`, OrderCheck
**268/268**. The nine `frmMainPageV2.vb` censuses → **68 occurrences across 64 lines**. ⚠ They are
**occurrence** counts; a count-mode grep returns 64 and reads as four missing. **You are editing
`frmMainPageV2.vb`, so they can genuinely move** — if they do, say which and why.

**8 — Consider a fixture.** `RiskSizedBase` is already fixture-pinned. If a fixture for
`maxSizeUsd = 0` does not exist, add one — it is the cheapest possible guard on the behaviour this
spec depends on.

---

## §Commits

1. The three coordinated edits (§2.1) — they must land **together**, or the §1.5 invariant is broken
   between commits.
2. The two visibility lines (§2.2).
3. The impl report.

Docs tracked and committed with the work. **The owner is the only pusher.**

**The impl report must carry:** all eight acceptance results, with acceptance 5 stated honestly if no
sub-$32-stop payload appeared; a numbered § answering the tiering question; and an explicit *what was
NOT established* section.

---

## §Model and effort (`HANDOVER-6.md` §7b)

> **Recommendation: Opus, HIGH effort, fresh conversation.**

**Why — tied to what it touches:**

- It changes what reaches `MaxSizeUsd`, which feeds `RiskSizedBase` on the **bridge act path**.
  `HANDOVER-6.md` §7a reserves Opus-HIGH for the order / SL / receive / bridge / act paths and calls
  it non-negotiable.
- **The change removes a safety ceiling.** Getting it half-right — commit without warning, or a
  blank box now meaning uncapped — hands the bridge an unbounded size. That is the single worst
  outcome available in this codebase.
- **The diff is about six lines. That is exactly why the tier is high.** Three of those lines must
  change together, and the failure is silent.

**Where the thinking should go:**

1. **§1.5's invariant** — commit and warning must move together. The seat that changes one and
   forgets the other produces a box that stores what its own validator rejects.
2. **§2.3's asymmetry** — Risk / Trade must NOT get the same treatment, and the reason is not
   obvious from the code. It is `RiskSizedOrFallback`'s `-1` path.
3. **Acceptance 2** — blank must still keep the last good value. Blank and `0` now mean *different*
   things, and that is the entire point of the change.

**What would change the answer:** nothing lowers it. If the work turns out to need a change in
`RiskSizedBase` or anywhere in `SignalBridge`'s sizing arithmetic, **stop and escalate** — that is a
spec defect, not an implementation detail.

**Answer in the impl report:** was Opus-HIGH right? Name where the depth was needed, or say plainly
that it was not.
