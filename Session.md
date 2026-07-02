# VirtualDevTeam Session Handoff

> **Purpose:** Everything a new Copilot CLI session needs to get up to speed quickly. Read this file first, then follow the steps below.

---

## 1. Essential Context Documents

Read these in order to understand the project and expectations:

```
Read C:\Git\VirtualDevTeam\Session.md           (this file — session setup)
Read C:\Git\VirtualDevTeam\docs\MonitorPrompt.md (monitoring checklist & failure modes)
Read C:\Git\VirtualDevTeam\docs\Requirements.md  (project requirements)
Read C:\Git\VirtualDevTeam\docs\LessonsLearned.md     (hard-won operational knowledge)
Read C:\Git\VirtualDevTeam\docs\AzureDevOpsSetup.md (ADO platform setup — required for ADO runs)
Read C:\Git\VirtualDevTeam\docs\PerfectLoopSinglePR.md (framework validation loop — testing protocol)
```

Also read the `.github/copilot-instructions.md` (auto-loaded) for architecture, conventions, and build/test commands.

---

## 2. Reset (Fresh Run)

Before starting a new agent workflow run, fully reset the target repository (GitHub or ADO).

> 🚨 **CRITICAL: NEVER PUT SECRETS/TOKENS IN `appsettings.json` — IT IS TRACKED BY GIT.**
>
> `src/VirtualDevTeam.Runner/appsettings.json` is **NOT gitignored** — it is committed to the repository. **NEVER** write PAT tokens, API keys, or any secrets to this file. Always use `dotnet user-secrets` for sensitive values. If the runner can't find a token at startup, the fix is to add explicit user-secrets loading in `Program.cs` (`builder.Configuration.AddUserSecrets<Program>(optional: true)`), **not** to write the secret into appsettings.json. This applies to ALL tracked config files in the repo.

> 🚨 **CRITICAL: NEVER DISPLAY PAT TOKENS IN CLI OUTPUT.**
>
> When running shell commands (PowerShell, reset scripts, ADO API calls), **NEVER** hardcode PAT values in command text (e.g., `$pat = "actual-token"`). The Copilot CLI preview pane shows all command text to the user. Instead:
> - Read PATs from `dotnet user-secrets list` and parse the value into a variable
> - Or use environment variables set outside the visible command
> - The PAT must never appear as a literal string in any shell command

> 🚨 **CRITICAL: NEVER DO A MANUAL RESET. ALWAYS USE THE SCRIPTS.**
>
> The full safety-rules block (process kills, manual reset prohibition, PowerShell pipeline gotchas) lives in **`.github/copilot-instructions.md` → "Process Hygiene & Safety Rules"** and is auto-loaded by Copilot every session. The summary below is for human readers and quick reference.
>
> Manual resets (ad-hoc process kills, manual DB deletes, manual GitHub API calls) **always miss steps** and leave the environment in an inconsistent state: stale code in target repo, open issues/PRs that confuse fresh agents, ghost SQLite state, leaked agent branches.
>
> The reset scripts handle ALL of these atomically and verify the result. If a script fails, fix the script — do not work around it manually.

### Option A: Fresh reset (full clean slate — recommended for new projects)
```powershell
# Cleans EVERYTHING
.\scripts\fresh-reset.ps1
```

### Option B: Minimal reset (preserves startup docs — recommended for re-running engineering)
```powershell
# Preserves Research.md, PMSpec.md, Architecture.md so pipeline fast-forwards to engineering
.\scripts\minimal-reset.ps1
```

### Option C: Reset-runner (legacy, also works)
```powershell
# Full reset — reads PAT from user-secrets (falls back to appsettings.json), stops runner, cleans GitHub + local state
.\scripts\reset-runner.ps1
```

