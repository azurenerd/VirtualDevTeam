<div align="center">

# 🤖 AgentSquad

**An AI-powered autonomous development team that builds software end-to-end**

*Give it a project description — it researches, architects, plans, codes, tests, and delivers through real GitHub PRs and Issues, with human oversight at every critical gate.*

</div>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-purple" />
  <img alt="C#" src="https://img.shields.io/badge/C%23-12-blue" />
  <img alt="Blazor" src="https://img.shields.io/badge/Blazor-Server-orange" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green" />
</p>

---

AgentSquad is a .NET 8 multi-agent AI system that manages a full software development team — from PM through Test Engineer — to autonomously build software projects. You provide a project description and a GitHub repo; AgentSquad handles research, architecture, engineering planning, parallel implementation, multi-tier testing, code review, and delivery. Every artifact lives in GitHub as real PRs and Issues. A Blazor dashboard gives you real-time visibility, and configurable human gates let you control how much autonomy the team has.

## Key Capabilities

- **Full Development Lifecycle** — From a single project description, agents autonomously produce Research.md → PMSpec.md → Architecture.md → EngineeringPlan.md → implemented PRs → test PRs → reviewed and merged code
- **Dynamic SME Agents** — The PM and PE can spawn Subject Matter Expert agents on-demand (security auditors, database specialists, etc.) with custom personas, MCP tool servers, and external knowledge sources — driven by AI assessment of project needs
- **Multi-Tier Test Automation** — The Test Engineer generates and runs unit tests, integration tests, and Playwright UI/E2E tests in local workspaces, with AI-powered failure classification (test bug vs source bug) and automatic retry/fix cycles
- **Human Gate Checkpoints** — Configurable gates pause workflow at critical points for human approval. Three presets (Full Auto, Supervised, Full Control) with hot-reloadable config via `IOptionsMonitor`
- **GitHub Copilot CLI as AI Backend** — All model tiers route through the `copilot` CLI binary by default — no API keys required. Process-per-request with concurrency limiting, MCP server passthrough, and automatic fallback to direct API providers
- **Agent Memory & Learning** — SQLite-backed persistent memory records agent decisions, learnings, and operator instructions. Agents recall up to 30 recent entries across restarts for context continuity
- **Vision-Based PR Review** — AI reviewers download and analyze screenshots from PR comments using base64-embedded images, catching broken UIs that text-only reviews miss
- **Local Build & Test Verification** — Agents clone repos into local workspaces and run real `dotnet build`, `dotnet test`, and Playwright commands — not just AI-generated code, but verified code
- **MCP Server Integration** — Agents can be equipped with Model Context Protocol tool servers (code search, documentation, issue tracking) that are automatically configured in the Copilot CLI's `mcp.json`
- **Knowledge Pipeline** — Agents fetch, extract, and summarize external documentation (HTML/Markdown URLs) with per-tier budget limits, injecting domain knowledge directly into system prompts
- **Custom Agent Definitions** — Define new agent roles via configuration (persona, tools, knowledge links) without writing code. The `CustomAgent` base class handles the rest
- **Externalized Prompt Templates** — All ~95 agent prompts live in editable `.md` files under `prompts/`, with YAML frontmatter metadata and `{{variable}}` substitution. Change agent behavior without recompiling — templates are loaded at runtime with in-memory caching and hardcoded fallbacks for resilience
- **Dynamic Team Scaling** — The PM analyzes project requirements and proposes an optimal team composition (agent counts, SME specialists), enforced through human gate approval
- **Crash-Resilient Sessions** — CLI session IDs persist to SQLite so agents resume the same Copilot conversation after runner restarts, preserving full AI context for rework
- **15-Page Real-Time Dashboard** — Blazor Server UI with agent overview, project timeline, metrics, health monitor, PR/issue browsers, engineering plan graph, team visualization, director CLI terminal, and approval management
- **Phase-Gated Workflow** — State machine enforces linear progression: Initialization → Research → Architecture → Planning → Development → Testing → Review → Finalization
- **GitHub-Native Coordination** — Dual-layer communication: in-process message bus (<1ms, real-time) + GitHub API (durable PRs/Issues, human-visible). All work products are real GitHub artifacts
- **Multi-Model Support** — Anthropic Claude, OpenAI GPT, Azure OpenAI, and local Ollama with four configurable tiers (premium / standard / budget / local) assigned per agent role
- **Operational Resilience** — 60s TTL API cache (~90% reduction in GitHub calls), deadlock detection via wait-for graph analysis, health monitoring with stuck-agent detection, graceful shutdown with state persistence

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     AgentSquad.Runner (Host, port 5050)                      │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                     AgentSquad.Orchestrator                           │  │
│  │  ┌───────────────┐  ┌───────────────┐  ┌──────────────────────────┐  │  │
│  │  │ AgentRegistry  │  │ SpawnManager  │  │ WorkflowStateMachine     │  │  │
│  │  └───────┬───────┘  └───────┬───────┘  │ Init → Research → Arch → │  │  │
│  │          │                  │           │ Plan → Dev → Test →      │  │  │
│  │  ┌───────┴───────┐  ┌──────┴────────┐  │ Review → Finalization    │  │  │
│  │  │ HealthMonitor  │  │DeadlockDetect │  └──────────────────────────┘  │  │
│  │  └───────────────┘  └───────────────┘                                │  │
│  └────────────────────────────┬─────────────────────────────────────────┘  │
│                               │                                            │
│  ┌────────────────────────────┴─────────────────────────────────────────┐  │
│  │                   InProcessMessageBus (Channels)                     │  │
│  │          pub/sub: TaskAssignment, StatusUpdate, HelpRequest,         │  │
│  │          ResourceRequest, ReviewRequest, SpawnSme, SmeResult         │  │
│  └──┬──────┬──────┬──────────┬──────────┬──────────┬──────────┬─────┬──┘  │
│     │      │      │          │          │          │          │     │      │
│  ┌──┴──┐┌──┴──┐┌──┴───┐┌────┴───┐┌─────┴──┐┌─────┴──┐┌─────┴─┐┌──┴───┐  │
│  │ PM  ││Rschr││Archt ││ PE     ││SE (×n) ││JE (×n) ││ TE    ││ SME  │  │
│  │Agent││Agent││Agent ││Agent   ││Agents  ││Agents  ││Agent  ││(×n)  │  │
│  └──┬──┘└──┬──┘└──┬───┘└────┬───┘└────┬───┘└────┬───┘└────┬──┘└──┬───┘  │
│     └──────┴──────┴─────────┴─────────┴─────────┴─────────┴──────┘      │
│              GitHubService (60s TTL cache) · REST API                     │
│              CopilotCliChatCompletionService · MCP Servers                │
│              AgentStateStore (SQLite) · AgentMemoryStore                  │
│              LocalWorkspace · BuildRunner · TestRunner · Playwright       │
└──────────────────────────┬────────────────┬─────────────────────────────┘
                           │                │
            ┌──────────────┴───────┐  ┌─────┴──────────────────────────────┐
            │   GitHub (Remote)    │  │  Dashboard.Host (port 5051)        │
            │  PRs · Issues · Code │  │  Blazor Server (standalone)        │
            │  Research.md         │  │  15 pages: Overview, Timeline,     │
            │  PMSpec.md           │  │  Metrics, Health, PRs, Issues,     │
            │  Architecture.md     │  │  Eng Plan, Team, Director CLI,     │
            │  EngineeringPlan.md  │  │  Approvals, Config, Agent Detail,  │
            │  TeamComposition.md  │  │  Reasoning, GitHub Feed, Repo      │
            └──────────────────────┘  └────────────────────────────────────┘
