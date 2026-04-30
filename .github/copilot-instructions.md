# Copilot Instructions for AgentSquad

## Build, Test, and Lint

```bash
# Build the entire solution
dotnet build AgentSquad.sln

# Run all tests
dotnet test AgentSquad.sln

# Run a single test project
dotnet test tests/AgentSquad.Core.Tests

# Run a specific test by fully-qualified name
dotnet test tests/AgentSquad.Core.Tests --filter "FullyQualifiedName~MessageBusTests.PublishAsync_DeliversToTargetedSubscriber"

# Run the application
cd src/AgentSquad.Runner && dotnet run
```

## Architecture

AgentSquad is a multi-agent AI system where 7 specialized agent roles collaborate through GitHub PRs/Issues and an in-process message bus, coordinated by an orchestrator with phase-gated workflow.

### Project Dependency Graph

```
AgentSquad.Runner (host)
├── AgentSquad.Orchestrator (coordination)
│   └── AgentSquad.Core (shared abstractions)
├── AgentSquad.Agents (concrete agent implementations)
│   ├── AgentSquad.Orchestrator
│   └── AgentSquad.Core
└── AgentSquad.Dashboard (Blazor Server monitoring UI)
    └── AgentSquad.Core
```

- **Core** — Agent abstractions (`IAgent`, `AgentBase`), message bus (`IMessageBus`), GitHub service (`IGitHubService`), persistence (`AgentStateStore`), configuration models, and Semantic Kernel integration. All other projects depend on Core.
- **Agents** — Seven concrete agent implementations, each extending `AgentBase` with role-specific AI conversation logic and GitHub workflows. Created via `AgentFactory` using `ActivatorUtilities.CreateInstance<T>`.
- **Orchestrator** — Runtime coordination: `WorkflowStateMachine` (phase-gated progression), `AgentRegistry` (thread-safe lifecycle), `AgentSpawnManager` (dynamic scaling with slot reservation), `DeadlockDetector` (DFS wait-for graph), `HealthMonitor`, `GracefulShutdownHandler`.
- **Runner** — Application host. Registers all services via DI in `Program.cs`, bootstraps core agents in phased sequence via `AgentSquadWorker` (a `BackgroundService`).
- **Dashboard** — Blazor Server app with SignalR push updates. `DashboardDataService` subscribes to `AgentRegistry` events and broadcasts via `IHubContext<AgentHub>`. Includes Testing page (Preview Build + Test Artifacts). Also has a standalone host (`AgentSquad.Dashboard.Host`) for running the UI without the Runner.

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
                                  → EngineeringPlan.md (Software Engineer, after Architecture)
                                  → PRs with enriched task context (assigned to engineers)
