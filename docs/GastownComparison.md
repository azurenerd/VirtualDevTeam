# AgentSquad vs Gas Town: Multi-Agent Orchestration Comparison

> **Purpose:** Side-by-side comparison of two multi-agent AI coding systems — AgentSquad (azurenerd/VirtualDevTeam) and Gas Town (gastownhall/gastown). Both coordinate multiple AI agents to build software, but with fundamentally different architectures and philosophies.

---

## Executive Summary

| Dimension | AgentSquad | Gas Town |
|-----------|-----------|----------|
| **Philosophy** | Opinionated SDLC pipeline — agents fill real software team roles | Flexible workspace manager — agents are generic workers you assign tasks to |
| **Language** | C# / .NET 8 | Go 1.25 |
| **Agent Identity** | 5 core roles (PM, Researcher, Architect, SE, TE) + dynamic SE pool | Generic "polecats" (workers) + named infrastructure roles (Mayor, Witness, Deacon) |
| **Coordination** | Centralized orchestrator with phase-gated workflow | Decentralized — Mayor provides guidance, agents self-coordinate via mail/nudge |
| **Communication** | In-process message bus (System.Threading.Channels) + GitHub API | tmux sessions + git-backed mailboxes + nudge (inter-process messaging) |
| **Work Tracking** | GitHub Issues + PRs + SQLite state | Beads (git-backed issue tracker) + Convoys (work bundles) + Dolt database |
| **Scale Target** | 7-10 agents (focused team simulation) | 20-50+ agents (swarm-scale coordination) |
| **AI Runtime** | Copilot CLI (process-per-request via Semantic Kernel) | Multi-runtime: Claude Code, Copilot CLI, Codex, Cursor, Gemini, etc. |
| **Dashboard** | Blazor Server (real-time SignalR, Grafana-style dark theme) | htmx web dashboard + TUI terminal feed (`gt feed`) |
| **Persistence** | SQLite + GitHub (dual-layer) | Git worktrees ("hooks") + Dolt (SQL database) |
| **License** | Private | Open source |

---

## Similarities

### 1. Multi-Agent AI Orchestration
Both systems coordinate multiple AI coding agents to build software collaboratively. Both recognize that single-agent AI coding breaks down for complex projects requiring parallel work, specialization, and coordination.

### 2. Persistent Work State
Both solve the "agent amnesia" problem — AI agents lose context on restart.
- **AgentSquad**: SQLite `agent_state` table + GitHub Issues/PRs as durable state
- **Gas Town**: Git worktree "hooks" + Beads ledger for issue state

### 3. GitHub Integration
Both use GitHub as a durable collaboration layer:
- AgentSquad: PRs for code, Issues for tasks, Comments for discussion, Labels for workflow state
- Gas Town: Git branches for work, GitHub Issues via Beads, merge queue via Refinery

### 4. Human-in-the-Loop Gates
Both support human oversight at key decision points:
- **AgentSquad**: Configurable human gates per phase (PM Spec, Architecture, Engineering Plan, Final PR Approval)
- **Gas Town**: `--human` flag on convoys for human approval, escalation routing through Mayor → Overseer

### 5. Monitoring & Dashboard
Both provide real-time visibility into agent activity:
- **AgentSquad**: Blazor Server dashboard with agent cards, timeline, health monitor, engineering plan graph
- **Gas Town**: `gt feed` TUI with agent tree, convoy panel, event stream + web dashboard via `gt dashboard`

### 6. Stuck Agent Detection
Both detect and recover from stuck agents:
- **AgentSquad**: `DeadlockDetector` (DFS wait-for graph), `HealthMonitor`
- **Gas Town**: Witness (per-rig), Deacon (cross-rig patrol), Dogs (maintenance), "GUPP Violation" detection in feed

### 7. Escalation Mechanisms
Both route problems that agents can't solve to higher authority:
- **AgentSquad**: Engineer → PM clarification → Executive request Issues
- **Gas Town**: `gt escalate` with severity routing (CRITICAL/HIGH/MEDIUM) through Deacon → Mayor → Overseer

### 8. Merge Queue / Code Integration
Both manage how agent code gets into main:
- **AgentSquad**: SE's `MergeTestedPRsAsync` with label-gated squash merge
- **Gas Town**: Refinery — Bors-style bisecting merge queue with verification gates