```

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- A [GitHub Personal Access Token](https://github.com/settings/tokens) with `repo` scope
- [GitHub Copilot CLI](https://github.com/features/copilot) v1.0.18+ (default AI provider — no API keys needed)
- **Or** at least one AI provider API key as fallback:
  - [Anthropic API key](https://console.anthropic.com/) (recommended for premium tier)
  - [OpenAI API key](https://platform.openai.com/api-keys)
  - [Ollama](https://ollama.ai/) installed locally (for local/free tier)

### 1. Clone and Build

```bash
git clone <repository-url>
cd AgentSquad
dotnet build
```

### 2. Configure

Edit `src/AgentSquad.Runner/appsettings.json` with your project settings (non-secret values like project name, description, repo, model tiers, etc. are committed to git):

```json
{
  "AgentSquad": {
    "Project": {
      "Name": "my-project",
      "Description": "A brief description of what to build",
      "GitHubRepo": "owner/repo",
      "DefaultBranch": "main"
    },
    "CopilotCli": {
      "Enabled": true,
      "MaxConcurrentRequests": 4
    }
  }
}
```

**Store secrets using .NET User Secrets** (never committed to git):

```bash
cd src/AgentSquad.Runner

# Required: GitHub PAT
dotnet user-secrets set "AgentSquad:Project:GitHubToken" "github_pat_..."

