# Spec-back — the owned-window union fix works, but it doubles every settings-window control (D1)

**For:** the owner + orchestrator. **From:** the `spec-harness-owned-window` implementer seat
(Sonnet, medium), 2026-08-07. **Status:** BLOCKED before commit 3 — the merge seam and the union
rewire are done and correct in isolation; acceptance 1 does not pass as things stand. No `.vb` file
touched, nothing pushed, app stopped, settings window closed cleanly, no value left mutated.

**Context:** implementing `docs/spec-harness-owned-window.md` exactly as written. Commits
`1f4b931` (`Merge-ProcessWindows` pure seam + `tools/checks/test-merge-process-windows.ps1`, no
behaviour change) and `2c5e986` (`Get-ProcessWindows` rewired to the union rule) land on `master`,
not pushed. Both pass their own tests.

---

## 1. What works — verified live, not theorised

Launched via `tools/launch-app.ps1` (title carried `— TESTNET`). With the settings window open,
`Get-ProcessWindows -Diagnostic` returns exactly the union the spec asks for:

```
Get-ProcessWindows [Root] 'Deribit Order Placement App V2.2 — TESTNET'
Get-ProcessWindows [Descendant] 'AutoTradeSettings'
```

Main-form-first (§2.2 order), one entry each (§2.2 de-dup by RuntimeId), found via the DESCENDANTS
half exactly as the spec's own evidence (§1.3) predicts. `tools/checks/test-merge-process-windows.ps1`
passes 5/5 (both-empty, set-2-empty, de-dup, order, duplicate-in-the-middle) and
`tools/checks/test-select-best-match.ps1` still passes 7/7, unaffected. **This part of the spec is
right, and this part of the implementation of it is right.**

## 2. What breaks — a new, deterministic defect the spec's acceptance criteria don't catch

Acceptance 2 ("no duplicates") only checks the **window** list returned by `Get-ProcessWindows`.
Nothing checks the **control** list each call site builds afterward, by looping
`Find-ByControlType` (`TreeScope::Descendants`) over every window in that list.

The settings window is a UIA descendant of the main form — that is the exact fact §1.2 relies on to
find it at all. But that means `Find-ByControlType(Element=mainForm, TreeScope::Descendants)`
**also** walks down into the settings window's own controls, because `Descendants` does not stop at
a nested window boundary. So once the union returns both windows, every settings-window control is
found **twice**: once via the main form's traversal (mislabelled as belonging to the main-form
window), once via the settings window's own traversal. `Select-BestMatchIndex` then sees two
exact-id-tier hits for the same control and returns `Ambiguous`. Every settings-window drive script
refuses.

**Verified read-only, three control types, 3/3 reproduced.** Real run, real refusal:

```
$ select-combo-item.ps1 cboBridgeMode "Log-only"
Get-ProcessWindows [Root] 'Deribit Order Placement App V2.2 — TESTNET'
Get-ProcessWindows [Descendant] 'AutoTradeSettings'
REFUSED: 'cboBridgeMode' matches more than one combo at the same tier — refusing to pick arbitrarily.
  - 'cboBridgeMode' (id 'cboBridgeMode', window 'Deribit Order Placement App V2.2 — TESTNET')
  - 'cboBridgeMode' (id 'cboBridgeMode', window 'AutoTradeSettings')
exit 3
```

Same tie, same shape, for `txtAtrLength` (Edit) and `chkSessionPolicyOn` (CheckBox) — checked with a
direct, read-only `Select-MatchingElement` call, no mutation performed. The `cboBridgeMode` selection
above never committed (refused before any write), so nothing was left in a dirty state; the settings
window's controls never had a persisted value touched this session.

**This is not the old flakiness recurring in a new shape.** The old defect was intermittent — the
window was *sometimes* missing from the enumerated set, and callers occasionally couldn't find its
controls at all. This one is deterministic: every settings-window control is doubled, every time the
settings window is open, on every one of the five drive scripts that can target it (`set-textbox`,
`toggle-checkbox`, `select-combo-item`, and `click-button` / `click-PLACES-ORDER` for any button that
happens to live there). §2.1's union fixes the *missing-window* fault completely, and introduces an
*always-on false-Ambiguous* fault in its place — worse for every settings-window drive step, and
better only for a caller that merely enumerates windows without searching their controls (none of
the seven call sites do that).

## 3. Why I stopped instead of fixing it myself

The fix lives downstream of `Get-ProcessWindows`, in code this spec's own do-not-touch table
protects: `Select-BestMatchIndex` and `Select-MatchingElement` are "landed and approved 2026-08-06 —
do not re-open the selection rules." A de-dup-by-`RuntimeId` inside `Select-MatchingElement`'s
candidate-collection loop would close this in a few lines, but I don't have standing to decide that
"re-open" excludes a de-dup addition that leaves the tier/precedence rules untouched.

