# SE Agent Task Tracker — Time Gap Analysis

> **Date:** 2026-05-19  
> **Investigation:** 5 parallel agents (Opus 4.7, GPT-5.5, Sonnet 4.6, Opus 4.6, GPT-5.4)  
> **Symptom:** User sees ~10m32s total wall-clock but task list only accounts for ~7m. Clarification questions visible in agent status but not in task list.

## Executive Summary

The SE agent's `IAgentTaskTracker` coverage has significant gaps. Multiple operations taking 1–120+ seconds run with **no BeginStep/CompleteStep calls**, making them invisible on the dashboard. All 5 investigation agents converged on the same top findings. The ~3 minute gap is fully explained by gaps #1 + #2 + #4 below.

---

## 🔴 Critical Gaps (P0)

### Gap 1 — Pre-PR Clarification LLM: SE Direct Path Fully Untracked

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:3185–3203` |
| **Method** | `WorkOnOwnTasksAsync` |
| **Est. Time** | **60–120s** (premium-tier LLM call; up to ~2min on retry) |
| **Root Cause** | Lesson #16 dual-path parity bug |

The **specialist path** (`EngineerAgentBase.WorkOnIssueAsync:918–929`) correctly wraps the call:
```csharp
// EngineerAgentBase.cs:918 — TRACKED ✅
var clarStepId = TaskTracker.BeginChildStep(Identity.Id, taskId, claimStepId,
    "Generate clarification questions", "Calling LLM…");
try { clarificationContext = await GeneratePrePRQuestionsAsync(...); }
finally { TaskTracker.CompleteStep(clarStepId); }
```

The **SE leader path** calls the same method with **zero tracker coverage**:
```csharp
// SoftwareEngineerAgent.cs:3196 — UNTRACKED ❌
clarificationContext = await GeneratePrePRQuestionsAsync(taskIssue, pmSpec, archDoc, ct);
// ... first tracker call doesn't appear until line 3232
var descStepId = TaskTracker.BeginStep(Identity.Id, task.Id, "Generate PR description", ...);
```

**Fix:** Wrap lines 3189–3203 with `TaskTracker.BeginStep(Identity.Id, task.Id, "Generate clarification questions", ...)` / `finally { CompleteStep(...) }` + `RecordLlmCall` matching the specialist path.

---

### Gap 2 — Workspace Clone in `OnInitializeAsync`: Zero Tracker Calls

| | |
|---|---|
| **File** | `EngineerAgentBase.cs:148–315` (specifically 236–276) |
| **Method** | `OnInitializeAsync` |
| **Est. Time** | **20–90s** (git clone cold start); **2–10s** (worktree add) |

The entire `OnInitializeAsync` method emits `UpdateStatus(AgentStatus.Working, "📁 Setting up workspace")` but makes **zero `IAgentTaskTracker` calls**. This includes:

- **Engineering-done probe** (lines 196–226): 3 sequential GitHub API calls (`ListByLabelAsync`, `ListOpenAsync`, `ListMergedAsync`) — 3–10s
- **`Workspace.InitializeAsync(ct)`** (line 267): actual git clone or worktree add — the single largest cold-start cost (20–90s)
- **State restoration** (lines 283–311): `LoadCliSessionsAsync` + `LoadReworkAttemptsAsync` — 0.5–2s

This is the root cause of the "30s per agent wasted on restart" scenario from Lesson #24.

**Fix:** Add a well-known task ID (e.g., `"initialization"`) with `BeginStep(agentId, "initialization", "Workspace setup", "git clone / worktree init")` wrapping lines 236–276.

---

## 🟠 High Priority Gaps (P1)

### Gap 3 — SE Recovery Sweep: 5 Methods Every Loop, Untracked

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:540–548` |
| **Method** | `RunAgentLoopAsync` |
| **Est. Time** | **10–30s per loop iteration** (5 recovery methods, each makes 1–3 API calls) |

Five sequential recovery methods run **every loop iteration** between tracked steps:
1. `RecoverReadyForReviewPRsAsync`
2. `RecoverOwnInProgressPRAsync`
3. `CheckOwnPrStatusAsync`
4. `RecoverStuckInProgressPRAsync`
5. `RecoverConflictingApprovedPRsAsync`