---

## Key Differences

### 1. Agent Model: Roles vs Generic Workers

**AgentSquad** assigns agents to **fixed SDLC roles** that mirror a real software team:
- PM → Researcher → Architect → Software Engineer → Software Engineers → Test Engineer
- Each role has specialized AI prompts, different model tiers, and distinct workflow behaviors
- Agents produce domain-specific artifacts (PMSpec.md, Architecture.md, EngineeringPlan.md)

**Gas Town** uses **generic worker agents** ("polecats") with flexible assignment:
- Workers are interchangeable — any polecat can be assigned any bead (task)
- Specialization comes from the task definition and runtime configuration, not agent identity
- Infrastructure roles (Mayor, Witness, Deacon, Refinery) handle coordination, not software engineering tasks

**Implication:** AgentSquad produces richer, more structured output (specs, architecture docs, code reviews with role-specific perspectives) but is less flexible. Gas Town can tackle any kind of work but requires the human (or Mayor) to define the breakdown.

### 2. Workflow: Phase-Gated Pipeline vs Task Queue

**AgentSquad** follows a **strict linear pipeline** with gate conditions:
```
Initialization → Research → Architecture → EngineeringPlanning
→ ParallelDevelopment → Testing → Review → Completion
```
Each phase must complete before the next begins. The `WorkflowStateMachine` enforces this — no backward transitions.

**Gas Town** uses a **task queue model** with convoys:
- Create a convoy with beads → assign beads to agents → agents work in parallel → merge queue
- No enforced phase ordering — the Mayor (or human) decides what to work on and when
- Formulas (TOML-defined workflows) provide optional structured pipelines

**Implication:** AgentSquad guarantees a complete SDLC process (you always get specs before code) but can't skip phases. Gas Town is more flexible but relies on human/Mayor judgment for process discipline.

### 3. Communication: In-Process Bus vs Inter-Process Messaging

**AgentSquad**: `InProcessMessageBus` via `System.Threading.Channels` (bounded capacity 1000)
- Sub-millisecond delivery within a single .NET process
- Five typed message types: TaskAssignment, StatusUpdate, HelpRequest, ResourceRequest, ReviewRequest
- No durability — messages lost on restart (GitHub is the durable layer)

**Gas Town**: Multiple inter-process channels:
- `gt nudge` — immediate delivery to another agent's tmux session
- `gt mail` — persistent mailboxes that survive restarts (git-backed)
- Hooks — git worktree state shared between agents
- Seance — query previous agent sessions for decisions/context

**Implication:** AgentSquad is faster (in-process) but more fragile (single process = single point of failure). Gas Town is more resilient (agents are independent processes) and supports richer communication patterns (mail + nudge + seance).

### 4. AI Runtime: Single Provider vs Multi-Runtime

**AgentSquad**: Routes all AI calls through Copilot CLI via `CopilotCliChatCompletionService`:
- 4 model tiers (premium/standard/budget/local) mapped to agent roles
- Single provider with automatic fallback to API keys
- Multi-turn conversations via Semantic Kernel `ChatHistory`

**Gas Town**: Supports **10+ AI runtimes** as first-class citizens:
- Claude Code, GitHub Copilot, Codex, Cursor, Gemini, Auggie, Amp, OpenCode, Pi, OMP
- Per-rig and per-sling runtime override (`gt sling <bead> <rig> --agent cursor`)
- Custom agent commands via `gt config agent set`

**Implication:** Gas Town's multi-runtime support is a major differentiator — you can use the best AI for each task. AgentSquad's single-provider approach is simpler but less flexible.

### 5. Scale: Team Simulation vs Swarm Coordination

**AgentSquad** targets **7-10 agents** simulating a small development team:
- `AgentSpawnManager` with configurable max agents per role
- Thread-safe registry with slot reservation
- Designed for one project at a time

**Gas Town** targets **20-50+ agents** across multiple projects:
- Scheduler with configurable `max_polecats` concurrency limits
- Rigs (project containers) with independent agent pools
- Wasteland federation for cross-organization work sharing
- `gt feed --problems` view for surfacing stuck agents at scale

