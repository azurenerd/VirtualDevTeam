<div align="center">

# 🤖 AgentSquad

**An AI-powered autonomous development team**

*Orchestrate multiple AI agents with distinct roles to collaboratively develop software — coordinated through GitHub PRs and Issues.*

</div>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-purple" />
  <img alt="C#" src="https://img.shields.io/badge/C%23-12-blue" />
  <img alt="Blazor" src="https://img.shields.io/badge/Blazor-Server-orange" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green" />
</p>

---

AgentSquad is a C# .NET 8 system that creates and manages a team of specialized AI agents — each with a distinct role, model tier, and set of responsibilities — that work together to build software projects. The agents coordinate entirely through GitHub PRs and Issues, with an in-process message bus for real-time orchestration and a Blazor dashboard for monitoring.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        AgentSquad.Runner (Host)                         │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │                   AgentSquad.Orchestrator                         │  │
│  │  ┌──────────────┐  ┌──────────────┐  ┌────────────────────────┐  │  │
│  │  │ AgentRegistry │  │ SpawnManager │  │ WorkflowStateMachine   │  │  │
│  │  └──────┬───────┘  └──────┬───────┘  │  Init → Research →    │  │  │
│  │         │                 │           │  Arch → Plan → Dev →  │  │  │
│  │  ┌──────┴───────┐  ┌─────┴────────┐  │  Test → Review → Done │  │  │
│  │  │HealthMonitor │  │DeadlockDetect│  └────────────────────────┘  │  │
│  │  └──────────────┘  └──────────────┘                              │  │
│  └──────────────────────────┬───────────────────────────────────────┘  │
│                             │                                          │
│  ┌──────────────────────────┴───────────────────────────────────────┐  │
│  │                  InProcessMessageBus (Channels)                  │  │
│  │              pub/sub: TaskAssignment, StatusUpdate,               │  │
│  │              HelpRequest, ResourceRequest, ReviewRequest          │  │
│  └───┬──────┬──────┬──────────┬──────────┬──────────┬───────────┬──┘  │
│      │      │      │          │          │          │           │      │
│  ┌───┴──┐┌──┴───┐┌─┴────┐┌───┴────┐┌────┴───┐┌────┴───┐┌─────┴──┐   │
│  │  PM  ││Rsrchr││Archt ││Prncpl  ││Senior  ││Junior  ││  Test  │   │
│  │Agent ││Agent ││Agent ││Eng.    ││Eng.(n) ││Eng.(n) ││  Eng.  │   │
│  │      ││      ││      ││Agent   ││Agents  ││Agents  ││ Agent  │   │
│  └───┬──┘└──┬───┘└─┬────┘└───┬────┘└────┬───┘└────┬───┘└────┬───┘   │
│      │      │      │          │          │         │          │       │
│  ┌───┴──────┴──────┴──────────┴──────────┴─────────┴──────────┴───┐   │
│  │                    AgentSquad.Core                              │   │
│  │  GitHub Service │ Persistence │ Config │ Shared Models         │   │
│  └──────────────────────┬─────────────────────────────────────────┘   │
└─────────────────────────┼────────────────────────────────────────────┘
                          │
           ┌──────────────┴──────────────┐
           │        GitHub (Remote)       │
           │  PRs │ Issues │ Repo Files   │
           │  TeamMembers.md              │
           │  Research.md                 │
           │  Architecture.md             │
           │  EngineeringPlan.md          │
           └─────────────────────────────┘