**Fix:** Add a container step `"Recovery checks"` wrapping lines 540–548.

---

### Gap 4 — Task Selection/Claim Scan: Untracked Per-Loop GitHub Work

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:2781–3168` |
| **Method** | `WorkOnOwnTasksAsync` (entry through claim validation) |
| **Est. Time** | **2–30s per loop** (leader with 5+ tasks) |

Operations before any tracked step:

| Operation | Location | ~Time |
|-----------|----------|-------|
| `_taskManager.LoadTasksAsync(ct)` | Lines 2899, 2925, 3041 (2–3× per iteration) | 200–800ms each |
| `AssignTasksToAvailableEngineersAsync()` | Line 2921 (multiple GitHub label writes) | 500ms–2s |
| `GetCachedMergedPRsAsync(ct)` | Line 2844 / 3139 | 200–500ms |
| `PrService.GetChangedFilesAsync()` loop | Lines 3143–3147 (up to 10 calls) | 2–10s |
| `FindExistingPrForTaskAsync` | Line 3042 | 200–500ms |

**Fix:** Add `BeginStep(agentId, taskId, "Scanning for tasks", ...)` at entry to `WorkOnOwnTasksAsync`, completed before `descStepId` starts.

---

### Gap 5 — `FinalizeReadyForReview`: 60–250s Bundled Under One Step

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:3623–3700` |
| **Method** | `FinalizeReadyForReviewAsync` |
| **Est. Time** | **60–250s total** in a single "Mark ready for review" step |

Hidden sub-operations inside the single step:

| Operation | Line | ~Time |
|-----------|------|-------|
| `RunPrePublishScreenshotCheckAsync` | 3633 | 10–30s |
| `RunPrePublishAssessmentAsync` (fresh AI self-assessment + possible fix loop) | 3641 | **30–180s** |
| `SyncBranchWithMainAsync` (git fetch/merge/push) | 3654 | 5–20s |
| `CheckPrForPlaceholderStringsAsync` (fetches PR file diffs) | 3677 | 2–5s |
| `MarkReadyForReviewWithScreenshotAsync` (Playwright + label update) | 3690 | 5–15s |

**Fix:** Split into sub-steps: self-assessment (with `RecordLlmCall`), sync branch, mark ready. Also add `SetStepWaiting` for any gate waits.

---

### Gap 6 — Branch Setup: `PrepareWorkspaceBranch` Untracked

| | |
|---|---|
| **File** | `EngineerAgentBase.cs:5778–5783` |
| **Method** | `PrepareWorkspaceBranchAsync` |
| **Est. Time** | **10–30s** (git sync + branch create) |

Called before implementation steps begin. `SyncWithMainAsync` + `CreateBranchAsync` takes 10–30s with no tracking.

**Fix:** Add `BeginStep("Setup workspace branch")` wrapping these calls.

---

## 🟡 Medium Priority Gaps (P2)

### Gap 7 — Strategy Evaluation: Zero `IAgentTaskTracker` Usage

| | |
|---|---|
| **File** | `StrategyOrchestrator.cs` (entire file) |
| **Method** | `RunCandidatesAsync`, `EvaluateAsync`, `RunWithRevisionAsync` |
| **Est. Time** | **60–300s** for CLI judge; **5–15 min** for full T-FINAL |

The strategy framework has ZERO `IAgentTaskTracker` calls. It only emits `_events.EmitAsync` SignalR events. The `StrategyTaskStepBridge` translates candidate lifecycle events into tracker steps, but:

- **Evaluation phase** (`EvaluateAsync` — build gates + CLI judge LLM) has no dedicated container step
- **Revision rounds** (rubber-duck + re-execution) are not represented
- **Concurrency gate waits** (when `MaxParallelCandidates = 1`) show no waiting state

**Fix:** In `StrategyTaskStepBridge.EmitAsync`, handle `EvaluationProgress` events to create an "⚖️ Evaluation" child step. Add `StrategyConcurrencyGate.Waiting` event support.

---

### Gap 8 — Gate Polling Shows InProgress Instead of WaitingOnHuman

