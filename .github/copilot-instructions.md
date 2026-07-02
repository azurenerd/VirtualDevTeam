# Copilot Instructions for VirtualDevTeam

> **🚨 MANDATORY SESSION STARTUP:**
> At the start of EVERY new session, before doing any other work, read **`Session.md`** in full and follow its guidance. It is the operator's runbook covering how they like to work, reset workflow, monitoring red flags, and dashboard reference. This is not optional — read it first so you understand the operating context, then proceed with the user's task.
>
> **Companion docs (read on demand):**
> - **`docs/system/LessonsLearned.md`** — full archive of 80+ retrospectives the human operator wrote whenever Copilot needed correction. The most actionable items are condensed in the "Lessons Learned" section near the bottom of this file; reach for the archive when investigating a recurring class of failure.
> - **`docs/system/MonitoringLoops.md`** — checklist for an agent monitoring an active pipeline overnight.

## Build, Test, and Lint

```bash
# Build the entire solution
dotnet build VirtualDevTeam.sln

# Run all tests
dotnet test VirtualDevTeam.sln

# Run a single test project
dotnet test tests/VirtualDevTeam.Core.Tests

# Run a specific test by fully-qualified name
dotnet test tests/VirtualDevTeam.Core.Tests --filter "FullyQualifiedName~MessageBusTests.PublishAsync_DeliversToTargetedSubscriber"

# Run the application
cd src/VirtualDevTeam.Runner && dotnet run
```

**Build caveat:** When the Runner is running, `dotnet build VirtualDevTeam.sln` will fail with MSB3027 file-lock errors for the Runner project. Build individual projects instead (e.g., `dotnet build src/VirtualDevTeam.Core`) to verify code compiles without stopping the Runner.

## Architecture

VirtualDevTeam is a multi-agent AI system where 7 specialized agent roles collaborate through GitHub PRs/Issues (or Azure DevOps Work Items/PRs) and an in-process message bus, coordinated by an orchestrator with phase-gated workflow.

### Project Dependency Graph

```
VirtualDevTeam.Runner (host — Blazor Server on port 5050)
├── VirtualDevTeam.Orchestrator (coordination)
│   └── VirtualDevTeam.Core (shared abstractions)
├── VirtualDevTeam.Agents (concrete agent implementations)
│   ├── VirtualDevTeam.Orchestrator
│   └── VirtualDevTeam.Core
├── VirtualDevTeam.Dashboard (Blazor Server RCL — monitoring UI)
│   └── VirtualDevTeam.Core
└── VirtualDevTeam.McpServer (MCP tool server for external AI tools)

VirtualDevTeam.Dashboard.Host (standalone dashboard — runs UI without Runner)
```

- **Core** — Agent abstractions (`IAgent`, `AgentBase`), message bus (`IMessageBus`), DevPlatform capability interfaces (`IPullRequestService`, `IWorkItemService`, `IReviewService`, etc.), persistence (`AgentStateStore`, `AgentMemoryStore`), configuration models, prompt template engine, Copilot CLI integration, and Semantic Kernel integration. All other projects depend on Core.
- **Agents** — Concrete agent implementations (PM, Researcher, Architect, SE, TE, Custom, SME, Specialist), each extending `AgentBase`. Created via `AgentFactory` using `ActivatorUtilities.CreateInstance<T>`.
- **Orchestrator** — Runtime coordination: `WorkflowStateMachine` (phase-gated progression), `AgentRegistry` (thread-safe lifecycle), `AgentSpawnManager` (dynamic scaling with slot reservation), `DeadlockDetector` (DFS wait-for graph), `HealthMonitor`, `GracefulShutdownHandler`, `RunCoordinator` (single-active-run enforcement).
- **Runner** — Application host. Registers all services via DI in `Program.cs`, bootstraps core agents in phased sequence via `VirtualDevTeamWorker` (a `BackgroundService`). Serves the Dashboard UI directly.
- **Dashboard** — Blazor Server RCL with SignalR push updates. `DashboardDataService` subscribes to `AgentRegistry` events and broadcasts via `IHubContext<AgentHub>`. The standalone `Dashboard.Host` project runs the UI independently using HTTP-based data service.
- **McpServer** — A Model Context Protocol tool server exposing workspace operations to external AI tools via stdio JSON-RPC.

### PlaywrightRunner Decomposition

`PlaywrightRunner` was refactored from a 4766-line monolith into a facade (2603 lines) delegating to extracted services:
- **`AppLauncher`** (`Core/Workspace/`) — App lifecycle management, port selection, companion frontend hosting.
- **App startup detection** — `DetectAppStartCommandFallback` and the CLI app-start prompt explicitly skip likely test projects by path/name and apply a `-200` ranking penalty so startup selects the real app instead of a test host.
- **`MediaRecorder`** (`Core/Workspace/`) — Video recording, GIF conversion, screenshot capture.
- **`ApiSmokeRunner`** (`Core/Workspace/`) — OpenAPI-driven API smoke testing.
- **`IMediaCaptureService`** — Interface abstracting `PlaywrightRunner` for media capture. Registered in DI so non-workspace code can request screenshots/video without depending on `PlaywrightRunner` directly.
- **`MediaCaptureGate`** — Static class with `ShouldCapture()` pre-flight check. Returns false for non-UI tasks to skip expensive MCP/video/GIF pipelines.
- **`CaptureMode`** enum — `ScreenshotOnly` (skips MCP + video + GIF) vs `FullMedia` (runs everything). Selected per-task based on whether the task has visual verification URLs.
- **`CandidatePreviewSource`** — Strategy gallery preview states now distinguish `CaptureUnavailable` (Playwright/browser tooling missing), `CaptureFailed` (app failed to boot or only produced blank shots), and `NoVisualContent` (legit backend-only / non-visual work) so operators can tell setup failures from expected placeholders.

### Strategy Recovery

`StrategyRecoveryStore` provides SQLite-backed checkpoint persistence for the strategy framework. After each candidate is executed, a checkpoint is written with the candidate patch, scores, and base SHA. On restart, `TryRecoverFromCheckpointAsync` resumes evaluation if the `baseSha` matches the current HEAD — avoiding re-execution of expensive strategy candidates. Config: `RecoverOrphanedCandidates` (default `true`). Visual scoring via `ApplyVisualScoresAsync` runs before winner selection sort/pick, ensuring visual quality participates in the final ranking. `CandidateSnapshot` now also records `ProcessId` and `ProcessStartedAt`, and `CandidateStateStore` exposes `RecordProcessStarted` plus `GetStuckCandidates(TimeSpan threshold)` as the foundation for FlowMonitor stuck-candidate detection. Operators can now also reset a running candidate from the `/strategies` page: `OrchestrationCancellationService.RequestCandidateReset` cancels the current process, sets a reset flag, and the orchestrator retries in a fresh worktree/CTS via the escalation ladder (rung 1 = same config, rung 2 = `ForceNoWrapper`). REST endpoint: `POST /api/strategies/reset/{runId}/{taskId}/{strategyId}`. `IStrategiesDataService.ResetCandidateAsync` is implemented by both the in-process and HTTP dashboard services.

### Workflow State Machine

Agents progress through a linear phase pipeline — no backward transitions:

```
Initialization → Research → Architecture → EngineeringPlanning
→ ParallelDevelopment → Testing → Review → Completion
```

Each phase has gate conditions (signals + document readiness) that must be met before advancing. Signals use dot-notation naming (e.g., `"research.complete"`, `"engineering.plan.ready"`).

### Document Flow

The agent pipeline produces shared Markdown documents that each phase builds upon:

```
Project.Description → PM kickoff → Research.md (Researcher)
                                  → PMSpec.md (PM, after ResearchComplete)
                                  → Architecture.md (Architect, after PMSpecReady)
                                  → engineering-task issues (Software Engineer, after Architecture)
                                  → PRs with enriched task context (assigned to engineers)
```

- **PMSpec.md** — Business specification created by the PM from Research.md + project description. Contains: Executive Summary, Business Goals, User Stories & Acceptance Criteria, Scope, Non-Functional Requirements, Success Metrics, Constraints.
- All downstream agents (Architect, Software Engineers) read PMSpec.md for business context alignment.
- The SE's planning phase produces **engineering-task issues** (not a committed EngineeringPlan.md file). Each issue contains implementation details, acceptance criteria, and wave/dependency metadata.

### Dual-Layer Communication

Agents communicate through two complementary layers simultaneously:

1. **In-process message bus** (`InProcessMessageBus` via `System.Threading.Channels`, bounded capacity 1000) — instant agent-to-agent signaling (<1ms), no durability. Messages routed by `ToAgentId` — set to `"*"` or `null` for broadcast. Five message types derive from the `AgentMessage` base record: `TaskAssignmentMessage`, `StatusUpdateMessage`, `HelpRequestMessage`, `ResourceRequestMessage`, `ReviewRequestMessage`.
2. **GitHub API** (Octokit) — durable artifacts (PRs with code, Issues for tasks, Comments for discussion), human oversight, external coordination (100–500ms latency).

An agent typically does both: creates a PR on GitHub AND sends a bus message to notify the PM. The bus is for real-time internal coordination; GitHub is the permanent record.

## Key Conventions

### Agent Implementation Pattern

Agent constructors use **service bundles** to keep parameter lists manageable:

- **`AgentCoreServices`** — Required by every agent. Contains `IMessageBus`, `ModelRegistry`, `IChatCompletionRunner`, `ProjectFileManager`, `AgentMemoryStore`, `IGateCheckService`, config, and optional services (`IPromptTemplateService`, `RoleContextProvider`, `IAgentTaskTracker`, `AgentStateStore`).
- **`AgentPlatformServices`** — For agents interacting with PRs/issues. Contains `IPullRequestService`, `IWorkItemService`, `IRepositoryContentService`, `IReviewService`, `PullRequestWorkflow`, and optional branch/doc services.
- **`AgentWorkspaceServices`** — For engineering agents. Contains `BuildRunner`, `TestRunner`, `PlaywrightRunner`, `BuildTestMetrics` (all optional).