# Optional: API keys (only if not using Copilot CLI)
dotnet user-secrets set "AgentSquad:Models:premium:ApiKey" "sk-ant-..."
dotnet user-secrets set "AgentSquad:Models:standard:ApiKey" "sk-ant-..."
dotnet user-secrets set "AgentSquad:Models:budget:ApiKey" "sk-..."
```

> **Note:** User secrets are stored locally at `%APPDATA%\Microsoft\UserSecrets\` (Windows) or `~/.microsoft/usersecrets/` (macOS/Linux). Run the `dotnet user-secrets set` commands on each machine. Alternatively, use environment variables with `__` as separator: `AGENTSQUAD__PROJECT__GITHUBTOKEN=github_pat_...`
```

When `CopilotCli.Enabled` is `true` (default), all model tiers route through the `copilot` binary — no API keys needed. For direct API access, configure providers per tier:

```json
{
  "AgentSquad": {
    "Models": {
      "premium":  { "Provider": "Anthropic", "Model": "claude-opus-4-20250514", "ApiKey": "sk-ant-..." },
      "standard": { "Provider": "Anthropic", "Model": "claude-sonnet-4-20250514", "ApiKey": "sk-ant-..." },
      "budget":   { "Provider": "OpenAI",    "Model": "gpt-4o-mini",            "ApiKey": "sk-..." },
      "local":    { "Provider": "Ollama",    "Model": "qwen2.5-coder:14b",     "Endpoint": "http://localhost:11434" }
    }
  }
}
```

### 3. Run

```bash
cd src/AgentSquad.Runner
dotnet run
```

### 4. Monitor

The dashboard runs embedded at `http://localhost:5050`, or standalone:

```bash
cd src/AgentSquad.Dashboard.Host
dotnet run    # → http://localhost:5051
```

Standalone mode lets you restart the dashboard without disrupting running agents.

## How It Works

```
You provide a project description
         │
         ▼
┌─ Initialization ──────────────────────────────────────────────────────┐
│  PM spawns → Researcher, Architect, PE, Engineers, Test Engineer      │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Research ────────────────────────────────────────────────────────────┐
│  Researcher conducts multi-turn technical research → Research.md     │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Architecture ────────────────────────────────────────────────────────┐
│  PM writes PMSpec.md (business spec with user stories)               │
│  Architect designs system → Architecture.md (reviewed by PE)         │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Engineering Planning ────────────────────────────────────────────────┐
│  PE decomposes Architecture into tasks with dependencies             │
│  PM proposes team composition (core agents + SME specialists)        │
│  Human gate → approve team → EngineeringPlan.md                      │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Parallel Development ────────────────────────────────────────────────┐
│  PE assigns tasks to engineers based on complexity                    │
│  Engineers create PRs with implementation (local build verification)  │
│  PE + Architect review PRs → approve or request rework               │
│  SME agents provide specialist input on-demand                       │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Testing ─────────────────────────────────────────────────────────────┐
│  Test Engineer scans approved PRs → generates test strategy          │
│  Creates test PRs: unit → integration → UI/E2E (Playwright)         │
│  Classifies failures as test bugs vs source bugs → routes rework     │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ Review & Finalization ───────────────────────────────────────────────┐
│  PM conducts final review for business alignment                     │
│  All PRs merged → project complete                                   │
└──────────────────────────────────────────────────────────────────────┘
```

