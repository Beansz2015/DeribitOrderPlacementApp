# Coordinator review — `Max Size = 0` means NO CAP

**Reviewing:** `8831f62` · `d1d17ad` · `717eb9b` (report `docs/impl-report-max-size-uncapped.md`).
**Spec:** `docs/spec-max-size-uncapped.md`. **Reviewer:** coordinator, Opus HIGH, 2026-08-18.
**HEAD reviewed:** `717eb9b`, tree clean, 7 ahead of `origin/master`. **Not pushed.**

## Verdict — APPROVED. Four findings, none blocking.

The three coordinated edits are correct, the asymmetry against Risk / Trade is right and is right for
the stated reason, the re-seed is correctly scoped, **and the one place this change could have gone
catastrophically wrong was found by the implementer and closed properly.** All six of the report's
spec-back findings are upheld, one with its evidence corrected. Nothing here needs a code change
before the owner rebuilds x64.

---

## 1. Executed at this seat — not taken on report

| Check | Result |
|---|---|
| `tools/checks/verify-gate.ps1` at `717eb9b` | **GATE PASSED, OrderCheck 294/294** |
| Nine `frmMainPageV2.vb` censuses at `717eb9b` | **68 occurrences across 64 lines, zero drift** |
| Full code diff `44dddd1..717eb9b` | read line by line |
| `_host` null-safety on the new parse-failure read | ✅ guarded at `AutoTradeSettings.vb:413`, before the `:441` read |
| Re-seed recursion risk | ✅ none — the ten boxes wire `Enter`/`Click`/`Leave`/`KeyDown` only (`:163-169`), **no `TextChanged`** |
| Threading — the new `AppendColoredText` inside `SetRiskSizingValues` | ✅ safe. **One caller only** (`AutoTradeSettings.vb:442`), UI thread. The receive-loop trap does not bite |
| All four `CommitToolingConfig` paths | `:162` seed-then-commit (no-op) · `:267` Leave ✅ re-seeds · `:275` Enter ✅ re-seeds · `:501` `btnRiskSize_Click` — no re-seed, but unreachable with a dirty box, same focus argument as `SB4` |
| Failed-settings-load direction | ✅ **safe**: `AppUserSettings.vb:45`, `:140` and `frmMainPageV2.vb:76` all default `500D`. A failed load is **capped**, never uncapped |
| Acceptance 5 arithmetic, recomputed | ✅ `floor(1.0 × 64000 ÷ 20 ÷ 10) × 10 = 3200`, and 20 is under the spec's ~$32 threshold |
| Owner's x64 bin carries this change? | ✅ **No** — confirms report §6 item 1. `BridgeMaxSizeLine` absent from the x64 assembly |

---

## 2. The spec-back findings — all six upheld

