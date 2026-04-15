# AgentSquad Session Handoff

> **Purpose:** Everything a new Copilot CLI session needs to get up to speed quickly. Read this file first, then follow the steps below.

---

## 1. Essential Context Documents

Read these in order to understand the project and expectations:

```
Read C:\Git\AgentSquad\Session.md           (this file — session setup)
Read C:\Git\AgentSquad\docs\MonitorPrompt.md (monitoring checklist & failure modes)
Read C:\Git\AgentSquad\docs\Requirements.md  (project requirements)
Read C:\Git\AgentSquad\LessonsLearned.md     (hard-won operational knowledge)
```

Also read the `.github/copilot-instructions.md` (auto-loaded) for architecture, conventions, and build/test commands.

---

## 2. GitHub Reset (Fresh Run)

Before starting a new agent workflow run, fully reset the target GitHub repo:

### Option A: Use the fresh-reset script (recommended)
```powershell
# No PAT argument needed — reads from dotnet user-secrets automatically
.\scripts\fresh-reset.ps1

# Or with explicit PAT
.\scripts\fresh-reset.ps1 -GitHubToken $pat

# Preserve the HTML design reference (default preserves .gitignore + OriginalDesignConcept.html)
.\scripts\fresh-reset.ps1 -PreserveFiles "OriginalDesignConcept.html,.gitignore"
```

### Option B: Use reset-runner script (reads PAT from user-secrets automatically)
```powershell
# Full reset — reads PAT from user-secrets (falls back to appsettings.json), stops runner, cleans GitHub + local state
.\scripts\reset-runner.ps1
```

