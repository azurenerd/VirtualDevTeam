# Plan: Fix the dashboard-quality failure mode

## Status: Runner + dashboard already stopped; user's 4 copilot CLIs preserved

## Problem statement

After 9 merged PRs (#2071–#2079) and ~10 hours, the shipped dashboard looks like this:

```
(placeholder)
Timeline placeholder
Heatmap placeholder
```

That's it. Three lines of literal placeholder text on a blank 1920×1080 white page. Nothing resembling `OriginalDesignConcept.html` (which specifies a Privacy Automation Release Roadmap header, timeline SVG with 3 lanes + milestones, and a 5-column × 5-row heatmap).

Meanwhile the PM review pipeline **did detect** the failure every time — PR #2071's PM comment reads *"completely blank white page with no rendered dashboard content"* — but the system still merged every PR.

## Root causes (ranked by impact)

### RC-1 (CRITICAL): `MaxReworkCycles = 1` defeats every review

- `src/AgentSquad.Core/Configuration/AgentSquadConfig.cs` line 211, 218, 225, 231 — all four rework limits default to **1**.
- `src/AgentSquad.Runner/appsettings.json` line 118 reinforces `MaxReworkCycles: 1`.
- PM correctly identifies "blank white page" → requests changes.
- SE hits the per-reviewer limit on the **very first** rework request.
- `EngineerAgentBase.HandleReworkAsync` (line 1492) posts "maximum rework cycle limit (1)" + requests force-approval.
- `ProgramManagerAgent.cs` line 994–1001 silently force-approves with zero semantic check.
- Net effect: the PM's feedback is never actually acted on — it's logged, then overridden.

### RC-2 (CRITICAL): No design reference image in the repo

- `ProgramManagerAgent.cs` line 2844 expects design screenshots under `docs/design-screenshots/` (committed by the Researcher).
- Repo tree check: only `OriginalDesignConcept.html` exists — **no `docs/design-screenshots/` folder, no rendered image**.
- PM's LLM vision call therefore has no visual anchor to compare the SE's screenshot against. It can only "describe" the screenshot in isolation, not judge fidelity.
- Result: PM catches *blank* pages (obvious) but would miss *wrong-layout* pages (which is the real failure mode).

### RC-3 (HIGH): TE reuses stale screenshots from prior merged branches

- PR #2079's screenshot URL: `…/test-results/screenshots/pr-2071-app-preview.png` — filename hard-codes PR **2071**, not 2079.
- `TestEngineerAgent.cs` line 1606 generates the *correct* filename `pr-{pr.Number}-app-preview.png` for the standalone-capture fallback.
- But line 1720–1721 `UploadTestArtifactsToPrAsync` uploads *any* `.png` it finds under `test-results/screenshots/` using its on-disk filename. When a prior PR's screenshot is in the merge base or in Playwright test fixtures, the same file gets re-committed to every subsequent branch.
- Consequence: the screenshot the PM reviews for a new PR may be an old image from a prior, already-merged PR → false confidence.

### RC-4 (HIGH): UI test failures don't block merge

- PR #2079 TE comment: *"UI Tests: ❌ 29 passed, **7 failed**"* — `div.hdr` missing, `.hm-wrap` missing, `.hm-grid` has 0 children, etc. These are ground-truth proof the components aren't rendering.
- PR was **still merged** after force-approval.
- PM review logic at `ProgramManagerAgent.cs` ~line 960 checks for a TE "test results" comment but does not parse the pass/fail counts. Any TE comment whatsoever satisfies `hasTeCompletionComment`.

### RC-5 (MEDIUM): SE keeps shipping stub `(placeholder)` strings

- The merged `Dashboard.razor` literally renders hardcoded `"(placeholder)"`, `"Timeline placeholder"`, `"Heatmap placeholder"` text (confirmed from PR #2079's screenshot).
- Despite PR #2079 titled "Build Error Banner and Wire Dashboard Composition" claiming to compose `DashboardHeader` + `TimelineSvg` + `Heatmap`, the runtime output has none of those.
- Symptom of SE's copilot-delegation strategy producing stub code when the task is ambiguous, + no downstream check that the composed components are actually referenced in Dashboard.razor.

### RC-6 (MEDIUM): `data.json` lifecycle confusion in scaffolding PR

- PR #2071 (scaffolding) did NOT create a `data.json` — only an interface stub + tests.
- PR #2078 (much later) "Author Sample data.json and README" is when data.json was introduced.
- Between #2071 and #2078, every intermediate PR had the service hitting a `NotFound` error on boot — but the ErrorBanner wasn't wired until PR #2079, so the page rendered *nothing* for 7 PRs in a row.
- A scaffolding-phase PR should have shipped at minimum a `data.template.json` (already exists at repo root in AgentSquad's own workspace — user even maintains one) into the target app's `wwwroot/` so the app boots non-blank from day 1.

### RC-7 (MEDIUM): PM review prompt lacks explicit design-fidelity comparison

- `ProgramManagerAgent.cs` lines 2556–2600 build a screenshot review prompt with a generic "VISUAL VALIDATION" preamble.
- Prompt does not reference `OriginalDesignConcept.html` or attach any design reference.
- Vision model is asked "does this look right?" with no anchor — it answers based on LLM priors, which produces low-quality verdicts.

## Proposed fixes (in recommended application order)

### Phase A — Make review cycles actually matter (biggest lever)

- **A1** Raise defaults in `AgentSquadConfig.cs`: `MaxReworkCycles = 3`, `MaxPmReworkCycles = 3`, `MaxArchitectReworkCycles = 2`, `MaxTestReworkCycles = 2`. Also raise in `appsettings.json`.
- **A2** Add a hard merge-block condition in `ProgramManagerAgent` review path: if latest TE comment shows `❌ N failed` where N > 0 for UI tests, DO NOT approve — request changes regardless of rework count. (Force-approval should still apply for non-UI flakes, but UI failures are ground truth for visual tasks.)
- **A3** Distinguish "force-approve with explicit human gate" from "silently merge": the `ReworkExhaustion` gate (`EngineerAgentBase.cs` line 1500) already exists and the code waits on it. Confirm the gate is actually blocking in the current `appsettings` (not auto-released). If auto-released, add a boolean `AgentSquad.Gates.ReworkExhaustion.BlockOnUiFailures = true`.

### Phase B — Give the PM a real visual reference

- **B1** Add a Researcher workflow step: render `OriginalDesignConcept.html` → `docs/design-screenshots/design.png` using Playwright (headless Chromium at 1920×1080). Commit on first Research phase.
- **B2** Update `ProgramManagerAgent.cs` PM review prompt to attach the design screenshot as a *second* vision input alongside the SE screenshot, with explicit instruction: *"Compare Screenshot 1 (actual) to Screenshot 2 (target design). Approve only if: header text, timeline lanes, heatmap grid are recognizably present and positioned similarly. Blank pages, literal 'placeholder' strings, or missing components → CHANGES REQUESTED."*
- **B3** Same wiring for Architect review — the architect should reject scaffolding PRs that don't put a `data.template.json` in `wwwroot/` so the app doesn't boot blank.

### Phase C — Fix screenshot pipeline truthfulness

- **C1** `TestEngineerAgent.UploadTestArtifactsToPrAsync`: skip any `.png` in `test-results/screenshots/` whose filename starts with `pr-` and doesn't match the current `pr.Number`. Prevents inheritance of old branch screenshots.
- **C2** `TestEngineerAgent.cs` line 1599 standalone fallback: on capture failure, post an explicit **"⚠️ No live screenshot captured — PM should flag this"** comment instead of silently falling through. Today, when capture fails and stale files exist, the stale file silently sneaks into the comment.
- **C3** Add a pre-capture step: delete `test-results/screenshots/pr-*.png` in the agent's local workspace before each TE run, so only the current PR's capture (or absence) is visible.

### Phase D — Keep placeholder stubs from merging

- **D1** Add an SE self-check at PR-ready time: grep the diff for quoted strings `"placeholder"` (case-insensitive) in `.razor`/`.cs`/`.html` files touched by the PR. If found AND the task title contains words like "wire", "compose", "integrate", "final", auto-comment "⚠️ Placeholder strings detected; task claims integration is complete" and refuse to mark ready.
- **D2** Enhance the baseline strategy's prompt template with an explicit "DO NOT leave placeholder strings; render real components or `<Empty*/>`" instruction for UI files.

### Phase E — Ship a bootable scaffold

- **E1** First-SE-PR template (Project Foundation / scaffolding) MUST include `wwwroot/data.json` as a minimal valid sample (copy of `data.template.json` contents or inline sample). Enforce via PR checklist in the SE's PR description template.

### Phase F — Validate with a mini-reset end-to-end test

- **F1** Apply Phase A + Phase B (highest leverage) and ship commit.
- **F2** Mini-reset, run fresh, monitor PR #1 (scaffolding) — expect a bootable app + a non-blank screenshot + visible components.
- **F3** If screenshot still blank: iterate on Phase C + Phase D.
- **F4** Validate through at least PR #3 (after scaffold + model + first UI component) to confirm the page progressively fills in.

## Open questions for user (no decisions made yet)

1. **Rework cap appetite**: raising to 3 cycles roughly doubles AI cost per PR but gives real review teeth. Acceptable, or should I make Phase A smaller (e.g. 2 instead of 3)?
2. **Design screenshot rendering**: Researcher currently doesn't own a Playwright runner. I'd need to either (a) give it one, or (b) delegate that single step to TestEngineer's existing Playwright runner on its first spawn. Preference?
3. **UI test failure hard block**: any appetite for making *any* UI test failure auto-block-merge until the human releases a ReworkExhaustion gate? This is blunt but would have prevented every bad merge in the current run.
4. **Scope**: do you want me to implement Phases A, B, C, D, E, F in this session, or is this a plan-only exercise and we implement after sleep?

## Notes / cross-refs

- Previous fix shipping in commit `3614436` (empty default `EnabledStrategies`) is unrelated to this failure — it was a throughput / config-merge bug. The review-cycle bug is pre-existing.
- `OriginalDesignConcept.html` itself contains `TODO — item shipped` / `TODO — in progress` as *deliberate* placeholder labels for the heatmap cells — those strings would be replaced from `data.json`, not hardcoded in components. Don't confuse them with the "(placeholder)" / "Timeline placeholder" strings seen in the failing screenshot.
- All 4 user-interactive copilot CLIs (PIDs 62768, 78784, 79812, 83904) remain untouched and must stay that way.