| Finding | Verdict | How it was checked here |
|---|---|---|
| **`SB1`** (spec §2.2b names only `Leave`) | ✅ **UPHELD** | Spec lines 137-139 cite `Leave` alone; `CommitOnEnterKey` is wired to the same ten boxes at `AutoTradeSettings.vb:169` and commits at `:275`. Implementing both paths was **required**, not optional |
| **`SB2`** (acceptance 7's OrderCheck figure stale) | ✅ **UPHELD** | Spec line 221 asks **268/268**. It was already **289/289** at `8232e9e` — verified at this seat before the implementer started |
| **`SB3`** (acceptance 8's fixture already existed) | ✅ **UPHELD, independently** | Both uncapped-arm fixtures appear in **this seat's own gate output at `8232e9e`**, before any of this work. Not taken from the report |
| **`SB4`** (acceptance 2c cannot discriminate) | ✅ **UPHELD — and now CONFIRMED AT THE CODE, not merely accepted as reasoning** | See §2.1 below |
| **`SB5`** (two `tools/` harness findings) | ⚠️ **UPHELD on bullet 2; bullet 1's EVIDENCE is wrong** — see `SB8` below | `Get-FocusSink` (`tools/harness-common.ps1:266-275`) does return the **first** focusable, on-screen, non-excluded Edit. Confirmed |
| **`SB6`** (commit line is a superset of §2.2 (a)) | ✅ **UPHELD, and the superset is the better behaviour** | Spec line 103 asks for a line *"when the setting changes to uncapped"*. Firing on **any** change is correct: a line only on the way in would leave the log's last word reading `NO CAP` while a cap was back in force |

### 2.1 `SB4` — confirmed at the code, and it had an unclosed consequence

**The structural claim is TRUE, and it is now proven rather than argued.** Re-seeding fires from
**exactly two call sites** — `CommitOnLeave` (`AutoTradeSettings.vb:268`) and `CommitOnEnterKey`
(`:276`) — and **both pass the focused box as `sender`**. Enumerated at this seat: the form declares
**no `TextChanged` handler at all**; its whole `AddHandler` set is the ten boxes'
`Enter`/`Click`/`Leave`/`KeyDown` (`:166-169`), `txtSessionPolicy.Leave` (`:183`), two
`CheckedChanged`, and three non-input handlers. All four `CommitToolingConfig` callers were checked
(`:162`, `:267`, `:275`, `:501`) — **none can fire while a non-focused box is dirty.**

⇒ With one keyboard focus, **the box holding uncommitted keystrokes is always the sender.** A blanket
re-seed and a sender-only re-seed are behaviourally identical through this UI. `SB4` is right.

🚨 **The consequence the report did not draw, and which this review acts on:** if 2c cannot fail,
then **the spec still contains a live-looking acceptance that is unfalsifiable**, and the next seat
greps the spec, finds it, and re-runs a test that proves nothing. The report's warning was in the
*report*; the trap was in the *spec*. **`docs/spec-max-size-uncapped.md` acceptance 2c is now STRUCK
in place**, with the reason, the code evidence, and — importantly — a note that the sender-scoping
**stays**, because it now guards a *future* hazard (a `TextChanged` commit, a timer commit, a second
editing surface) rather than a reachable one. Without that note the next simplification pass deletes
the scoping on the grounds that nothing tests it.

**A second-order point worth stating:** the spec's own justification for §2.2b — *"a blanket re-seed
would silently discard their keystrokes"* — describes a state this UI cannot reach. The rule is
right; its stated reason is not. That is recorded in the spec rather than left for rediscovery.

**On the design decision the report calls out (§2.1 of the report):** it is the right call and it is
the load-bearing one. `SetRiskSizingValues` carries both settings, so the breaker's guard-the-call shape is genuinely
unavailable, and the obvious substitute — leaving `maxSize = 0D` on a parse failure — **would have made
every typo mean UNCAPPED** after edit 2. Re-sending `_host.MaxSizeUsd` is correct and stays silent.

---

## 3. Findings raised by this review

**`D1` — a coordinator-review CODE defect in the Max Size uncapped work: an OrderCheck fixture
description asserts a guard that does not exist.**
`tools/OrderCheck/Program.vb:493-494` reads *"a negative `max_size_usd` is also 'no cap', **matching
the button's `maxUsd > 0` guard**"*. **There is no such guard.** `ApplyRiskBasedSize`
(`frmMainPageV2.vb:322-364`) guards `refPrice`, `dist`, `riskUsd` and the min-10 result — `maxUsd`
appears exactly twice in the whole file, the read at `:339` and the pass-through at `:353`.
**Pre-existing, not introduced here** — but it was harmless while `0` was unreachable and is
misleading now that it is not. Severity LOW. Fix is one comment.

**`D2` — a coordinator-review CODE defect: the new invariant-culture fixture is vacuous on this
machine.**
`tools/OrderCheck/Program.vb` pins `BridgeMaxSizeLine(2000.5D) = "Bridge max size: 2000.5"` and its
comment claims the line *"renders INVARIANTLY … a current-culture render would print `2000,5`"*.
**The fixture never switches culture.** This box is **en-MY**, whose decimal separator is `.`, so the
invariant and current-culture renderings are byte-identical and the fixture would pass unchanged if
the code used `CurrentCulture`. **The code is correct** (`frmMainPageV2.vb` uses
`CultureInfo.InvariantCulture`); the *test* cannot detect a regression away from it.
Severity LOW–MEDIUM — a silent-regression hole, and the repo already has the fix pattern twice:
fixture 1's culture loop (`:108-119`) and C1 fixture 6's de-DE switch (`:964-972`).

**`SB7` — a spec-back against `docs/spec-max-size-uncapped.md`: the spec never says
`max_size_usd` also governs the MANUAL SIZE button.**
`ApplyRiskBasedSize` reads the same `MaxSizeUsd` (`frmMainPageV2.vb:339`) and passes it to the same
`RiskSizedBase` (`:353`). So **`Max Size = 0` uncaps the manual SIZE button too**, not only the bridge
act path. The spec's Do-not-touch table never mentions that button, and the impl report's §6
*what was NOT established* does not either. Two consequences worth the owner's eye:
- The change is **wider than the spec's framing** by one path.
- Both new lines say *"**Bridge** max size"*, which **understates what the setting now governs**.
⚠ **Not silent and not obviously wrong** — the button writes a visible number into the Amount box and
the min-10 refusal still applies, so this may be exactly what the owner wants. **It needs a ruling,
not a fix.** Severity LOW–MEDIUM.

**`SB8` — a spec-back against `docs/impl-report-max-size-uncapped.md`: `SB5`'s first bullet
misquotes the harness script.**
It reports *"`tools/set-textbox.ps1`'s **'value NOT committed — focus untouched'** message is wrong on
the second clause"*. **That string does not exist in the script.** The actual message (`:93`) is
*"NOTE: value NOT committed to the app's mirrors until the box loses focus…"* — it makes **no claim
about focus at all**, so it cannot be wrong on a second clause it does not have, and as written it is
accurate. **The substantive claim — that `ValuePattern.SetValue` focuses the box it writes — is not
disproved**, and it is plausible; it simply is not evidenced by the quoted message. Note also that the
script **explicitly** calls `$e.SetFocus()` before writing, but only under `-CommitViaBlur`
(`:54-67`). **Re-scope the finding** to *"the script does not document that `SetValue` may take
focus"*. Severity LOW, report accuracy only.

---

## 4. What this review did NOT verify

1. **No runtime.** Nothing was launched at this seat. **All eight acceptance results are taken on the
   report**, including the acceptance 5 counterfactual and the 5.08-minute soak. The arithmetic was
   re-derived; the observations were not re-run.
2. **The gate at the intermediate commit `8831f62`** (report claims 289/289). Executed at `717eb9b`
   only. Fixture attribution *was* checked: `tools/OrderCheck/Program.vb` changed **only** in
   `d1d17ad`, which is consistent with the report's 289 → 294.
3. **`SB4`'s blanket-vs-sender claim and `SB5`'s SetValue-focus claim** — both need the harness.
   Accepted as reasoning, not reproduced.
4. **Line colours** (`Color.Yellow` uncapped, `Color.Gray` capped) — unverified here as well as in the
   report. No screenshot exists.
5. **The Enter-key re-seed path at runtime** — code-inspected only, as the report states.

## 5. Disposition

- **No code change is required before the owner rebuilds x64.** `D1`, `D2` and `SB8` are comment- and
  test-level; `SB7` wants a ruling.
- 🚨 **The owner's x64 bin does NOT carry this change** — verified at the binary. `Max Size = 0` will
  do nothing in the owner's app until it is rebuilt:
  `dotnet build DeribitOrderPlacementApp\DeribitOrderPlacementApp.vbproj -c Debug -p:Platform=x64`,
  then re-check `Environment: testnet` **and** the `— TESTNET` title.
- **Owner rulings wanted:** `SB7` (is uncapping the manual SIZE button intended, and should the two
  lines stop saying "Bridge"?) and `SB6` (keep the superset — this review recommends **keep**).

---

## 6. ✅ RULINGS RECEIVED AND LANDED — 2026-08-19

**The owner accepted `SB6` and `SB7` as recommended. All four findings are now closed.**

| Finding | Ruling | Landed as |
|---|---|---|
| **`SB7`** (`max_size_usd` also governs the manual SIZE button) | **ACCEPTED** — one setting, one meaning. Wording drops "Bridge" | `MaxSizeLine` (renamed from `BridgeMaxSizeLine`); lines now read `Max size: NO CAP (max_size_usd = 0)` / `Max size: 2000`. Spec §2.2c + the Do-not-touch table now name the button |
| **`SB6`** (commit line is a superset of §2.2 (a)) | **KEEP the superset** | No code change. Ruling recorded at `frmMainPageV2.vb:130`-region and spec §2.2c, with an explicit *do not narrow this* |
| **`D1`** (fixture asserts a nonexistent `maxUsd > 0` guard) | fix | Fixture renamed to *"and NOTHING guards maxUsd upstream"*, with the real guard list in a comment |
| **`D2`** (invariant-culture fixture was vacuous) | fix | Now renders under **de-DE** inside a save/restore, asserting `2000.5` and **not** `2000,5`. Verified the two renderings genuinely differ under de-DE, so the pin has teeth |

**Re-verified after these edits:** `GATE PASSED, OrderCheck 294/294` · nine censuses **68 across 64
lines, zero drift** (`frmMainPageV2.vb` was edited, so they were re-run, not assumed).

⚠ **Unchanged by all of this: the owner's x64 bin still does not carry the feature.** The rename does
not alter that — `MaxSizeLine` is absent from the x64 assembly until it is rebuilt.