### Option C: Dashboard UI reset
Navigate to the **Configuration** page (http://localhost:5050/configuration) in the embedded dashboard.
Use the "Scan Repository" button to preview, then "Clean & Restart" to execute.
This is only available in embedded mode (Runner-hosted dashboard on port 5050).

### Verify reset before proceeding
```powershell
# Get PAT from user-secrets
$patLine = dotnet user-secrets list --project src\AgentSquad.Runner 2>&1 | Where-Object { $_ -match 'GitHubToken' }
$pat = ($patLine -split '= ', 2)[1].Trim()
$settings = Get-Content src\AgentSquad.Runner\appsettings.json | ConvertFrom-Json
$repo = $settings.AgentSquad.Project.GitHubRepo

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

# Files: must only be preserved files (.gitignore, OriginalDesignConcept.html)
$contents = Invoke-RestMethod "https://api.github.com/repos/$repo/contents?ref=main" -Headers $headers
Write-Host "Repo files: $($contents.name -join ', ')"  # MUST only show preserved files
```

> ⚠️ **CRITICAL: Always paginate GitHub API calls during reset.** A typical agent run creates 200+ issues. The API returns max 100 per page. A single non-paginated fetch will miss items and leave the repo dirty. When closing items, re-fetch page 1 each iteration (closing shifts items between pages).

**Important:** The PAT is stored in `dotnet user-secrets` for the Runner project. To set or update it:
```powershell
dotnet user-secrets set "AgentSquad:Project:GitHubToken" "<your-pat>" --project src\AgentSquad.Runner
```
The repo name is read from `src/AgentSquad.Runner/appsettings.json` under `AgentSquad.Project.GitHubRepo`. Note: this user is an Enterprise Managed User (EMU) — `gh issue create` may fail with 403. Use the runner's Octokit integration or direct REST API with the PAT instead.

---

## 3. Building & Running

### Architecture: Two Processes

The system runs as two independent processes:

| Process | Port | Purpose |
|---------|------|---------|
| **Runner** | 5050 | Agent orchestration + REST API + embedded dashboard |
| **Dashboard.Host** (optional) | 5051 | Standalone dashboard — can restart without killing agents |

The **Runner** hosts an embedded Blazor dashboard at port 5050 that has full functionality (including Configuration page and cleanup).

The **Dashboard.Host** is an optional standalone process that connects to the Runner's REST API. It can be restarted independently without disrupting running agents. Some features (Configuration, Engineering Plan) are hidden in standalone mode.

### Starting the Runner

```powershell
# Option 1: Use the start script (recommended — builds, logs, manages PID)
.\scripts\start-runner.ps1

# Option 2: Manual start (detached, no Tee-Object!)
dotnet build src\AgentSquad.Runner
Start-Process -FilePath "dotnet" -ArgumentList "run --project src\AgentSquad.Runner --no-build" -WindowStyle Hidden -PassThru

# Option 3: Direct exe (after building)
dotnet build AgentSquad.sln
Start-Process -FilePath "src\AgentSquad.Runner\bin\Debug\net8.0\AgentSquad.Runner.exe" -WindowStyle Hidden -PassThru
```

Dashboard is available at **http://localhost:5050** once the Runner starts.

### Starting the Standalone Dashboard (optional)

```powershell
# Option 1: Use the start script
.\scripts\start-dashboard.ps1

# Option 2: Manual start
dotnet build src\AgentSquad.Dashboard.Host
Start-Process -FilePath "dotnet" -ArgumentList "run --project src\AgentSquad.Dashboard.Host --no-build" -WindowStyle Hidden -PassThru
```

Standalone dashboard at **http://localhost:5051** connects to Runner API at port 5050.

### Stopping

```powershell
# Stop runner (also stops embedded dashboard)
.\scripts\stop-runner.ps1

# Or by PID
Stop-Process -Id <PID>
```

### Critical runner rules
- **NEVER** use `dotnet run | Tee-Object` — it kills the runner during Copilot CLI subprocess calls
- **NEVER** kill processes by name (`Stop-Process -Name`, `taskkill /IM`) — it kills your own CLI session
- **Always** stop the runner before building (file locks on DLLs)
- Find runner PID: `Get-Process -Id (Get-Content runner.pid)` or `Get-NetTCPConnection -LocalPort 5050`
- The Runner spawns a child dotnet process — the child owns port 5050. Check both PIDs.

---

## 4. Monitoring Expectations

Read `docs/MonitorPrompt.md` for the full checklist. Key points:

### What to watch
1. **Phase progression**: Research → PM Spec → Architecture → Engineering Planning → Development → Testing → Review → Complete
2. **Agent status cycles**: Idle → Working → Idle is normal. Idle → Idle → Idle with open work = stuck.
3. **PR pipeline per engineering PR**: created → `ready-for-review` → Architect review → `architect-approved` → TE tests → `tests-added` → PM review → `pm-approved` → PE merge
4. **Rate limiting**: GitHub API limit is 5000/hr. Runner has 30s TTL shared cache (~90% reduction). Watch for `Rate limit exceeded` in logs.
5. **Human gates**: If FinalPRApproval gate is enabled, PRs will pause with `awaiting-human-review` label. Check the Approvals page or PR comments to approve/reject.

### Dashboard pages
| Page | URL | Key Features |
|------|-----|-------------|
| Overview | `/` | Agent cards, status, activity logs, agent visibility filter |
| Project Timeline | `/timeline` | Phase-grouped issues/PRs, PM/Engineering toggle, auto-refresh |
| Metrics | `/metrics` | Build/test metrics, agent performance |
| Health Monitor | `/health` | Deadlock detection, health checks |
| Repository | `/repository` | Combined PR + Issue tabs |
| Configuration | `/configuration` | Settings editor, GitHub cleanup (embedded mode only) |

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
- TE UI test failure "App did not respond at http://localhost:XXXX within 90s" — likely hardcoded port in AI-generated Program.cs (see Lesson #20 in LessonsLearned.md)
- Agent card flashing "⏳ Awaiting human approval..." when gates are in auto mode — pre-gate status update not guarded (see Lesson #22)

---

## 5. Dashboard Features

### Embedded Dashboard (http://localhost:5050)
Full-featured dashboard hosted by the Runner process. Includes all pages and Configuration/cleanup functionality.

### Standalone Dashboard (http://localhost:5051)
Optional independent process. Connects to Runner REST API at `/api/dashboard/*`.
- **Can restart** without disrupting running agents
- **Hidden pages**: Configuration, Engineering Plan (require Runner-only services)
- **All other pages** work identically via HTTP proxy

### Key features
- **Project Timeline**: Phase-grouped view with PM/Engineering toggle, node type indicators (PR vs Issue), clickable GitHub links, 30s auto-refresh (background, no overlay)
- **Agent Overview**: Real-time agent cards with visibility filter (hide/show agents)
- **Repository**: Combined Pull Requests + Issues view with tab navigation
- **Force refresh**: SVG refresh button on Timeline and Overview pages

### Timeline data flow
- Issues/PRs fetched via `DashboardDataService` (30s TTL cache, shared with GitHubService)
- `BuildTimeline()` pipeline: run detection → filter → dedup → synthetic doc PRs → parent-child → phase grouping
- Doc phases (Research, PM Spec, Architecture) appear as synthetic nodes from PRs
- Engineering tasks filtered to latest burst (30-min window from newest)

---

## 6. Key Configuration

Config is in `src/AgentSquad.Runner/appsettings.json`. Sensitive values (PAT) are in `dotnet user-secrets`.

Key settings:
- `AgentSquad.Project.GitHubRepo`: `"azurenerd/ReportingDashboard"`
- `AgentSquad.Project.GitHubToken`: Stored in **dotnet user-secrets** (not in appsettings.json)
- `AgentSquad.CopilotCli.Enabled`: `true` (routes all AI through `copilot` CLI)
- `AgentSquad.CopilotCli.SinglePassMode`: `true` (single AI call per doc, not multi-turn)
- `AgentSquad.CopilotCli.MaxConcurrentRequests`: `5`
- `AgentSquad.Models`: Per-tier model definitions (premium/standard/budget/local)
- `AgentSquad.Limits.MaxAdditionalEngineers`: `3`
- `AgentSquad.HumanInteraction.Enabled`: `true` (enables human gate checkpoints)
- `AgentSquad.HumanInteraction.Preset`: Use Full Auto / Supervised / Full Control via Configuration page
- Note: Gate configuration is hot-reloadable — changes take effect without runner restart

### Model tier strategy
| Tier | Used By | Default Model |
|------|---------|---------------|
| premium | PM, Architect, PE | claude-opus-4.6 |
| standard | Researcher, Senior Engineers, TE | claude-sonnet-4.6 |
| budget | Junior Engineers | gpt-5.2 |

---

## 7. Known Issues & Workarounds

1. **GitHub EMU restrictions**: `gh issue create` fails with 403. Use Octokit via the runner or REST API with PAT.
2. **Rate limiting**: Heavy runs can exhaust the 5000/hr GitHub API limit. The 30s TTL cache reduces API calls by ~90%. Dashboard shows rate-limit status.
3. **Stale checkpoint recovery**: Runner uses `WorkflowStateMachine` checkpoint. If resuming an old run, the phase may be wrong. Delete the DB for a fresh start.
4. **Agent workspaces**: TE and engineers clone repos to `C:\Agents\`. These persist across runs — delete for fresh start.
5. **PM issue ordering**: The PM extraction prompt instructs dependency-ordered issue creation (scaffolding first). If issues come out in wrong order, check the extraction prompt in `ProgramManagerAgent.CreateUserStoryIssuesAsync()`.
6. **DLL locks during build**: Runner/Dashboard must be stopped before rebuilding. Use `.\scripts\stop-runner.ps1` first.
7. **Standalone dashboard limitations**: Configuration page and Engineering Plan page are hidden. Use embedded dashboard (port 5050) or `fresh-reset.ps1` script for cleanup.
8. **Vision review requires network access**: Screenshot download in PR reviews needs the runner to reach GitHub's raw content URLs. If behind a proxy, images fall back to URL-only text context.
9. **Gate config hot-reload**: Gate settings are hot-reloaded via `IOptionsMonitor`. Other config sections (Models, Agents, Limits) still require restart.
10. **Port conflicts between agents**: PE screenshots and TE UI tests both start the app under test. Each agent now uses a unique port derived from its workspace path (range 5100–5899). If you see "App did not respond" errors, check for port conflicts.
11. **Standalone dashboard stale agents**: The DB accumulates agent records across restarts. `RecordBoot()` writes `last_boot_utc` to filter to current-run agents only. If dashboard shows old agents, restart the Runner to update the boot timestamp.
12. **TE data.json**: Blazor apps that depend on `wwwroot/data.json` may fail on fresh clones. `EnsureSampleDataExists()` auto-creates a sample data file if missing.

Note: Don't do any long pauses that are more than 1 minute long in the Copilot chat, as that makes it so you ignore me for X minutes--always keep checking back no more than a minute so the chat
thread isn't blocked to get instructions from me. 

*Last updated: 2026-04-15*