### 6. Persistence: SQLite vs Dolt + Git Worktrees

**AgentSquad**: SQLite database + GitHub API
- `AgentStateStore` persists agent state, gate approvals, activity logs
- GitHub Issues/PRs/Labels as durable workflow state
- Simple but limited to single machine

**Gas Town**: Dolt (SQL database with git-like versioning) + git worktrees ("hooks")
- Every hook is a git worktree — version controlled, rollbackable, shared via git
- Dolt provides SQL queries with time-travel (branch, diff, merge on data)
- Beads issue tracker stored as structured data in git
- Wasteland federation via DoltHub for cross-organization sharing

### 7. Testing Strategy

**AgentSquad**: Built-in multi-tier test execution:
- `TestStrategyAnalyzer` determines which test tiers are needed (Unit/Integration/UI)
- `PlaywrightRunner` for automated UI testing with screenshots
- TE generates and runs tests as part of the review pipeline (Phase 2)
- Test evidence (screenshots/videos) used by PM in Phase 3 review

**Gas Town**: No built-in test framework:
- Testing is a task assigned to agents, not a first-class system feature
- Quality review is a plugin (`plugins/quality-review`)
- Agents run whatever tests the project already has

### 8. Code Review Pipeline

**AgentSquad**: Three-phase sequential review with gate labels:
1. Architect review → `architect-approved` label
2. Test Engineer → adds tests to same PR → `tests-added` label
3. PM Final Review (with visual evidence) → `pm-approved` label
4. SE merges only when all 3 labels present

**Gas Town**: Merge queue via Refinery:
- Polecats run `gt done` → branch pushed → MR bead created
- Refinery batches MRs, runs verification gates, bisects on failure
- No role-based review pipeline — review is optional/manual

---

## Where AgentSquad Shines

### 1. **End-to-End SDLC Simulation**
AgentSquad is the only system that produces a complete software development lifecycle — from research and spec writing through architecture, engineering planning, parallel development, testing with visual evidence, and multi-phase code review. Gas Town coordinates workers but doesn't enforce (or even suggest) a structured development process.

### 2. **Rich Code Review with Visual Evidence**
The three-phase review pipeline with role-specific perspectives (Architect validates architecture, TE adds tests + screenshots, PM validates business outcomes) produces higher-quality output than any single reviewer. The requirement that reviewers read actual code (not just PR descriptions) and that PM reviews include screenshot evidence is a differentiator.

### 3. **AI-Powered Test Generation**
The `TestStrategyAnalyzer` + `PlaywrightRunner` pipeline that automatically determines which test tiers are needed, generates tests with business context from PMSpec/Architecture/Issues, runs them, captures screenshots, and posts evidence to PRs is unique. Gas Town has no equivalent.

### 4. **Real-Time Monitoring Dashboard**
The Blazor Server dashboard with SignalR push updates, agent cards with live status, engineering plan dependency graph, project timeline, and health monitoring provides richer visualization than Gas Town's htmx dashboard or TUI feed.

### 5. **Model Tier Strategy**
Mapping AI model quality to agent role importance (premium for PM/Architect/SE, standard for SE/TE, budget for SE) is a practical cost optimization that Gas Town doesn't offer — all agents use whatever runtime is configured.

### 6. **Document Pipeline**
The structured document flow (Research.md → PMSpec.md → Architecture.md → EngineeringPlan.md) creates a traceable chain of decisions from business requirements to code. Each downstream agent builds on the previous agent's output, creating coherent context.

---

## Where Gas Town Excels

### 1. **Multi-Runtime Support**
Supporting 10+ AI runtimes (Claude, Copilot, Codex, Cursor, Gemini, etc.) with per-task overrides means you can use the best tool for each job. AgentSquad is locked to Copilot CLI with fallback to API keys.

### 2. **Scale (20-50+ Agents)**
Gas Town's architecture (independent processes, tmux sessions, persistent mail, git-backed state) is designed for much larger agent populations. AgentSquad's in-process model tops out around 10 agents in a single .NET process.

### 3. **Resilience & Fault Isolation**
Each Gas Town agent runs as an independent process. One agent crashing doesn't affect others. AgentSquad runs all agents in a single process — a crash kills everything (though SQLite + GitHub enable restart recovery).