## Agent Roles

### Core Team (always present)

| Role | Tier | Responsibilities |
|------|------|------------------|
| **Program Manager** | `premium` | Orchestrates team composition, writes PMSpec with user stories, triages blockers, reviews PRs for business alignment, manages escalations to human executive |
| **Researcher** | `standard` | Multi-turn technical research, technology evaluation, feasibility analysis → produces Research.md |
| **Architect** | `premium` | System design via 5-turn AI conversation, API/data modeling, technology selection → produces Architecture.md, reviews PRs for architectural compliance |
| **Principal Engineer** | `premium` | Decomposes architecture into engineering tasks, assigns work by complexity, conducts rigorous code reviews with scoring rubrics, handles high-complexity PRs directly |
| **Senior Engineer** | `standard` | Implements medium-complexity tasks with plan → implement → self-review pipeline. Local build/test verification before PR submission |
| **Junior Engineer** | `budget` | Implements low-complexity tasks with self-validation retries. Escalates tasks that exceed capability threshold |
| **Test Engineer** | `standard` | Three-tier test generation (unit → integration → UI/E2E), testability assessment, source-bug classification, coverage tracking |

### Dynamic Specialists (spawned on-demand)

| Type | How Created | Lifecycle |
|------|-------------|-----------|
| **Custom Agents** | Defined in config with role description, MCP servers, knowledge links | Persistent — run alongside core team |
| **SME Agents** | AI-generated or from templates when specialist knowledge is needed | OnDemand, Continuous, or OneShot — retire when work completes |
| **Additional Engineers** | PM requests scaling; Orchestrator enforces limits | Persistent — fill engineer slots dynamically |

See [docs/agent-behaviors.md](docs/agent-behaviors.md) for detailed behavior documentation.

## Configuration

Configuration lives in `src/AgentSquad.Runner/appsettings.json` under the `AgentSquad` section (committed to git). Secrets (GitHub PAT, API keys) are stored separately via [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) and never committed.

| Section | Description |
|---------|-------------|
| `Project` | GitHub repo, PAT, project name/description, default branch, executive username |
| `CopilotCli` | Enable/disable Copilot CLI provider, max concurrent requests |
| `Models` | Model tier definitions — provider, model name, API key, endpoint, temperature, max tokens |
| `Agents` | Per-role model tier assignments, MCP servers, knowledge links, custom prompts |
| `McpServers` | Global MCP server definitions (name, command, transport, capabilities) |
| `SmeAgents` | SME templates, max instances, spawn limits, definition persistence |
| `Limits` | Max additional engineers, daily token budget, poll intervals, timeouts, concurrency |
| `Workspace` | Local build/test paths, commands, per-tier test timeouts, max retries |
| `Gates` | Human gate configuration, presets (FullAuto / Supervised / FullControl) |
| `Dashboard` | Dashboard port and SignalR toggle |

See [docs/setup-guide.md](docs/setup-guide.md) for a detailed walkthrough of every configuration option.

## Dashboard

The Blazor Server dashboard provides real-time visibility into the agent team with 15 pages. Runs embedded in the Runner or as a standalone process.

