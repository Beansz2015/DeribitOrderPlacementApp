# Impl report — R3 owned-window timing probe (`docs/spec-harness-owned-window.md` §Ruling)

**Seat:** Sonnet, medium — same conversation, continued per the ruling's seat assignment.
**Date:** 2026-08-07. **Scope:** read-only diagnostics in `tools/` only. No `.vb` file touched. No
control inside the settings window set, toggled or selected. No trade. Per R3: **no fix is included
here** — this report is the probe only.

---

## 1. What was built

`tools/probe-owned-window-timing.ps1` — polls, per round, **Query A** (is `AutoTradeSettings` a root
child of the desktop for the harness PID?) and **Query B** (does
`Find-ByControlType(mainForm, ComboBox)` reach `cboBridgeMode`?), timing each independently from the
moment the settings window is opened via its own toggle button, over many open/close rounds.

## 2. A probe defect, found and fixed before the results below can be trusted

The first run (20 rounds) alternated open/close by blindly invoking the toggle button once per
transition, assuming strict alternation. Results 1–8 looked clean; from round 9 onward **both**
queries went to "never" for every remaining round. That pattern isn't in R3's outcome table, so
before reporting it I checked ground truth with a raw Win32 `EnumWindows`/`IsWindowVisible` probe,
independent of UIA. It showed the toggle's actual open/closed state was **inverted** from what the
script assumed — one `Invoke()` somewhere had silently not registered, and every subsequent round's
"open" was actually a "close". **This was a bug in my probe, not a finding about the app or UIA.**
The 20-round table from that run is **discarded**; none of it is used below.

Fixed by adding Win32 ground-truth verification (`OwnedWindowProbeWin32.VisibleState`, independent of
the two UIA queries under test) **before and after each round, outside the timed measurement
window** — so it establishes/confirms state without perturbing what Query A/B measure. If a desync is
detected, the script corrects it and counts it, rather than silently drifting for the rest of the run.
This is now standing in the script for any future re-run.

## 3. Results — three valid run sets, 39 rounds total

**Set 1** (20 rounds, PID 18144, baseline required a forced close — the app was left open from the
discarded run above):

| Round | A | B | Win32Visible |
|---|---|---|---|
| 1 | never | never | True |
| 2 | never | 9 | True |
| 3 | never | 13 | True |
| 4 | never | 10 | True |
| 5 | never | 13 | True |
| 6 | never | 22 | True |
| 7 | never | 15 | True |
| 8 | never | 14 | True |
| 9 | never | 16 | True |
| 10 | never | 10 | True |
| 11 | never | 10 | True |
| 12 | never | 11 | True |
| 13 | never | 14 | True |
| 14 | never | 17 | True |
| 15 | never | 16 | True |
| 16 | never | 17 | True |
| 17 | never | 18 | True |
| 18 | never | 16 | True |
| 19 | never | 18 | True |
| 20 | never | 17 | True |

**Set 2** (15 rounds, same PID, baseline already closed cleanly — no correction needed):

| Round | A | B | Win32Visible |
|---|---|---|---|
| 1 | never | 18 | True |
| 2 | never | 11 | True |
| 3 | never | 10 | True |
| 4 | never | 14 | True |
| 5 | never | 14 | True |
| 6 | never | 19 | True |
| 7 | never | 19 | True |
| 8 | never | 11 | True |
| 9 | never | 12 | True |
| 10 | never | 16 | True |
| 11 | never | 12 | True |
| 12 | never | 14 | True |
| 13 | never | 12 | True |
| 14 | never | 16 | True |
| 15 | never | 11 | True |

**Set 3** — four isolated 1-round trials (fresh PID 29840), run to test whether Set 1 round 1's
anomaly was tied to the forced-baseline-close path specifically (alternating forced-close /
already-closed baselines):

| Trial | Baseline | A | B | Win32Visible |
|---|---|---|---|---|
| 1 | forced close | never | 21 | True |
| 2 | forced close | never | never | **False** |
| 3 | already closed | never | 13 | True |
| 4 | forced close | never | 15 | True |

**Totals (39 rounds):**
- **Query A: 0/39 succeeded (0%).** Never once, in any round, under any baseline condition, within
  the 2000 ms budget.
