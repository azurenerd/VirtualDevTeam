# Architecture

Technical architecture documentation for VirtualDevTeam.

## Table of Contents

- [System Overview](#system-overview)
- [Design Principles](#design-principles)
- [Solution Structure](#solution-structure)
- [Agent Lifecycle](#agent-lifecycle)
- [Communication Patterns](#communication-patterns)
- [Project Workflow Phases](#project-workflow-phases)
- [Model Allocation Strategy](#model-allocation-strategy)
- [Persistence Strategy](#persistence-strategy)
- [Monitoring and Observability](#monitoring-and-observability)
- [Security Considerations](#security-considerations)

---

## System Overview

VirtualDevTeam is a multi-agent orchestration system built on .NET 8 that coordinates specialized AI agents to collaboratively develop software projects. The system is structured as a layered architecture:

```
┌─────────────────────────────────────────────────────────┐
│  Presentation: VirtualDevTeam.Dashboard (Blazor + SignalR)   │
├─────────────────────────────────────────────────────────┤
│  Host: VirtualDevTeam.Runner (Generic Host + Worker)         │
├─────────────────────────────────────────────────────────┤
│  Orchestration: VirtualDevTeam.Orchestrator                  │
│  (Registry, Spawning, Workflow, Health, Deadlocks)       │
├─────────────────────────────────────────────────────────┤
│  Agents: VirtualDevTeam.Agents                               │
│  (7 specialized agent implementations + factory)         │
├─────────────────────────────────────────────────────────┤
│  Core: VirtualDevTeam.Core                                   │
│  (Abstractions, GitHub, Messaging, Config, Persistence)  │
├─────────────────────────────────────────────────────────┤
│  External: GitHub API (Octokit) + AI Providers (SK)      │
└─────────────────────────────────────────────────────────┘
```

## Design Principles

1. **Platform as Source of Truth** — All coordination artifacts (PRs, work items, shared documents) live in the target platform (GitHub or Azure DevOps), enabling human observability and intervention at any point. Agents use `IPullRequestService`, `IWorkItemService`, and other capability interfaces from the `DevPlatform` abstraction layer — never `IGitHubService` directly.

2. **Loose Coupling via Message Bus** — Agents communicate through an in-process message bus using typed messages. No agent directly references another agent's implementation.

3. **Configuration-Driven Behavior** — Model tiers, resource limits, polling intervals, and agent assignments are all externalized to configuration, enabling runtime tuning without code changes.

4. **Graceful Degradation** — Rate limiting, timeout detection, deadlock detection, and checkpoint-based recovery ensure the system handles failures without data loss.

5. **Phase-Gated Progression** — A state machine enforces that the project advances through well-defined phases with explicit gate conditions, preventing premature execution.

## Solution Structure

### VirtualDevTeam.Core

The shared foundation layer containing abstractions, infrastructure services, and data models.

| Namespace | Key Types | Purpose |
|-----------|-----------|---------|
| `Agents` | `IAgent`, `AgentBase`, `AgentIdentity`, `AgentRole`, `AgentStatus`, `AgentMessage` | Agent contracts, lifecycle base class, role/status enums, typed message hierarchy |
| `Configuration` | `VirtualDevTeamConfig`, `ConfigValidator`, `ConfigWizard`, `ModelRegistry` | Configuration models, validation, first-run wizard, Semantic Kernel factory |
| `DevPlatform` | `IPullRequestService`, `IWorkItemService`, `IBranchService`, `IReviewService`, `IPlatformInfoService`, `IPlatformHostContext` | Platform-agnostic capability interfaces (GitHub and Azure DevOps implementations) |
| `GitHub` | `IGitHubService`, `GitHubService`, `IssueWorkflow`, `PullRequestWorkflow`, `RateLimitManager` | GitHub API adapter (wrapped by DevPlatform for agent use) |
| `Messaging` | `IMessageBus`, `InProcessMessageBus` | Async pub/sub using `System.Threading.Channels` |
| `Persistence` | `AgentStateStore`, `ProjectFileManager` | SQLite checkpoints/activity logs, shared markdown file management |

### VirtualDevTeam.Agents

Concrete implementations of the 7 agent roles plus the factory.

| Type | Description |
|------|-------------|
| `AgentFactory` | DI-based factory implementing `IAgentFactory`; creates agents via `ActivatorUtilities` |
| `ProgramManagerAgent` | Team leadership, resource management, blocker triage |
| `ResearcherAgent` | Multi-turn technical research → Research.md |
| `ArchitectAgent` | 5-turn architecture design → Architecture.md + PR reviews |
| `SoftwareEngineerAgent` | Engineering plan creation, task assignment, high-complexity implementation, code review |
| `SoftwareEngineerAgent` | Medium-complexity implementation with 3-turn AI loop |
| `SoftwareEngineerAgent` | Low-complexity with self-validation and escalation |
| `TestEngineerAgent` | Scans untested PRs, generates test plans and test PRs |
| `AgentTracking` | Internal DTO for PM's team status tracking |

### VirtualDevTeam.Orchestrator

Runtime coordination services registered as singletons.

| Type | Description |
|------|-------------|
| `AgentRegistry` | Thread-safe `ConcurrentDictionary`-based agent registration with status change events |
| `AgentSpawnManager` | Creates agents with limit enforcement; core roles are singletons, engineers share a pool |
| `WorkflowStateMachine` | Phase progression with signal-based gate conditions and PM force-override |
| `DeadlockDetector` | DFS-based cycle detection on a wait-for graph of agent dependencies |
| `HealthMonitor` | Periodic health checks detecting stuck agents (configurable timeout) |
| `GracefulShutdownHandler` | `IHostedService` that persists all agent state on Ctrl+C or host shutdown |
| `RunCoordinator` | Single-active-run enforcement, run lifecycle management, service reconfiguration at project start |

### VirtualDevTeam.Dashboard

Blazor Server application for real-time monitoring.

| Type | Description |
|------|-------------|
| `AgentHub` | SignalR hub broadcasting agent state changes to connected clients |
| `DashboardDataService` | `BackgroundService` caching agent snapshots; bridges orchestrator events → SignalR |
| Pages: `AgentOverview` | Grid of agents with status, summary stats, deadlock alerts |
| Pages: `AgentDetail` | Single-agent deep dive with pause/resume/terminate controls |
| Pages: `GitHubFeed` | Timeline of agent-generated GitHub artifacts |
| Pages: `Metrics` | Utilization chart, status breakdown, longest-running task |
| Pages: `TeamViz` | Visual team layout with connection lines and status indicators |

### VirtualDevTeam.Runner

Application host and bootstrap.

| Type | Description |
|------|-------------|
| `Program` | DI composition root — registers all services, config binding, host setup |
| `VirtualDevTeamWorker` | `BackgroundService` that spawns the 5 core agents in sequence on startup |

## Agent Lifecycle

Each agent follows a defined state machine:

```
                    ┌──────────┐
                    │Requested │ (Agent creation requested)
                    └────┬─────┘
                         │ InitializeAsync()
                    ┌────▼──────────┐
                    │ Initializing  │ (Loading config, connecting services)
                    └────┬──────────┘
                         │ OnInitializeAsync() completes
                    ┌────▼─────┐
              ┌────►│  Online  │◄──────────────────┐
              │     └────┬─────┘                    │
              │          │ Task received             │ Task complete
              │     ┌────▼─────┐                    │
              │     │ Working  ├────────────────────┘
              │     └────┬─────┘
              │          │ Dependency unresolvable
              │     ┌────▼─────┐
              │     │ Blocked  │ (Awaiting external resolution)
              │     └────┬─────┘
              │          │ Issue resolved
              │          │
              │     ┌────▼─────┐
              └─────┤  Paused  │ (Manual pause via dashboard)
                    └────┬─────┘
                         │ StopAsync() or terminate
                    ┌────▼─────┐       ┌────────┐
                    │ Offline  │       │ Error  │
                    └────┬─────┘       └────────┘
                         │
                    ┌────▼──────────┐
                    │  Terminated   │ (Fully stopped, unregistered)
                    └───────────────┘
```

**Key transitions:**
- `AgentBase.UpdateStatus()` handles thread-safe status changes and fires `StatusChanged` events
- The `HealthMonitor` detects agents stuck in `Working` beyond `AgentTimeoutMinutes`
- `GracefulShutdownHandler` saves checkpoints before transitioning to `Offline`
- The `AgentRegistry` subscribes to `StatusChanged` and propagates events to the dashboard

## Communication Patterns

VirtualDevTeam uses four complementary communication channels:

### 1. In-Process Message Bus (Real-Time, Typed)

The `InProcessMessageBus` provides async pub/sub via `System.Threading.Channels`:

```
┌──────────┐   PublishAsync()   ┌──────────────────┐   Handler dispatch   ┌──────────┐
│  Agent A  ├──────────────────►│  InProcessMsgBus  ├─────────────────────►│  Agent B  │
└──────────┘                    │  Channel<object>  │                      └──────────┘
                                │  per-agent mailbox│
                                └──────────────────┘
```

**Message types** (all inherit from `AgentMessage`):

| Message | From → To | Purpose |
|---------|-----------|---------|
| `TaskAssignmentMessage` | Principal → Engineers | Assign implementation task with PR URL |
| `StatusUpdateMessage` | Any → Broadcast (`*`) | Notify status transitions and task completion |
| `HelpRequestMessage` | Engineer → Principal/PM | Escalate blockers or complexity issues |
| `ResourceRequestMessage` | Principal → PM | Request additional Software Engineers |
| `ReviewRequestMessage` | Any → Any | Request code review on a PR |

**Routing:** Messages with `ToAgentId = "*"` or `null` broadcast to all subscribers. Otherwise, delivered to the specific agent's mailbox.

### 2. Work Items / Issues (Async, Human-Readable)

Work items (GitHub Issues or Azure DevOps Work Items) serve as durable, inspectable communication for escalation and cross-agent requests:

```
┌───────────┐  Create issue   ┌────────────────────┐  Poll & respond   ┌───────────┐
│  Agent A   ├───────────────►│   GitHub Issues     ├──────────────────►│  Agent B   │
└───────────┘                 │  Labels: blocker,   │                   └───────────┘
                              │  resource-request,  │
      ┌───────────┐           │  executive-request  │
      │  Human    ├──────────►│  agent-question     │
      └───────────┘           └────────────────────┘
```

**Use cases:**
- PM creates `executive-request` issues for decisions beyond agent authority
- Engineers create `blocker` issues when stuck
- Principal creates `resource-request` issues for more engineers
- Agents create `agent-question` issues for cross-team queries
- Humans can comment on any issue to inject guidance

### 3. Pull Requests (Task Assignment and Code Delivery)

PRs are the primary vehicle for task tracking and code delivery (GitHub PRs or Azure DevOps PRs):

```
┌──────────────┐  Create PR   ┌─────────────────────┐  Submit review   ┌──────────────┐
│  Principal   ├─────────────►│   GitHub PRs        ├─────────────────►│  Reviewer    │
│  Engineer    │              │  Branch: agent/...   │                  │  (Arch/SE)   │
└──────────────┘              │  Labels: in-progress │                  └──────────────┘
                              │  → ready-for-review  │
                              │  → approved / tested │
                              └─────────────────────┘
```

**PR workflow:**
1. Software Engineer creates a task branch and PR from the engineering plan
2. Assigned engineer implements and marks `ready-for-review`
3. Architect reviews for architectural alignment
4. Software Engineer reviews for quality; approves or requests changes
5. Test Engineer generates a test plan PR
6. PR is merged on approval

### 4. Shared Markdown Files (State Documents)

Repository-hosted markdown files serve as shared, versioned state:

| File | Owner | Purpose |
|------|-------|---------|
| `TeamMembers.md` | PM | Markdown table tracking all agents: name, role, status, model tier, current PR |
| `Research.md` | Researcher | Accumulated research findings with sections per topic |
| `Architecture.md` | Architect | System design document (components, data model, APIs, security) |
| `EngineeringPlan.md` | Software Engineer | Task breakdown with status, assignments, dependencies, PRs |

These files are managed via `ProjectFileManager` using the GitHub contents API (get/create/update with SHA tracking).

## Project Workflow Phases

The `WorkflowStateMachine` enforces phase-gated progression:

```
┌────────────────┐     ┌──────────┐     ┌──────────────┐     ┌─────────────────────┐
│ Initialization ├────►│ Research ├────►│ Architecture ├────►│ EngineeringPlanning │
│                │     │          │     │              │     │                     │
│ Gates:         │     │ Gates:   │     │ Gates:       │     │ Gates:              │
│ • config valid │     │ • doc    │     │ • doc ready  │     │ • plan published    │
│ • repo access  │     │ • rsch   │     │ • arch       │     │ • tasks assigned    │
│                │     │   done   │     │   complete   │     │                     │
└────────────────┘     └──────────┘     └──────────────┘     └──────────┬──────────┘
                                                                        │
    ┌────────────┐     ┌────────┐     ┌─────────┐     ┌────────────────▼──────────┐
    │ Completion │◄────┤ Review │◄────┤ Testing │◄────┤ ParallelDevelopment       │
    │            │     │        │     │         │     │                           │
    │ Gates:     │     │ Gates: │     │ Gates:  │     │ Gates:                    │
    │ • all PRs  │     │ • all  │     │ • test  │     │ • active engineering      │
    │   merged   │     │   appr │     │   plans │     │ • tasks in progress       │
    └────────────┘     └────────┘     └─────────┘     └───────────────────────────┘
```

**Signals:** Agents emit signals (e.g., `"research.complete"`, `"architecture.doc.ready"`) that satisfy gate conditions. The PM can also force phase transitions via `ForcePhase()`.

**Phase history:** All transitions are logged with timestamps for audit trails (`GetTransitionHistory()`).

## Model Allocation Strategy

Agents are assigned to model tiers based on the cognitive complexity of their role:

| Tier | Typical Provider | Default Agents | Rationale |
|------|-----------------|----------------|-----------|
| **Premium** | Claude Opus / GPT-4o | PM, Architect, Software Engineer | Leadership roles requiring deep reasoning, multi-turn planning, and nuanced judgment |
| **Standard** | Claude Sonnet / GPT-4o | Researcher, Software Engineer, Test Engineer | Strong implementation capability with good cost efficiency |
| **Budget** | GPT-4o-mini | Software Engineer | Simple, well-scoped tasks with self-validation guardrails |
| **Local** | Ollama (deepseek-coder-v2) | Configurable | Zero-cost inference for development/testing or low-priority tasks |

The `ModelRegistry` manages `Kernel` instances (Microsoft Semantic Kernel) per tier, creating them lazily and caching for reuse.

**Default provider:** When `CopilotCli.Enabled` is `true` (the default), all tiers route through the GitHub Copilot CLI binary — no API keys needed. `CopilotCliChatCompletionService` implements `IChatCompletionService`, spawning a fresh `copilot` process per request with concurrency limiting via `SemaphoreSlim`. Model IDs use dots: `claude-opus-4.7`, `claude-sonnet-4.6`, `gpt-5.2`. If `copilot` isn't found at startup, `ModelRegistry` automatically falls back to the API-key providers below.

**JSONL output mode:** When `CopilotCli.JsonOutput` is `true` (the default), the CLI receives `--output-format json` and returns JSONL (one JSON object per line with `type` and `data` fields). `CopilotCliChatCompletionService` handles this internally via `CliOutputParser.ParseJsonOutput()` + `ParseJsonUsage()`. **Important:** `CopilotCliProcessManager.ExecutePromptAsync()` returns raw stdout — any code calling it directly (e.g., the Develop wizard) MUST parse JSONL via `CliOutputParser.ParseJsonOutput()` before processing text content.

**Fallback provider routing:**
- **OpenAI / Azure OpenAI** → `AddOpenAIChatCompletion()` / `AddAzureOpenAIChatCompletion()`
- **Anthropic** → Native Anthropic SDK via Semantic Kernel connector
- **Ollama** → OpenAI-compatible API at local endpoint

## Persistence Strategy

### SQLite (Local State)

`AgentStateStore` uses `Microsoft.Data.Sqlite` for local persistence with three tables:

| Table | Purpose | Key Fields |
|-------|---------|------------|
| `agent_state` | Checkpoint snapshots (UPSERT on `agent_id`) | agent_id, role, status, current_task, serialized_state, timestamp |
| `activity_log` | Event audit trail (append-only) | agent_id, event_type, details, timestamp |
| `metrics` | Time-series numeric data | agent_id, metric_name, value, timestamp |

**Checkpoint flow:**
1. Agents periodically save state via `SaveCheckpointAsync()`
2. `GracefulShutdownHandler` saves all agent states on shutdown
3. On restart, agents can restore from `LoadCheckpointAsync()`
4. `PruneOldEntriesAsync()` provides retention cleanup (wired into HealthMonitor, 30-day retention, daily)

### Dev Platform (Coordination State)

All durable coordination state lives in the target dev platform (GitHub or Azure DevOps):

- **PRs** — Task assignments, code delivery, review status
- **Work Items / Issues** — Escalations, resource requests, blockers
- **Files** — TeamMembers.md, Research.md, Architecture.md, EngineeringPlan.md

This ensures full observability — a human can inspect the repo at any time to understand what the agents are doing and intervene via comments.

## Monitoring and Observability

### Dashboard (Blazor + SignalR)

The dashboard provides real-time monitoring through a push-based architecture:

```
┌──────────────┐  Events   ┌──────────────────┐  SignalR   ┌──────────────┐
│ AgentRegistry├──────────►│DashboardDataSvc  ├──────────►│ Browser UI   │
│ HealthMonitor│           │ (BackgroundSvc)  │           │ (Blazor SSR) │
│ DeadlockDet. │           │ Snapshot cache   │           │              │
└──────────────┘           └──────────────────┘           └──────────────┘
```

**Event flow:**
1. `AgentRegistry` fires `AgentRegistered`, `AgentUnregistered`, `AgentStatusChanged`
2. `DashboardDataService` subscribes, updates its `AgentSnapshot` cache, broadcasts via `AgentHub`
3. Blazor components subscribe to `OnChange` and re-render
4. `HealthMonitor` pushes periodic health snapshots (every 5 seconds)

### Health Monitor

The `HealthMonitor` runs as an `IHostedService` with configurable check intervals:

- **Stuck detection:** Agents in `Working` status longer than `AgentTimeoutMinutes` trigger `AgentStuck` events
- **Health snapshots:** `AgentHealthSnapshot` includes status counts, active count, and longest-running task
- **Work timing:** Tracks when each agent enters `Working` status; clears on transition to other states

### Deadlock Detection

The `DeadlockDetector` maintains a wait-for graph:

- `RecordWaiting(agentA, agentB)` adds an edge
- `ClearWaiting(agentA)` removes an edge
- `HasDeadlock()` runs DFS cycle detection on a snapshot of the graph
- Detected cycles are surfaced in the dashboard as alerts

## Security Considerations

### Secrets Management

- **API keys** should be stored in environment variables or user secrets, not committed to source control
- The `appsettings.json` template uses placeholder values
- .NET User Secrets (`dotnet user-secrets`) is recommended for development
- For production, use a secrets manager or environment variables

### GitHub Token Scope

- Three authentication methods are supported: `GhCli` (default, uses `gh auth token`), `Pat` (static Personal Access Token), and `AzureCliBearerToken` (for Azure DevOps)
- The `GhCli` method dynamically resolves tokens via the GitHub CLI — ideal for EMU accounts where PATs are restricted
- For PAT auth, the token requires only `repo` scope — no admin, org, or user scopes needed
- Tokens should be scoped to the minimum necessary repositories
- Consider using fine-grained tokens for production deployments

### AI Provider API Keys

- Each provider key is stored per model tier in configuration
- Keys are only sent to their respective provider endpoints
- Ollama runs locally and requires no external API key

### Agent Sandboxing

- All code changes are delivered via PRs on the target platform, enabling human review before merge
- Engineering agents clone repos into local workspaces (`.agents/` directory) for build and test verification
- Agent workspaces are isolated per-agent and per-repo, with hash-derived ports for test apps
- The message bus is in-process only; no network exposure
- When `CopilotCli.Enabled` is `true` (default), no API keys are needed — authentication flows through the GitHub CLI

### Rate Limiting

- `RateLimitManager` wraps all GitHub API calls with:
  - Quota tracking (slowdown at 100 remaining, block at 10)
  - Exponential backoff for 429/403 responses
  - Serialized execution via `SemaphoreSlim` to prevent burst abuse