┌──────────────────────────────────────────┐
│        AgentSquad.Dashboard (Blazor)     │
│  SignalR ← AgentHub ← DashboardData     │
│  Pages: Overview │ Detail │ Metrics      │
│         GitHub Feed │ Team Viz           │
└──────────────────────────────────────────┘
```

## Features

- **7 Specialized Agent Roles** — Program Manager, Researcher, Architect, Principal Engineer, Senior Engineer, Junior Engineer, and Test Engineer — each with distinct responsibilities and AI behaviors
- **Multi-Model Support** — Anthropic Claude, OpenAI GPT, Azure OpenAI, and local Ollama models with configurable tier assignments (premium / standard / budget / local)
- **GitHub-Native Coordination** — Agents communicate and deliver work through real GitHub PRs and Issues with structured conventions for titles, labels, and branches
- **Dynamic Agent Scaling** — The PM can request additional Senior/Junior Engineers at runtime; the Orchestrator enforces configurable limits
- **Real-Time Blazor Dashboard** — Monitor agent status, team topology, GitHub activity feed, and system metrics with animated team visualization and SignalR push updates
- **SQLite State Persistence** — Checkpoint agent state and activity logs for graceful shutdown and recovery
- **Deadlock Detection** — Wait-for graph analysis detects circular agent dependencies
- **Health Monitoring** — Background service detects stuck agents, tracks task duration, and reports system health
- **Phase-Gated Workflow** — State machine enforces project progression: Initialization → Research → Architecture → Engineering → Development → Testing → Review → Completion
- **Configurable Token Budgets & Rate Limiting** — Per-model token limits, daily budget caps, and GitHub API rate limit management with exponential backoff

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- A [GitHub Personal Access Token](https://github.com/settings/tokens) with `repo` scope
- At least one AI provider API key:
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

Edit `src/AgentSquad.Runner/appsettings.json` with your settings, or run the interactive wizard on first launch:

```json
{
  "AgentSquad": {
    "Project": {
      "Name": "my-project",
      "Description": "A brief description of what to build",
      "GitHubRepo": "owner/repo",
      "GitHubToken": "ghp_...",
      "DefaultBranch": "main"
    },
    "Models": {
      "premium":  { "Provider": "Anthropic", "Model": "claude-opus-4-20250514", "ApiKey": "sk-ant-..." },
      "standard": { "Provider": "Anthropic", "Model": "claude-sonnet-4-20250514", "ApiKey": "sk-ant-..." },
      "budget":   { "Provider": "OpenAI",    "Model": "gpt-4o-mini",            "ApiKey": "sk-..." },
      "local":    { "Provider": "Ollama",    "Model": "deepseek-coder-v2",      "Endpoint": "http://localhost:11434" }
    }
  }
}
```

### 3. Run

```bash
cd src/AgentSquad.Runner
dotnet run
```

### 4. Open the Dashboard

Navigate to `http://localhost:5000` to view the real-time monitoring dashboard.

## Configuration

Configuration lives in `src/AgentSquad.Runner/appsettings.json` under the `AgentSquad` section:

| Section | Description |
|---------|-------------|
| `Project` | GitHub repo, PAT, project name/description, default branch |
| `Models` | Model tier definitions — provider, model name, API key, endpoint, temperature, max tokens |
| `Agents` | Per-role model tier assignments and token limits |
| `Limits` | Max additional engineers, daily token budget, poll intervals, timeouts, concurrency |
| `Dashboard` | Dashboard port and SignalR toggle |

See [docs/setup-guide.md](docs/setup-guide.md) for a detailed walkthrough of every configuration option.

## Agent Roles

| Role | Default Model Tier | Responsibilities |
|------|-------------------|------------------|
| **Program Manager** | `premium` | Orchestrates team, manages resources, triages blockers, reviews PR alignment, updates tracking |
| **Researcher** | `standard` | Conducts multi-turn technical research, produces Research.md with findings and recommendations |
| **Architect** | `premium` | Designs system architecture (5-turn AI conversation), produces Architecture.md, reviews PRs for alignment |
| **Principal Engineer** | `premium` | Creates engineering plan, assigns tasks to team, handles high-complexity work, reviews engineer PRs |
| **Senior Engineer** | `standard` | Implements medium-complexity tasks with 3-turn AI (plan → implement → self-review) |
| **Junior Engineer** | `budget` | Implements low-complexity tasks with self-validation retries, escalates when task exceeds capability |
| **Test Engineer** | `standard` | Scans for untested PRs, generates test plans, creates test PRs with coverage documentation |

See [docs/agent-behaviors.md](docs/agent-behaviors.md) for detailed behavior documentation for each agent.

## Dashboard

The Blazor Server dashboard provides real-time visibility into the agent team:

| Page | Route | Description |
|------|-------|-------------|
| **Agent Overview** | `/` | Grid of all agents with status badges, summary stats, and deadlock alerts |
| **Agent Detail** | `/agent/{id}` | Deep dive into a single agent with pause/resume/terminate controls |
| **GitHub Feed** | `/github` | Timeline of PRs, issues, and comments generated by the agents |
| **Metrics** | `/metrics` | System health, utilization ring chart, status breakdown, longest-running tasks |
| **Team Viz** | `/team` | Visual office-metaphor layout with agent desks, status dots, and connection lines |