| Page | Route | Description |
|------|-------|-------------|
| **Agent Overview** | `/` | Grid of all agents with status badges, model selectors, chat, error tracking, and deadlock alerts |
| **Project Timeline** | `/timeline` | Visual workflow timeline with PM/Engineering views, phase grouping, PR/Issue type indicators |
| **Metrics** | `/metrics` | System health, utilization ring chart, status breakdown, longest-running tasks |
| **Health Monitor** | `/health` | Real-time health checks, stuck agent detection, system diagnostics |
| **Pull Requests** | `/pullrequests` | GitHub PR browser with state filters, labels, and branch info |
| **Issues** | `/issues` | GitHub issue browser with label/assignee filters and sorting |
| **Engineering Plan** | `/engineering-plan` | Interactive Cytoscape.js dependency graph of engineering tasks |
| **Team View** | `/team` | Visual office-metaphor layout with agent desks and connection lines |
| **Director CLI** | `/director-cli` | Terminal interface for issuing executive directives to agents |
| **Approvals** | `/approvals` | Human gate approval management with filter buttons |
| **Configuration** | `/configuration` | Settings editor, gate presets, SME management, GitHub cleanup |
| **Agent Detail** | `/agent/{id}` | Deep dive into a single agent with pause/resume/terminate controls |
| **Agent Reasoning** | `/reasoning` | View agent decision-making chains and AI conversation history |
| **GitHub Feed** | `/github-feed` | Live feed of GitHub activity across the project |
| **Repository** | `/repository` | Browse repository file tree and content |

## Project Structure

```
AgentSquad/
├── AgentSquad.sln
├── src/
│   ├── AgentSquad.Core/                # Shared abstractions and infrastructure
│   │   ├── Agents/                     # AgentBase, IAgent, AgentRole, AgentStatus, messages
│   │   ├── AI/                         # CopilotCli provider, MCP config, knowledge pipeline
│   │   ├── Configuration/              # Config models, SME definitions, MCP server defs
│   │   ├── GitHub/                     # GitHubService, rate limiting, PR/Issue workflows
│   │   ├── Messaging/                  # IMessageBus, InProcessMessageBus (Channels)
│   │   ├── Persistence/                # AgentStateStore, AgentMemoryStore (SQLite)
│   │   └── Services/                   # McpServerRegistry, TeamComposer, SmeDefinitions
│   │
│   ├── AgentSquad.Agents/              # Concrete agent implementations
│   │   ├── ProgramManagerAgent.cs      # Team composition, PMSpec, blocker triage
│   │   ├── ResearcherAgent.cs          # Multi-turn technical research
│   │   ├── ArchitectAgent.cs           # System architecture design + PR review
│   │   ├── PrincipalEngineerAgent.cs   # Eng planning, task assignment, code review
│   │   ├── EngineerAgentBase.cs        # Shared engineer logic (sessions, rework, build)
│   │   ├── SeniorEngineerAgent.cs      # Medium-complexity implementation
│   │   ├── JuniorEngineerAgent.cs      # Low-complexity with escalation
│   │   ├── TestEngineerAgent.cs        # Multi-tier test generation + execution
│   │   ├── CustomAgent.cs              # Config-driven custom agent roles
│   │   ├── SmeAgent.cs                 # Dynamic SME specialist agents
│   │   └── AgentFactory.cs             # DI-based agent creation
│   │
│   ├── AgentSquad.Orchestrator/        # Runtime coordination
│   │   ├── AgentRegistry.cs            # Thread-safe agent lifecycle (ConcurrentDictionary)
│   │   ├── AgentSpawnManager.cs        # Dynamic spawning with slot reservation + SME limits
│   │   ├── WorkflowStateMachine.cs     # Phase-gated project progression
│   │   ├── DeadlockDetector.cs         # Wait-for graph DFS cycle detection
│   │   ├── HealthMonitor.cs            # Stuck agent detection and health snapshots
│   │   └── GracefulShutdownHandler.cs  # Clean shutdown with state persistence
│   │
│   ├── AgentSquad.Dashboard/           # Real-time monitoring UI (shared library)
│   │   ├── Components/Pages/           # 15 Blazor pages
│   │   ├── Hubs/AgentHub.cs            # SignalR hub for push updates
│   │   └── Services/                   # IDashboardDataService, HttpDashboardDataService
│   │
│   ├── AgentSquad.Dashboard.Host/      # Standalone dashboard process (port 5051)
│   └── AgentSquad.Runner/              # Application host (port 5050)
│       ├── Program.cs                  # DI setup, REST API, service registration
│       └── AgentSquadWorker.cs         # Bootstrap: spawns core agents in phased sequence
│
├── tests/
│   ├── AgentSquad.Core.Tests/          # 258 unit tests
│   ├── AgentSquad.Agents.Tests/        # 22 agent behavior tests
│   └── AgentSquad.Integration.Tests/   # 37 integration tests
│
├── scripts/
│   ├── start-runner.ps1                # Start the Runner process
│   ├── stop-runner.ps1                 # Stop the Runner process
│   ├── runner-status.ps1               # Check Runner health
│   ├── start-dashboard.ps1             # Start standalone dashboard
│   ├── fresh-reset.ps1                 # Full cleanup: close PRs/Issues, delete branches, reset DB
│   └── reset-runner.ps1                # Reset Runner state
│
├── prompts/                            # Externalized AI prompt templates (.md)
│   ├── researcher/                     # 10 templates (research phases, synthesis)
│   ├── pm/                             # 21 templates (specs, stories, reviews)
│   ├── architect/                      # 13 templates (architecture design, review)
│   ├── engineer-base/                  # 13 shared templates (planning, build-fix, rework)
│   ├── senior-engineer/                # 2 templates (implementation, self-review)
│   ├── junior-engineer/                # 1 template (implementation)
│   ├── principal-engineer/             # 14 templates (plan gen, code review, integration)
│   ├── test-engineer/                  # 17 templates (test gen, tiers, failure mgmt)
│   └── custom/                         # 4 templates (task/issue processing)
│
└── docs/
    ├── Requirements.md                 # 30-section requirements with workflow scenarios
    ├── agent-behaviors.md              # Detailed per-agent behavior documentation
    ├── architecture.md                 # System architecture documentation
    ├── setup-guide.md                  # Configuration walkthrough
    ├── PromptExternalizationPlan.md    # Plan for externalizing AI prompts to templates
    ├── PEParallelismEnhancements.md    # Fleet-style parallelism enhancements
    ├── MonitorPrompt.md                # Dashboard monitoring expectations
    ├── Research.md                     # Technical research findings
    └── LessonsLearned.md              # Operational lessons from 80+ runs
```