```

- **PMSpec.md** — Business specification created by the PM from Research.md + project description. Contains: Executive Summary, Business Goals, User Stories & Acceptance Criteria, Scope, Non-Functional Requirements, Success Metrics, Constraints.
- All downstream agents (Architect, Software Engineers) read PMSpec.md for business context alignment.

### Dual-Layer Communication

Agents communicate through two complementary layers simultaneously:

1. **In-process message bus** (`InProcessMessageBus` via `System.Threading.Channels`, bounded capacity 1000) — instant agent-to-agent signaling (<1ms), no durability. Messages routed by `ToAgentId` — set to `"*"` or `null` for broadcast. Five message types derive from the `AgentMessage` base record: `TaskAssignmentMessage`, `StatusUpdateMessage`, `HelpRequestMessage`, `ResourceRequestMessage`, `ReviewRequestMessage`.
2. **GitHub API** (Octokit) — durable artifacts (PRs with code, Issues for tasks, Comments for discussion), human oversight, external coordination (100–500ms latency).

An agent typically does both: creates a PR on GitHub AND sends a bus message to notify the PM. The bus is for real-time internal coordination; GitHub is the permanent record.

## Key Conventions

### Agent Implementation Pattern

Every agent follows the same structure:

1. **Constructor** accepts `AgentIdentity`, `IMessageBus`, `IGitHubService`, role-specific workflows, `ProjectFileManager`, `ModelRegistry`, `IOptions<AgentSquadConfig>`, and `ILogger<T>` — then calls `base(identity, logger)`.
2. **`OnInitializeAsync`** subscribes to message types via `_messageBus.Subscribe<T>(Identity.Id, handler)`, storing subscriptions in a `List<IDisposable>`.
3. **`RunAgentLoopAsync`** runs a `while (!ct.IsCancellationRequested)` loop with a `Task.Delay` poll interval. Catches `OperationCanceledException` to break cleanly; catches other exceptions, logs them, and retries after a 5-second backoff.
4. **`OnStopAsync`** disposes all message subscriptions.

**Specialist (SME) agents** are dynamically spawned via `AgentSpawnManager` during the ParallelDevelopment phase. They share the `SoftwareEngineerAgent` class but receive specialized `AgentIdentity` (e.g., "Game Engine Engineer 1", "Backend Engineer 1"). The `SMEAgentDefinition` record includes `SpawnedDisplayName` for identity persistence across restarts and slot reservation for concurrency limits.

### Multi-Turn AI Conversations

Agents use Semantic Kernel's `ChatHistory` for stateful multi-turn conversations. The pattern is:
- Get a kernel via `_modelRegistry.GetKernel(Identity.ModelTier)` (tiers: premium, standard, budget, local)
- Build turns: system message (role definition) → user prompt → assistant response → follow-up user prompt → etc.
- Turn counts vary by agent complexity (Researcher: 3, Architect: 5, Software Engineer: 3)

### GitHub Conventions

- **PR titles**: `{AgentDisplayName}: {TaskTitle}` (e.g., `"Software Engineer 1: Implement auth"`)
- **PR branches**: `agent/{name}/{task-slug}` (e.g., `agent/software-engineer-1/implement-auth`)
- **Issue titles**: `{TargetAgent}: {Title}` or `Executive Request: {Title}`
- **Labels**: `in-progress`, `ready-for-review`, `blocker`, `agent-stuck`, `executive-request`, `resource-request`, `agent-question`, plus complexity labels
- Agents parse their name from PR/issue titles to find their assigned work

### Thread Safety

- `AgentRegistry` uses `ConcurrentDictionary` for lock-free reads with `TryAdd`/`TryRemove` for atomic mutations.
- `WorkflowStateMachine` and `AgentSpawnManager` use `lock` for state transitions and slot reservation.
- `DeadlockDetector` snapshots its `ConcurrentDictionary` before DFS traversal.
- `AgentBase.Status` is guarded by a dedicated `_statusLock`.

### Configuration

All config lives under the `AgentSquad` section in `appsettings.json`, bound via `IOptions<AgentSquadConfig>`. Key sections: `Project` (GitHub repo/PAT), `Models` (provider/tier definitions), `Agents` (per-role tier assignments), `Limits` (scaling caps, timeouts, poll intervals), `Dashboard` (port, SignalR toggle). `appsettings.json` is committed — never put secrets in it; use `dotnet user-secrets` instead.

### Workspace Configuration

Agent workspaces default to `.agents/` (relative to project root). `WorkspaceConfig.ResolveRootPath()` resolves relative paths at startup via `PostConfigure<AgentSquadConfig>` in `Program.cs`. The `.agents/` folder is gitignored.

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

Key components in `AgentSquad.Core/AI/`:
- **`CopilotCliChatCompletionService`** — Implements `IChatCompletionService`. Flattens multi-turn `ChatHistory` into a single labeled prompt, sends it to the process manager, and parses the response. Uses JSON parsing when `JsonOutput` is enabled, falls back to text parsing. Integrates `ActiveLlmCallTracker` to notify when LLM calls start/complete.
- **`CopilotCliProcessManager`** — Spawns fresh `copilot` processes per request. Runs as `IHostedService` to verify `copilot` availability at startup. Key method: `ExecutePromptAsync(prompt, ct)` returns `CopilotCliResult`.
- **`CliInteractiveWatchdog`** — Monitors stdout for unexpected interactive prompts and auto-responds. Handles y/n confirmations, selection menus, "press enter" prompts. Fails fast on credential prompts or auth failures.
- **`CliOutputParser`** — Strips ANSI escape codes, removes CLI chrome (banners, prompt markers, separators), resolves carriage-return overwrites. Also parses JSONL output from `--output-format json` mode.
- **`ActiveLlmCallTracker`** — Singleton (`ConcurrentDictionary<agentId, LlmCallInfo>`) tracking which agents have in-flight LLM calls. `DashboardDataService` reads this to overlay "Working (AI)" status in the UI. Required by `ModelRegistry` constructor.

Fallback: if `copilot` isn't found at startup, `ModelRegistry` automatically falls back to the API-key provider configured for each tier. Fallback can also be triggered at runtime via `ModelRegistry.TriggerFallback()`.

**Model IDs use dots**: `claude-opus-4.7`, `claude-sonnet-4.6`, `claude-haiku-4.5`, `gpt-5.2` (not dashes).

### Human Gate for Agent-to-Agent Responses

When any agent answers questions from another agent (e.g., PM responding to engineer clarification requests on a PR), the response is routed through the Approvals page for human review/edit before posting. This is controlled by the `AgentToAgentResponse` gate in the configuration.

- Gate ID: `ApprovalGates.AgentToAgentResponse`
- Toggle: Available on the Approvals configuration page in the Dashboard UI
- Behavior: If enabled, agent pauses before posting its answer and waits for human approval. The human can edit the text, approve as-is, or reject (skips posting entirely).
- Implementation: `_gateCheck.WaitForGateAsync(...)` in the answering agent's clarification processing method.

### Preview Build & Test Artifacts (Testing Dashboard)

The Testing page (`/testing`) provides two tabs:

1. **Preview Build** — Clone/update the working branch to a user-specified local directory, auto-detect build/run commands, and stream output. Managed by `PreviewBuildService` singleton in `AgentSquad.Core/Preview/`.
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

### Testing

- **xUnit** with `[Fact]` attributes. Naming: `MethodName_ExpectedBehavior()`.
- **Moq** for mocking external dependencies (GitHub service) in integration tests.
- Test classes implement `IDisposable` for cleanup.
- Integration tests build a full DI container with real services and mock only external APIs.
- Inner helper classes (e.g., `TestAgent : AgentBase`) are used for testing abstract types.

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
├── Agents         — Live agent status cards
├── Timeline       — Project phase timeline
├── Repository     — Pull Requests, Issues, Code (file browser) tabs
└── Testing        — Preview Build + Test Artifacts tabs

Operations
├── Metrics        — Usage stats & performance
├── Configuration  — Settings & model config
└── Approvals      — Human gate decisions
```

The **Repository** page (`/repository`) has three tabs in this order: **Code** (links to `/repository/files`), **Pull Requests**, **Issues**. The nav bar shows "Repository" as a single link — the Code/Files browser is a sub-page, not a separate nav item.

### Lessons Learned (Common Pitfalls)

1. **File-lock build errors**: When the Runner is running, `dotnet build` on the full solution will fail with MSB3027 file-lock errors for the Runner project. Build individual projects (Core, Agents, Orchestrator, Dashboard, tests) to verify code compiles without stopping the Runner.
2. **Constructor parameter propagation**: When adding new dependencies to core services like `ModelRegistry`, all call sites must be updated — including test files that construct the service directly (not via DI). Check both DI factories (in extension methods) and test constructors.
3. **Orphan process cleanup**: The `GracefulShutdownHandler` kills child `copilot` processes on runner shutdown. Use `SquadReadinessChecker` with PATH augmentation to find tools like `npm`/`node` in non-standard locations.
4. **PR review on deleted branches**: `GetPRCodeContextAsync` may encounter PRs where the source branch was already deleted — guard with `filesRead` counter and log a warning instead of failing.