### Option D: Dashboard UI reset
Navigate to the **Configuration** page (http://localhost:5050/configuration) in the embedded dashboard.
Use the "Scan Repository" button to preview, then "Clean & Restart" to execute.
This is only available in embedded mode (Runner-hosted dashboard on port 5050).

### Local mode reset notes
- If `develop-settings.json` sets `"devPlatformKind": "Local"`, PRs/reviews/merges stay local: agents still use the same capability interfaces, but state lives in the run-scoped SQLite DB (`local_pull_requests`, `local_work_items`, etc.) plus a bare git repo under `.agents/local-platform/{repo}.git`.
- This mode is useful for enterprise repos where agents can work normally but are not allowed to merge their own PRs upstream.
- On a **full reset**, clean both the run-scoped SQLite state **and** the bare repo under `.agents/local-platform/`.
- The bare-repo root may differ from the main `.agents/` workspace path — check `LocalBareRepoManager` logs for the actual path before verifying cleanup.

### 🧹 After a crash, killed runner, or before a restart — kill orphan processes
The Runner spawns many child processes (Copilot CLI MCP servers, Squad framework subtrees, Blazor dev servers, dotnet test workers). When the Runner exits cleanly, the runner-scoped Win32 Job Object terminates them all atomically. **But** if the Runner was force-killed, crashed, or the Job Object was bypassed (older code paths), some `node.exe` / `dotnet.exe` processes can leak — sometimes hundreds, holding 10+ GB of RAM.

> 🚨 **NEVER run `Stop-Process -Name node`** — it kills your interactive Copilot CLI sessions, VS Code language servers, and any other unrelated tooling.
>
> ✅ **ALWAYS use the surgical orphan killer instead:**
> ```powershell
> # Preview only
> .\scripts\kill-orphan-runner-procs.ps1 -WhatIf
>
> # Actually kill
> .\scripts\kill-orphan-runner-procs.ps1
> ```
>
> The script uses a multi-criteria filter (CommandLine pattern match for `@playwright/mcp`, `@modelcontextprotocol/server-`, `blazor-devserver`, `--agent squad`; AND/OR working directory inside `.agents\` or `.candidates\`; AND age >= 120s) so it touches only runner-spawned orphans. Interactive Copilot CLI sessions and unrelated tooling are preserved.
>
> Run this **before** any reset script and **before** restarting the Runner. The reset scripts do **not** call it automatically because they rely on the Runner being killable in isolation.

> **What the scripts do (so you don't have to):**
> 1. Kill running dotnet processes
> 2. Delete SQLite DBs + WAL/SHM files
> 3. Purge agent-created SME definitions
> 4. Clean agent workspaces (C:\Agents\*)
> 5. Clean Playwright temp files
> 6. Clone repo → delete all non-preserved files → commit → push
> 7. Delete all remote agent branches
> 8. Close all open issues (paginated, with retry)
> 9. Close all open PRs (paginated, with retry)
> 10. **Verify everything is clean before reporting success**

### ⚠️ MANDATORY: Verify reset before proceeding

> **After ANY reset (scripted or manual), you MUST run the verification block below and confirm ALL checks pass before starting services. Do NOT skip this step. Do NOT start the Runner or Dashboard until every check shows the expected value.**
```powershell
# Get PAT from user-secrets
$patLine = dotnet user-secrets list --project src\VirtualDevTeam.Runner 2>&1 | Where-Object { $_ -match 'GitHubToken' }
$pat = ($patLine -split '= ', 2)[1].Trim()
$settings = Get-Content src\VirtualDevTeam.Runner\appsettings.json | ConvertFrom-Json
$repo = $settings.VirtualDevTeam.Project.GitHubRepo

# Check GitHub API rate limit (must have remaining > 100)
$headers = @{ Authorization = "token $pat"; Accept = "application/vnd.github+json" }
Invoke-RestMethod "https://api.github.com/rate_limit" -Headers $headers | Select-Object -ExpandProperty rate

# Must all return 0 — MUST PAGINATE (GitHub returns max 100/page, runs often create 200+ issues)
$page = 1; $total = 0
do {
    $batch = Invoke-RestMethod "https://api.github.com/repos/$repo/issues?state=open&per_page=100&page=$page" -Headers $headers
    $total += $batch.Count; $page++
} while ($batch.Count -eq 100)
Write-Host "Open issues+PRs: $total"  # MUST be 0

# Branches: must only be 'main'
$branches = Invoke-RestMethod "https://api.github.com/repos/$repo/branches?per_page=100" -Headers $headers
Write-Host "Branches: $($branches.Count) ($($branches.name -join ', '))"  # MUST be 1 (main)

# Files: must be empty after fresh reset (.gitignore may be created by scaffold PR)
$contents = Invoke-RestMethod "https://api.github.com/repos/$repo/contents?ref=main" -Headers $headers
Write-Host "Repo files: $($contents.name -join ', ')"  # MUST only show preserved files

# DB: must have no stale checkpoint databases
$dbs = Get-ChildItem src\VirtualDevTeam.Runner -Filter "virtualdevteam_*.db*" -ErrorAction SilentlyContinue
Write-Host "Stale DBs: $($dbs.Count)"  # MUST be 0 — stale DBs cause ghost notifications

# ⚠️ CRITICAL: SME definitions file — persisted SME agents auto-respawn on startup if this exists!
$smeDefs = Get-ChildItem src\VirtualDevTeam.Runner -Filter "sme-definitions*" -Recurse -ErrorAction SilentlyContinue
Write-Host "SME definition files: $($smeDefs.Count)"  # MUST be 0 — stale definitions auto-spawn old specialists
if ($smeDefs) { $smeDefs | Remove-Item -Force; Write-Host "  → DELETED stale SME definitions" }

# Workspaces: must be empty
$ws = Get-ChildItem C:\Agents -Directory -ErrorAction SilentlyContinue
Write-Host "Agent workspaces: $($ws.Count)"  # MUST be 0
```

> ⚠️ **CRITICAL: Always paginate GitHub API calls during reset.** A typical agent run creates 200+ issues. The API returns max 100 per page. A single non-paginated fetch will miss items and leave the repo dirty. When closing items, re-fetch page 1 each iteration (closing shifts items between pages).

**Important:** The PAT is stored in `dotnet user-secrets` for the Runner project. To set or update it:
```powershell
dotnet user-secrets set "VirtualDevTeam:Project:GitHubToken" "<your-pat>" --project src\VirtualDevTeam.Runner
```
The repo name is read from `src/VirtualDevTeam.Runner/appsettings.json` under `VirtualDevTeam.Project.GitHubRepo`. Note: this user is an Enterprise Managed User (EMU) — `gh issue create` may fail with 403. Use the runner's Octokit integration or direct REST API with the PAT instead.

---

## 3. Building & Running

### Architecture: Single Process

The system runs as a single process — the Runner hosts everything (agents + dashboard + API):

| Process | Port | Purpose |
|---------|------|---------|
| **Runner** | 5050 | Agent orchestration + REST API + full Blazor dashboard (all 18 pages) |

The Runner hosts a full Blazor Server dashboard with direct in-process access to all services (AgentRegistry, TaskTracker, etc.) — no HTTP proxying, real-time data, zero DI stub issues.

**Primary onboarding flow:** Navigate to the **Develop** page (`/develop`) in the dashboard for a guided multi-step wizard: What to Build → Repo & Auth → Work Item selection → Review & Launch.

> 💡 **Standalone Dashboard (optional):** A separate `Dashboard.Host` project exists at `src/VirtualDevTeam.Dashboard.Host` for scenarios where you need a remote UI (e.g., monitoring from another machine) or want to iterate on UI without restarting agents. Start it with `cd src\VirtualDevTeam.Dashboard.Host && dotnet run` — it connects to the Runner's REST API on port 5050 and runs on port 5051. This is rarely needed for normal operation.

### Starting the Runner

```powershell
# Option 1: Use the start script (recommended — builds, logs, manages PID)
.\scripts\start-runner.ps1

# Option 2: Manual start (detached, no Tee-Object!)
dotnet build src\VirtualDevTeam.Runner
Start-Process -FilePath "dotnet" -ArgumentList "run --project src\VirtualDevTeam.Runner --no-build" -WindowStyle Hidden -PassThru

# Option 3: Direct exe (after building)
dotnet build VirtualDevTeam.sln
Start-Process -FilePath "src\VirtualDevTeam.Runner\bin\Debug\net8.0\VirtualDevTeam.Runner.exe" -WindowStyle Hidden -PassThru
```

> ⚠️ **PowerShell 7+ is now required for `scripts/start-runner.ps1`.** Root cause: when the runner inherits a Windows PowerShell 5.1 environment, a configured CLI wrapper can start but never spawn `copilot.exe`. PowerShell 7 (`pwsh`) consistently spawns the child CLI within a few seconds. The start script now hard-fails on PS < 7 with a clear error.

Dashboard is available at **http://localhost:5050** once the Runner starts — all 18 pages are accessible from a single process.

### Stopping

```powershell
# Stop runner (also stops embedded dashboard)
.\scripts\stop-runner.ps1

# Or by PID
Stop-Process -Id <PID>
```

### Per-agent restart (prefer this before a warm restart)
- **REST endpoint**: `POST /api/dashboard/agents/{agentId}/restart`
- **Dashboard UI**: use the `🔄` button on the agent card
- Calls `AgentSpawnManager.RespawnAgentAsync`, which stops the old agent and creates a new instance with the same identity
- Use this when a single agent is stuck; prefer it over a full runner warm restart unless the whole process is unhealthy

### Critical runner rules
- **ALWAYS** verify reset before starting (run Section 2 verification block) — never start services on a dirty repo
- **NEVER** use `dotnet run | Tee-Object` — it kills the runner during Copilot CLI subprocess calls
- **NEVER** kill processes by name (`Stop-Process -Name`, `taskkill /IM`) — it kills your own CLI session
- **NEVER** approve gates or start agent runs without explicit user permission — the user may be away
- **Always** stop the runner before building (file locks on DLLs)
- Find runner PID: `Get-Process -Id (Get-Content runner.pid)` or `Get-NetTCPConnection -LocalPort 5050`
- The Runner spawns a child dotnet process — the child owns port 5050. Check both PIDs.

---

## 3b. Dev Platform Parity Rule (GitHub ↔ Azure DevOps)

> 🚨 **CRITICAL: Every fix to a platform-specific service MUST be assessed for cross-platform parity.**

VirtualDevTeam supports two dev platforms via the `IPullRequestService`, `IWorkItemService`, and `IRepositoryContentService` abstractions:
- **GitHub** — `src/VirtualDevTeam.Core/DevPlatform/Providers/GitHub/`
- **Azure DevOps** — `src/VirtualDevTeam.Core/DevPlatform/Providers/AzureDevOps/`

When fixing a bug or adding a feature in **either** provider:
1. **Ask:** Does this same issue exist in the other provider? (Often yes — e.g., API limits, missing field hydration, pagination gaps)
2. **Ask:** Is the fix applicable? (Sometimes no — e.g., ADO has overflow comments due to 4000-char PR description limits; GitHub doesn't need this because its limit is 65K)
3. **Document** the decision — if a fix is deliberately NOT applied to the other platform, add a code comment explaining why (e.g., `// GitHub: not needed — 65K char limit is sufficient`)

**Known platform differences:**
| Aspect | GitHub | Azure DevOps |
|--------|--------|--------------|
| PR description limit | 65,536 chars | 4,000 chars (overflow comment pattern) |
| PR list returns labels | ✅ Yes | ❌ No (separate `/labels` endpoint) |
| Work item description | 65K (Markdown body) | Unlimited (HTML field) |
| Merge = auto-close linked issues | ✅ Built-in (`Closes #X`) | ❌ Manual (must close via API) |
| Rate limiting | 5000/hr with shared cache | Per-user, higher limits |

---

## 3c. Generality Rule — No Project-Specific Implementations

> 🚨 **CRITICAL: NEVER hard-code rules, keyword lists, or category whitelists tied to a specific project type, technology, or domain. ALWAYS prefer data-driven / capability-driven approaches that work across project types.**

VirtualDevTeam is built to ship arbitrary projects — tower-defense games, CRUD apps, compliance audit tools, ML pipelines, anything that comes through the wizard. If your fix only works for one of those, **it's the wrong fix**.

### What this rule forbids

- ❌ Hard-coded keyword whitelists scoped to a domain (e.g., `var artKeywords = new[] { "sprite", "art", ... }` in core routing code)
- ❌ Hard-coded categorization predicates (`if (task.Title.Contains("sprite")) ...`, `if (capabilities contains "frontend") ...`)
- ❌ Hard-coded technology-specific branches in agent logic (`if (techStack == "Unity") ...`, `if (project.IsGame) ...`)
- ❌ Conditional code paths that only make sense for "this kind of project"
- ❌ Hard-coded file paths or asset names tied to a specific project's structure

### What this rule requires

- ✅ Data-driven scoring (numeric match counts between capability keywords and task text, comparing across all peer agents)
- ✅ Capability-vector matching (each agent declares its caps; routing uses set overlap or score comparison)
- ✅ Generalized predicates (e.g., "does this agent's capabilities strictly beat all peers for this task?" rather than "is this an art task?")
- ✅ Configuration / definition files that the operator or wizard fills in (e.g., SME definitions declaring caps); never hardcode the contents in C#
- ✅ Prompt-level instructions over code-level logic — the LLM can generalize from natural language better than a Boolean tree

### Test your fix against this checklist

Before merging any agent-routing, task-assignment, or capability-matching change, ask:

1. Would this still work if I deleted every keyword in my whitelist and replaced them with a totally different domain (security, compliance, accounting, biology)? If no → the fix is too specific.
2. Would this still work if a new SME role appeared tomorrow with capabilities I've never heard of? If no → the fix is too specific.
3. Would this still work for a pure-generalist team (no specialists)? If no → the fix is too specific.
4. Would this still work for a 1-of-each team (one specialist of each declared capability)? If no → the fix is too specific.

If you answer "yes" to all four, you've written a general solution. If you answer "no" to any, refactor before merging.

### Worked example (2026-05-12)

**Bad:** detecting "art-only" specialists with a hardcoded keyword list, and "art-only tasks" with hardcoded title substrings (`sprite`, `art asset`, `concept art`). Won't generalize to a "Database Migrator" SME or a "Compliance Auditor" SME — every new domain requires touching this code.

**Good:** every specialist computes its OWN capability-match score for each task AND looks up its peer specialists' scores. If any peer strictly beats my score, I defer. Otherwise I'm eligible. Same scoring function operates on every (agent, task) pair. Adding a new SME role (security, database, compliance) — no code changes required; the new role's capabilities automatically participate in the comparison.

See `SpecialistEngineerAgent.RunAdditionalLoopWorkAsync` for the implementation pattern. See `docs/system/LessonsLearned.md` entry on "Capability scoring with peer deferral" for the full rationale.

---

Read `docs/MonitorPrompt.md` for the full checklist. Key points:

### What to watch
1. **Phase progression**: Research → PM Spec → Architecture → Engineering Planning → Development → Testing → Review → Complete
2. **Agent status cycles**: Idle → Working → Idle is normal. Idle → Idle → Idle with open work = stuck.
3. **PR pipeline per engineering PR**: created → `ready-for-review` → Architect review → `architect-approved` → TE tests/assessment → `tests-added` (TE-owned, not SE) → PM review → `pm-approved` → SE merge. **Note:** SE no longer adds `tests-added` — TE owns the entire testing lifecycle including T-FINAL (empty PRs get "No Tests Needed" comment). **Fallback:** if `tests-added` is missing but TE posted an error comment, PM now accepts that as sufficient signal and should still proceed with review. **T-FINAL approval rule:** if the integration/T-FINAL PR accurately reports remaining gaps or failing checks, approve it — do not reject it merely because gaps still exist.
4. **Rate limiting**: GitHub API limit is 5000/hr (ADO limits are per-user, higher). Runner has 30s TTL shared cache (~90% reduction). Watch for `Rate limit exceeded` in logs.
5. **Human gates**: If FinalPRApproval gate is enabled, PRs will pause with `awaiting-human-review` label. Check the Approvals page or PR comments to approve/reject.
6. **Decision gates**: With `DecisionGating.Enabled: true`, agent decisions are impact-classified by the LLM. `MinimumGateLevel: "L"` means `L` and `XL` decisions require human approval; the decision card appears on the Approvals page immediately after classification. `GateTimeoutMinutes: 0` means wait indefinitely.
7. **Local platform runs**: If `devPlatformKind` is `Local`, PR/review/merge state is local-only (SQLite + bare repo). If PRs stop advancing, inspect `LocalBareRepoManager` logs to confirm the actual bare-repo path and repo health. `WorktreeWorkspace` now self-cleans stale git state (`rebase-merge`, `rebase-apply`, `MERGE_HEAD`, etc.), so transient `git rebase orig...` failures in TE/worktree agents should usually self-heal. `LocalBareRepoManager.MergeBranchAsync` also has a rebase fallback, so parallel PRs touching shared files should merge more reliably.

### Dashboard navigation structure

```
Main
├── Overview       — Agent cards, status, activity logs
├── Develop        — Multi-step wizard (project setup, auth, launch)
├── Configuration  — Settings editor, platform cleanup
├── Frameworks     — Strategy framework gallery
├── Walkthrough    — Interactive tour with 22 animated GIF demos
└── Approvals      — Human gate approval queue + FlowMonitor diagnostics

Project
├── Project Timeline — Phase-grouped issues/PRs
├── Repository       — Pull Requests, Issues, Code tabs
├── Scenarios        — Observation surface, progress rings, metric pills
└── Testing          — Preview Build + Test Artifacts

Operations
├── Metrics        — Build/test metrics, agent performance
├── Health Monitor — Deadlock detection, health checks, warm restart
├── Flow Monitor   — Structured incident console (Active Issues, Recent Changes, Detectors grid)
├── Flow Timeline  — Wall-clock pipeline milestone breakdown
├── Flow Log       — Raw FlowMonitor log (xterm.js terminal)
├── Team View      — Team composition view
└── Pipelines      — CI/CD pipeline status

Advanced
├── Reasoning      — Agent decision logs, reasoning events
└── Director CLI   — Direct CLI interface
```

### Dashboard pages
| Page | URL | Key Features |
|------|-----|-------------|
| Overview | `/` | Agent cards (Task + Step display), status, activity logs, agent visibility filter, and a live `📋 Log` viewer for CLI session output with LOW/MEDIUM/HIGH verbosity |
| Welcome | `/welcome` | 4-step setup wizard: Welcome → Prerequisites (parallel tool detection) → Auth → Get Started |
| Develop | `/develop` | Multi-step wizard: project setup, platform/auth, work item selection, launch |
| Project Timeline | `/timeline` | Phase-grouped issues/PRs, PM/Engineering toggle, auto-refresh, scroll arrows on viewport edges |
| Metrics | `/metrics` | Build/test metrics, agent performance |
| Health Monitor | `/health` | Deadlock detection, health checks, warm restart button |
| Repository | `/repository` | Combined PR + Issue tabs with PR Lifecycle Timeline in detail popups |
| Scenarios | `/scenarios` | Progress ring, metric pills, ObservationSurface, timeline dots for steps, priority accents |
| Pipelines | `/pipelines` | CI/CD pipeline status |
| Configuration | `/configuration` | Settings editor, platform cleanup (embedded mode only). Self Assessment (renamed from Agentic Loop), ~120 prompt template tooltips |
| Agent Reasoning | `/reasoning` | Agent decision logs, reasoning events, step tracking. Decisions tab selected by default |
| Approvals | `/approvals` | Human gate approval queue + FlowMonitor diagnostic checklists (decisions require in-process access) |
| Flow Monitor | `/flow-monitor` | Structured incident console: Active Issues cards, Recent Changes feed, All Detectors grid |
| Flow Log | `/flow-monitor-log` | Raw FlowMonitor log stream (xterm.js terminal, legacy) |
| Walkthrough | `/walkthrough` | Interactive tour with 22 animated GIFs showing dashboard features, linked from Welcome wizard |

### SQL monitoring tables
```sql
-- Track PRs through review pipeline
CREATE TABLE IF NOT EXISTS pr_monitor (
    pr_number INTEGER PRIMARY KEY, title TEXT, author TEXT,
    phase TEXT, status TEXT, last_checked TEXT
);

-- Track overall run progress
CREATE TABLE IF NOT EXISTS run_monitor (
    id INTEGER PRIMARY KEY, phase TEXT, started_at TEXT,
    agents_active INTEGER, issues_open INTEGER, prs_open INTEGER
);
```

### Red flags (investigate immediately)
- Agent Idle with open work in their phase
- Agent Working >10 minutes on same item
- `RateLimitExceededException` — all API calls pause until reset
- `OperationCanceledException` outside of shutdown — possible deadlock
- TE in "API-only mode" — tests committed without building/running
- TE UI test failure "App did not respond at http://localhost:XXXX within 90s" — likely hardcoded port in AI-generated Program.cs (see Lesson #20 in docs/system/LessonsLearned.md)
- Agent card flashing "⏳ Awaiting human approval..." when gates are in auto mode — pre-gate status update not guarded (see Lesson #22)
- **SinglePRMode task inflation**: In SinglePRMode, only T1 should be created. If T2+ tasks appear, the `ValidateEnhancementCoverageAsync` guard has regressed — check that it short-circuits before spawning additional work items.
- **Wave ID collisions**: Tasks use hash-based IDs, not sequential. If you see ID collisions in logs, check the cache merge logic in the wave builder.
- **Rate limit notification on Approvals page**: When the pipeline exceeds the 5000/hr GitHub API limit, a banner appears on the Approvals page. Investigate API call volume — SE per-iteration cache, FlowMonitor 90s ticks, and PR review context cache should keep usage below the limit.

---

## 5. Dashboard Features

### Dashboard (http://localhost:5050)
Full-featured dashboard hosted by the Runner process. All 18 pages with direct in-process access to all services — real-time data, no HTTP polling latency.

### Key features
- **Welcome Wizard** (`/welcome`): 4-step first-run setup: Welcome → Prerequisites (parallel tool detection for `dotnet`, `git`, `gh`, `copilot`, `ffmpeg`) → Auth → Get Started. OSHA-style "Productivity Hazard" warning label on step 1. Guides new operators through environment readiness before reaching the Develop wizard. Links to `/walkthrough` interactive tour.
- **Develop Wizard** (`/develop`): Multi-step guided setup — project description, platform/auth (GitHub or ADO), work item selection, review & launch. Primary onboarding flow
- **Project Timeline**: Phase-grouped view with PM/Engineering toggle, node type indicators (PR vs Work Item), clickable platform links, 30s auto-refresh (background, no overlay). **Timeline Scroll Arrows**: left/right overlay buttons on viewport edges, JS-driven show/hide based on scroll position.
- **New Story Wizard**: Timeline `+` actions can open a 3-step modal directly inside a selected wave. Operators can enter title/description/acceptance criteria/dependencies, review AI-generated clarifying questions, and create a new story without leaving the timeline.
- **Agent Overview**: Real-time agent cards with visibility filter (hide/show agents). Cards display "Task" (parent group name) and "⚡ Step" (current activity), falling back to StatusReason for monitoring/waiting states. Each card also has a `🔄` restart action that calls `POST /api/dashboard/agents/{agentId}/restart` via `AgentSpawnManager.RespawnAgentAsync`, preserving the same agent identity so you can recover one stuck agent without warm-restarting the whole Runner. Cards now also expose a `📋 Log` button that opens a live CLI session log viewer with LOW/MEDIUM/HIGH verbosity filters. The tooltip now makes the trade-off explicit: in-flight LLM work is lost, but durable state (PRs/issues/task assignments) is recovered automatically.
- **Repository**: Combined Pull Requests + Issues view with tab navigation. **PR Lifecycle Timeline** in detail popups: horizontal timeline with emoji icons showing 6 stages (Dev → Architect → Peer Review → Testing → PM → Merge). Powered by `PrLifecycleCalculator` in `Core/Lifecycle/` — config-aware (TE enabled/disabled, SinglePR, peer review agents). 14 unit tests.
- **Scenarios** (`/scenarios`): Observation surface with progress ring, metric pills, timeline dots for steps, and priority accents. Redesigned May 2026.
- **Pipelines** (`/pipelines`): CI/CD pipeline status for the target repository
- **Force refresh**: SVG refresh button on Timeline and Overview pages
- **Strategy Gallery**: When the agentic frameworks pipeline is enabled, shows per-candidate screenshots for all approaches (baseline, mcp-enhanced, copilot-cli, squad). External frameworks (🔌) display a purple right-border. Winner tile displays the live screenshot or "Capturing..." while the upload is in progress. Preview states now distinguish `CaptureUnavailable` (Playwright/browser tooling missing), `CaptureFailed` (app booted badly or never produced a usable shot), and `NoVisualContent` (legitimate backend-only/non-visual work) so empty tiles are diagnosable instead of ambiguous. Winner identification reads the `<!-- winner-strategy: {key} -->` HTML comment from the PR body. **Nav badge** (May 2026): real-time active candidate count from `CandidateStateStore` shown on the Frameworks nav item. **Operator controls** (2026-05-21): running candidates now have a `🔄` reset action with two-click confirmation (`🔄` → `⚠ Reset?`). Reset kills the current process, retries in a fresh worktree, and uses the same escalation ladder as automatic stuck recovery (rung 1 = same config, rung 2 = `ForceNoWrapper`). Reset / Cancel / Cancel All tooltips now explain the exact blast radius before you click.
- **Multi-Process Preview**: `TryDetectCompanionFrontend` auto-detects Vite/React frontend alongside .NET API. `StartCompanionProcessAsync` starts both processes. Screenshots navigate to the frontend URL for accurate visual capture.
- **Health Monitor**: System Assessment alignment fixed (removed margin-top:auto gap). Warm restart button.
- **Pipeline Status API**: `GET /api/pipeline/status` is the fastest operator/CLI snapshot when the question is “what is the pipeline doing right now?” It returns phase, agents, work items, linked PR lifecycle data, dependencies, and summary metrics in one call.
- **Timeline**: Screenshot lightbox (May 2026) — replaced broken `target=_blank` link with inline overlay lightbox for screenshot previews.
- **Agent Detail**: PR link (May 2026) — PR numbers in agent detail view are now clickable links to the platform PR page via `IPlatformLinkService`.
- **PR Detail / Operator feedback**: "Add Changes" posts a structured `**[Operator] CHANGES REQUESTED**` comment, sanitizes HTML-comment delimiters before posting, publishes `ChangesRequestedMessage`, preserves existing approvals for operator-only rework, and finishes with `**[Operator-Addressed]**` when the engineer completes the requested update.

### Timeline data flow
- Issues/PRs fetched via `DashboardDataService` using platform-agnostic `IPullRequestService` and `IWorkItemService` (30s TTL cache)
- `BuildTimeline()` pipeline: run detection → filter → dedup → synthetic doc PRs → parent-child → phase grouping
- Doc phases (Research, PM Spec, Architecture) appear as synthetic nodes from PRs
- Engineering tasks filtered to latest burst (30-min window from newest)

---

## 6. Key Configuration

Static defaults live in `src/VirtualDevTeam.Runner/appsettings.json`. Active project/platform settings come from `develop-settings.json`. Sensitive values (PATs, API keys) stay in `dotnet user-secrets`.

Key settings:
- `develop-settings.json` → `devPlatformKind`: `"GitHub"`, `"AzureDevOps"`, or `"Local"`
- `develop-settings.json` → `devPlatformKind: "Local"`: local PRs/reviews/merges use the same capability interfaces with zero agent-code changes, but persist to the run-scoped SQLite DB plus a bare git repo at `.agents/local-platform/{repo}.git` managed by `LocalBareRepoManager`
- `VirtualDevTeam.Project.GitHubRepo`: Configured via Develop wizard → `develop-settings.json` (appsettings.json default is blank)
- `VirtualDevTeam.Project.GitHubToken`: Stored in **dotnet user-secrets** (not in appsettings.json)
- `VirtualDevTeam.DecisionGating.Enabled`: `true` enables LLM-based impact classification for agent decisions
- `VirtualDevTeam.DecisionGating.MinimumGateLevel`: `L` means `L` and `XL` decisions require human approval on the Approvals page
- `VirtualDevTeam.DecisionGating.GateTimeoutMinutes`: `0` means wait indefinitely; decision cards appear on the Approvals page immediately after classification

---

## 7. Fix Verification Protocol

When asked to fix a bug or issue, **ALWAYS** follow this protocol before reporting it as fixed:

1. **Make the code change** — apply the fix
2. **Stop the runner** — `.\scripts\stop-runner.ps1`
3. **Build the full solution** — `dotnet build VirtualDevTeam.sln` — confirm 0 errors
4. **Start the runner** — `.\scripts\start-runner.ps1`
5. **Wait for boot** — allow 30–45s for checkpoint restore + first periodic tick
6. **Verify backend** — check logs (`Get-Content Logs\runner-latest-stdout.log -Tail 50`) and API responses (e.g., `Invoke-RestMethod http://localhost:5050/api/...`) to confirm the fix took effect in the data layer
7. **Verify UI with Playwright** — use Playwright to navigate to the affected page, take a screenshot, and visually confirm the fix is reflected in the rendered UI. Do NOT rely solely on backend checks — the UI rendering path has its own timing, state, and conditional logic that can differ from backend state.
8. **Report with evidence** — include the Playwright screenshot and relevant log/API output as proof the fix works end-to-end

This protocol exists because backend state can be correct while the UI still renders stale/wrong data due to async timing, conditional rendering guards, or Blazor lifecycle ordering (see: `_isProjectComplete` race between `LoadRunStateAsync` and `RefreshData`).

> 🚨 **CRITICAL: NEVER APPLY BANDAIDS — ALWAYS ROOT-CAUSE BEFORE FIXING.**
>
> When a component fails, you MUST find the **actual root cause** before writing any code. Do NOT add workarounds, promotions, fallbacks, or "resilience" patches that mask the real problem. A bandaid lets the pipeline appear to pass while the underlying bug persists and resurfaces on future runs.
>
> **Example of what NOT to do:**
> AppPlaytester failed to deserialize LLM JSON → instead of fixing the deserialization, a promotion hack was added that auto-promoted `InferredPass → Verified` when `verifiedCount == 0`. This masked the real issue (LLM returning camelCase property names vs. expected snake_case + a Layer 2 stub vetoing all Verified verdicts). The pipeline "passed" but no scenario was ever truly verified. The hack caused T-FINAL to re-run 4+ times per session and gave false confidence that acceptance criteria were met.
>
> **Example of correct root-cause fixing:**
> 1. **Reproduce** — Run the failing scenario and capture the exact error (e.g., `JsonException: 'N' is an invalid start of a value`)
> 2. **Trace to root cause** — Read the LLM's raw output → it returned `actionType` (camelCase) but the schema expects `action_type` (snake_case). `PropertyNameCaseInsensitive` handles case but NOT naming convention differences. Also: Layer 2 vision is a stub always returning `Inconclusive`, and `ConservativeMerge` treats `Inconclusive > Verified`, making genuine `Verified` verdicts structurally impossible.
> 3. **Fix the root cause** — Add JSON-aware property name normalization, treat the unimplemented Layer 2 as neutral in verdict merge, and add post-deserialization validation.
> 4. **Question the architecture** — Ask whether the approach itself is sound. In this case, asking an LLM to produce a full executable DSL is inherently brittle. A CLI agentic session (`--allow-all`) that autonomously verifies and returns only a verdict is far more durable.
> 5. **Verify end-to-end** — Reset to clean state, run the full pipeline, and confirm the component produces genuine results (not promoted/faked ones).
>
> **Rule of thumb:** If your "fix" adds a conditional that says "if the real thing failed, pretend it succeeded" — that is a bandaid, not a fix. Stop and find the real cause.
>
> 🚨 **CRITICAL: Every fix MUST be validated before declaring it done.**
>
> After implementing a fix, you MUST validate it works. Ask yourself: "Can I reasonably quickly validate this in the running app, or would it be faster to write a simple side script?" Choose the fastest path:
>
> 1. **In-app validation** — If the Runner is active and the fix is behavioral, trigger the relevant code path and observe the results via dashboard, logs, or API endpoints (e.g., `GET /api/pipeline/status`).
> 2. **Side-script validation** — Write a small standalone script (PowerShell, Node.js, Python) that replicates the setup and calls the relevant code or APIs to verify the fix. This is often faster than waiting for a full pipeline cycle.
> 3. **Unit test validation** — If the fix is in a testable unit, write or run a targeted test: `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"`.
>
> **Example:** After fixing JSON property normalization in AppPlaytester, write a quick Node.js script that sends the same malformed JSON (with `actionType` instead of `action_type`) through the normalization function and asserts the output has the correct property names — rather than waiting 20 minutes for a full T-FINAL cycle.
>
> **Rule of thumb:** If you say "fix applied" but haven't run any validation beyond "it compiles" — you haven't finished the task. A fix is done when you have evidence it works.

> 🚨 **CRITICAL: Write DETAILED PR/commit descriptions when pushing to the VDT repo.**
>
> Every PR and significant commit pushed to this repository must include a thorough description that explains:
>
> 1. **What was done** — A clear summary of ALL changes, not just file names. Describe the behavioral change, new components, modified logic, and any refactors.
> 2. **Why it was done** — The root cause, user request, or design rationale that motivated the change. Link to the problem you observed or the failure mode you fixed.
> 3. **Considerations & trade-offs** — Design decisions you made and alternatives you considered. Why you chose approach A over approach B.
> 4. **What's NOT addressed** — Known limitations, follow-up work needed, edge cases deferred. Be honest about gaps so future sessions don't assume everything is handled.
> 5. **Testing & validation** — How you verified the change works. What you tested, what test results showed, and any manual verification steps taken.
> 6. **Context for other AI sessions** — Remember that other Copilot sessions on other machines may pick up this code. Include enough context that an AI agent reading the PR description can understand the full picture without re-investigating from scratch — the architectural reasoning, the failure mode it prevents, and any non-obvious dependencies.
>
> **Example of a BAD PR description:**
> ```
> fix: AppPlaytester fixes
> Fixed JSON parsing and added fallback logic.
> ```
>
> **Example of a GOOD PR description:**
> ```
> fix: replace AppPlaytester 3-layer JSON pipeline with CLI-agentic sessions
>
> Root cause: The JSON-based pipeline consistently failed because LLMs returned
> `actionType` (camelCase) but the schema expects `action_type` (snake_case).
> PropertyNameCaseInsensitive handles case but not naming conventions. Additionally,
> Layer 2 vision was an unimplemented stub always returning Inconclusive, and
> ConservativeMerge treated Inconclusive > Verified, making genuine Verified
> verdicts structurally impossible.
>
> Changes:
> - Created CliAppPlaytester using CopilotCliProcessManager.ExecuteAgenticSessionAsync
> - Each scenario gets an autonomous CLI session with Playwright MCP tools
> - Verdict uses simple text markers (RESULT/CONFIDENCE/NOTES), not JSON schemas
> - DI routes to CliAppPlaytester when CopilotCli.Enabled=true, falls back to legacy
>
> Not addressed: Legacy AppPlaytester still has the original JSON bugs (kept as
> fallback only). MCP tool availability not pre-checked.
>
> Validated: 9/9 verdict parser tests pass, Core/Agents/Runner all build clean.
> ```

- `VirtualDevTeam.CopilotCli.Enabled`: `true` (routes all AI through `copilot` CLI)
- `VirtualDevTeam.CopilotCli.JsonOutput`: `true` (JSONL output format — **any code calling `ExecutePromptAsync` directly must parse JSONL via `CliOutputParser.ParseJsonOutput()`**)
- `VirtualDevTeam.CopilotCli.SinglePassMode`: `true` (single AI call per doc, not multi-turn)
- `VirtualDevTeam.CopilotCli.MaxConcurrentRequests`: `10`
- `VirtualDevTeam.CopilotCli.ModelName`: `claude-opus-4.6`
- `VirtualDevTeam.CopilotCli.ReasoningEffort`: `high`
- `VirtualDevTeam.CopilotCli.FastModeModel`: `claude-haiku-4.5`
- `VirtualDevTeam.Models`: Per-tier model definitions (premium/standard/budget/local)
- `VirtualDevTeam.Limits.MaxAdditionalEngineers`: `3`
- `VirtualDevTeam.HumanInteraction.Enabled`: `true` (enables human gate checkpoints)
- `VirtualDevTeam.HumanInteraction.Preset`: Use Full Auto / Supervised / Full Control via Configuration page
- Note: Gate configuration is hot-reloadable — changes take effect without runner restart
- `VirtualDevTeam.ApprovalGates.ArchitectureDesign.RequiresHuman`: `true` (default — architecture gate requires human approval)
- `VirtualDevTeam.CopilotCli.ReasoningLevelValidation`: Dropdown filtered per model capabilities with warning for unsupported levels
- `VirtualDevTeam.StrategyFramework.EnabledStrategies`: Defaults to empty — baseline always runs regardless; other strategies (mcp-enhanced, copilot-cli) must be explicitly listed. `squad` can be added to enable the Squad external framework adapter.

### Model tier strategy
| Tier | Used By | Default Model |
|------|---------|---------------|
| premium | PM, Architect, SE | claude-opus-4.6 |
| standard | Researcher, Software Engineers, TE | claude-sonnet-4.6 |
| budget | Software Engineers | gpt-5.2 |

### Image-gen deployment ladder (CRITICAL — multiple deployments required)

For projects with visual deliverables (sprite sheets, character art, UI icons, illustrations) the Artist agent calls Azure OpenAI image-gen REST endpoints. The recipe in `prompts/_shared/image-gen-instructions.md` walks a deployment ladder on transient failures (429 rate-limit, 503 capacity, 404 deployment-not-found). **You MUST provision multiple gpt-image-* deployments in your Azure OpenAI resource for the ladder to actually function** — a single-deployment resource degrades to single-shot retries.

**Recommended deployment configuration** (set in Azure portal, then mirror into `develop-settings.json` → `AzureOpenAIImage`):

| Deployment | Suggested RPM (operator-tunable) | Use case |
|---|---|---|
| `gpt-image-1.5` | 9 RPM | **Recommended PRIMARY** — best detail/quality at human-verified standards (operator-validated 2026-05-12 side-by-side test). Outputs ornate detail at the same prompt that produces simpler results from image-1 / image-1-mini. |
| `gpt-image-1` | 9 RPM | Solid mid-tier fallback when 1.5 throttles |
| `gpt-image-1-mini` | 12 RPM | Highest-throughput fallback for bulk multi-frame work; cheaper |
| `gpt-image-2` | 2 RPM (very gated) | Last-resort fallback only — too slow + tight quota for primary |

`develop-settings.json` example:
```jsonc
{
  "AzureOpenAIImage": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiVersion": "2025-04-01-preview",
    "AuthMethod": "ApiKey",                         // or "DefaultAzureCredential"
    "PrimaryDeployment": "gpt-image-1.5",
    "FallbackDeployments": ["gpt-image-1", "gpt-image-1-mini", "gpt-image-2"]
  }
}
```

API key (when `AuthMethod = "ApiKey"`): set in `dotnet user-secrets` under `VirtualDevTeam:AzureOpenAI:ImageApiKey` — never put it in `appsettings.json` or `develop-settings.json`. Azure OpenAI image-gen ALWAYS uses the `api-key` HTTP header regardless of key length; recipes that switch on key-length are wrong.

**Within an animation cycle, every frame MUST come from the same model.** The deployment ladder kicks in PER CALL; switching mid-animation produces visually different characters across frames. The 3-retry-with-5/10/15s-backoff per-deployment policy protects within-animation continuity. The ladder protects throughput across DIFFERENT entities/assets.

**Agent-side image-gen behaviors** (no operator setup required, but worth knowing):
- Agents call the REST endpoint **directly** (no MCP wrapper). Auth + endpoint + deployment list are injected as `AZURE_OPENAI_IMAGE_*` env vars into the child process by `CopilotCliProcessManager.ApplyImageGenEnvVars` and `SquadFrameworkAdapter.ApplyImageGenEnvVars`.
- Agents MUST verify saved files via PNG signature (`89 50 4E 47`), NOT by file size.
- Agents MUST NEVER fabricate PNGs from Pillow primitives / System.Drawing rectangles / ASCII / placeholder bytes when the API fails. Absent assets are honest; fabricated PNGs deceive gates.
- Multi-asset tasks must batch in waves of max 8 concurrent. Master frames first (no deps, fully parallel across entities), then variant frames within each entity.
- The Strategy Framework dashboard (`/strategies`) streams `🎨 path/to/image.png (size KB)` activity events as files land in candidate worktrees (every 5s poll via `CandidateArtifactWatcher`).

---

## 7. Known Issues & Workarounds

1. **GitHub EMU restrictions**: `gh issue create` fails with 403. Use Octokit via the runner or REST API with PAT.
2. **Rate limiting**: Heavy runs can exhaust the 5000/hr GitHub API limit. The 30s TTL cache reduces API calls by ~90%. Dashboard shows rate-limit status. **2026-05-16 update**: Parallel development consumed 8400 API calls/hr (vs 5000 limit). Three mitigations now reduce usage: SE per-iteration cache, FlowMonitor 90s ticks, and PR review context cache. When rate limited, a notification banner appears on the Approvals page.
3. **Stale checkpoint recovery**: Runner uses `WorkflowStateMachine` checkpoint. If resuming an old run, the phase may be wrong. Delete the DB for a fresh start.
4. **Agent workspaces**: TE and engineers clone repos to `C:\Agents\`. These persist across runs — delete for fresh start.
5. **PM issue ordering**: The PM extraction prompt instructs dependency-ordered issue creation (scaffolding first). If issues come out in wrong order, check the extraction prompt in `ProgramManagerAgent.CreateUserStoryIssuesAsync()`.
6. **DLL locks during build**: Runner/Dashboard must be stopped before rebuilding. Use `.\scripts\stop-runner.ps1` first.
7. **Standalone dashboard limitations**: Configuration settings editor and Engineering Plan page are embedded-only (require in-process access). All other pages work in standalone mode via HTTP polling to the Runner API. Configuration cleanup IS available in standalone. CostBadge, PlaywrightBadge, and all status indicators poll the Runner API correctly.
8. **Vision review requires network access**: Screenshot download in PR reviews needs the runner to reach GitHub's raw content URLs. If behind a proxy, images fall back to URL-only text context.
9. **Gate config hot-reload**: Gate settings are hot-reloaded via `IOptionsMonitor`. Other config sections (Models, Agents, Limits) still require restart.
10. **⚠️ CRITICAL — Stale SME definitions auto-respawn**: SME agents persist their definitions to `sme-definitions.json` in the Runner directory. On startup, any `Continuous` mode definitions auto-respawn. **This file MUST be deleted during reset** or old specialists will load before the PM creates them. The dashboard Config page cleanup now handles this automatically.
11. **Port conflicts between agents**: SE screenshots and TE UI tests both start the app under test. Each agent now uses a unique port derived from its workspace path (range 5100–5899). If you see "App did not respond" errors, check for port conflicts.
11b. **Playwright badge is 🔴 after a partial build**: the Runner's `bin\Debug\net8.0\.playwright\` output can lose its actual driver files and be left with only empty directories. If Playwright suddenly shows red, inspect `src\VirtualDevTeam.Runner\bin\Debug\net8.0\.playwright\` first. Quick fix: copy the populated `.playwright\` contents from the matching NuGet cache location back into the Runner bin output.
12. **Standalone dashboard stale agents**: The DB accumulates agent records across restarts. `RecordBoot()` writes `last_boot_utc` to filter to current-run agents only. If dashboard shows old agents, restart the Runner to update the boot timestamp.
13. **TE data.json**: Blazor apps that depend on `wwwroot/data.json` may fail on fresh clones. `EnsureSampleDataExists()` auto-creates a sample data file if missing.

13. **Strategy Framework worktree leaks**: When enabled (`VirtualDevTeam.StrategyFramework.Enabled=true`), per-candidate git worktrees live under `<agent-repo>/.candidates/<runId>-<strategy>/`. The orchestrator cleans up on exit, but if the runner is killed mid-orchestration they persist. Run `git worktree prune` in the agent repo + delete `.candidates/` if disk fills up. ndjson artifacts go to `experiment-data/` — by default resolved against the runner's cwd (bin dir), not the repo root.
14. **Copilot CLI reports 0 tokens**: The `copilot` binary doesn't emit usage counts, so per-strategy cost attribution is always `$0` with the default provider. Cost budget enforcement only kicks in when using an API-key fallback (Anthropic/OpenAI/Azure OpenAI direct). Not a bug — documented limitation.
15. **`.screenshots/` directory in target repo**: The strategy framework commits per-candidate screenshots to `.screenshots/pr-{N}-{strategyId}.png` on PR branches. These are lightweight artifacts (~50–200KB PNGs) that persist after merge into the target repo. Reset scripts do not clean them (they live in the target repo, not the agent workspace). Harmless but accumulate over runs — delete manually from the target repo if desired.
16. **Winner-strategy marker in PR bodies**: PR bodies contain a `<!-- winner-strategy: {key} -->` HTML comment used by the dashboard for winner identification. If the dashboard misidentifies the winner, inspect the PR body for a missing or malformed marker.
17. **Azure DevOps platform support**: ADO provider is implemented and live-tested against Azure DevOps. The 7 capability interfaces (PR, Work Item, Branch, File, Review, Info, HostContext) have full implementations. Known platform differences documented in `docs/AzureDevOpsSetup.md`. Configure via the Develop wizard, dashboard Dev Platform dropdown, or `appsettings.json` → `DevPlatform` section.
17b. **Silent engineering stalls now have a dedicated detector**: If the pipeline looks "quiet" but work remains, check FlowMonitor for `pipeline-stall` findings. It specifically catches stale `status:blocked` engineering tasks with no active PR and the "all engineers idle / no PRs / claimable work remains" condition that normal stuck-agent checks miss.
18. **FlowMonitor v2 shipped May 2026 (17 detectors, 4 actions, diagnostic enrichment)**: Always-on watchdog (`FlowMonitorService`) runs every 30s. **Tier-1 detectors** (5): `agent-stuck`, `phase-completion-mismatch`, `deadlock`, `pr-merge-conflict`, `unmerged-approved-pr`. **Tier-2 detectors** (12, incl. `agent-disappearance` added 2026-05-15): `idle-agent-phase-stuck`, `te-false-completion`, `label-transition-timeout`, `rework-saturation`, `handoff-gap`, `phase-advancement-watchdog`, `status-reason-stagnant`, `orphan-pr`, `idle-idle-cycle`, `empty-queue`, `ai-anomaly`, `agent-disappearance` (fires when agents vanish during active run). Four actions: `kick-agent-poll` → `post-explicit-ask` → `escalate-to-human` 3-rung escalation ladder + `merge-approved-pr` safety-net merger. **Dashboard redesign** (2026-05-15): new `/flow-monitor` page replaces xterm.js terminal with structured incident console — Active Issues cards, Recent Changes feed, All Detectors grid. Old terminal kept at `/flow-monitor-log` as Raw Log. **Diagnostic enrichment** (May 2026): `IFlowDiagnosticEnricher` runs after detection, before action selection. `PrLifecycleDiagnosticEnricher` checks PM/TE/Architect gate conditions (labels present, comments missing, dependency chain). Findings carry ✅/❌ diagnostic checklist via `Diagnostics` + `RecommendedFixId`/`RecommendedFixDescription`. Persisted as `diagnostics_json` in `flow_findings` table. Approvals page shows diagnostics inline with collapsible details and honest messaging. Critical findings without a handler trigger the FixRecommendation flow (T1.5 — `/plan` + rubber-duck → `/FixRecommendations/` → Approvals page). Warm restart button on Health Monitor preserves workflow state. See `docs/Requirements.md` REQ-FLOW-005..013 for full spec, `docs/Tier2Recommendations.md` for the 12 deferred detectors, `docs/system/LessonsLearned.md` #87-98 for design rationale + observed pitfalls.
18b. **FlowMonitor improvements (May 2026)**: Rung-2 `PostExplicitAskAction` disabled for both PR comments AND issue comments (logs only — confirmed no agent parses them; previously only PR comments were suppressed). `AgentStuckDetector` smart threshold: 3× multiplier for agents in strategy framework evaluation, rework cycles, or self-assessment (these legitimately take 30-45m). `EmptyQueueDetector` phase guard: only fires during ParallelDevelopment/Testing (not during Research/Architecture when empty queues are expected); agent-name prefix match + 6-min threshold to reduce false positives. Engineering PR guard on premature "Project Complete" notification: suppresses `project.complete` when no engineering PRs exist yet. Stale findings for merged PRs: PR-state short-circuit resolves findings when the associated PR has already been merged. `agent-stuck` label no longer blocks merge when all other approvals are present.
19. **AgentStuckDetector threshold (30m) is too aggressive** (open TODO `post-run-stuck-threshold`): Strategy framework + Copilot CLI candidates + LLM Judge + Playwright eval can take 30-45m on complex tasks. Threshold is hardcoded in `Program.cs:228` — make configurable + bump default to 45m, or detect activity (LLM call recency) instead of status-change time.
20. **Squad framework crashes silently on Windows runtime errors** (open TODO `post-run-squad-crash-retry`): `STATUS_STACK_BUFFER_OVERRUN` (exit code -1073740791) observed during T-FINAL — strategy orchestrator declared "no winner" and proceeded, but no PR was opened. Should fall through to copilot-cli strategy automatically on non-business-logic exit codes.
21. **PR merge conflict auto-recovery requires active SE Leader loop** (open TODO `post-run-pr-merge-conflict-detector`): `TryCloseAndRecreatePRAsync` only fires while SE Leader is in its merge loop. If a PR's conflict surfaces after SE Leader moves on, no auto-rebase happens. Operator manually rebased PR #1347 after the May 2026 run; needs new `IFlowDetector` for stale CONFLICTING PRs (>15min) paired with `rebase-pr` action.
22. **`agent-stuck` label sticky after Resolved** (open TODO `post-run-stuck-label-cleanup`): Rung 3 escalation applies the label as a side effect; T1.3 verification marks the finding `Resolved` when condition clears, but doesn't undo the label. Fix: each `IFlowAction` grows `UndoAsync`; `VerifyActedOnFindingsAsync` calls it on resolve.
23. **`cli-mcp` orphans hold workspace handles** (manual cleanup pattern): GitHub Copilot CLI sessions spawn `node <copilot-cli-mcp>/dist/cli.js start` MCP servers that leak across sessions. Cleanup script's existing filter doesn't include them (they're Copilot CLI MCPs, not runner-spawned). Mitigation: kill ones older than 6h with `cli-mcp` in cmdline. **Never kill `node` by name** — the user's active Copilot CLI is also `node`.
24. **White-screenshot detection shipped May 2026** (`ScreenshotQualityChecker` + `PullRequestWorkflow.EvaluateScreenshotAgainstExpectationsAsync`): two-layer defense for the silent-blank-canvas failure mode observed in the 2026-05-11 tower-defense run. Layer 1 = cheap file-size heuristic in `PlaywrightRunner.CaptureAppScreenshotAsync` (returns null when PNG is < 15 KB on a 1000+px canvas — almost certainly uniform fill). **Enhanced May 2026 with IDAT ratio check** for high-res PNGs: compares IDAT chunk size against total file size to catch blank images that pass the raw size threshold due to resolution. Blank filtering applied in all capture paths. Layer 2 = vision-AI semantic check that asks "does this screenshot match what the PR title/body promised to deliver?" with structured verdict (`MATCHES` / `DOES_NOT_MATCH` / `INCONCLUSIVE` + confidence). Wired into THREE places: (a) `EngineerAgentBase.MarkPrCompleteAsync` AND `SoftwareEngineerAgent.FinalizeReadyForReviewAsync` run pre-publish check that surfaces verdict as implementation note for downstream self-assessment LLM, (b) `TestEngineerAgent` standalone-screenshot path blocks upload on high-confidence DOES_NOT_MATCH and posts "App Preview Rejected" warning to PM, (c) `engineer-base/self-assessment-system` prompt instructs the LLM to treat DOES_NOT_MATCH as a HARD GAP. Safe-by-default: any check failure (no vision model, INCONCLUSIVE verdict, backend-only PR) is a no-op so legitimate PRs don't stall. See `docs/system/LessonsLearned.md` for the failure-mode background.
25. **Idempotent-startup rule added to architect + engineer prompts** (May 2026): mandatory checklist in `prompts/architect/multi-turn-data-model.md` and `prompts/engineer-base/single-pass-implementation.md` requires running `dotnet run` / `npm start` mentally TWICE in a row. Common silent failure: non-idempotent seed code crashes on second startup with SQLite UNIQUE-constraint violation → backend 500s on `/api/config/*` → frontend renders blank canvas → pipeline approves a broken PR because compile passes and unit tests don't exercise seed→serve flow. Required patterns: EF Core `OnModelCreating + HasData`, `INSERT OR IGNORE`, or check-then-insert. Forbidden: `EnsureCreated()` combined with imperative INSERT into UNIQUE columns.
26. **Action handlers must opt-in for new detector types** (May 2026, post-Tier-2): when adding a new `IFlowDetector` that emits agent-targeted findings, the `CanHandle` predicate on each `IFlowAction` (KickAgentPoll / PostExplicitAsk / EscalateToHuman) must be updated to include the new `DetectorId` — otherwise findings emit but no action fires. Today's `_kickableDetectorIds` / `_commentableDetectorIds` / `_escalatableDetectorIds` HashSets cover: agent-stuck, phase-completion-mismatch, idle-agent-phase-stuck, te-false-completion, handoff-gap, empty-queue. Intentionally excluded: status-reason-stagnant (busy agent), idle-idle-cycle (compound), ai-anomaly (warn-only meta), deadlock (own channel).
27. **TE owns T-FINAL testing — SE must not add `tests-added`** (May 2026): SE's T-FINAL path previously added `tests-added` directly, bypassing the TE. The PM requires both label AND TE comment before reviewing. TE was bypassed → no comment → PM silently skipped review for 6h. Fix: removed `tests-added` from SE. TE now handles T-FINAL (empty PRs get "[TestEngineer] No Tests Needed" comment, code changes get normal assessment). PM defense-in-depth comment check works correctly now. SE merge gate made conditional on `TestEngineerReviews` config.
28. **FlowMonitor diagnostic enrichment — rung-2 PR comments are unread noise** (May 2026): Research confirmed no agent parses FlowMonitor comments. Rung-1 bus nudge is a no-op log. Only rung-3 (human label + notification) is effective. `IFlowDiagnosticEnricher` added to run after detection, before action selection. `PrLifecycleDiagnosticEnricher` checks PM/TE/Architect gate conditions with ✅/❌ checklist. Approvals page shows collapsible diagnostic details with honest messaging. Future: consider 2-rung ladder (nudge → human with diagnostics).
29. **Always re-fetch PR after `MarkReadyForReviewAsync` before label writes** (May 2026): The label API replaces the entire set atomically (see Lesson #4). Using the original `pr.Labels` after a label swap silently overwrites the swap. The T-FINAL path caused PR #1628 to stay on `in-progress` for 6h. Rule: after any `MarkReadyForReviewAsync` or `UpdateAsync` on labels, re-fetch before the next write.
30. **Centralized PR lifecycle via `PrLifecycleCalculator`** (May 2026): Label-checking logic was scattered across agents, detectors, and UI. Created a pure stateless calculator in `Core/Lifecycle/` that derives stages from labels + comments + config. 6 stages: Dev → Architect → Peer Review → Testing → PM → Merge. Stages built dynamically (not hardcoded). Config-aware: `IsInlineTestWorkflow`, `TestEngineerReviews`, `IsSinglePr`, peer review agent configuration. `PrLifecycleTimeline.razor` renders horizontal timeline with emoji icons in PR detail popups. 14 unit tests.
31. **ffmpeg detection at startup** (May 2026): Runner probes for `ffmpeg` at startup. If not found, GIF generation is skipped with a visible "GIF skipped — ffmpeg not found" log message. Welcome wizard Prerequisites step also detects ffmpeg. README prereqs updated to list ffmpeg as optional dependency.
32. **Multi-Process Preview for full-stack apps** (May 2026): `PreviewBuildService.TryDetectCompanionFrontend` auto-detects Vite/React frontend alongside a .NET API backend. `StartCompanionProcessAsync` starts both processes. Screenshots navigate to the frontend URL for accurate visual capture instead of the API port.
33. **npm.cmd requires `cmd /c` wrapping on Windows** (May 2026): Spawning `npm` directly via `Process.Start` fails on Windows because `npm` is a `.cmd` batch file, not an executable. All child process spawns for npm must use `cmd /c npm ...`. Fixed in `BuildRunner` and related process spawn paths.
34. **FreshPathResolver centralizes PATH for child processes** (May 2026): `ffmpeg`, `npm`, and other tools may not be on the PATH inherited by the Runner's child processes (especially after `dotnet run` strips environment). `FreshPathResolver` reads the Windows registry to get the current system+user PATH and injects it into `ProcessStartInfo.Environment["PATH"]` for all child process spawns (`CopilotCliProcessManager`, `BuildRunner`, `TestRunner`, `PlaywrightRunner`). Replaces per-tool PATH workarounds.
35. **SE rework on already-merged PRs** (May 2026): If an SE agent receives rework feedback on a PR that was already merged (race between review and merge), the rework path would fail trying to update a closed PR. Fix: API fallback check detects merged state and skips rework gracefully.
36. **T-FINAL premature start** (May 2026): `CreateIntegrationPRAsync` could start T-FINAL before all preceding task PRs were merged, leading to merge conflicts (22 conflicts observed in compliance run). Fix: merge guard in `CreateIntegrationPRAsync` verifies all task PRs are in merged state before proceeding.
37. **Model pricing suffix matching** (May 2026): `claude-opus-4.6-1m` was priced as `claude-opus-4.6` because the suffix matcher didn't account for the `-1m` variant. This underestimated costs by 5x. Fix: pricing lookup uses exact model ID match first, then falls back to prefix matching.
38. **TruncateForPrompt removed** (May 2026): With 1M context window models (claude-opus-4.6-1m, claude-opus-4.7), document truncation is unnecessary and harmful — agents were making decisions on partial information. Removed `TruncateForPrompt` calls; full documents are now passed to all agent prompts.
39. **Duplicate inline + summary review comments** (May 2026): Review agents were posting the same feedback as both inline PR comments AND summary body text, creating noise. Fix: suppressed duplicate inline comments when the same content already appears in the review summary.
40. **Media pipeline: test URL targeting + health probe + parallel capture** (May 2026): DirectCapture and MCP exploration now use acceptance criteria test URLs (parsed via `ExtractTestUrlPaths()`) instead of blindly hitting the root URL. Health probe (5s HTTP GET) runs before MCP exploration and DirectCapture to catch dead servers early. Screenshots captured even for loading/blank pages as diagnostic evidence. `CandidateEvaluator` runs gate+media for all candidates in parallel via `Task.WhenAll`. Plan generation prompts include `## Visual Verification` section.
41. **Ghost approval cards for failed clarification generation** (May 2026): When `GeneratePrePRQuestionsAsync` fails (e.g., LLM timeout), the PrePRClarificationStore entry exists but has no questions. The Approvals page rendered an empty card with no actionable content. Fix: detect failed generation and auto-approve the empty set so agents don't block on phantom approvals.
42. **Scenario approval persistence** (May 2026): Scenario configuration (observation surface settings) now persists to `develop-settings.json` instead of being lost on runner restart.
43. **Blank architect/PM review comment suppression** (May 2026): When architect or PM review produced empty/whitespace-only review text (e.g., LLM returned no actionable feedback), the agent would post a blank comment to the PR. Fix: skip posting when review body is empty or whitespace-only.
44. **Strategy Recovery Checkpoints** (May 2026): `StrategyRecoveryStore` persists candidate patches + metadata at `ExecutionDone` checkpoint. On restart, `TryRecoverFromCheckpointAsync` resumes evaluation without re-executing strategies. Config: `StrategyFrameworkConfig.RecoverOrphanedCandidates` (default `true`). Eliminates wasted re-runs when runner restarts mid-evaluation.
45. **Refresh-kills-agents fix** (May 2026): Timeline and Overview page refresh buttons were calling `ResetCaches` which killed agent state. Fix: removed `ResetCaches` from refresh button handlers — refresh now only re-fetches display data without side effects.
46. **T-FINAL re-invocation guard** (May 2026): Recreate counter prevents infinite strategy re-runs when T-FINAL fails and retries. Without this guard, a failing T-FINAL could loop indefinitely through strategy framework evaluation.
47. **Binary-quality gate fix** (May 2026): When only one strategy candidate survives elimination, neutral-only visual scores (no positive/negative signal) no longer reject the sole survivor. Previously the quality gate could reject the only remaining candidate, leaving no winner.
48. **Visual Score Winner Selection ordering** (May 2026): `ApplyVisualScoresAsync` now runs BEFORE winner selection so `VisualsScore` actually affects candidate ranking. Previously visual scores were applied after selection, making them purely decorative.
49. **PlaywrightRunner split** (May 2026): Refactored from 4766 → 2603 lines. Extracted `AppLauncher.cs` (app process management), `MediaRecorder.cs` (video/GIF capture), `ApiSmokeRunner.cs` (API smoke tests). `CaptureMode.ScreenshotOnly` skips video/GIF. `MediaCaptureGate` pre-flight check. `IMediaCaptureService` interface. Unified capture pipeline (ready-for-review uses same path as strategy framework).
50. **Scenario loading fix** (May 2026): `ScenarioReview` now loads configuration from parent parameter instead of re-reading stale bin copy. Prevents stale scenario data after hot-reload.
51. **`scenarios.architecture.mapped` signal** (May 2026): Auto-detected when architecture phase completes, enabling downstream gates without manual signal firing.
52. **SE Lead foundation task retry loop** (May 2026): `ClaimRegistry` pre-check prevents SE Lead from repeatedly claiming the same foundation task, eliminating the retry loop.
53. **Strategies page crash** (May 2026): Deduplicate recent tasks before rendering to prevent `ArgumentException` from duplicate keys in the task list.
54. **TeamViz crash** (May 2026): Bounds check on agent/position mismatch array prevents `IndexOutOfRangeException` when agent count doesn't match expected positions.
55. **T-FINAL prompt hardened** (May 2026): Mandatory build/test/startup verification steps added to T-FINAL prompt. Removed "close if no fixes needed" escape clause that allowed T-FINAL to skip integration without attempting verification.
56. **FlowMonitor auto-approval for stuck gates** (2026-05-16): FlowMonitor now auto-approves stuck gates and decisions after `AutoApprovalMinutes` (default 30, configurable on the Configuration page). Gate-stuck findings route directly to `AutoApproveGateAction`. Decision gate REST API: `GET /api/decisions/pending`, `POST /api/decisions/{id}/approve`.
57. **Wave gate fix — `IsWaveEligible` requires merged PR** (2026-05-16): `IsWaveEligible` now requires the PR to be merged (not just pushed) before later waves start. A 30-minute grace period prevents deadlock when merges are slow. This eliminated the merge conflicts observed in previous runs where later-wave work started before earlier-wave PRs had merged.
58. **Flow Timeline page** (2026-05-16): New dashboard page at `/timeline/timing` showing wall-clock pipeline milestone breakdown — visualizes time spent in each workflow phase from start to finish.
59. **Remaining work from 2026-05-16 session**: 3 pending TODOs tracked in `docs/REMAINING-TODOS-20260516.md`: (a) parallel MCP + C# Playwright capture, (b) Chrome DevTools MCP integration, (c) screenshot metrics.
60. **Per-agent restart endpoint** (2026-05-19): `POST /api/dashboard/agents/{agentId}/restart` is now available, and the same action is exposed as a `🔄` button on Dashboard agent cards. It routes through `AgentSpawnManager.RespawnAgentAsync`, stopping the old agent and creating a new instance with the same identity. Use this before a full warm restart when only one agent is stuck.
61. **Worktree stale git state cleanup parity** (2026-05-19): `WorktreeWorkspace` now clears stale git state (`rebase-merge`, `rebase-apply`, `MERGE_HEAD`, etc.) just like `LocalWorkspace`. If TE or another worktree-mode agent logs `git rebase orig...` failures, the next attempt should usually self-heal without manual cleanup.
62. **PM fallback for TE error comments** (2026-05-19): PM no longer requires only the `tests-added` label to proceed. If TE posted an error comment and the label is missing, PM should still review the PR. When a PR looks stuck without PM review, check for TE error comments before intervening.
63. **Local merge conflict rebase fallback** (2026-05-19): `LocalBareRepoManager.MergeBranchAsync` now retries conflicted local merges through a rebase fallback. Parallel PRs touching shared files should merge more reliably, reducing the need for manual local rebases.
64. **Strategies page reset endpoint** (2026-05-21): `POST /api/strategies/reset/{runId}/{taskId}/{strategyId}` is now live. `OrchestrationCancellationService.RequestCandidateReset` cancels the current process, sets a reset flag, and the orchestrator retries with a fresh CTS/worktree via the escalation ladder (rung 1 = same config, rung 2 = `ForceNoWrapper`). `IStrategiesDataService.ResetCandidateAsync` is implemented by both in-process and HTTP dashboard services.
65. **Wrapper liveness watchdog fix** (2026-05-21): watchdog now probes child processes with `pwsh` first and falls back to `powershell` only if `pwsh` is missing. It logs startup and each empty-child count at Information level, so production logs now show wrapper freezes instead of silently hiding them behind Debug.
66. **PS 5.1 root cause confirmed** (2026-05-21): when the runner inherits Windows PowerShell 5.1, a configured CLI wrapper can launch but never spawn `copilot.exe`; under PowerShell 7 it consistently spawns within ~3 seconds. Always launch the runner from `pwsh`; `scripts/start-runner.ps1` now enforces this.
67. **State snapshot test harness** (2026-05-21): `tests/temp/capture-state.ps1` captures full VDT state (SQLite DBs, workspaces, worktrees, target repo, `develop-settings.json`, local-platform repo). Restore scripts such as `tests/temp/BeforePR_Local/setup.ps1` rebuild and restart the runner from a captured snapshot, enabling reproducible tests at specific pipeline stages.
68. **Pipeline status + stall triage shortcut** (2026-05-22): start with `GET /api/pipeline/status` for the one-call snapshot, then check FlowMonitor for `pipeline-stall` if the phase is still active but nothing is moving. This is now the quickest way to diagnose stale `status:blocked` work and all-idle/no-PR stalls.
69. **Strategy preview states are now diagnostic, not cosmetic** (2026-05-22): empty strategy tiles have three distinct meanings — `CaptureUnavailable`, `CaptureFailed`, `NoVisualContent`. Treat `CaptureUnavailable` / `CaptureFailed` as environment or app-start problems, not as harmless backend-only placeholders.
70. **Operator change requests preserve approvals by design** (2026-05-22): the PR detail "Add Changes" flow is meant for human governance. It posts `**[Operator] CHANGES REQUESTED**`, does not consume automated rework budgets, carries the request into `_implementationNotes`, and completes with `**[Operator-Addressed]**` when done.
71. **Timeline story creation is in-dashboard now** (2026-05-22): use the New Story Wizard from the timeline `+` action when you need to drop a new story into a specific wave with clarifications and dependencies, instead of leaving the dashboard to author it elsewhere.

Note: Don't do any long pauses that are more than 1 minute long in the Copilot chat, as that makes it so you ignore me for X minutes--always keep checking back no more than a minute so the chat
thread isn't blocked to get instructions from me. 

## 8. Working Preferences

1. **Rubber-duck validation is MANDATORY**: Every TODO plan and implementation must go through rubber-duck agent validation before executing. No exceptions — even for "simple" changes. Call the rubber-duck agent after planning your approach but before implementing it.
2. **Playwright iterative assessment is MANDATORY for UI changes**: Read `docs/system/PlaywrightAssessment.md` for the full protocol. After every UI fix/feature: define acceptance criteria → launch Playwright → assess each scenario → ask 5+ validation questions → report → create TODOs for gaps → iterate until all criteria pass. If the UI state isn't visible yet, set up a scheduled check at an appropriate interval (1m/5m/10m/30m). Never claim a UI fix works without Playwright visual evidence.
3. **Never sleep > 60 seconds**: Keep checking back within 1 minute.
4. **Never kill by process name**: Always use `Stop-Process -Id <PID>` with a specific PID.
5. **Never use Tee-Object**: It causes issues in this environment.
6. **Never approve gates without user permission**: Gates require explicit user sign-off.
7. **No timeout bandaids**: Never fix hangs with timeouts. Always find the true root cause using multi-agent research. Timeouts cause retries that double end-to-end time. If something is stuck, diagnose the underlying issue — don't mask it with a timer.
8. **Detailed PR descriptions are mandatory**: When creating VDT PRs (`behumphr` → `main`), always include a detailed description with: (a) summary, (b) itemized changes with what/why/impact for each, (c) root causes for bug fixes, (d) testing notes. See `copilot-instructions.md` → "VDT Development PR Descriptions" for the full template. The description is the permanent record — a reviewer should understand the full scope without reading every diff line.

*Last updated: 2026-05-22 (Added pipeline-status / pipeline-stall operating guidance, strategy preview-state diagnostics, operator change-request workflow notes, New Story Wizard timeline docs, Agent Log Viewer notes, Playwright driver-file recovery guidance, and T-FINAL approval guidance; previous 2026-05-21 state-snapshot and wrapper-liveness notes retained above.)*