## Development

### Build

```bash
dotnet build AgentSquad.sln
```

### Test

```bash
# Run all 317 tests
dotnet test AgentSquad.sln

# Run a specific test project
dotnet test tests/AgentSquad.Core.Tests

# Run a specific test by name
dotnet test tests/AgentSquad.Core.Tests --filter "FullyQualifiedName~McpServerRegistryTests"
```

### Run

```bash
cd src/AgentSquad.Runner
dotnet run
```

### Fresh Reset

To clean all GitHub artifacts and start over:

```powershell
./scripts/fresh-reset.ps1
```

This closes all PRs/Issues, deletes agent branches, removes repo files, and resets the SQLite database.

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 8 / C# 12 |
| AI Integration | Microsoft Semantic Kernel |
| AI Providers | GitHub Copilot CLI (default), Anthropic Claude, OpenAI GPT, Azure OpenAI, Ollama |
| Tool Integration | Model Context Protocol (MCP) servers via Copilot CLI |
| GitHub Integration | Octokit.net |
| Dashboard | Blazor Server + SignalR (embedded or standalone) |
| Persistence | SQLite via Microsoft.Data.Sqlite |
| Agent Memory | SQLite-backed persistent recall (decisions, learnings, instructions) |
| Message Bus | System.Threading.Channels (bounded, in-process pub/sub) |
| Local Testing | dotnet CLI, Playwright (UI/E2E) |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| Hosting | Microsoft.Extensions.Hosting (Generic Host) |

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
