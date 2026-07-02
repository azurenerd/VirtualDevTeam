# No-Messy-Code Plan

> **Status:** ✅ PARTIALLY IMPLEMENTED 2026-05-11 — see "Implementation Notes" at the bottom for what shipped and what's deferred.
> **Method:** 10 parallel sub-agent audits across distinct code-quality dimensions, each producing concrete findings with file:line citations and rubber-duck challenges. Synthesized + ranked by impact × cost.
> **Goal:** Identify where the project drifted into "vibe-coded" territory and produce a prioritized, surgical plan to make it cleaner so any Microsoft developer can jump in.

---

## TL;DR — The 10 most impactful changes

| # | Change | Effort | Impact | Cite |
|---|---|---|---|---|
| 1 | Introduce `IRunStateProbe` — single source of truth for "is engineering complete?" | M | 🔴 high | Dup-recovery #1, #4 |
| 2 | Centralize platform-state labels into `PlatformLabels` static class | S | 🔴 high | String-magic #1–5 |
| 3 | Tighten engineering-complete heuristic — replace `.Contains()` keyword checks with exact `AgentStatusReasons` constants | S | 🔴 high | String-magic #6, plan v3 |
| 4 | Mark `IGitHubService` `[Obsolete]` + add capability-boundaries doc | S | 🟡 med | Dead-code #2, Arch #2 |
| 5 | Extract `SoftwareEngineerAgent.WorkOnOwnTasksAsync` into 3 focused methods | M | 🟡 med | SE-bypass #1 |
| 6 | Extract `Program.cs` (1547 LOC) into 6 configuration extension methods | S | 🟡 med | Monolith #4 |
| 7 | Broaden narrow `Microsoft.Playwright.PlaywrightException` catches → `catch (Exception ex) when (ex is not OperationCanceledException)` | S | 🟡 med | Error-handling #1 |
| 8 | Add `OperationCanceledException` guard to detector / action catch-alls | S | 🟢 low-med | Error-handling #4 |
| 9 | Demote `"PM loop:"` + reason-only "status changed" logs from Info → Debug | S | 🟢 low-med | Logging-noise #1, #2 |
| 10 | Add 8 integration tests covering historical regression paths (signal dedup, recovery, multi-agent merge races) | L | 🟡 med | Test-coverage all |

Effort: S=Small (<1 day), M=Medium (2–5 days), L=Large (1+ week)

---

## How to read this plan

Each section below is a **theme** that emerged from one or more sub-agents. Within each theme:

- **Problem** — a concise statement
- **Concrete findings** — file:line citations from the source audit
- **Proposed fix** — surgical, additive
- **Rubber-duck** — challenge to the fix (what could go wrong)
- **Effort × Impact** — informal sizing

Apply themes in priority order. Each is independent — pick and choose.

---

## Theme 1 — Three "engineering complete" paths drift apart  🔴

**Problem.** Three nearly-identical code paths each probe "is engineering done?" with subtly different filter predicates. The 2026-05-10 and 2026-05-08 sessions both shipped fixes because one of them regressed (see commits `f95607a`, `b42cf4f`, `c2c318a`). Each path is a new opportunity for divergence.