- **Query B: 37/39 succeeded (94.9%)**, and fast every time it did — 9 to 22 ms, no outliers.
- **Desync corrections needed after the fix: 0/39.** The round-9 defect from the discarded run did
  not recur once ground truth was checked at every transition.

## 4. Two anomalies, reported exactly, not folded into a headline number

Two of the 39 rounds don't fit cleanly, and I'm recording both rather than averaging them away:

- **Set 1, round 1:** A and B both "never" — but Win32 ground truth confirms the window **was**
  genuinely open (`Win32Visible=True`) for the full 2-second window. Neither query found it despite
  it being there the whole time.
- **Set 3, trial 2:** A and B both "never" — and this time Win32 confirms the window was **not**
  actually open (`Win32Visible=False`) by round's end. The toggle's `Invoke()` did not durably produce
  an open window within 2 seconds on this one occasion.

These are two different failure shapes, not one repeated pattern. **Neither is the pattern that would
justify reinstating a union** (R3's row 2: "B never succeeds in some rounds, but A does") — A did not
succeed in either of these rounds, or in any of the other 37. Filtering out trial 2 (window genuinely
never opened, so it isn't a valid A/B comparison round), Query B succeeds in **37/38 rounds where the
window was confirmed open (97.4%)**.

I have not established a root cause for either anomaly. My best unverified guess is an occasional
UI-thread message race around the toggle click itself — separate from the window-enumeration
question this probe was built to answer — but I'm not asserting that as fact. Recorded as open rather
than smoothed over, per R3's "report the numbers even if inconvenient."

## 5. Reading this against R3's outcome table

| Outcome | Cause | Fix |
|---|---|---|
| B succeeds within a bounded time, every round | Render latency | Bounded wait-for-condition. No union. |
| B never succeeds in some rounds, but A does | Topology | Union needed, with option (a) to stop the double-count |
| Neither succeeds in some rounds | Absent from the tree | Bounded wait + Win32 `EnumWindows` in reserve |

**This data matches row 1, not row 2 or row 3, on the dimension that decides the fix.** The
discriminating question per R1/R2 is not "does B ever fail" (it does, rarely) but "does A succeed
where B fails" — because that is the only pattern a union could help with, and it **never occurred,
not once in 39 rounds**. A is uniformly, deterministically absent. Where B does fail, it fails
alongside A, not instead of it — a union would have found nothing in either anomalous round either.

**Recommendation: a bounded wait-for-condition before the settings-window control search, no union,
no change to `Get-ProcessWindows`.** Consistent with §2.5's corrected rejection table. I have not
written that fix — R3 asked for the probe only ("do NOT write a fix yet").

## 6. What this probe did NOT establish

- Root cause of either anomaly in §4.
- Whether the pattern holds for control types other than `ComboBox`/`cboBridgeMode` — R3 specified
  this exact pair, and that's all this probe measured. (The spec-back's earlier finding that `Edit`
  and `CheckBox` targets also doubled was observed **under the withdrawn union**, which no longer
  applies — it doesn't bear on this probe.)
- Any concrete deadline/interval value for a bounded wait-for-condition — that's a fix-spec decision,
  not something this probe was asked to produce.
- Whether the two anomalies would recur at a materially different rate over, say, 100+ rounds. 39 is
  what R3 asked for (~20, doubled here for the anomaly follow-up); a much larger sample was not run.

## 7. Repo state

`tools/probe-owned-window-timing.ps1` is new. No `.vb` file touched. No control inside the settings
window was set, toggled or selected — the only control driven was the main form's own "Auto Settings"
toggle button (`InvokePattern` only), used solely to open/close the settings window between rounds.
No trade. The app was launched fresh for each run set and stopped cleanly after each (`tools/stop-app.ps1`);
nothing was left running. Nothing pushed.

## 8. Was Sonnet at medium right for the probe?

Yes. The work stayed exactly where the ruling scoped it — read-only `tools/` diagnostics, no app
change, no trade. The one place more depth would have helped is generic rather than probe-specific:
catching my own probe defect (§2) required cross-checking one measurement against an independent
ground truth rather than trusting the first clean-looking 8-of-20 result and reporting it as-is — that
kind of self-skepticism is something medium effort supports but doesn't guarantee by itself; I only
did it because the round-9 pattern didn't match anything in R3's outcome table and looked wrong on its
face. Nothing in this probe needed architectural judgment or touched the app, so there's no indication
it needed Opus or high effort.