Constructor signature: `AgentIdentity`, `AgentCoreServices`, `AgentPlatformServices` (if platform-interacting), `AgentWorkspaceServices` (if builds/tests), role-specific services, `ILogger<T>` — then calls `base(identity, core, logger)`.

Agent lifecycle:

1. **`OnInitializeAsync`** subscribes to message types via `Core.MessageBus.Subscribe<T>(Identity.Id, handler)`, storing subscriptions in a `List<IDisposable>`.
2. **`RunAgentLoopAsync`** runs a `while (!ct.IsCancellationRequested)` loop with a `Task.Delay` poll interval. Catches `OperationCanceledException` to break cleanly; catches other exceptions, logs them, and retries after a 5-second backoff.
3. **`OnStopAsync`** disposes all message subscriptions.

**Specialist (SME) agents** are dynamically spawned via `AgentSpawnManager` during the ParallelDevelopment phase. `AgentFactory.CreateSme()` routes to `SpecialistEngineerAgent` (for engineer-based templates with full rework/build/test) or `SmeAgent` (for custom templates). The `SMEAgentDefinition` record includes `SpawnedDisplayName` for identity persistence across restarts and slot reservation for concurrency limits.

RunScope PR filtering is now centralized in `PullRequestWorkflow.IsCurrentRunScopePr(headBranch, prBody, runScope)`. `EngineerAgentBase`, `TestEngineerAgent`, `FindExistingPullRequestAsync`, and `ProjectTimeline` all delegate to this utility; when adding new RunScope-aware filtering, use it instead of inline `HeadBranch.Contains(...)` checks.

For targeted recovery, `AgentSpawnManager.RespawnAgentAsync(agentId)` stops one agent and recreates it with the same identity. The Dashboard exposes this via `POST /api/dashboard/agents/{agentId}/restart` and a 🔄 restart button on agent cards with two-click confirmation, which is lighter-weight than a full runner warm restart.

### Multi-Turn AI Conversations