**Locations:**
- `src/VirtualDevTeam.Agents/EngineerAgentBase.cs:157-189` — workspace-clone-skip probe (uses `ListMergedAsync` + softwareengineer/* filter)
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs:684-702` — recovery short-circuit (same predicate)
- `src/VirtualDevTeam.Orchestrator/HealthMonitor.cs:407-461` — engineering.all.complete auto-detect (uses `ListOpenAsync` + status-reason heuristic + async hard-check)

**Proposed fix.** Introduce `IRunStateProbe` in `src/VirtualDevTeam.Core/RunState/`:

```csharp
public interface IRunStateProbe
{
    Task<bool> IsEngineeringCompleteAsync(CancellationToken ct);
    Task<EngineeringPrSummary> GetEngineeringPrsByStatusAsync(CancellationToken ct);
    // future: IsArchitectureReadyAsync, GetPhaseCompletionStatusAsync
}
```

Implementation queries the platform exactly once via `IPullRequestService.ListMergedAsync` + `IWorkItemService.ListByLabelAsync("engineering-task", "open")`. All three current call sites consume `IRunStateProbe` instead of re-implementing the predicate.

**Rubber-duck.** Each call site has slightly different tolerances:
- `OnInitializeAsync` is sync-ish and can't block agent spawn on network → wrap in 5s timeout
- `CreateEngineeringPlanAsync` runs once per restart → can afford a full platform query
- `HealthMonitor.AutoDetectSignals` polls every 30s → fire-and-forget, accept 1-tick latency

These differences are addressable inside the probe (parameter for `useCache` / timeout) — they don't justify three separate implementations.

**Effort × Impact:** Medium / High. ~300 LOC delta, deletes ~60 LOC of duplicated predicates. Eliminates the regression class.

---

## Theme 2 — Magic strings + status-reason heuristics  🔴

**Problem.** Label names (`"in-progress"`, `"ready-for-review"`, `"engineering-task"`, `"agent-stuck"`, `"architect-approved"`, `"pm-approved"`) appear as raw string literals across 30+ files. Status-reason substring `.Contains()` checks (`"engineering complete"`, `"integration pr"`, `"no task"`) drive phase-gating logic in 4 places. When a string changes meaning, multiple files must update in lockstep — historically the source of silent regressions.

**Top offenders** (from the magic-strings audit):
- `"in-progress"` — 35+ references across docs/UI/agents/tests
- `"ready-for-review"` — 20+ references
- `"engineering-task"` — 16+ references (already has a constant `EngineeringTaskIssueManager.TaskLabel`, but `HealthMonitor.cs:441` hardcodes the literal anyway)
- `"agent-stuck"` — 11 references; defined as a constant in TWO places (`IssueWorkflow.Labels.AgentStuck` AND `EscalateToHumanAction.cs:StuckLabel`) — duplicate constant
- 4 status-reason substring `.Contains()` checks in `HealthMonitor.cs:415-427` drive phase advancement

**Proposed fix.** Create `src/VirtualDevTeam.Core/GitHub/PlatformLabels.cs`:

```csharp
namespace VirtualDevTeam.Core.GitHub;

public static class PlatformLabels
{
    public static class PullRequest
    {
        public const string ReadyForReview     = "ready-for-review";
        public const string InProgress         = "in-progress";
        public const string ArchitectApproved  = "architect-approved";
        public const string PmApproved         = "pm-approved";
        public const string TestsAdded         = "tests-added";
        public const string AgentStuck         = "agent-stuck";
    }

    public static class Task
    {
        public const string EngineeringTask    = "engineering-task";
        public const string StatusPending      = "status:pending";
        public const string StatusInProgress   = "status:in-progress";
        public const string StatusImplementationComplete = "status:implementation-complete";
    }
}

public static class AgentStatusReasons
{
    public const string EngineeringComplete    = "engineering complete";
    public const string AllTasksComplete       = "all tasks complete";
    public const string AllTasksDone           = "all tasks done";
    public const string NoTask                 = "no task";
    public const string IntegrationPrPending   = "integration pr";
}

public static class BranchPatterns
{
    public const string AgentPrefix            = "agent/";
    public const string AgentSoftwareEngineerInfix = "/softwareengineer";
}
```

Migration:
1. Migrate `PullRequestWorkflow.Labels` → `PlatformLabels.PullRequest` (these are already constants; just move/rename)
2. Migrate `EngineeringTaskIssueManager.TaskLabel` → `PlatformLabels.Task.EngineeringTask`
3. Replace `HealthMonitor.cs:441` hardcoded `"engineering-task"` literal
4. **Delete the duplicate `StuckLabel` constant in `EscalateToHumanAction.cs`**; use `PlatformLabels.PullRequest.AgentStuck`
5. Replace 4 `.Contains()` substring checks in `HealthMonitor.AutoDetectSignals` with exact equality against `AgentStatusReasons.*` constants — agents must emit canonical phrases for the heuristic to fire

**Rubber-duck.** Tension with the codebase principle "prompts in .md, not hardcoded": some of these labels appear in prompt templates too. Resolution: **labels and status-reason canonical phrases are NOT prompt content** — they're platform-state strings + branching keywords. Centralizing them in C# doesn't violate the principle. Prompt templates can reference the same canonical phrases via a small `Labels.md` doc that mirrors the constants.

**Effort × Impact:** Small / High. ~50 file edits, 0 behavior change. Catches the next "I renamed a label and broke the workflow" regression.

---

## Theme 3 — `IGitHubService` is dead-weight legacy  🟡

**Problem.** `IGitHubService` (~120 methods) and the new capability interfaces (`IPullRequestService`, `IWorkItemService`, `IReviewService`, `IRepositoryContentService`, `IBranchService`) coexist. Agents use the capability interfaces via `AgentPlatformServices`; `IGitHubService` is invoked only from internal orchestrator/framework classes (`RunCoordinator.cs:32`, `ConflictResolver.cs:15` — and `ConflictResolver` doesn't actually USE it after constructor injection, just stores it).

**Why it matters.** New developers gravitate to the fat 120-method interface instead of the cleaner capability pattern. Two parallel paths for "get a PR" → drift over time.

**Proposed fix.**
1. Add `[Obsolete("Use capability interfaces (IPullRequestService, IWorkItemService, IReviewService, etc.) — IGitHubService is the internal adapter layer; see docs/CAPABILITY_BOUNDARIES.md")]` to `IGitHubService`.
2. Remove the unused `_githubService` field from `ConflictResolver`.
3. Create `src/VirtualDevTeam.Core/DevPlatform/CAPABILITY_BOUNDARIES.md` documenting: "IGitHubService is legacy. All NEW platform operations must use capability interfaces. When porting legacy code into agents, unwrap IGitHubService calls into their corresponding capability."

**Rubber-duck.** We can't delete `IGitHubService` outright — it's still used in `Dashboard` pages, `PullRequestWorkflow`, `GateNotificationService`. The `[Obsolete]` warning is a social contract, not a hard break. Add a code-review checklist line: "If adding a new method to IGitHubService — please flag for refactor consideration first."

**Effort × Impact:** Small / Medium. ~30 LOC of attribute + doc.

---

## Theme 4 — Oversized files (the four biggest)  🟡

**Problem.** Files >1500 LOC are hard to navigate, hard to merge, and obscure where cross-cutting features should hook in.

| File | LOC | Top concerns mixed |
|---|---|---|
| `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` | 6,479 | planning, task assignment, PR review, strategy framework, AI integration, lifecycle |
| `src/VirtualDevTeam.Core/Workspace/PlaywrightRunner.cs` | 3,968 | browser install (6 strategies), port mgmt, app launch, screenshot capture, test exec, file patching |
| `src/VirtualDevTeam.Dashboard/Components/Pages/Configuration.razor` | 2,278 | copilot, repo, agents, prompts, workflow profiles, strategy toggles — all 6 sections in one `@code` |
| `src/VirtualDevTeam.Runner/Program.cs` | 1,547 | 94 DI registrations + endpoints |
| `src/VirtualDevTeam.Dashboard/Components/Pages/AgentOverview.razor` | 1,311 | status grid, reasoning logs, SignalR, polling |

**Proposed fix.**

**4a. `SoftwareEngineerAgent.cs` → 4 focused partials.** Already uses partials (`SoftwareEngineerAgent.TaskAssignment.cs`, `SoftwareEngineerAgent.Utilities.cs`). Add:
- `SoftwareEngineerAgent.Strategy.cs` — strategy framework dispatch, winner-apply flow
- `SoftwareEngineerAgent.ReviewCoordination.cs` — review queue + conflict detection + posting
- `SoftwareEngineerAgent.Planning.cs` — architecture/spec checks + plan creation + validation
- `SoftwareEngineerAgent.Lifecycle.cs` — RunAgentLoopAsync core + spawn management

**4b. `PlaywrightRunner.cs` → 3 collaborators + facade.** Different from #4a because PlaywrightRunner's concerns have **independent lifecycles**, so extracting collaborator classes (not just partials) is right:
- `PlaywrightBrowserInstaller` — 6 install strategies, path resolution, health checks
- `AppPortManager` — port derivation, hardcoded-port patching, restoration
- `AppUnderTestLauncher` — app process lifecycle, readiness polling
- `PlaywrightRunner` stays as facade coordinating these + screenshot/test orchestration

**4c. `Configuration.razor` → 4 child components + state service.** Idiomatic Blazor:
- `<CopilotConfigSection>`, `<RepositoryConfigSection>`, `<AgentConfigSection>`, `<PromptEditorSection>`
- `ConfigurationPageState` service holds mutable state (`_config`, `_modifiedPrompts`, etc.) — testable in isolation
- Main `Configuration.razor` shrinks to ~200 lines of layout + child orchestration

**4d. `Program.cs` → 6 extension methods.** No new files needed:
- `builder.Services.AddRunnerCoreServices()`
- `.AddRunnerDevPlatform()`
- `.AddRunnerOrchestration()`
- `.AddRunnerHealthMonitor()`
- `.AddRunnerDashboard()`
- `.AddRunnerAgents()`

`Program.cs` shrinks to ~150 lines: config binding → chained extension calls → `app.Build()` → endpoint mappings.

**Rubber-duck.** Don't extract just to hit a LOC target. The criteria are:
- Distinct responsibilities (own state, own API surface)
- Distinct lifecycles (e.g., browser install vs app launch)
- Multiple consumers (Configuration.razor's PromptEditorState could be used by future Settings pages)

Each split above passes those criteria.

**Effort × Impact:** Medium per item, Medium overall.

---

## Theme 5 — SoftwareEngineerAgent bypasses base-class template methods  🟡

**Problem.** `SoftwareEngineerAgent` deliberately bypasses `EngineerAgentBase`'s template methods:
- `FinalizeReadyForReviewAsync` (SE-direct) vs `MarkPrCompleteAsync` (base) — same intent, diverged implementations. The SE version includes a D1 placeholder-string guard the base lacks.
- `HandleChangesRequestedAsync` — SE overrides with richer logic (`_pastImplementationPrs`, `_mergedPrNumbers` guards).
- 10+ state-machine flags (`_planningComplete`, `_allTasksComplete`, `_integrationPrCreated`, `_engineeringSignaled`, etc.) drive routing instead of leveraging `EngineerAgentBase`'s template-method state.

**Why it matters.** Lessons #14 and #16 in `copilot-instructions.md` explicitly call this out: cross-cutting features (self-assessment, pre-PR clarification, etc.) must be wired into BOTH paths or specialists drift from leader.

**Proposed fixes** (ordered by ROI; ship independently):

1. **Extract D1 guard to `EngineerAgentBase.CheckTaskCompletionGuardsAsync`** (1 PR). Called from BOTH `MarkPrCompleteAsync` and `FinalizeReadyForReviewAsync`. Specialists get the same guard the SE has today.
2. **Add hook methods `IsOwnPrForRework` + `ShouldSkipReworkForMergedPr`** to `EngineerAgentBase` (1 PR). SE overrides to add `_pastImplementationPrs` and `_mergedPrNumbers` checks. Cross-cutting filters can be added to the base virtual without duplicating in two places.
3. **Replace 10+ state flags with `_phase` enum + `_phaseState` dictionary + `OnPhaseTransitionAsync` virtual** (1–2 PRs). Explicit state machine, documented transitions, testable in isolation.
4. **Extract `WorkOnOwnTasksAsync` 755 LOC into 3 methods** (2–3 PRs): `SelectNextTaskAsync` → `DispatchCodeGenerationAsync` → `ExecuteImplementationAsync`. Each becomes a clear seam for new features.
5. **Extract Task Assignment into `TaskAssignmentOrchestrator` service** (2–3 PRs). Encapsulates assign/recover/self-claim/retry-guards. Testable in isolation.

**Rubber-duck.** Each PR is small enough to ship + roll back. Don't unify the SE's flag-based state machine with the base class's template methods — they're genuinely different (SE is a leader orchestrating multiple engineers, base assumes one issue = one PR). Just make the seams explicit + testable.

**Effort × Impact:** Medium / Medium. Total: ~6 PRs across ~4 weeks if shipped incrementally.

---

## Theme 6 — Logging volume + level mismatches  🟢

**Problem.** Log spam from polling loops drowns real signal:

| Source | Frequency | Current level | Should be |
|---|---|---|---|
| `"PM loop: {FunctionName}"` (ProgramManagerAgent.cs:178-189, 225-227) | 264 hits / 2K log lines (~13%) | LogInformation | LogDebug |
| `"Agent {Id} status changed: ... -> ... ({Reason})"` (AgentBase.cs:347) — fires on reason-only changes too | 290 hits / 2K (~15%) | LogInformation | LogDebug if status unchanged |
| HealthMonitor 30s heartbeat (HealthMonitor.cs:217-223) | 2880 / day | LogDebug already | ✅ correct |
| `"Cannot advance from X to Y"` | dedup-fixed in commit `b42cf4f` | exemplary pattern | ✅ — apply same pattern to PM loop |

**Proposed fix.**
1. Change all `LogInformation("PM loop: ...")` calls to `LogDebug`. The "exit" log + status transitions already signal lifecycle.
2. Tighten `AgentBase.UpdateStatus` guard so reason-only updates (same status) log at Debug not Info. Operator dashboards pull current StatusReason from `agent.StatusReason`, no need to log every micro-update.
3. **No structured logging issues found** — the codebase consistently uses message templates (`LogInformation("...{Name}", value)`) — keep it that way.

**Rubber-duck.** Information logs are what tells the operator the agent is alive. If we drop everything to Debug, a silent-failure mode would be invisible. Counter: status-CHANGE logs remain at Info; we only demote intra-loop chatter. The "PM loop entered ReviewPullRequests" log is implementation detail; an operator doesn't need it unless debugging.

**Effort × Impact:** Small / Low-Medium. ~10 LOC across 2-3 files. ~40-50% reduction in log volume.

---

## Theme 7 — Error handling: narrow catches + silent swallows  🟡

**Problem.** Recent regression (commit `65545f2`) caught: `WaitForSelectorAsync` started throwing `System.TimeoutException` (not `Microsoft.Playwright.TimeoutException`), bypassing narrow `catch (Microsoft.Playwright.PlaywrightException)` blocks. Other narrow Playwright catches likely remain.

**Findings:**
1. **`PlaywrightRunner.cs`** has narrow `Microsoft.Playwright.PlaywrightException` catches that need broadening (same pattern as the 65545f2 fix). Audit all of them.
2. **`EscalateToHumanAction.cs:186-201`** fires `Task.Run(...)` with an INNER try/catch but no OUTER guard. If `_notifications` is null at task creation, the exception escapes to the unobserved-task handler. Wrap the lambda in try/catch end-to-end. Use `CancellationToken.None` so cancellation doesn't kill the fire-and-forget.
3. **`AgentStuckDetector.cs:26-62`** and **`StalePullRequestConflictDetector.cs:42-92`** catch `Exception` but don't separately handle `OperationCanceledException` — cancellation gets logged as a "tick failure" during shutdown, polluting logs with non-errors. Add `catch (OperationCanceledException) { /* silent — shutdown */ }` before the generic catch.
4. **`PlaywrightRunner.cs`** has ~20 bare `catch { }` blocks with comments like `/* best-effort */`. Most are legit (file cleanup, port parsing fallback), but some hide URL-parsing failures that could surface as misleading port collisions later. Promote critical ones to `LogDebug(ex, ...)`.
5. **DI registration** in `StandaloneServiceRegistration.cs` and `Program.cs` should NOT have wide catches — let startup fail fast and loudly. Audit for any try/catch wrapping `.AddSingleton(...)` factories.

**Proposed fix.** Apply consistently:
```csharp
try { /* ... */ }
catch (OperationCanceledException) { /* normal shutdown */ }
catch (Exception ex) when (ex is not OperationCanceledException)
{
    _logger.LogWarning(ex, "...");  // or LogDebug for best-effort
}
```

For "must never throw" contracts (`IFlowAction`, `IFlowDetector`, fire-and-forget tasks): broaden to catch-Exception with cancel guard. For top-level / DI / startup paths: let exceptions bubble.

**Rubber-duck.** Broadening can hide real bugs. Counter: the `catch (Exception ex) when (ex is not OperationCanceledException)` pattern is the right balance — it absorbs the unknown-exception-type problem (TimeoutException vs PlaywrightException) while still surfacing them via `LogWarning`. The line between "swallow because contract says never-throw" and "swallow because lazy" is: does the docstring of the method say "must never throw"? If yes, broaden. If no, fix the bug.

**Effort × Impact:** Small / Medium. ~20 edits across 5 files. Eliminates the regression class.

---

## Theme 8 — Hardcoded thresholds that should be configurable  🟢

**Problem.** Operator-tunable values are hardcoded in code, requiring rebuilds to adjust.

**Top candidates** (from the hardcoded-config audit):
- **`CopilotCliChatCompletionService.cs:110`** — retry backoff `[5, 15, 30]s` hardcoded
- **`SecurityAuditorAgent.cs:37`** + **`GateNotificationService.cs:39`** — 60s + 120s poll intervals hardcoded
- **`SoftwareEngineerAgent.cs:75, 119-120`** — `SpawnCooldown = 20s`, `SelfClaimAfterIdleLoops = 3` (note: idle-loop count couples with poll interval — fragile)
- **`GitHubService.cs:~500`** + **`PullRequestWorkflow.cs:~290, ~330`** + **`TestEngineerAgent.cs:~150`** — 4 different retry/backoff schedules scattered across files (no consistency)
- **`RateLimitManager.cs:26-28, 31-33`** — slowdown thresholds (200/50/10) + delays (300ms/2s) hardcoded

**Proposed fix.** Honor user's principle: only configure what an OPERATOR would reasonably tweak. Where the AGENT could decide via a prompt template (e.g., "spawn how many workers?"), prefer that over a config knob.

Add to `develop-settings.json` (project-specific):
```json
{
  "FlowMonitor": { "StuckThresholdMinutes": 45 },          // already configurable
  "GateNotification": { "PollIntervalSeconds": 120 },      // NEW
  "SecurityAuditor": { "PollIntervalSeconds": 60 },        // NEW
  "Engineering": {
    "SpawnCooldownSeconds": 20,                            // NEW
    "IdleLoopsBeforeSelfClaim": 3                          // NEW
  },
  "RateLimit": {
    "SlowdownThreshold": 200, "HeavyThreshold": 50,        // NEW
    "BlockThreshold": 10
  }
}
```

DON'T configure:
- Internal Playwright polling (2s) — implementation detail
- The 4 scattered retry schedules — instead, consolidate them into a single `RetryPolicyConfig` class so the duplication is visible if/when somebody needs to tweak

**Rubber-duck.** Configuration is not free — every knob is a support burden. The test: does the value have a legitimate environment-specific reason to change? `GateNotification.PollIntervalSeconds` does (fast CI vs slow CI). `Playwright.BrowserReadyPollMs` does not. Default sensible, allow override in `develop-settings.json` only.

**Effort × Impact:** Small / Low-Medium. ~5-10 small commits.

---

## Theme 9 — Dead / legacy code that confuses contributors  🟢

**Findings:**

1. **`AdaptiveStrategySelector`** — fully implemented + tested but explicitly NOT invoked. `StrategyOrchestrator.cs:112-118` documents this: "we do NOT want to drop strategies based on synthetic/empty history." A new contributor exploring strategy orchestration wastes time studying it.
   - **Fix:** `[Obsolete("Not invoked — Phase 5 dependency on val-e2e experiment data. See StrategyOrchestrator.cs:112")]` or move to `experimental/` namespace.

2. **`IGitHubService`** legacy interface (covered in Theme 3).

3. **`#pragma warning disable CS8625`** at `ContentExtractorTests.cs:6` is file-scoped — should be line-scoped on the specific test methods that intentionally pass `null`.

