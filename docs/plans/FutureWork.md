# VirtualDevTeam — Future Work

> **Purpose:** Tracks known improvements, enhancements, and technical debt items that are not yet implemented. Each item includes context on why it matters and what needs to change.
>
> **Owner:** Ben Humphrey (@azurenerd)
>
> **Last Updated:** 2026-05-04

---

## Table of Contents

1. [Rework Quality — Increase MaxReworkCycles + Semantic Force-Approve Gate](#1-rework-quality)
2. [Design Fidelity Screenshots — Visual Reference for PM Review](#2-design-fidelity-screenshots)
3. [Stuck Issue Recovery — Re-Dispatch After SE Signals Complete](#3-stuck-issue-recovery)
4. [Per-Candidate Strategy Screenshots (Backend)](#4-per-candidate-strategy-screenshots)
5. [ScriptedCopilotCli — WS3 Fixture Test Runner](#5-scriptedcopilotcli)
6. [Cost Attribution — Copilot CLI Token Reporting](#6-cost-attribution)
7. [Multi-PR Mode Hardening](#7-multi-pr-mode-hardening)
8. [Dashboard PR Details Panel with Candidate Gallery](#8-dashboard-pr-details-panel)

---

## 1. Rework Quality

**Priority:** High  
**Status:** ✅ Resolved  

Defaults already at `MaxReworkCycles=3`, `MaxPmReworkCycles=3`, `MaxArchitectReworkCycles=2`, `MaxTestReworkCycles=2`. PM has UI quality gate that blocks force-approval on broken UIs. Validated in successful E2E run.

---

## 2. Design Fidelity Screenshots

**Priority:** Medium  
**Status:** ✅ Resolved  

PM now enforces strict visual rules even when no design reference PNGs exist. Added HTML design fallback: PM discovers design HTML files (e.g., `OriginalDesignConcept.html`) and extracts structural context (headings, CSS classes, SVG/grid presence) to anchor review prompts. Blank pages, placeholder strings, and missing components trigger REQUEST_CHANGES regardless of design image availability.

---

## 3. Stuck Issue Recovery

**Priority:** Medium  
**Status:** ✅ Resolved  

PM now auto-closes enhancement issues when NEEDS_MORE_WORK is detected but all tasks are already merged. Creates a follow-up issue tracking the gaps with `enhancement` and `follow-up` labels, then closes the original with a reference to the follow-up. Prevents issues from staying open forever with no agent able to act on them.

---

## 4. Per-Candidate Strategy Screenshots

**Priority:** Medium  
**Status:** Blocked (backend)  

### Problem

The Strategy Framework generates multiple candidate implementations (baseline, agentic-delegation, etc.) but only the **winner's** screenshot gets committed to the PR. The dashboard can't show a gallery of all candidates' visual output.

### What Needs to Change

1. **StrategyOrchestrator** — After each candidate builds successfully, capture a Playwright screenshot and upload to `.candidates/{strategy}/screenshot.png` on the PR branch
2. **Dashboard** — Enumerate candidate screenshots from the PR branch and display them in the PR details panel

### Key Files

- `src/VirtualDevTeam.Core/Strategies/StrategyOrchestrator.cs`
- `src/VirtualDevTeam.Core/Strategies/CandidateEvaluator.cs`

---

## 5. ScriptedCopilotCli

**Priority:** Medium  
**Status:** Partial (fixture loader exists, no runner)  

### Problem

The WS3 offline integration test infrastructure has `FixtureLoader.WriteScriptFile()` which writes agent scripts to a temp JSON file, but there's no actual CLI runner that consumes these scripts. Fixtures define scripted AI responses but can't be executed end-to-end.

### What Needs to Change

1. **Implement `ScriptedCopilotCli`** — An `IChatCompletionService` implementation that matches prompts against fixture scripts and returns pre-recorded responses
2. **Register in test DI** — `WorkflowTestHarness` should optionally wire up `ScriptedCopilotCli` from fixture data
3. **Create test methods** — xUnit `[Theory]` tests that load fixtures, hydrate the harness, run agent loops, and assert expectations

### Key Files

- `tests/VirtualDevTeam.Integration.Tests/Fakes/` — new `ScriptedCopilotCli.cs`
- `tests/VirtualDevTeam.Integration.Tests/Fakes/WorkflowTestHarness.cs` — DI registration
- `tests/VirtualDevTeam.Integration.Tests/Fixtures/FixtureLoader.cs` — already has `WriteScriptFile()`

---

## 6. Cost Attribution

**Priority:** Low  
**Status:** Not Started  

### Problem

When using Copilot CLI as the AI backend (default), token usage reports `$0` because the CLI doesn't expose token counts. The dashboard's cost metrics are meaningless in this mode.

### What Needs to Change

- Option A: Parse Copilot CLI's `--output-format json` JSONL for token metadata (if available)
- Option B: Estimate tokens from prompt/response character counts using a tiktoken-equivalent
- Option C: Surface a "Cost tracking unavailable in Copilot CLI mode" notice on the dashboard

### Key Files

- `src/VirtualDevTeam.Core/AI/CopilotCliChatCompletionService.cs`
- `src/VirtualDevTeam.Core/AI/CliOutputParser.cs`

---

## 7. Multi-PR Mode Hardening

**Priority:** Low  
**Status:** Not Started  

### Problem

Most recent E2E runs used SinglePRMode. The multi-PR mode with parallel SE workers hasn't been validated end-to-end recently. Wave scheduling, file overlap detection, and merge conflict resolution may have regressions.

### What Needs to Change

- Run a full E2E validation with SinglePRMode=false and 2-3 SE workers
- Fix any issues discovered
- Add WS3 fixtures for multi-PR scenarios

---

## 8. Dashboard PR Details Panel

**Priority:** Low  
**Status:** Blocked on #4  

### Problem

Clicking a PR on the Timeline dashboard navigates to the platform (GitHub/Azure DevOps) instead of showing an inline details panel. The panel should show PR metadata, review status, and a candidate screenshot gallery (once #4 is implemented).

### What Needs to Change

1. PR click → opens bottom details popup (not external navigation)
2. Show PR metadata: title, branch, reviewers, labels, status
3. Show winner screenshot with green border
4. Show all candidate screenshots (requires #4 backend)

### Key Files

- `src/VirtualDevTeam.Dashboard/Components/Pages/PullRequests.razor`
- New component: `PRDetailPanel.razor`

---

## 9. Iterative Clarifying Questions ("Ask More")

**Priority:** Medium  
**Status:** Planned (rubber-ducked, 7 todos ready)  

### Problem

The Develop wizard's clarifying questions step generates a single batch of questions. For short/vague project descriptions, users may want more probing — but the current "Regenerate" button replaces all questions. Users need the ability to iteratively request additional unique questions.

### What Needs to Change

1. **Config** — Add `MaxWizardQuestionIterations` (default: 3) to `LimitsConfig` — named distinctly from `MaxClarificationRoundTrips` (agent-to-agent)
2. **Persistence** — Add `ClarifyingIterationCount`, `ClarifyingSourceHash` to `DevelopSettings`; `Iteration` to `ClarifyingQA` record
3. **Prompt** — Create `prompts/wizard/clarifying-questions-more.md` with `{{existingQuestions}}` exclusion list
4. **Core Method** — New `AskMoreQuestionsAsync()` with guard flag, JSONL parsing, normalized dedup (exact-match + token overlap >70%), append behavior
5. **UI** — Split loading states (`_initialClarifyLoading` vs `_askMoreLoading`), "💬 Ask More Questions" button with Round X/Y badge, "🔄 Regenerate" with confirm if answers exist, hard cap 30 questions
6. **Config Page** — "Wizard Question Iterations" number input (min 1, max 10) on Configuration page
7. **Visual Grouping** — Round dividers using `Iteration` property, scroll to new section after Ask More

### Key Design Decisions (from rubber-duck critique)

- **Append** new questions (not prepend) to avoid reordering disruption
- **Split loading states** so existing questions stay visible during "Ask More"
- **No fire-and-forget** — use `async Task` handlers with guard flag
- **`ClarifyingSourceHash`** ties state to description; mismatch invalidates all state
- **Dedup** via prompt exclusion (primary) + normalized exact-match + 70% token overlap (safety net)

### Key Files

- `src/VirtualDevTeam.Dashboard/Components/Pages/Develop.razor`
- `prompts/wizard/clarifying-questions-more.md` (new)
- `src/VirtualDevTeam.Core/Configuration/DevelopSettings.cs`
- `src/VirtualDevTeam.Core/Configuration/VirtualDevTeamConfig.cs`

---

## 10. Testing Dashboard Page

**Priority:** Low  
**Status:** Hidden (nav item commented out)  

### Problem

The Testing page (`/testing`) with Preview Build and Test Artifacts tabs is not fully validated. The nav menu item is currently hidden via a Razor comment in `NavMenu.razor` to avoid confusing users.

### What Needs to Change

1. Validate Preview Build tab (clone, build, run with streaming output)
2. Validate Test Artifacts tab (screenshot/video/trace browsing from agent workspaces)
3. Uncomment the Testing nav item in `NavMenu.razor` once validated
4. Ensure port auto-selection and token redaction work correctly

### Key Files

- `src/VirtualDevTeam.Dashboard/Components/Layout/NavMenu.razor` (nav item is commented out)
- `src/VirtualDevTeam.Core/Preview/PreviewBuildService.cs`
- `src/VirtualDevTeam.Core/Preview/TestArtifactIndexService.cs`

---

*This document is updated as items are completed or new work is identified.*