<!-- TODO: Add dashboard screenshots here -->

## Project Structure

```
AgentSquad/
├── AgentSquad.sln
├── src/
│   ├── AgentSquad.Core/              # Shared abstractions and infrastructure
│   │   ├── Agents/                   # AgentBase, IAgent, AgentRole, AgentStatus, AgentMessage
│   │   ├── Configuration/            # Config models, validation, wizard, ModelRegistry
│   │   ├── GitHub/                   # GitHubService, PR/Issue workflows, rate limiting
│   │   ├── Messaging/                # IMessageBus, InProcessMessageBus (Channels-based)
│   │   └── Persistence/              # AgentStateStore (SQLite), ProjectFileManager
│   │
│   ├── AgentSquad.Agents/            # Concrete agent implementations
│   │   ├── ProgramManagerAgent.cs    # Team orchestration and blocker triage
│   │   ├── ResearcherAgent.cs        # Multi-turn technical research
│   │   ├── ArchitectAgent.cs         # System architecture design
│   │   ├── PrincipalEngineerAgent.cs # Engineering planning and task assignment
│   │   ├── SeniorEngineerAgent.cs    # Medium-complexity implementation
│   │   ├── JuniorEngineerAgent.cs    # Low-complexity with self-validation
│   │   ├── TestEngineerAgent.cs      # Test plan generation
│   │   ├── AgentFactory.cs           # DI-based agent creation
│   │   └── AgentTracking.cs          # Agent status DTO for PM tracking
│   │
│   ├── AgentSquad.Orchestrator/      # Runtime coordination
│   │   ├── AgentRegistry.cs          # Thread-safe agent lifecycle registry
│   │   ├── AgentSpawnManager.cs      # Dynamic agent spawning with limits
│   │   ├── WorkflowStateMachine.cs   # Phase-gated project progression
│   │   ├── DeadlockDetector.cs       # Wait-for graph cycle detection
│   │   ├── HealthMonitor.cs          # Stuck agent detection and health snapshots
│   │   └── GracefulShutdownHandler.cs# Clean shutdown with state persistence
│   │
│   ├── AgentSquad.Dashboard/         # Real-time monitoring UI
│   │   ├── Components/Pages/         # Blazor pages (Overview, Detail, Metrics, etc.)
│   │   ├── Hubs/AgentHub.cs          # SignalR hub for push updates
│   │   └── Services/                 # DashboardDataService (cache + broadcast)
│   │
│   └── AgentSquad.Runner/            # Application host
│       ├── Program.cs                # DI setup and service registration
│       ├── AgentSquadWorker.cs       # Bootstrap: spawns core agents
│       └── appsettings.json          # Configuration file
│
├── tests/
│   ├── AgentSquad.Core.Tests/
│   ├── AgentSquad.Agents.Tests/
│   └── AgentSquad.Integration.Tests/
│
└── docs/
    ├── setup-guide.md
    ├── architecture.md
    └── agent-behaviors.md
```

## Development

### Build

```bash
dotnet build AgentSquad.sln
```

### Test

```bash
dotnet test AgentSquad.sln
```

### Run in Development

```bash
cd src/AgentSquad.Runner
dotnet run --environment Development
```

The dashboard runs on the configured port (default `5000`). The Runner bootstraps the core agents and enters a steady-state loop where the PM manages all further coordination.

### Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Make your changes with tests
4. Run `dotnet build && dotnet test` to verify
5. Submit a pull request

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 8 / C# 12 |
| AI Integration | Microsoft Semantic Kernel |
| AI Providers | Anthropic Claude, OpenAI GPT, Azure OpenAI, Ollama |
| GitHub Integration | Octokit.net |
| Dashboard | Blazor Server + SignalR |
| Persistence | SQLite via Microsoft.Data.Sqlite |
| Message Bus | System.Threading.Channels (in-process pub/sub) |
| Dependency Injection | Microsoft.Extensions.DependencyInjection |
| Hosting | Microsoft.Extensions.Hosting (Generic Host) |

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