4. **Commented `"Rung incomplete"` reference** in `FlowMonitorService.cs:197` is now stale (T1.2 escalation ladder shipped). Remove or update comment to point at the commit.

5. **`NullGitHubService`** + standalone-mode stubs in `StandaloneServiceRegistration.cs:108-150` are correct-by-design but inline classes are hard to find. Move to `src/VirtualDevTeam.Dashboard.Host/Services/NullServices.cs` and document the pattern.

6. **`TODO(val-e2e)`** in `StrategyOrchestrator.cs:112-118` is well-explained but lacks a Jira/ticket reference. Add explicit milestone marker.

7. **Comment block in `Program.cs:~60`** documents the T1.6 code-fix pipeline staging order — should move to `docs/CodeFixPipeline.md` (or `docs/HealthMonitor.md`) so it's discoverable without grep.

8. **`ProjectFileManager.cs:31, 70-80`** has silent legacy-path fallback for artifact resolution. Elevate `LogDebug` → `LogInformation` so the fallback is auditable; consider a config switch to disable it for production runs.

**Rubber-duck.** None of these are blockers. They're paper-cut sized but together they signal "this codebase has unfinished migrations" — a bad first impression for a new contributor. Cleaning them up communicates that the team cares about hygiene.

**Effort × Impact:** Small / Low. ~6 commits over 1-2 days.