### 4. **Session Continuity (Seance)**
The ability to query previous agent sessions for context and decisions (`gt seance --talk <id>`) is a unique feature. When agents restart, they can ask their predecessors "what did you find?" instead of re-doing work.

### 5. **Merge Queue (Refinery)**
The Bors-style bisecting merge queue is more sophisticated than AgentSquad's label-gated merge. It batches MRs, runs verification, and isolates failures automatically. AgentSquad relies on the SE to manage merges sequentially.

### 6. **Multi-Project Support**
Gas Town's Rig model manages multiple projects simultaneously with independent agent pools, hooks, and refineries. AgentSquad processes one project at a time.

### 7. **Watchdog Hierarchy**
The three-tier Witness → Deacon → Dogs monitoring chain with automatic recovery (nudge stuck agents, handoff context) is more mature than AgentSquad's `DeadlockDetector` + `HealthMonitor`.

### 8. **Federation (Wasteland)**
Cross-organization work sharing via DoltHub with portable reputation stamps is a forward-looking feature that has no equivalent in AgentSquad.

### 9. **Plugin Architecture**
Gas Town has a formal plugin system (compactor-dog, stuck-agent-dog, quality-review, github-sheriff, etc.) for extensibility. AgentSquad's functionality is compiled into the core assemblies.

### 10. **OpenTelemetry Integration**
Structured telemetry with OTLP export to VictoriaMetrics/VictoriaLogs provides production-grade observability. AgentSquad uses ILogger<T> structured logging but has no OTEL integration.

---

## Architecture Comparison

```
AgentSquad (Single Process)              Gas Town (Multi-Process)
┌────────────────────────────┐          ┌─────────────────────────────┐
│  .NET 8 Process (Runner)   │          │  Town (~/gt/)               │
│  ┌──────────────────────┐  │          │  ├── Mayor (Claude session) │
│  │ Orchestrator          │  │          │  ├── Deacon (patrol)       │
│  │ ├─ WorkflowStateMachine│ │          │  ├── Rig: ProjectA        │
│  │ ├─ AgentRegistry      │  │          │  │   ├── Witness          │
│  │ ├─ SpawnManager       │  │          │  │   ├── Refinery         │
│  │ └─ DeadlockDetector   │  │          │  │   ├── Crew/you         │
│  ├──────────────────────┤  │          │  │   └── Polecats (N)     │
│  │ Message Bus (Channels)│  │          │  └── Rig: ProjectB        │
│  ├──────────────────────┤  │          │      └── ...              │
│  │ Agents (7 roles)      │  │          │                           │
│  │ ├─ PM                 │  │          │  Communication:           │
│  │ ├─ Researcher         │  │          │  ├── gt nudge (immediate) │
│  │ ├─ Architect          │  │          │  ├── gt mail (persistent) │
│  │ ├─ Software Engineer │  │          │  ├── gt seance (history)  │
│  │ ├─ Software Engineer/Software Engineer Eng  │  │          │  └── git hooks (state)   │
│  │ └─ Test Engineer      │  │          │                           │
│  ├──────────────────────┤  │          │  Persistence:             │
│  │ SQLite + GitHub API   │  │          │  ├── Dolt (SQL + git)    │
│  └──────────────────────┘  │          │  ├── Beads (git-backed)  │
│  Dashboard (Blazor/SignalR)│          │  └── Git worktrees       │
└────────────────────────────┘          └─────────────────────────────┘
```

---

## Summary

**AgentSquad** is a **vertical solution** — deeply opinionated about how software should be built, with a complete SDLC pipeline, role-based specialization, and rich AI-powered code review with test generation and visual evidence. Best for teams that want a turnkey "AI development team" simulation.

**Gas Town** is a **horizontal platform** — flexible, scalable, and runtime-agnostic, designed to coordinate large numbers of AI agents across multiple projects. Best for power users who want to orchestrate many agents with fine-grained control over process and tooling.

They solve the same fundamental problem (multi-agent AI coordination) but at different layers of abstraction. AgentSquad could potentially use Gas Town as its underlying coordination layer, while Gas Town could adopt AgentSquad's SDLC pipeline as a formula/molecule workflow.