Agents use `IChatCompletionRunner` (wraps Semantic Kernel's `IChatCompletionService`) for stateful multi-turn conversations. The pattern is:
- Get a kernel via `Core.ModelRegistry.GetKernel(Identity.ModelTier)` (tiers: premium, standard, budget, local)
- Build turns: system message (role definition) → user prompt → assistant response → follow-up user prompt → etc.
- Turn counts vary by agent complexity (Researcher: 3, Architect: 5, Software Engineer: 3)

### CLI-Native Review Architecture

`ICliReviewService` (in `Core/Review/`) provides CLI-based code review by launching Copilot CLI agentic sessions pointed at local worktrees. The reviewer browses files, runs builds/tests, and makes assessments directly — eliminating truncation issues from serializing code into LLM prompts.

**Review types** (`ReviewType` enum):
- `Judge` — Scores code 0–10 on acceptance criteria, design, readability (used by strategy framework's `CliNativeJudge`)
- `ArchitectReview` — Architecture alignment review with inline comments
- `PMReview` — Business alignment review with approval decision
- `PeerReview` — Engineer code review with inline comments
- `TestReview` — Test coverage gap analysis
- `Rework` — Adversarial critique generating targeted improvement suggestions

**Implementation:** `CliReviewService` (in `Agents/AI/`) launches the CLI with `--allow-all` and working directory set to the review path, then pipes role-specific instructions. Results include `ReviewBody`, `InlineComments`, `ReviewDecision`, and `CandidateScore` (for judge reviews).

### CLI Edit Mode for Engineer Rework

Engineers can use Copilot CLI's native edit tools for rework instead of FILE: block parsing. Dedicated prompt templates exist for this mode:
- `prompts/engineer-base/rework-system-cli-edit.md` — System prompt for CLI edit rework
- `prompts/engineer-base/rework-user-cli-edit.md` — User prompt for CLI edit rework
- `prompts/engineer-base/build-fix-cli-edit.md` — Build fix using CLI edit

This mode leverages the CLI's built-in file editing capabilities, producing more reliable edits than parsing FILE: blocks from raw LLM output.

### DevPlatform Abstraction (Multi-Platform Support)

Agents code against **capability-based interfaces** in `Core/DevPlatform/Capabilities/`, not platform-specific types:

| Capability | GitHub Provider | ADO Provider | Local Provider |
|-----------|----------------|-------------|----------------|
| `IPullRequestService` | `GitHubPullRequestAdapter` | `AdoPullRequestService` | `LocalPullRequestService` |
| `IWorkItemService` | `GitHubWorkItemAdapter` | `AdoWorkItemService` | `LocalWorkItemService` |
| `IReviewService` | `GitHubReviewAdapter` | `AdoReviewService` | `LocalReviewService` |
| `IRepositoryContentService` | `GitHubRepositoryContentAdapter` | `AdoRepositoryContentService` | `LocalRepositoryContentService` |
| `IBranchService` | `GitHubBranchAdapter` | `AdoBranchService` | `LocalBranchService` |

Platform models (`PlatformPullRequest`, `PlatformWorkItem`, `PlatformFileDiff`, etc.) in `Core/DevPlatform/Models/` are platform-neutral. Provider adapters in `Core/DevPlatform/Providers/{GitHub,AzureDevOps,Local}/` map to/from Octokit, ADO REST types, or the local SQLite + bare-git backing store.

**Important:** Never use `IGitHubService` directly for new agent work. Use the capability interfaces via `AgentPlatformServices` instead. `IGitHubService` is the legacy layer that the GitHub adapters wrap.

### LocalDevPlatform

`DevPlatformKind.Local` is a third provider for enterprise repos where agents cannot create or merge PRs on the real platform directly. In this mode, PRs, reviews, work items, and merges are stored in a local SQLite database plus a local bare git repo managed by `LocalBareRepoManager` at `.agents/local-platform/{repo}.git`.

Core services live under `Core/DevPlatform/Providers/Local/`: `LocalPullRequestService`, `LocalWorkItemService`, `LocalReviewService`, `LocalBranchService`, `LocalRepositoryContentService`, `LocalRepositoryManagementService`, and `LocalPlatformInfoService`. `LocalPlatformSchema` defines the run-scoped schema, `LocalPlatformContext` carries the shared connection/run/default-branch state, and `LocalPlatformInitializer` is the `IHostedService` that provisions local platform state at startup.

`LocalPullRequestService.CreateAsync` always adds the `AI-Generated` label (required by the Timeline page). Final handoff back to the real platform goes through `IFinalSubmissionService`; `GitHubFinalSubmissionService` creates the final PR for human review, while `NoOpFinalSubmissionService` is used when no final submission step applies.

`LocalBareRepoManager.MergeBranchAsync` now mirrors the GitHub provider's conflict-resolution pattern with a rebase fallback: on merge conflict it creates a temporary worktree, rebases the source branch onto the target, then retries the merge.

For LocalPlatform diffs, `LocalPullRequestService.GetFileDiffsAsync` now populates `PlatformFileDiff.Patch` on demand via `LocalBareRepoManager.GetFilePatchAsync(baseRef, headRef, filePath)`. This fixes `SecurityAuditor` reviews on local PRs, which previously saw empty diffs because `Patch` was always null.

### GitHub Conventions

- **PR titles**: `{AgentDisplayName}: {TaskTitle}` (e.g., `"Software Engineer 1: Implement auth"`)
- **PR branches**: `agent/{name}/{task-slug}` (e.g., `agent/software-engineer-1/implement-auth`)
- **Issue titles**: `{TargetAgent}: {Title}` or `Executive Request: {Title}`
- **Labels**: `in-progress`, `ready-for-review`, `blocker`, `agent-stuck`, `executive-request`, `resource-request`, `agent-question`, `awaiting-human-review`, `human-approved`, plus complexity labels
- Agents parse their name from PR/issue titles to find their assigned work

### VDT Development PR Descriptions

When creating PRs on the VDT codebase itself (the `behumphr` → `main` workflow), always write **detailed, impact-focused descriptions**. The PR description is the permanent record of WHY changes were made and WHAT effect they have. A reviewer (or future contributor) should understand the full scope without reading every diff line.

**Required sections:**
1. **Summary** — one-paragraph overview of the change set
2. **Changes** — itemized list of each change with:
   - **What** was changed (file/component/system)
   - **Why** it matters (the problem it solves or the capability it adds)
   - **Impact** (what breaks if this is missing, what improves with it)
3. **Root causes** — for bug fixes, explain the root cause (not just the symptom)
4. **Testing** — how changes were verified (even if "not compiled yet — code review only")

**Example format:**
```markdown
## Summary
Adds intelligent Playwright testing and fixes false-positive FlowMonitor alerts.

## Changes

### Intelligent Playwright Interaction Testing
- **DiffAnalyzer** (`Core/Workspace/`): Extracts routes, components, and form elements from git diffs using regex. **Impact**: Without this, Playwright tests are generic page-screenshot walkthroughs that never interact with the actual UI features built in the PR.
- **InteractionPlanGenerator**: LLM-based plan generation with safety filters. **Impact**: Prevents unsafe actions (credential injection, destructive clicks) while enabling form-fill testing.
- **PlaywrightRunner prompt fix**: SafeWrite rules no longer conflict with submit/save prohibitions. **Impact**: Previously, the prompt told the agent to fill wizard forms AND simultaneously forbade clicking Submit — making wizard testing impossible.

### FlowMonitor False-Positive Fix
- **StrategyEvaluationStuckDetector**: All 3 conditions now check agent log activity before firing. **Root cause**: Detector used only CandidateStateStore timing, ignoring active LLM calls during scoring phase — caused spurious "media capture stuck" alerts while scoring was actively running.
```

### Thread Safety

- `AgentRegistry` uses `ConcurrentDictionary` for lock-free reads with `TryAdd`/`TryRemove` for atomic mutations.
- `WorkflowStateMachine` and `AgentSpawnManager` use `lock` for state transitions and slot reservation.
- `DeadlockDetector` snapshots its `ConcurrentDictionary` before DFS traversal.
- `AgentBase.Status` is guarded by a dedicated `_statusLock`.

### Configuration

All static config lives under the `VirtualDevTeam` section in `appsettings.json`, bound via `IOptions<VirtualDevTeamConfig>`. Key sections: `Project` (GitHub repo/PAT), `Models` (provider/tier definitions), `Agents` (per-role tier assignments), `Limits` (scaling caps, timeouts, poll intervals), `Dashboard` (port, SignalR toggle). `appsettings.json` is committed — never put secrets in it; use `dotnet user-secrets` instead.

**Runtime configuration** comes from `develop-settings.json` (gitignored, per-user), created by the Develop wizard. This file contains the active project settings (repo, auth method, gate preferences, work item generation mode) and is the source of truth at runtime — NOT `appsettings.json`. `RunCoordinator.ReconfigureServicesForRepoAsync` applies wizard settings to the runtime `IOptions` at project start and recovery. The `appsettings.json` project defaults are intentionally blank; all project-specific settings come from the wizard.

`develop-settings.json` now also carries `devPlatformKind`, which can be `GitHub`, `AzureDevOps`, or `Local`. The Develop wizard exposes this choice; `Local` routes agent PR/review/work-item activity through LocalDevPlatform and uses `IFinalSubmissionService` to publish the final submission back to the real platform.

**Authentication methods** (`DevPlatformAuthMethod`):
- `GhCli` (default) — Uses `gh auth token` for dynamic token resolution via `GhCliAuthProvider`. Ideal for EMU (Enterprise Managed User) accounts where PATs are restricted. No token stored in config.
- `Pat` — Static Personal Access Token stored in user secrets or config.
- `AzureCliBearerToken` — Azure CLI bearer token for Azure DevOps (auto-refreshed 5 minutes before expiry).

**Notable configuration defaults:**
- `CopilotCli.ReasoningEffort`: `high` (was `low`)
- `CopilotCli.FastModeModel`: `claude-haiku-4.5`
- `HumanInteraction.Gates.ArchitectureDesign.RequiresHuman`: `true`
- Self Assessment (renamed from "Agentic Loop"): `Enabled` toggle + `MaxIterations` setting

### Workspace Configuration

Agent workspaces default to `.agents/` (relative to project root). `WorkspaceConfig.ResolveRootPath()` resolves relative paths at startup via `PostConfigure<VirtualDevTeamConfig>` in `Program.cs`. The `.agents/` folder is gitignored. Reset scripts (`fresh-reset.ps1`, `minimal-reset.ps1`, `reset-runner.ps1`) read `Workspace.RootPath` from `appsettings.json` and resolve relative paths against the Runner directory — never hardcode workspace paths.

`WorktreeWorkspace` now has `AbortInProgressOperationsAsync`, matching `LocalWorkspace` stale-state cleanup. `SyncWithMainAsync` calls it up front to probe for and abort leftover `rebase-merge`, `rebase-apply`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`, and `REVERT_HEAD` state before syncing.

### Prompt Templates

All agent prompts (~100 templates) live in editable `.md` files under `prompts/{role}/`, organized by agent role:

```
prompts/
├── pm/                    # PM agent prompts (spec, review, clarification, etc.)
├── researcher/            # Research prompts (multi-turn, quick, revision)
├── architect/             # Architecture prompts (multi-turn, review)
├── software-engineer/     # SE leader prompts (plan, implementation, review)
├── engineer-base/         # Shared prompts for all engineer variants (incl. self-assessment)
├── specialist-engineer/   # Specialist engineer overrides
├── test-engineer/         # TE prompts (test gen, classify failures, rework)
├── custom/                # Custom agent prompts
├── wizard/                # Develop wizard prompts (clarifying questions)
├── frontend-engineer/     # Frontend specialist role description
└── infra-engineer/        # Infrastructure specialist role description
```

Templates use `{{variable}}` substitution and `{{> fragment}}` includes with YAML frontmatter metadata. `PromptTemplateService` loads them at runtime with `ConcurrentDictionary` caching. Hardcoded fallbacks exist in agent code for resilience if template files are missing.

### Pre-Publish Self-Assessment

Engineer agents perform a **pre-publish self-assessment** before marking PRs ready-for-review. This is a fresh AI context window that re-reads the Issue requirements and compares against the actual changed files in the workspace.

**Flow:**
```
implement → build → test → commit → push → SELF-ASSESS → [fix if needed] → PRCodeComplete gate → mark ready
```

**Key design decisions:**
- **Fresh context, not the implementation conversation** — prevents self-justification bias and context overload
- **Implementation handoff context** — `_implementationNotes` list accumulates key decisions (plan generation, build failures, scope reverts) during implementation and is passed to the assessor so intentional decisions aren't flagged as gaps
- **Prompt-driven** — Assessment criteria, quality bar, and fix instructions live in `prompts/engineer-base/self-assessment-*.md` templates, not hardcoded C#
- **JSON verdict** — Returns `PASS` or `NEEDS_CHANGES` with specific gaps; if `NEEDS_CHANGES`, a separate fresh AI call attempts surgical fixes (max 1 retry). Can also return `INCONCLUSIVE` when the agentic CLI explores the workspace instead of answering the assessment prompt directly — treated as a pass with a logged warning.
- **Integration points**: Wired into `EngineerAgentBase.MarkPrCompleteAsync`, `CommitAndNotifyAsync`, AND `SoftwareEngineerAgent.FinalizeReadyForReviewAsync` (the SE has its own completion path that bypasses the base class)
- **Workspace-only** — Skipped in API-only mode (no local files to inspect)

### Agent Memory

SQLite-backed persistent memory (`AgentMemoryStore`) records agent decisions, learnings, actions, and operator instructions. Agents recall up to 30 recent entries across restarts for context continuity. Memory types: `Action`, `Decision`, `Learning`, `Instruction`.

### Restart Recovery

SE agents recover state on restart via:
1. **`CreateEngineeringPlanAsync`** — restores tasks from ADO/GitHub issues, checks if all done
2. **`RecoverReadyForReviewPRsAsync`** — cross-references open PRs with past-implementation labels against tasks; marks tasks Done (closes the work item) via linked work items or title matching
3. **`RecoverOwnInProgressPRAsync`** — reclaims in-progress PRs as `CurrentPrNumber`

Critical invariant: `PullRequestNumber` is NOT persisted in issue metadata. After restart, task↔PR correlation must use linked work items or title matching.

**Known restart pitfalls:**
- If a runner is restarted while an SE agent has an in-progress PR, the agent must find and reclaim it. If the PR gets orphaned (e.g., agent was duplicated due to a bug), manually close the PR and reset the issue labels (`status:in-progress` → remove, strip agent prefix from title) so it's eligible for reassignment.
- Spawned specialist agents (Game Engine Engineer, Backend Engineer, etc.) persist via `SpawnedDisplayName` in `SMEAgentDefinition` so they maintain identity across restarts.
- Task dependency enforcement happens at assignment time — tasks with unmet dependencies are skipped even if the agent is idle.

### Model Tier Strategy

Four tiers map to agent roles by quality requirements:

| Tier | Default Provider | Used By |
|------|-----------------|---------|
| `premium` | Anthropic Opus | PM, Architect, Software Engineer (quality-critical decisions) |
| `standard` | Anthropic Sonnet | Researcher, SE Workers, Test Engineer (best cost/quality for code) |
| `budget` | OpenAI GPT-mini | SE Workers (alternative) |
| `local` | Ollama (qwen2.5-coder:14b) | SE Workers (alternative to budget) |

Design principle from benchmarking: generating from scratch always beats a draft→fix pipeline in cost, speed, and quality. Prefer single high-quality generation passes over iterative refinement with cheaper models.

### Copilot CLI Provider

When `CopilotCli.Enabled` is `true` (the default), all tiers route through the `copilot` CLI (GitHub Copilot CLI, v1.0.18+) instead of API keys. This is implemented as a custom `IChatCompletionService` — agents require zero code changes.

**Architecture**: Process-per-request model. Each `GetChatMessageContentsAsync()` call spawns a fresh `copilot` process with autonomous-operation flags:

```bash
copilot --allow-all --no-ask-user --silent --no-color --no-auto-update --no-custom-instructions --model claude-opus-4.7
```

Prompts are piped via stdin (avoids shell escaping issues with long multi-KB prompts). SemaphoreSlim limits concurrency (configurable `MaxConcurrentRequests`, default 4).

Key components in `VirtualDevTeam.Core/AI/`:
- **`CopilotCliChatCompletionService`** — Implements `IChatCompletionService`. Flattens multi-turn `ChatHistory` into a single labeled prompt, sends it to the process manager, and parses the response. Uses JSON parsing when `JsonOutput` is enabled, falls back to text parsing. Integrates `ActiveLlmCallTracker` to notify when LLM calls start/complete.
- **`CopilotCliProcessManager`** — Spawns fresh `copilot` processes per request. Runs as `IHostedService` to verify `copilot` availability at startup. Key method: `ExecutePromptAsync(prompt, ct)` returns `CopilotCliResult`.
- **`CliInteractiveWatchdog`** — Monitors stdout for unexpected interactive prompts and auto-responds. Handles y/n confirmations, selection menus, "press enter" prompts. Fails fast on credential prompts or auth failures.
- **`CliOutputParser`** — Strips ANSI escape codes, removes CLI chrome (banners, prompt markers, separators), resolves carriage-return overwrites. Also parses JSONL output from `--output-format json` mode.
- **`ActiveLlmCallTracker`** — Singleton (`ConcurrentDictionary<agentId, LlmCallInfo>`) tracking which agents have in-flight LLM calls. `DashboardDataService` reads this to overlay "Working (AI)" status in the UI. Required by `ModelRegistry` constructor.

Fallback: if `copilot` isn't found at startup, `ModelRegistry` automatically falls back to the API-key provider configured for each tier. Fallback can also be triggered at runtime via `ModelRegistry.TriggerFallback()`.

**Model IDs use dots**: `claude-opus-4.7`, `claude-sonnet-4.6`, `claude-haiku-4.5`, `gpt-5.2` (not dashes).

**Child process PATH resolution**: Tools installed at runtime (e.g., via winget during the Welcome wizard) are invisible to `Process.Start` because the runner inherits its startup PATH. `FreshPathResolver` in `Core/AI/` reads Machine+User PATH from the Windows registry to resolve executables. Used by `GifConverter`, `VideoTrimmer`, and should be applied to all child process spawns (`copilot`, `squad`, `az`, `gh`, `npm`).

### Human Gates & Approval System

Configurable human gates pause workflow at critical points for approval. Gate decisions are processed instantly via `SemaphoreSlim`-based signaling — no polling delay.

**Gate signaling architecture:**
- `GateCheckService` uses a `SemaphoreSlim _gateSignal` for instant wake-up of waiting agents.
- `ApproveGate()`/`RejectGate()` → `SignalWaiters()` → releases semaphore permits → all agents blocked on `WaitForSignalAsync()` wake up immediately.
- Agents call `WaitForSignalAsync(timeoutSeconds, ct)` which returns instantly on approval/rejection or falls back to timeout-based re-poll of the platform API.
- Dashboard approval updates PR labels: removes `awaiting-human-review`, adds `human-approved`.
- Rejection feedback is posted as a GitHub/ADO comment so agents can see the rework instructions.

**Agent-to-Agent response gate:**
When any agent answers questions from another agent (e.g., PM responding to engineer clarification requests on a PR), the response is routed through the Approvals page for human review/edit before posting. This is controlled by the `AgentToAgentResponse` gate in the configuration.

- Gate ID: `ApprovalGates.AgentToAgentResponse`
- Toggle: Available on the Approvals configuration page in the Dashboard UI
- Behavior: If enabled, agent pauses before posting its answer and waits for human approval. The human can edit the text, approve as-is, or reject (skips posting entirely).
- Implementation: `_gateCheck.WaitForGateAsync(...)` in the answering agent's clarification processing method.

**Approvals page UI:**
- 2-section layout: top row of pending approval cards + full-width footer for history/configuration.
- Rework-in-progress state shows animated spinner, feedback quote, and commit/changes links after requesting rework.

**Operator PR feedback loop:**
- PR detail view exposes a **💬 Add Changes** button for human/operator feedback on open PRs.
- The endpoint posts a `**[Operator] CHANGES REQUESTED**` comment wrapped in `vdt:operator-feedback` markers, sanitizes comment-delimiter text, and publishes a `ChangesRequestedMessage` so the author wakes immediately.
- Engineer rework reuses the normal `HandleReworkAsync` path, but operator/human feedback is **exempt from `MaxReworkCycles` exhaustion**.
- Successful operator-requested rework posts `**[Operator-Addressed]** ...`, preserves existing approvals, and copies the operator's intent into `_implementationNotes` so fresh self-assessment/rework prompts do not undo the requested change.

### Pre-PR Clarification Questions

Before engineer agents begin implementation on a PR task, they generate up to 10 clarifying questions with AI-proposed answers. This helps validate assumptions and align implementation with human expectations.

**Flow:**
```
task claimed → generate questions → [if gate enabled: wait for approval] → incorporate answers → begin implementation
```

**Key components:**
- **`PrePRClarificationStore`** (`Core/Agents/Decisions/`) — Thread-safe in-memory store for question sets. Keyed by `setId`. Events: `OnChange` for UI reactivity.
- **`PrePRClarificationSet`** / **`PrePRQuestion`** (`Core/Agents/Decisions/PrePRClarificationModels.cs`) — Records with Question, ProposedAnswer, ImpactLevel, Category, FinalAnswer fields.
- **`GeneratePrePRQuestionsAsync()`** (`EngineerAgentBase`) — LLM call using `prompts/engineer-base/pre-pr-questions.md` template. Parses JSON array response.
- **Gate ID**: `ApprovalGates.PrePRClarification` — configurable via `develop-settings.json` (`prePRClarificationGate: true/false`).
- **Wizard toggle**: Develop wizard first page has "Require Approval" / "Auto-proceed" radio for this gate.

**Dual-path wiring (CRITICAL):**
- `EngineerAgentBase.WorkOnIssueAsync()` — Used by `SpecialistEngineerAgent`
- `SoftwareEngineerAgent.WorkOnOwnTasksAsync()` — SE's independent path that bypasses base class

**Behavior:**
- Gate enabled: Agent pauses, questions appear on Approvals page with editable answer textareas. Human can edit answers, then approve.
- Gate disabled: `AutoApprove(id)` immediately uses proposed answers. Questions still logged as decisions.
- Rejection: Agent proceeds with proposed answers (graceful degradation).
- Each Q&A logged as `AgentDecision` for the Reasoning page with impact level classification.

### Preview Build & Test Artifacts (Testing Dashboard)

The Testing page (`/testing`) provides two tabs:

1. **Preview Build** — Clone/update the working branch to a user-specified local directory, auto-detect build/run commands, and stream output. Managed by `PreviewBuildService` singleton in `VirtualDevTeam.Core/Preview/`.
   - Settings persisted to `{WorkspaceRoot}/preview-settings.json`
   - Auto-detects project type: .sln → `dotnet build`/`dotnet run`, package.json → `npm install && npm run build`/`npm run dev`
   - Port auto-selection probes 5100-5199, then falls back to OS-assigned
   - Token redaction via regex on all output lines

2. **Test Artifacts** — Browse screenshots, videos, and Playwright traces from agent workspaces. Managed by `TestArtifactIndexService` singleton.
   - Scans `{WorkspaceRoot}/{agent}/{repo}/test-results/` directories
   - 30-second cache for performance
   - Stable IDs from SHA256 hash of file path (first 16 hex chars)
   - Attributes PR number from `pr-{N}` path segments

API endpoints: `/api/preview/settings` (GET/POST), `/api/preview/start` (POST), `/api/preview/stop` (POST), `/api/preview/status` (GET), `/api/preview/artifacts` (GET).

Additional operator snapshot endpoint: `GET /api/pipeline/status` returns a single-call view of workflow phase, live agents, work items, linked PR lifecycle data, dependencies, and summary metrics for CLI monitoring / FlowMonitor / external tools.

### Testing

- **xUnit** with `[Fact]` attributes. Naming: `MethodName_ExpectedBehavior()`.
- **Moq** for mocking external dependencies (GitHub service) in integration tests.
- Test classes implement `IDisposable` for cleanup.
- Integration tests build a full DI container with real services and mock only external APIs.
- Inner helper classes (e.g., `TestAgent : AgentBase`) are used for testing abstract types.
- **State snapshot repro harness** — `tests/temp/capture-state.ps1` captures full VDT state (DBs, local-platform repo, worktrees, workspaces, target repo, `develop-settings.json`). Restore scripts such as `tests/temp/BeforePR_Local/setup.ps1` rebuild and restart from a captured snapshot so specific pipeline stages can be reproduced deterministically.

### C# Conventions

- .NET 8 / C# 12 with nullable reference types enabled and implicit usings.
- File-scoped namespaces throughout.
- `record` types for messages, DTOs, and immutable data (with `required` and `init` properties).
- `ArgumentNullException.ThrowIfNull()` and `ObjectDisposedException.ThrowIf()` for guard clauses.
- Async methods suffixed with `Async`, accepting `CancellationToken ct = default`.
- `ILogger<T>` with structured logging (named parameters, not string interpolation).
- `IDisposable` with `_disposed` flag to prevent use-after-dispose.
- DI registration centralized in extension methods (e.g., `AddOrchestrator()`).

### Dashboard Navigation Structure

The Dashboard nav bar is organized into sections:

```
Project
├── Agents         — Live agent status cards, including per-agent restart (🔄) with two-click confirm
├── Timeline       — Project phase timeline
├── Repository     — Pull Requests, Issues, Code (file browser) tabs
└── Testing        — Preview Build + Test Artifacts tabs (currently hidden in nav)

Operations
├── Metrics        — Usage stats & performance
├── Configuration  — Settings & model config
└── Approvals      — Human gate decisions

Advanced
├── Reasoning      — Agent decision logs (Decisions tab default)
├── Flow Monitor   — Structured incident console (replaced terminal)
├── Flow Log       — Raw xterm.js terminal (debug)
├── Walkthrough    — Interactive 22-GIF tour
└── Director CLI   — Direct CLI interface
```

The **Repository** page (`/repository`) has three tabs in this order: **Code** (links to `/repository/files`), **Pull Requests**, **Issues**. The nav bar shows "Repository" as a single link — the Code/Files browser is a sub-page, not a separate nav item.

The **Project Timeline** page now also hosts `NewStoryWizard` behind wave-level `+` actions so operators can create a new story, collect clarifications, and keep it scoped to the selected wave without leaving the timeline.

The **Flow Monitor** page (`/flow-monitor`) is a structured incident console with finding cards, severity filters, and diagnostic checklists — replacing the original xterm.js terminal (now available as **Flow Log** at `/flow-monitor-log` for debug use).

The dedicated **Fix Recommendations** page lives at `/flow-monitor/recommendations`. It surfaces persisted FlowMonitor recommendations with the three-button operator flow (**Approve / Rework / Reject**) and routes execution through `IDiagnosticActionExecutor` instead of ad-hoc page logic.

Dashboard action buttons now use explicit tooltips: strategy **Reset** explains kill-process + fresh-worktree retry + escalation ladder, **Cancel** explains permanent stop/no retry, **Cancel All** explains task abort semantics, and agent **Restart** warns that in-flight LLM calls are lost while durable PR/issue state is recovered.

### Agent Session Log Viewer

- **`AgentCliLogService`** (`Core/AI/`) — Singleton per-agent ring buffer for CLI output with JSONL-first classification via `CliLineClassifier`.
- **Streaming pipeline** — `AgentLogHub` publishes at `/hubs/agentlog`, `AgentLogRelay` fans buffered updates to SignalR clients, and stdout/stderr are tapped inside the existing readers (`AgenticOutputMonitor.onLine` and `ReadOutputWithWatchdogAsync`).
- **UI** — `AgentLogViewer.razor` opens as a popup with Low (AI only), Medium (activity), and High (full) verbosity modes, and Overview agent cards expose a **📋 Log** button to open it.

## Process Hygiene & Safety Rules (READ FIRST — these rules have caused real damage when ignored)

### 🚨 Killing processes — NEVER by name
- ❌ **NEVER** run `Stop-Process -Name node`, `Stop-Process -Name dotnet`, `taskkill /IM node.exe`, or any name-based process kill. The host machine almost always has hundreds of unrelated `node` processes (VS Code language servers, the user's interactive Copilot CLI sessions, dev tools). Killing by name destroys those.
- ✅ **ALWAYS** use `scripts/kill-orphan-runner-procs.ps1` to clean up runner-spawned orphans. It uses a multi-criteria filter (CommandLine match for `@playwright/mcp`, `@modelcontextprotocol/server-`, `blazor-devserver`, `--agent squad`; AND/OR working dir inside `.agents\`/`.candidates\`; AND age >= 120s) and only touches runner-spawned processes. Supports `-WhatIf` to preview.
- ✅ **ALWAYS** stop the Runner by PID — find it via `Get-NetTCPConnection -LocalPort 5050 -State Listen` and `Stop-Process -Id <PID>`.
- The runner-scoped Win32 Job Object (`RunnerProcessJob`) was added so future graceful shutdowns clean up the entire descendant tree atomically. Already wired into `CopilotCliProcessManager`, `SquadFrameworkAdapter`, and `PlaywrightRunner.StartAppAsync`. Do NOT rely on it alone — the cleanup script is still needed for crashed/SIGKILL'd runners.

### 🚨 Resetting state — NEVER manually
- ❌ **NEVER** do an ad-hoc reset (manual issue/PR closes, manual DB delete, manual workspace wipe). Manual resets always miss steps and leave the environment inconsistent (stale code in target repo, ghost agents, orphan branches).
- ✅ **ALWAYS** use the reset scripts:
  - `scripts/fresh-reset.ps1` — full clean slate (closes all PRs/Issues, deletes branches, resets DB, cleans workspace)
  - `scripts/minimal-reset.ps1` — preserves `Research.md`, `PMSpec.md`, `Architecture.md` so the pipeline fast-forwards to engineering
  - `scripts/reset-runner.ps1` — process-only restart (state preserved)
- ⚠️ **The reset scripts are gitignored** (lines 99–101 of `.gitignore`) so they are NOT in fresh clones. They live only on machines where someone restored them. If they're missing, retrieve from git history: `git show 65e5415^:scripts/<file> > scripts/<file>` (the commit just before they were removed from tracking).

### 🚨 Avoid PowerShell pipeline gotchas
- ❌ **NEVER** use `dotnet run | Tee-Object` to run the Runner — it kills the runner during Copilot CLI subprocess calls. Use `Start-Process -RedirectStandardOutput` instead.
- ❌ **NEVER** start `scripts/start-runner.ps1` from Windows PowerShell 5.1. The `agency` wrapper can freeze there without ever spawning `copilot.exe`. ✅ **ALWAYS** launch the runner from PowerShell 7+ (`pwsh`). The start script now enforces this with a version guard.
- ❌ **NEVER** put a literal PAT in shell command text — the Copilot CLI preview pane shows command text to the user. Read tokens from `gh auth token`, environment variables, or `dotnet user-secrets`.
- ❌ **NEVER** force-push to `main` or to anyone else's branch.

### 🚨 NEVER work on `main` — always use a working branch
- ❌ **NEVER** commit directly to `main`. All VDT development MUST happen on a working/feature branch (e.g., `behumphr`, `feature/my-change`).
- ✅ **Before starting any work**, verify you are on a working branch: `git branch --show-current`. If it returns `main`, switch to or create a working branch first.
- ✅ **Before committing**, check the current branch. If on `main`, **STOP** — stash or stage changes and switch to a working branch before committing.
- ✅ **Before pushing**, verify the target: `git push origin <working-branch>`, never `git push origin main`.
- 🛑 **If you detect that local changes have been made while on `main`** (e.g., `git status` shows modifications on main), immediately:
  1. `git stash` the changes
  2. `git checkout <working-branch>` (or create one)
  3. `git stash pop` to restore changes on the correct branch
- The operator signs off on all merges to `main`. No exceptions.

### Repository moves & remote hygiene
- This repository is hosted at `azurenerd/VirtualDevTeam`. `origin` points to `https://github.com/azurenerd/VirtualDevTeam.git`.
- When in doubt about a remote operation, run `git remote -v` first and confirm the URL.

## Dev Platform Parity Rule (GitHub ↔ Azure DevOps)

> 🚨 **CRITICAL: Every fix to a platform-specific service MUST be assessed for cross-platform parity.**

VirtualDevTeam supports two dev platforms via the `IPullRequestService`, `IWorkItemService`, and `IRepositoryContentService` abstractions:
- **GitHub** — `src/VirtualDevTeam.Core/DevPlatform/Providers/GitHub/`
- **Azure DevOps** — `src/VirtualDevTeam.Core/DevPlatform/Providers/AzureDevOps/`

When fixing a bug or adding a feature in **either** provider:
1. **Ask:** Does this same issue exist in the other provider? (Often yes — API limits, missing field hydration, pagination gaps)
2. **Ask:** Is the fix applicable? (Sometimes no — e.g., ADO has overflow comments because of 4000-char PR description limits; GitHub doesn't need this because its limit is 65K)
3. **Document** the decision — if a fix is deliberately NOT applied to the other platform, add a code comment explaining why (e.g., `// GitHub: not needed — 65K char limit is sufficient`).

**Known platform differences:**
| Aspect | GitHub | Azure DevOps |
|--------|--------|--------------|
| PR description limit | 65,536 chars | 4,000 chars (overflow comment pattern) |
| PR list returns labels | ✅ Yes | ❌ No (separate `/labels` endpoint) |
| Work item description | 65K (Markdown body) | Unlimited (HTML field) |
| Merge = auto-close linked issues | ✅ Built-in (`Closes #X`) | ❌ Manual (must close via API) |
| Rate limiting | 5000/hr with shared cache | Per-user, higher limits |

### Lessons Learned (Common Pitfalls)

1. **File-lock build errors**: When the Runner is running, `dotnet build` on the full solution will fail with MSB3027 file-lock errors for the Runner project. Build individual projects (Core, Agents, Orchestrator, Dashboard, tests) to verify code compiles without stopping the Runner.
2. **Constructor parameter propagation**: When adding new dependencies to core services like `ModelRegistry` or the service bundles (`AgentCoreServices`, `AgentPlatformServices`), all call sites must be updated — including test files that construct the service directly (not via DI). Check both DI factories (in extension methods) and test constructors.
3. **DI dual-registration**: Runner (`Program.cs`) and Standalone Dashboard (`StandaloneServiceRegistration`) must register the same services. When adding a new singleton to Runner, add it to the standalone path too or the Dashboard.Host will crash at startup.
4. **Concurrent label writes**: GitHub's label API replaces the entire label set atomically. If two agents update labels on the same issue concurrently, one write silently overwrites the other. Always re-fetch labels immediately before writing.
5. **`.git/config.lock` races**: Parallel `git worktree add` calls race on the lock file. The Strategy Framework serializes worktree creation or retries on lock failures.
6. **PR review on deleted branches**: `GetPRCodeContextAsync` may encounter PRs where the source branch was already deleted — guard with `filesRead` counter and log a warning instead of failing.
7. **In-memory state flags**: Flags like `_allTasksComplete` and `_integrationPrCreated` are lost on process restart. SE agents recover these from durable state (GitHub PRs/issues) on startup — never assume in-memory flags survive across restarts.
8. **Workspace path resolution**: `WorkspaceConfig.ResolveRootPath()` skips already-absolute paths. If `appsettings.json` has a stale absolute path (e.g., from a repo rename/move), it won't be resolved against the current CWD. Always use relative paths (e.g., `.agents`) in config.
9. **develop-settings.json vs appsettings.json**: At runtime, project-specific settings (repo, auth, gates) come from `develop-settings.json`, NOT from `appsettings.json` defaults. The Configuration page and reset scripts must read from develop-settings.json for the active project context.
10. **Stale local gate approvals**: `_localApprovals` in `GateCheckService` must be keyed per-resource for multi-fire gates (FinalPRApproval, PRCodeComplete). A global-only key auto-approves all subsequent resources after the first dashboard approval.
11. **Generic LLM call status**: Dashboard "AI call in progress" is useless for monitoring. Use `AgentCallContext.CurrentCallContext` to set descriptive context before LLM calls so the dashboard shows what the agent is actually doing.
12. **JSONL output breaks direct CLI callers**: When `CopilotCli.JsonOutput` is `true`, `ExecutePromptAsync()` returns raw JSONL, not plaintext. Any code calling it directly (outside `CopilotCliChatCompletionService`) must use `CliOutputParser.ParseJsonOutput()` first. The Develop wizard was broken by this until the fix in commit `5075628`.
13. **Complexity-based PR sizing**: `AssessProjectComplexity()` caps task count (Small≤3, Medium≤6, Large≤10). `NormalizeTaskPlan()` merges excess. CSS-with-feature rule bundles styling with feature tasks. Runs before issue creation to prevent orphans.
14. **SoftwareEngineerAgent bypasses base class completion paths**: `SoftwareEngineerAgent` uses its own `FinalizeReadyForReviewAsync()` instead of `MarkPrCompleteAsync`/`CommitAndNotifyAsync`. Both the Squad framework path and the direct SE implementation path call this override. When adding cross-cutting behavior to the completion flow (e.g., self-assessment), it must be wired into BOTH the base class paths AND `SoftwareEngineerAgent.FinalizeReadyForReviewAsync`. `SpecialistEngineerAgent` does NOT override — it uses base class paths.
15. **Missing CSS for new Blazor button classes**: When adding new button CSS classes (e.g., `config-btn-primary`), always define them in `dashboard.css`. Blazor doesn't warn about undefined classes — buttons silently render as unstyled browser defaults on the dark theme.
16. **Pre-PR clarification dual-path**: `GeneratePrePRQuestionsAsync` must be called from BOTH `EngineerAgentBase.WorkOnIssueAsync` (specialist path) AND `SoftwareEngineerAgent.WorkOnOwnTasksAsync` (SE direct path). Missing either path means some engineers skip clarification questions silently.
17. **Orphan node/dotnet processes accumulate across Runner crashes**: Each Copilot CLI session spawns multiple node MCP servers (`@playwright/mcp`, `@modelcontextprotocol/server-*`); Squad spawns more (`copilot --agent squad` → node tree); Blazor dev servers in candidate worktrees are dotnet processes. When the Runner is force-killed (not graceful exit), they leak. A single overnight session left 382 orphan node processes consuming 14.9 GB of RAM. Two layers of defense exist: (a) `RunnerProcessJob` Win32 Job Object — wired into `CopilotCliProcessManager`, `SquadFrameworkAdapter`, `PlaywrightRunner.StartAppAsync` — atomic kill-tree on graceful runner exit; (b) `scripts/kill-orphan-runner-procs.ps1` — surgical filter for cleanup after a crash. **Never kill `node` by name; always use the script.** See "Process Hygiene & Safety Rules" above.
18. **Multi-worker merge race resets completed tasks**: When multiple SE workers see the same `pm-approved+tests-added` PR and race to merge, one wins and the others get a `PlatformConflictException(NotMergeable)` because the PR was just merged out from under them. The losing workers used to interpret that as a conflict, called `TryCloseAndRecreatePRAsync`, and reset the task to Pending — undoing completed work. `AttemptMergeAsync` now re-fetches the PR after `NotMergeable` and short-circuits to `MergeAttemptResult.NotOpen` if it's already merged. `TryCloseAndRecreatePRAsync` defensively re-fetches at entry and bails on already-closed/merged PRs.
19. **Recovery must scan merged PRs, not only open ones**: `MarkDoneAsync` does NOT apply the `status:implementation-complete` label (the merge itself is the proof). On restart, the SE recovery's "closed-without-PR" orphan check would re-open Done tasks because their `PullRequestNumber` is ephemeral and no implementation-complete label is present. Both orphan-detection paths (`SoftwareEngineerAgent` recovery and `CheckAllTasksCompleteAsync`) now scan merged PRs by this agent's display name, build a name-set, and exempt matching tasks from the orphan reset. Without this, every restart re-implements already-merged work.
20. **EMU GitHub restrictions block `gh` CLI for some flows**: Enterprise Managed User accounts (username ends in `_microsoft`) have `gh auth token` and PAT access restricted for some org policies. Auth fallback chain in `GhCliAuthProvider` matters — exhausts GhCli, then user-secrets, then config. When debugging auth failures in EMU contexts, check `develop-settings.json` `authMethod` value and the active token source.
21. **FlowMonitor must be deterministic, not AI-driven** (May 2026): The watchdog over an AI multi-agent system must be MORE reliable than the system it watches. All detectors are pure logic; escalation rungs are picked by `GetAttemptCount(dedupKey)`, not by an LLM. AI is allowed only in the FixRecommendation flow (T1.5) — and only as advisory output gated behind operator approval. Never put an LLM in FlowMonitor's control flow. See the AutoGen "supervisor pattern" reference and `docs/system/LessonsLearned.md` #87.
22. **Escalation ladder rate limit is global, not per-rung**: `MaxActionsPerHour=12` applies to the SUM of all FlowMonitor actions across ALL findings and ALL rungs. A flapping condition can blow through the budget; that's the intended circuit breaker. Don't add per-rung budgets — they defeat the global cap.
23. **Don't include "integration pr" substring in `engineering.all.complete` auto-detect** (regression May 2026): The `HealthMonitor.cs` heuristic that fires `Signals.AllEngineeringComplete` previously matched both "Creating integration PR" and "Waiting for integration PR" — the latter caused phase to advance Testing→Review→Completion BEFORE T-FINAL produced a PR. Trigger phrases must require post-T-FINAL state ("engineering complete", "all tasks complete"); add a defensive guard that skips the signal if any SE Leader has "integration pr" in their status reason.
24. **Workspace clone on cold start wastes 30s/agent for finished projects**: Engineers always clone repos on `OnInitializeAsync`. Probe `WorkItemService.ListByLabelAsync("engineering-task", state="open")` AND `PrService.ListOpenAsync()` filtered to role first; if both empty, skip the clone and go straight to Idle. Probe is best-effort — any platform exception falls through to normal clone. Saves ~2-3min per restart on completed projects.
25. **"Cannot advance from X to Y" log spam must be deduped**: `WorkflowStateMachine.TryAdvancePhase` logs at Information every tick a gate is unmet — produced 87+ identical lines per phase in production. Track last-logged blocker per phase-pair; log Information only when the reason CHANGES, Trace otherwise. Phase-advance event clears the cache so the next blocker logs fresh.
26. **Stale step on Idle agent cards**: `IAgentTaskTracker.GetCurrentStep(agentId)` returns last `InProgress` step regardless of agent status. Agents recovering from prior project state went Idle without explicitly `CompleteStep`'ing. Two of three call sites in `AgentSnapshotService` already gated with `Status == Working`; `ToSnapshot` did not. Now consistent: when `Status == Idle`, suppress current step + task name in the snapshot. Underlying tracker still has the data for timeline browsing.
27. **SE must not add `tests-added` — TE owns the testing lifecycle**: The SE's T-FINAL path added `tests-added` directly, bypassing the TE. The PM requires both label AND TE comment before reviewing. TE was bypassed → no comment → PM silently skipped for 6h. Fix: remove `tests-added` from SE, TE handles T-FINAL (empty PRs get "[TestEngineer] No Tests Needed"), SE merge gate conditional on `TestEngineerReviews`.
28. **FlowMonitor rung-2 PR comments are unread noise**: Research confirmed no agent parses FlowMonitor comments. Rung-1 bus nudge is a no-op log. Only rung-3 (human label + notification) is effective. Diagnostic enrichment (`IFlowDiagnosticEnricher`) now adds ✅/❌ checklist to findings explaining WHY an agent is stuck. Future: 2-rung ladder (nudge → human with diagnostics).
29. **Always re-fetch PR after `MarkReadyForReviewAsync` before label writes**: The label API replaces the entire set atomically (Lesson #4). Using the original `pr.Labels` after a label swap silently overwrites the swap. The T-FINAL path caused PR #1628 to stay on `in-progress` for 6h. Rule: after any `MarkReadyForReviewAsync` or `UpdateAsync` on labels, re-fetch before the next write.
30. **Centralized PR lifecycle via `PrLifecycleCalculator`**: Label-checking logic was scattered across agents, detectors, and UI. Created a pure stateless calculator in `Core/Lifecycle/` that derives stages from labels + comments + config. Stages built dynamically (not hardcoded). Config-aware: `IsInlineTestWorkflow`, `TestEngineerReviews`, `IsSinglePr`. `PrLifecycleTimeline.razor` renders the timeline in PR detail popups. 14 unit tests.
31. **ExtractTestUrlPaths literal `\n` bug**: Issue body text from the GitHub API contains literal `\n` escape sequences, not actual newlines. Any parser using `Split('\n')` on raw issue body text must first normalize `\\n` → `\n`. This broke the `## Visual Verification` section parsing in `PlaywrightRunner.ExtractTestUrlPaths`.
32. **MCP prompt data ordering**: When giving an AI agent both instructions (steps) and data (test URLs), put the data FIRST. The MCP exploration prompt had "navigate to URLs listed above" in the steps but the URLs were injected below. The agent reads top-down and never saw them. Fixed by restructuring: data block → steps → rules.
33. **Strategies.razor coalesce pattern for `_refreshing`**: The `if (_refreshing) return;` guard in `RefreshAsync` silently dropped bursty SignalR events during strategy evaluation. Media data was in `CandidateStateStore` but the page never re-pulled it until a later event landed when `_refreshing == false`. Fix: `_refreshPending` flag that re-queues a refresh after the current one completes.
34. **TestArtifactIndexService cache-miss on new files**: `GetArtifactById` returned null for newly-written strategy artifacts because the 30-second index cache hadn't refreshed yet. Browser negatively cached the 404 response. Fix: force rescan on cache miss before returning null.
35. **T-FINAL must verify all dependency PRs are merged**: `CreateIntegrationPRAsync` now checks for open engineering PRs before starting T-FINAL. Previously T-FINAL started based on task issue state (open/closed) not PR merge state, leading to 22-file merge conflicts when T4's PR hadn't merged yet.
36. **FreshPathResolver for tools installed at runtime**: Tools installed via winget during the Welcome wizard are invisible to `Process.Start` because the runner inherits its startup PATH. `FreshPathResolver` in `Core/AI/` reads Machine+User PATH from Windows registry. Used by `GifConverter`, `VideoTrimmer`, and should be applied to all child process spawns (copilot, squad, az, gh, npm).
37. **ModelPricing StartsWith matching for model families**: Exact string matching in `GetPricing` missed model variants with suffixes (e.g., `claude-opus-4.6-1m`). Use `StartsWith("claude-opus")` to match the family regardless of context-window or quality suffixes.
38. **Visual score winner selection was dead code**: `VisualsScore` sort ran after the winner was already locked in `SelectWinnerAsync`. `ApplyVisualScoresAsync` must run BEFORE winner selection so visual quality participates in the ranking. Fixed by reordering the call sequence in the strategy evaluation pipeline.
39. **Binary-quality gate false positive on non-art tasks**: Neutral-only binaries (build artifacts like `.dll`, `.exe`) triggered the `TotalCount > 0 && Score < 30` rejection gate. These tasks have no real/fake classifications — only neutral. Fix: gate condition changed to `(RealCount + FakeCount) > 0` so neutral-only sets are exempt.
40. **Refresh buttons calling ResetCaches killed agents**: Timeline and Overview force-refresh buttons called `DataService.ResetCaches()` which cleared the agent snapshot registry, causing all agent cards to vanish and requiring re-registration. Fix: refresh only reloads UI data from existing caches — never calls `ResetCaches()` from user-facing buttons.
41. **T-FINAL re-invocation**: Safety check title-match failure in `CreateIntegrationPRAsync` reset `_integrationPrCreated` flag, causing 3x redundant strategy re-runs on the same code. Fixed with a recreate counter and including `CurrentPrNumber` in the PR search to find already-created integration PRs.
42. **Scenario loading stale bin copy**: `ScenarioReview.razor` child component re-read `develop-settings.json` from the bin directory instead of using the parent page's already-loaded data. This caused stale/missing scenario lists after config changes. Fixed by passing `PersistedScenarios` as a cascading parameter from the parent.
43. **FlowMonitor rung-2 comments still posting on issues**: Lesson #28 suppressed rung-2 PR comments, but the same `post-explicit-ask` action was still posting comments on work items (issues). Now all rung-2 comments are log-only — neither PR nor issue comments are posted.
44. **TestRunner/BuildRunner pipe reads must use linked timeout token** — `ReadToEndAsync(ct)` used the outer token, not the timeout CTS. After kill, orphan processes held stdout open → indefinite hang. Fix in commit `28929b7`: create timeout CTS before IO tasks, bounded 5s drain after kill. Same pattern applies to ALL child process pipe reads.
45. **IsWaveEligible must require PR merged (IsTaskDone) not pushed (IsTaskPastImplementation)** — Using implementation-complete released later waves on stale main, guaranteeing merge conflicts on shared files (Program.cs, package.json). 30-min grace period fallback prevents deadlock from stuck merges. Fixed in commit `c031413`.
46. **CLI rework can commit behind the pipeline** — Copilot CLI `--allow-all` lets it `git commit` during rework, leaving working tree clean. `HandleReworkAsync` must compare HEAD SHA before/after CLI edit, not just `git status`. Fixed in commit `3bdf60d`.
47. **GitHub API rate limit budget: ~5000/hr with 5 parallel agents** — SE polling loops (11 ListMergedAsync call sites × 5 engineers) are the dominant API consumer (~3600 calls/hr). Per-iteration cache (`GetCachedMergedPRsAsync`) and FlowMonitor tick increase (30s→90s) are critical. PR review context cache shared between PM/Architect/TE avoids redundant file fetches.
48. **Never fix hangs with timeout bandaids** — Always find the true root cause with multi-agent parallel research (10+ agents across models). Timeouts cause retries that double end-to-end time. The PM hang was caused by RateLimitManager pausing all API calls for 50 min, not an unbounded HTTP call.
49. **FlowMonitor auto-approval routing** — `PickActionForRung` must short-circuit `gate-stuck:*` findings directly to `auto-approve-gate` action, bypassing the escalation ladder (kick-agent-poll → escalate-to-human). Without this, stuck gates are never auto-approved.
50. **Decision gate REST API required for automation** — Decision gates previously had no REST API (Blazor UI only). Added `GET /api/decisions/pending`, `POST /api/decisions/{id}/approve|reject`. Essential for CLI monitoring and FlowMonitor auto-approval.
51. **Decision gates must appear before plan generation finishes**: `DecisionGateService.ClassifyAndGateDecisionAsync` used to store the decision and create the Approvals notification only after both LLM turns (classification + plan generation) completed. When Turn 2 was slow, the decision was invisible on the Approvals page and agents appeared permanently stuck waiting. Fix: persist the decision in `Pending` state and create the gate notification immediately after Turn 1 classification, then run plan generation afterwards and update the same record. `AgentDecision.Plan` must remain mutable (`set`, not `init`) so Turn 2 can fill it in.
52. **Local git commits may legitimately be no-ops**: `LocalRepositoryContentService.RunGitAsync` used to surface `git commit` failures whenever Git reported "nothing to commit", even for expected no-op writes such as unchanged marker files. Fix: capture both stdout and stderr, and treat `"nothing to commit"` as a non-error for commit commands.
53. **PM deadlock from missing `tests-added` label**: When TE errors out and `ApplyTestsAddedLabelAsync` also fails, PM's Phase 3 gate can deadlock waiting for `tests-added`. PM now fetches TE comments once and reuses them for both the fallback and the defense-in-depth checks, allowing TE error/completion comments to unblock review when the label is missing. Always post the TE error/completion comment before attempting the label update so at least one signal reaches the PM.
54. **WorktreeWorkspace missing stale-state cleanup**: `LocalWorkspace` already had `AbortInProgressOperationsAsync` for stale rebase/merge state, but `WorktreeWorkspace` did not. `SyncWithMainAsync` could then fail on leftover `.git/rebase-merge` state from prior crashes (seen on 3/10 PRs). Fix: add the same stale-state cleanup to `WorktreeWorkspace` and run it at the start of sync.
55. **Agency wrapper freezes under PS 5.1 — always start runner from PS 7+**: when the runner inherits a Windows PowerShell 5.1 environment, the `agency` wrapper can launch but never spawn `copilot.exe`. Under PowerShell 7 (`pwsh`) it consistently spawns within ~3 seconds. `scripts/start-runner.ps1` now hard-fails on PS < 7, and the wrapper liveness watchdog logs startup + empty-child checks at Information level so freezes are visible in production logs.
56. **Dead retention code must have callers**: `AgentStateStore.PruneOldEntriesAsync()` existed since early development with zero callers — `activity_log` grew unbounded. Now wired into `HealthMonitor`'s health-check timer (30-day retention, once per 24h, best-effort). When writing retention methods, always verify callers exist.
57. **Candidate worktrees leak after crashes**: `WorktreeHandle.DisposeAsync` cleans up on normal exit, but crashes leave `.candidates/{id}/` directories on disk. `GitWorktreeManager.CleanupStaleCandidateWorktreesAsync` now runs on first HealthMonitor tick — prunes git metadata then removes physical dirs not in `git worktree list`.
58. **Preview build CancellationToken must outlive HTTP request**: `/api/preview/start` passed the HTTP request's `ct` to `Task.Run()` — token cancelled when response sent, killing the build. Fix: use `CancellationToken.None` for fire-and-forget background work launched from HTTP handlers.
59. **Preview build must not use VDT workspace config for target projects**: `Workspace.BuildCommand` ("dotnet build") is for VDT, not for agent-built projects. Preview now uses only user overrides or auto-detection (searches subdirectories, AI-driven via Copilot CLI with static fallback).
60. **GetPRCodeContextAsync must cap total output**: Serializing all changed files into a prompt hit 250K+ chars, crashing PM review. Added `maxTotalChars=80000` default — remaining files listed by name only when exceeded. All reviewers benefit automatically.
61. **Visual score hydration must be symmetric**: `VisualsScore` must survive both live strategy events and SQLite recovery paths. If one path drops the field, the dashboard shows winner/score mismatches after refresh or restart.
62. **Stale `status:blocked` labels silently stall the pipeline**: blocked engineering issues with no open PRs are invisible to normal pickup logic. `PipelineStallDetector` now treats this as a first-class FlowMonitor failure mode.
63. **Preview placeholders need explicit causes**: `NoVisualContent` was overloaded to mean "backend-only", "Playwright missing", and "app never started". Split preview states so operators can distinguish expected empty galleries from capture failures.
64. **Operator feedback must not consume rework budgets**: human/operator review is governance, not reviewer churn. Operator feedback is tracked for telemetry but exempt from `MaxReworkCycles` exhaustion.
65. **Operator intent must flow into self-assessment context**: when humans request a specific change, copy that request into `_implementationNotes`; otherwise a fresh self-assessment context can "clean up" the very change the operator asked for.
66. **Sanitize PR bodies before parsing or appending metadata**: Copilot/strategy output can include exploration chatter or HTML-comment-like text. Run PR bodies through `PullRequestWorkflow.SanitizePrBody(...)` before appending markers such as `winner-strategy` or deriving dashboard metadata.
67. **Playwright driver installs need real payload files**: a `.playwright/` tree with only directories is still broken — verify the driver files exist, not just the folders.
68. **AppLauncher build recovery can replay Lesson #44 pipe deadlocks**: any recovery path that reads child-process pipes must use the same linked-timeout-token pattern as the main runners.
69. **Restart recovery must avoid duplicate task assignment**: match existing work by task name and confirm there is no still-open PR before reassigning the issue.
70. **App startup detection must aggressively demote test projects**: filter likely test hosts by path/name and apply the `-200` ranking penalty so fallback startup chooses the real app.
71. **PM T-FINAL review must be purpose-aware**: derive the review rubric from the PR purpose so final-integration reviews are judged differently from ordinary engineering PRs.
72. **LocalPlatform merge fails with unrelated histories**: `LocalBareRepoManager` creates a bare repo via `git init` while the GitHub/ADO target repo was initialized with a README → no common ancestor → `git merge` rejects with "refusing to merge unrelated histories". Fix: add `--allow-unrelated-histories` to ALL merge commands in both `GitHubFinalSubmissionService` and `AdoFinalSubmissionService`. Applies to `MergeInTempWorktreeAndPushAsync` merge, rebase fallback, and any `LocalBareRepoManager.MergeBranchAsync` calls.
73. **workingBranch==defaultBranch blocks final PR creation**: If `develop-settings.json` has `"workingBranch": "main"` AND `"defaultBranch": "main"`, GitHub/ADO reject PR creation (head==base). 3-layer defense: (1) SE agent detects equality and falls back to `vdt/final/{defaultBranch}` branch name, (2) `GitHubFinalSubmissionService.SubmitFinalPRAsync` has the same defensive fallback, (3) `AdoFinalSubmissionService.SubmitFinalPRAsync` has the same check (cross-platform parity).
74. **VDT CLI packaging gaps**: Self-contained publish requires: (a) conditional `<AssemblyName>vdt</AssemblyName>` so exe isn't named `VirtualDevTeam.Runner.exe`, (b) `<Content>` items for `prompts/` directory (not copied by default), (c) `PostConfigure` fallback using `AppContext.BaseDirectory` for prompt path resolution when CWD != bin dir.
75. **CliInteractiveWatchdog YAML false-positives**: `IsCredentialPrompt` regex falsely matches GitHub Actions YAML in CLI output (`_token: ${{ secrets.GITHUB_TOKEN }}`). Exclusions needed for: indented lines (YAML/code context), template syntax (`${{`), compound identifiers with dots/underscores.
76. **Agency wrapper cross-contamination in parallel instances**: The `agency` CLI wrapper shares persisted CWD state (`resume-auto-cd`), so two parallel VDT instances with agency enabled can cross-contaminate (Checkers instance references Tetris scratch dir). Workaround: only one instance can use agency; others must set `"wrapperCommand": ""`.
77. **LocalPlatformInitializer timing bug with DevelopSettingsService**: `DevelopSettingsService.ApplyToConfigAsync` runs AFTER `IHostedService` startup, so `LocalPlatformInitializer` sees stale config (platform=GitHub when it should be Local). Fix: (1) search CWD first for develop-settings.json, (2) late-binding LocalPlatformContext init in RunCoordinator after config merge.
78. **VDT CLI agent bootstrap requires explicit spawn**: `VirtualDevTeamWorker` returns `NoRun` when no active run exists at startup. The `/project/start` endpoint must call `SpawnAgentsForRunAsync()` after creating the run, otherwise no agents spawn and the pipeline stalls silently.

## FlowMonitor v2— Always-On Watchdog Service (May 2026)

The FlowMonitor is a `BackgroundService` (`src/VirtualDevTeam.Orchestrator/FlowMonitorService.cs`) that watches the multi-agent flow, detects stuck states, and applies safe corrective actions. It NEVER restarts processes, recompiles, force-merges, modifies code, or deletes platform resources. All findings + actions are persisted to SQLite (`flow_findings`, `flow_actions`, `flow_monitor_ticks`, `fix_recommendations` tables).

**Detectors registered today** (each is `IFlowDetector`):
| Id | What it watches |
|---|---|
| `agent-stuck` | Agents in Working state for >30m without status change |
| `phase-completion-mismatch` | Workflow phase=Completion but agents still Working |
| `deadlock` | Wait-for graph cycles via existing `Orchestrator.DeadlockDetector` (T2.13 wraps it as observer) |

**Actions registered today** (each is `IFlowAction`, sequenced via T1.2 escalation ladder):
| Id | Rung | What it does |
|---|---|---|
| `kick-agent-poll` | 1 | Publishes `FlowMonitorNudge` bus message to target agent |
| `post-explicit-ask` | 2 | Posts a structured comment to the agent's open PR (or work item) |
| `escalate-to-human` | 3 | Applies `agent-stuck` label + emits non-auto-resolving notification with diagnostic checklist |

### Diagnostic Enrichment (May 2026)

`IFlowDiagnosticEnricher` implementations run after detection, before action selection. They add ✅/❌ diagnostic checklists to findings explaining WHY an agent is stuck (not just that it IS stuck). `PrLifecycleDiagnosticEnricher` checks PM/TE/Architect gate conditions: labels present, comments missing, dependency chain. Findings carry `Diagnostics`, `RecommendedFixId`, `RecommendedFixDescription`. Persisted as `diagnostics_json` in `flow_findings` table. Approvals page shows diagnostics inline with collapsible details.

### Tier 1 features shipped (master plan items T1.1 — T1.8 + T2.13)

- **T1.1 DetectorContext platform views**: `ctx.Platform : IPlatformView` exposes lazy/cached/fault-tolerant `ListOpenPullRequestsAsync`, `ListOpenWorkItemsAsync`, `ListUnresolvedThreadsAsync(prNumber)`, `GetLatestCommitAsync(prNumber)`. Per-tick caching means N detectors sharing a context pay the API cost once. `NullPlatformView.Instance` is the no-op fallback for pre-project state. `WorkflowSignals` is now populated from `WorkflowStateMachine.GetSignals()` (was hardcoded `Array.Empty<string>()`).
- **T1.2 Escalation ladder**: 3 rungs picked by `FlowMonitorPersistence.GetAttemptCount(dedupKey, 4h)`. `flow_actions.attempt_count` column. Rate limit is global. Falls back to first `CanHandle` action if rung-specific isn't registered.
- **T1.3 Verification-after-action**: At each tick start, `VerifyActedOnFindingsAsync` re-runs the originating detector for `ActedOn` findings <1h old. Cleared → `Resolved` + `verify-acted-on` action row. Persists → severity bump + `Expired` so dedup window doesn't suppress fresh re-emission.
- **T1.4 Real-time log stream**: `FlowMonitorEventBus` (bounded `Channel<FlowMonitorEvent>(200, DropOldest)`) publishes events on tick start, detector start/finish, finding insertion, action started/completed. SignalR hub at `/hubs/flowmonitor` fans out to subscribed circuits. Page `/flow-monitor-log` renders xterm.js terminal with Copilot-CLI color classification (purple=finding, green=success, red=error, cyan=detector, gray=lifecycle) and LOW/MEDIUM/HIGH verbosity selector.
- **T1.5 FixRecommendation flow**: For Critical findings WITHOUT an action handler, `FixRecommendationPlannerService` runs 2-pass Copilot CLI (`/plan` + rubber-duck critique) → persists to `fix_recommendations` table + saves `.md` to `/FixRecommendations/{ts}-{id}.md`. Recommendations with `Confidence ≥ 0.75` emit a notification → Approvals page card with Markdig + Ganss.Xss. Operator can Approve, Rework (edit), or Reject.
- **T1.6 Code-fix-without-restart classifier**: `FixClassifier.Classify(rec)` returns `FixTier { Live | DeferredRestart | Blocked }`. Live = prompts/configs (apply via `LiveFixApplicator` with `git status` snapshot diff for scope verification). Deferred = `.cs`/`.razor` (apply via Copilot CLI, state→`Coded`, restart needed). Blocked = `.csproj`/migrations (stage to `/FixRecommendations/staged/`, applied at next startup by `StagedFixApplicator : IHostedService`). Tier badge 🟢/🟡/🔴 in Approvals + Health Monitor.
- **T1.7 Warm restart**: `POST /api/dashboard/runtime/restart` spawns `scripts/restart-runner.ps1` detached, calls `IHostApplicationLifetime.StopApplication()` after flush. Helper waits for old PID to exit, force-kills if stuck, sleeps 2s, re-launches via `start-runner.ps1`. UI button on Health Monitor with two-click confirm. Workflow phase + agent identities + signals + CLI sessions checkpointed; durable platform work unaffected. Auto-reload-on-disconnect (App.razor) refreshes browser tabs.
- **T1.8 Liveness watchdog**: `HealthMonitor.CheckFlowMonitorLiveness()` runs at end of each cycle. If latest `flow_monitor_ticks.recorded_at` is staler than `2 × FlowMonitorConfig.PollIntervalSeconds`, fires `flow-monitor:liveness` notification ONCE (guarded by `_flowMonitorLivenessAlertSent`). Recovery resolves + clears the flag.
- **T2.13 Deadlock detector wrap**: `DeadlockFlowDetector` (Orchestrator project, not Core — circular-dep avoidance) subscribes to `DeadlockDetector.DeadlockDetected` event, caches with 5-min TTL, emits `Severity=Critical` findings with stable cycle-hash dedup keys. `DeadlockDetector` itself unmodified — pure observer pattern.

### FlowMonitor coding rules

- Detectors: stateless, pure logic, ≤2s per tick, never throw (the service wraps but each detector should also `try/catch` internally and log Warning on failure)
- Actions: must `CanHandle(finding)` test the dedup_key prefix or detector_id; pick ONE action per finding per tick (rate limit + ladder ensure progression)
- New `IFlowDetector`/`IFlowAction` registrations go in `src/VirtualDevTeam.Runner/Startup/RunnerHealthMonitorExtensions.cs` alongside the existing ones; standalone Dashboard host doesn't need them (they're runner-side only)
- New singletons that ARE referenced by Razor pages (e.g., `FlowMonitorPersistence` in bundled dashboard mode) MUST also be registered in `StandaloneServiceRegistration` or the Dashboard.Host crashes at startup
- All findings publish a `FlowMonitorEvent` to the bus; the bus auto-tags with `AgentCallContext.CurrentAgentId/SessionId` (AsyncLocal flow) — no need to thread agent ids through detectors

- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.