Per this spec's own instruction — "if you find a defect in this spec, escalate to the owner BEFORE
implementing. Do not work around it. The spec gets amended; rulings fold back in." — and the standing
[[spec-defect-escalation]] rule, I'm raising this rather than picking an option unilaterally.

## 4. Options

| | Option | Where it lives | Assessment |
|---|---|---|---|
| **(a)** | Bound `Find-ByControlType`'s traversal so it does not cross into a nested owned `Window` when walking a container window — a `TreeWalker`-based manual descent that stops recursing at any `Window`-type descendant other than the element it started from. | `harness-common.ps1`, `Find-ByControlType` only. **Not** in the do-not-touch table. | **Recommended.** Fixes it at the traversal that causes it; touches neither protected function; every call site keeps working unmodified, because each window's control search stays scoped to *that* window — which is what every caller already assumes it is. Costs more code (a manual walk instead of one `FindAll` call) and its own proof, though the stop-condition is the only new logic and could get a pure-seam test the same way §2.4 did (given a plain description of the tree shape, decide whether to descend). |
| **(b)** | De-dup the candidate list by `RuntimeId` inside `Select-MatchingElement`, before it reaches `Select-BestMatchIndex`. | `Select-MatchingElement` — do-not-touch. | Smallest diff, but needs the owner to explicitly lift "do not re-open" for this one function, scoped to de-dup only — the tier/precedence rules in `Select-BestMatchIndex` would stay untouched either way. |
| **(c)** | Don't return the settings window as an independent entry from `Get-ProcessWindows`; keep the union only for window-enumerating callers (e.g. `inspect-tree.ps1`) and leave control-searching callers relying on the main form's `Descendants` reach, which (§5 below) may already work once the window has rendered. | Would need callers to differ by purpose. | Not recommended — it's the shape of the `-Exact` switch this spec's own §3 already rejected (asks callers to know something new), and it doesn't resolve the open question in §5 either way. |
| **(d)** | Accept the original flakiness and add the retry loop §2.5 explicitly rejected, as a stopgap while (a) or (b) is scoped properly. | N/A | Not recommended as a real fix — the spec already ruled this out and gave the reason (a genuine absence, not just timing, can't be retried away). Listed only as the fallback if a ruling takes a while and something is wanted in the interim. |

I lean **(a)**: it fixes the actual cause (a `Descendants` search crossing a window boundary it
shouldn't), stays inside the file already in scope for this spec, and touches nothing the
do-not-touch table protects.

## 5. One more thing worth ruling on — is §1.2's stated root cause the whole story?

While reproducing this, `Find-ByControlType(Element=mainForm, TreeScope::Descendants)` **did**
successfully reach `cboBridgeMode` inside the settings window on every one of my manual checks, once
I gave the window ~800 ms to render after `Show()`. That is the exact traversal every OLD call site
already ran (the main form was always the sole entry in the old, single-window list). If that
traversal reliably reaches the settings window's controls given enough time, the SB3-era evidence in
§1.3 (first two attempts failing, the third succeeding right after) may be better explained as **UIA
tree-caching/timing latency immediately after `Show()`**, rather than strictly "an owned window isn't
a child of the desktop root."

I haven't proven this either way — it's a hypothesis from a handful of manual runs with a fixed
delay, not a controlled timing test — but if it's right, option (a) alone would fix the
false-`Ambiguous` defect without needing the missing-window race to be real, and it explains why (a)
would not reintroduce the old flakiness: it doesn't change *when* the settings window's controls
become reachable, only *which* window is credited with owning them once they are. Flagging it so the
ruling on §4 can account for it rather than have me chase it unasked.

## What I did NOT change

- No `.vb` file touched.
- No call-site signature touched; all seven callers are untouched.
- No trade placed. TESTNET was confirmed in the title. The deny check and the TESTNET/PID gate were
  not exercised — blocked before acceptance 5.
- `tools/select-combo-item.ps1`'s temporary `-Diagnostic` flag, added only to prove acceptance 1's
  "which half found it" requirement, was reverted (`git checkout`) before this write-up. It was never
  committed.
- The desktop-wide screenshot I took while debugging why the settings window appeared to vanish
  between two of my checks captured unrelated sensitive content on the user's screen. It was deleted
  immediately and not used for anything; recorded here in the interest of not hiding it, not because
  it bears on the finding.

## Current repo state

`1f4b931` and `2c5e986` are on `master`, not pushed. Working tree clean. No impl report yet — holding
it per house convention (a spec-back precedes the impl report when a mid-implementation defect blocks
acceptance, rather than writing up a pass that didn't happen).

**Related:** `docs/spec-harness-owned-window.md` (this feature), `docs/review-harness-exact-match.md`
§5.2 and `docs/impl-report-harness-exact-match.md` §4.5/§5 (origin of the defect this spec fixes),
`HANDOVER-6.md` §7 (escalation rule, finding-ID convention).