| | |
|---|---|
| **Files** | Multiple locations |
| **Est. Time** | **0s** (auto-approved) → **minutes–hours** (human gate enabled) |

Gate waits that show `InProgress` instead of `WaitingOnHuman`:

| Gate | Location |
|------|----------|
| SE pre-PR clarification gate | `SoftwareEngineerAgent.cs:3216` |
| EngineeringPlan gate | `SoftwareEngineerAgent.cs:2077` |
| TaskAssignment gate | `SoftwareEngineerAgent.cs:2613` |
| CommitAndNotify PRCodeComplete gate | `EngineerAgentBase.cs:2367` |
| SE `Mark ready for review` (no `SetStepWaiting`) | `SoftwareEngineerAgent.cs:3625` |
| Specialist path clarification gate (label wrong) | `EngineerAgentBase.cs:1769–1792` |

**Fix:** Add `TaskTracker.SetStepWaiting(stepId)` before entering each gate polling loop.

---

### Gap 9 — Engineering Plan Recovery Probe

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:843–913` |
| **Method** | `CreateEngineeringPlanAsync` (early recovery section) |
| **Est. Time** | **5–15s** |

Before any planning step starts:
- `GetCachedMergedPRsAsync` (line 845) — 1–3s
- `WorkItemService.ListByLabelAsync` (line 858) — 1–3s
- `ListByLabelAsync(Enhancement)` (line 905) — 1–2s
- `_taskManager.LoadTasksAsync` (line 913) — 3–10s

Only `restoreStepId` at line 971 starts tracking.

**Fix:** Add `BeginStep("Recovery probe")` wrapping lines 844–913.

---

### Gap 10 — Architecture Polling (Pre-Planning)

| | |
|---|---|
| **File** | `SoftwareEngineerAgent.cs:731–819` |
| **Method** | `CheckForArchitectureAsync` |
| **Est. Time** | **2–8s per poll** (up to 3 API paths) |

Runs every loop iteration before first step. Calls `GetArchitectureDocAsync`, `ListByLabelAsync` (×2-3). Currently invisible until architecture is found.

**Fix:** Add `BeginStep("Checking for architecture doc")` with short-lived step.

---

## Untracked LLM Calls (Secondary)

These LLM calls inside helper methods have no `RecordLlmCall`:

| Method | File | Context |
|--------|------|---------|
| `GeneratePrePRQuestionsAsync` internal LLM | `EngineerAgentBase.cs:1701,1715` | The actual LLM call + retry inside the helper |
| Build-fix / test-fix / remove-tests | `EngineerAgentBase.cs:5225,5317,5550` | Fix-loop LLM calls |
| Self-assessment fix | `EngineerAgentBase.cs:4112` | Follow-up fix after NEEDS_CHANGES |
| Enhancement coverage check | `SoftwareEngineerAgent.cs:2226` | Plan validation LLM |
| Scenario-tag micro-call | `SoftwareEngineerAgent.cs:3784` | Small but adds up |
| Task-plan overlap repair | `SoftwareEngineerAgent.cs:1285–1365` (Utilities) | Up to 2 LLM calls, 20–90s |

---

## Implementation Priority

| Phase | TODOs | Impact |
|-------|-------|--------|
| **Phase 1** (P0) | Gap 1 (SE clarification), Gap 2 (workspace clone) | Fixes 80–210s of invisible time |
| **Phase 2** (P1) | Gap 3 (recovery sweep), Gap 4 (task scan), Gap 5 (finalize sub-steps), Gap 6 (branch setup) | Fixes 30–100s per cycle |
| **Phase 3** (P2) | Gap 7 (strategy eval), Gap 8 (gate waiting state), Gap 9 (plan recovery), Gap 10 (arch polling) | Better status visibility |
| **Phase 4** | Untracked LLM calls | Accurate token/cost tracking |

## Cross-Model Consensus

All 5 models independently identified Gaps 1, 2, 4, and 7 as the most impactful. Gaps 3 and 5 were flagged by 4/5 models. Gap 8 (gate waiting state) was noted by 3/5 models as a UX quality issue rather than a time gap per se.