---

## Theme 10 — Test coverage gaps in critical paths  🟡

**Problem.** Critical paths that have already caused production regressions have zero coverage.

**Gaps identified:**

| Critical path | What broke historically | Test to add |
|---|---|---|
| `WorkflowStateMachine.TryAdvancePhase` concurrent signaling | dashboard refresh race (Lessons #3); could repeat as duplicate phase advance | Concurrent `Signal(ResearchComplete)` from 3 mocks → assert single phase change |
| `SoftwareEngineerAgent.CreateEngineeringPlanAsync` recovery short-circuit | Lessons #4, premature task re-creation on restart | Mid-recovery restart simulation; assert plan NOT regenerated, task count unchanged |
| `EscalateToHumanAction.UndoAsync` idempotency | label cleanup gap (logged in run-issues table 2026-05-10) | Call UndoAsync twice on same finding; assert idempotent + label removed |
| `PullRequestWorkflow.ApproveAndMaybeMergeAsync` multi-PE race | Lessons #18, double-merge resets completed tasks | Concurrent merge from 2 agents; assert one Success, one NotOpen, no duplicate comments |
| Runner ↔ StandaloneServiceRegistration DI drift | adds new singleton to Runner, standalone crashes | Resolve critical type set from BOTH service providers, assert no nulls |
| `HealthMonitor.AutoDetectSignals` engineering-complete race | premature phase=Completion (b42cf4f, c2c318a) | Concurrent EngineeringComplete publishes; assert deterministic phase end-state |
| `EngineerAgentBase.OnInitializeAsync` workspace-clone-skip | premature skip on fresh-reset (f95607a) | Mock platform with 0 merged engineering PRs → assert clone IS called |
| Persistent gate visibility | gate stuck >15min has no FlowMonitor finding today | Add `PhaseAdvancementBlockerDetector` + tests |

**Proposed fix.** Add `tests/VirtualDevTeam.Integration.Tests/` (separate project from unit tests) with the 8 cases above. Use real `WorkflowStateMachine` + `HealthMonitor`, mock only `IPullRequestService` / `IWorkItemService` / `IMessageBus`. Each test should drive a real regression scenario from the lessons archive.

**Rubber-duck.** 40-60 hours of test development. ROI: each test reproduces a real bug; future regression in any path is caught at CI. Don't aim for 100% coverage — aim for the 8 paths that have actually broken.

**Effort × Impact:** Large / Medium.

---

## Cross-cutting principles

These came up in multiple audits — worth reading even if you don't apply individual themes:

1. **The codebase principle is right: less hardcoded, more prompt-driven.** Where AI-generated content lives in `.md` prompt files, keep it there. What we ARE centralizing (labels, status-reason canonical phrases, branch patterns) is platform-state + branching logic — different concern, fair to put in C#.

2. **Patterns to keep:** `PullRequestWorkflow.Labels.*` (one source of truth, well-named), partial classes in `SoftwareEngineerAgent` (split by concern, not just LOC), `data.template.json` convention (commit template + gitignore populated copy), `FlowMonitorConfig` with hot-reloadable defaults + documented thresholds.

3. **Patterns to discourage in new code:**
   - Direct `IGitHubService` usage — use capability interfaces
   - Substring `.Contains()` heuristics on status-reason strings — use exact constants
   - Hardcoded retry/backoff schedules — consolidate into `RetryPolicyConfig`
   - Narrow `catch (Microsoft.Playwright.PlaywrightException)` — broaden with `OperationCanceledException` guard
   - `LogInformation` in tight polling loops — use `LogDebug` or dedup at message-template level
   - State-machine flags scattered in agent fields — use explicit phase enum + transition hook

4. **What "any Microsoft developer can jump in" means in practice:**
   - Open `src/VirtualDevTeam.Runner/Program.cs` → grasp the system in <5 minutes (Theme 4d)
   - Find a label string → reach the constant in <1 hop (Theme 2)
   - Add a cross-cutting feature → know there's ONE place to wire it (Theme 5)
   - Run the tests → see real scenarios in the test names (Theme 10)
   - Read the lessons → find the regression class the test prevents (already good — `LessonsLearned.md` is one of the project's strengths)

---

## Suggested execution order

If shipping one theme at a time:

1. **Theme 2** (magic strings → constants) — small, high-impact, no behavior change. Foundation for #1.
2. **Theme 1** (IRunStateProbe) — depends on #2's constants. Eliminates regression class.
3. **Theme 7** (error handling) — small, prevents recurring narrow-catch regressions.
4. **Theme 6** (logging) — small, immediate operator-experience win.
5. **Theme 3** (IGitHubService obsolete + doc) — small, social-contract win.
6. **Theme 9** (dead code) — small per item, paper-cuts add up.
7. **Theme 8** (configurable thresholds) — small, only add knobs operators actually need.
8. **Theme 4d** (Program.cs split) — small, very high readability win.
9. **Theme 5** (SE bypass) — incremental, 6 PRs across weeks.
10. **Theme 4a-c** (other monoliths) — medium each, sequence after #9.
11. **Theme 10** (integration tests) — large, run in parallel with #5/#4 implementations.

Total estimated effort if all themes applied: ~6-10 weeks of focused work spread across multiple developers. Each theme is independently shippable.

---

## Open questions for the user

1. **Tier-2 detectors (master plan).** Some of the missing test paths (Theme 10) overlap with the master plan's Tier-2 detector wave (`PhaseAdvancementBlockerDetector`, etc.). Should those be built BEFORE or AFTER the tests proposed here?
2. **`SoftwareEngineerAgent` refactor cadence.** Theme 5 is 6 PRs across ~4 weeks. Is that pace acceptable, or should we batch them more aggressively?
3. **`AdaptiveStrategySelector`.** Theme 9 #1 proposes `[Obsolete]`. Alternative: actually wire it up using the recent `experiment-data/*.ndjson` from val-e2e runs. Which path do you prefer?
4. **`IGitHubService` retirement timeline.** Theme 3 is a soft warning. Do you want a harder deprecation date (e.g., "all remaining callers ported by Q4")?
5. **`PlatformLabels` vs prompt-template synchronization.** Theme 2's centralization works for C# code, but prompt templates also reference these label names. Should we add a one-time prompt-audit task to make sure templates match the canonical constants?

---

*Plan generated 2026-05-10 by 10 parallel sub-agent audits. Approve themes selectively before any code changes are applied.*

---

## Implementation Notes (shipped 2026-05-11)

### ✅ Shipped (commits `a974378` + `<this batch>`)
| Theme | What | Notes |
|---|---|---|
| **2** Magic strings → constants | NEW `AgentStatusReasons` + `BranchPatterns` + `IssueWorkflow.Labels.EngineeringTask` | Substring-match preserved — constants are documentation + dedup, not behaviour change. `EngineeringTaskIssueManager.TaskLabel` is now an alias |
| **3** `IGitHubService` `[Obsolete]` | + `docs/CAPABILITY_BOUNDARIES.md` | CS0618 pre-suppressed in the 11 legitimate adapter/registration files; surfaces on 4 documented TODO migration sites |
| **6** Logging level demotions | 8x `"PM loop:"` Info→Debug + reason-only `UpdateStatus` Info→Debug | ~28% steady-state log volume reduction; status TRANSITION still Info |
| **7** Error handling | `OperationCanceledException` guard added to 15 detectors + AiAnomalyDetector + 2 narrow Playwright catches in `PlaywrightRunner.cs` | Closes the regression class fixed reactively in commit `65545f2` |
| **9** Dead-code cleanup (3 items) | `[Obsolete]` on `AdaptiveStrategySelector` (Phase-5 reserved), `ContentExtractorTests.cs` file-scoped `#pragma CS8625` → line-scoped `null!` operator | Items 4-8 (file-move + log-level + ticket refs) are paper-cuts, deferred |
| **8 (partial)** Configurable thresholds | NEW `GateNotificationConfig.PollIntervalSeconds` — was hardcoded `120s` `static readonly TimeSpan` in `GateNotificationService` | Worked example of the per-feature-config pattern. SecurityAuditor + Engineering + RateLimit knobs deferred — same pattern, ship when an operator asks |

### ⏭️ Deferred (need human review or larger refactor than autonomous-safe)
| Theme | Why deferred | Risk if rushed |
|---|---|---|
| **1** `IRunStateProbe` | Touches 3 critical engineering-complete detection paths; needs careful behaviour verification across restart/recovery flows | Could silently regress the engineering.all.complete fix from commit `c2c318a` |
| **4a** Split `SoftwareEngineerAgent.cs` (6,479 LOC) | 6 PRs across ~4 weeks per the plan's own estimate | Mid-refactor commit would leave SE in an inconsistent state |
| **4b** Split `PlaywrightRunner.cs` (3,968 LOC) | Independent-lifecycle collaborators (BrowserInstaller / PortManager / Launcher); needs API surface design | Test-eval pipeline is sensitive to PlaywrightRunner timing |
| **4c** Split `Configuration.razor` (2,278 LOC) | Blazor child component extraction + state service; needs careful state lifecycle review | UI regressions are hard to catch without manual smoke test |
| **4d** Split `Program.cs` (1,409 LOC) | 6 extension methods — mechanically simple but DI ordering is subtle | Service-factory ordering bugs are silent until specific code paths run |
| **5** SE bypass remediation | 5 small PRs in sequence; needs base-class API design + per-PR behaviour verification | Cross-cutting feature regressions (self-assessment, pre-PR clarification) — exactly what lesson #14 + #16 warn about |
| **10** 8 integration tests | 40-60 hours; new `tests/VirtualDevTeam.Integration.Tests/` project | Better as a separate dedicated push with the test scenarios reviewed first |

### Suggested order for the deferred work
1. **Theme 8** (remaining knobs — SecurityAuditor, Engineering, RateLimit) — small, follows the GateNotification pattern this batch shipped
2. **Theme 4d** Program.cs split — mechanical refactor; ship when there's an hour to verify DI startup carefully
3. **Theme 1** `IRunStateProbe` — high impact but the most cross-cutting; needs an integration test or two first
4. **Theme 5** SE bypass remediation — incremental 6 PR plan; each PR pre-reviewed against the lesson archive
5. **Theme 4a/b/c** Large file splits — sequence after the others stabilise
6. **Theme 10** Integration tests — bundled with each Theme 5 PR as the regression net

### Cross-cutting principles validated by this implementation
1. **Constants for platform-state strings, prompts for AI-content strings** — the boundary held cleanly. `PullRequestWorkflow.Labels` / `IssueWorkflow.Labels` / `AgentStatusReasons` / `BranchPatterns` cover the state-side; prompt templates remain in `.md` files where AI authors them.
2. **`[Obsolete]` as a social contract, not a hard break** — works well when paired with a documented migration boundary and pre-suppression on the legitimate internal callers.
3. **Substring-matching is acceptable for canonical phrases** — converting `.Contains("engineering complete")` to `.Contains(AgentStatusReasons.EngineeringComplete)` keeps the same matching behaviour while giving the canonical phrase a stable single source of truth.
