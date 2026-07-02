# Large Projects Plan — VirtualDevTeam In-Place Development Mode

> **Status:** Design proposal — not yet implemented
> **Created:** 2026-05-17
> **Design input:** 10 AI models (GPT-5.5, GPT-5.4, GPT-5.3-Codex, GPT-5.2, GPT-5-mini, Claude Opus 4.7, Claude Opus 4.6, Claude Sonnet 4.6, Claude Haiku 4.5, GPT-4.1) + 3 codebase exploration passes + 3 rubber-duck validation passes

---

## 1. Problem Statement

VDT's current workspace model requires **one full clone per agent** (`LocalWorkspace.InitializeAsync` → `git clone`). This fails catastrophically for massive projects:

| Scenario | Why it fails |
|----------|-------------|
| ADO monorepo (500+ services, 100+ GB) | 2+ hour clone × 5 agents = 10+ hours just to init |
| Windows repo (millions of files) | MAX_PATH failures, no GVFS/VFS awareness |
| Enterprise React+.NET (50 microservices) | One build command for 50 services; no service scoping |
| Game engine (200GB assets) | LFS objects re-downloaded per clone; binary assets wasteful |

**Goal:** Let VDT operate on massive existing projects by working in-place on an already-cloned, already-set-up developer checkout — without cloning, without disturbing the developer's working tree, and with service-level scoping for monorepos.

---

## 2. Architecture Overview

### 2.1 Three Workspace Modes

```csharp
public enum WorkspaceMode
{
    Clone,      // Current behavior — full clone per agent into .agents/
    Worktree,   // Single canonical clone, lightweight worktrees per agent
    InPlace,    // Use existing checkout; agent branches live in worktrees
}
```

| Behavior | Clone (current) | Worktree | InPlace |
|----------|----------------|----------|---------|
| Disk footprint | N × repo size | 1 × repo + small worktrees | 0 extra (operator already has it) |
| Clone cost | Every agent on init | Once at startup | None |
| Agent isolation | Full directory isolation | Branch isolation via worktrees | Branch isolation via worktrees |
| Operator's working tree | Never touched | Never touched | Never touched |
| Strategy worktrees | From agent's clone | From shared canonical clone | From operator's checkout |
| Sparse checkout | Optional | Recommended | Recommended |

**Key invariant:** In all modes, agents **never modify the operator's working tree**. In Worktree/InPlace modes, each agent gets a lightweight `git worktree` branched off the shared `.git`.

### 2.2 Why Git Worktrees Are the Right Primitive

`git worktree add` from an existing repo is:
- **Fast** — shares `.git/objects`, only materializes working tree files (seconds, not minutes)
- **Disk-efficient** — with sparse checkout, a worktree can be 10-500 MB instead of 100 GB
- **Isolated** — each worktree has its own branch, index, and working tree
- **Already used by VDT** — `GitWorktreeManager` already creates worktrees for strategy candidates

### 2.3 Component Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Develop Wizard                        │
│  Workspace Mode selector │ Service Registry builder      │
│  Existing repo path      │ Sparse checkout editor        │
└─────────────┬───────────────────────────┬───────────────┘
              │ develop-settings.json     │
              ▼                           ▼
┌─────────────────────┐   ┌──────────────────────────────┐
│  WorkspaceConfig     │   │  LargeProjectConfig          │
│  - WorkspaceMode     │   │  - Services[]                │
│  - LocalCheckoutPath │   │  - IncrementalBuild          │
│  - SparsePatterns    │   │  - DevServer                 │
│  - WorktreeRoot      │   │  - ServiceScopedAssignment   │
└────────┬────────────┘   └──────────┬───────────────────┘
         │                           │
         ▼                           ▼
┌─────────────────────────────────────────────────────────┐
│              SharedCloneManager (singleton)              │
│  - EnsureReadyAsync() — clone or validate existing      │
│  - CreateWorktreeAsync() — with sparse, per-agent lock  │
│  - RemoveWorktreeAsync() — cleanup + prune              │
│  - _hostGitLock (SemaphoreSlim) — serialize .git ops    │
└────────┬────────────────────────────┬───────────────────┘
         │                            │
    ┌────▼─────┐              ┌──────▼────────┐
    │ Agent    │              │ Strategy      │
    │ Worktree │              │ Candidate     │
    │ (branch) │              │ Worktree      │
    └──────────┘              └───────────────┘
```

---

## 3. Configuration Design

### 3.1 WorkspaceConfig Additions

```csharp
// Core/Workspace/WorkspaceConfig.cs — new properties

public WorkspaceMode WorkspaceMode { get; set; } = WorkspaceMode.Clone;

/// <summary>
/// Absolute path to the operator's existing checkout (InPlace mode).
/// Must contain a valid .git directory. VDT never modifies this working tree.
/// </summary>
public string? LocalCheckoutPath { get; set; }

/// <summary>
/// Where agent worktrees are created (Worktree/InPlace modes).
/// Default: sibling of LocalCheckoutPath (e.g., C:\src\.vdt-worktrees).
/// Must be on same filesystem as the source repo for hardlink sharing.
/// </summary>
public string? WorktreeRoot { get; set; }

/// <summary>
/// Sparse checkout cone patterns applied to each worktree.
/// Empty = full checkout. Example: ["src/services/auth", "src/shared", "build"]
/// </summary>
public List<string> SparseCheckoutPaths { get; set; } = new();

/// <summary>
/// Git clone flags for Worktree mode's initial clone.
/// Examples: "--filter=blob:none" (blobless), "--depth 1" (shallow).
/// Ignored in InPlace mode.
/// </summary>
public string CloneFlags { get; set; } = "";

/// <summary>
/// Refuse to operate if operator's checkout has uncommitted changes.
/// Default: true (safety first).
/// </summary>
public bool RequireCleanHostTree { get; set; } = true;
```

### 3.2 Service Registry

```csharp
// Core/Configuration/LargeProjectConfig.cs

public class LargeProjectConfig
{
    public bool Enabled { get; set; } = false;
    public List<ServiceDefinition> Services { get; set; } = new();
    public IncrementalBuildConfig IncrementalBuild { get; set; } = new();
    public DevServerConfig DevServer { get; set; } = new();
    public bool ServiceScopedTaskAssignment { get; set; } = true;
}

public class ServiceDefinition
{
    public required string Name { get; init; }          // "auth-api"
    public string? DisplayName { get; init; }           // "Auth API"
    public required string Path { get; init; }          // "src/services/auth"
    public string? BuildCommand { get; init; }          // "dotnet build Auth.csproj"
    public string? TestCommand { get; init; }           // "dotnet test Auth.Tests.csproj"
    public string? AppStartCommand { get; init; }       // "dotnet run --project Auth"
    public int? Port { get; init; }                     // 5001
    public string? HealthUrl { get; init; }             // "http://localhost:5001/health"
    public bool UseExistingDevServer { get; init; }     // true
    public string? TechStack { get; init; }             // "dotnet"
    public List<string> AdditionalSparsePaths { get; init; } = new();
    public List<string> ExpertiseTags { get; init; } = new(); // ["dotnet", "auth"]
}

public record DevServerConfig
{
    public string? BaseUrl { get; init; }               // "http://localhost:3000"
    public string? HealthUrl { get; init; }             // "http://localhost:3000/health"
    public bool AssumeAlwaysUp { get; init; } = true;
}

public class IncrementalBuildConfig
{
    public bool Enabled { get; set; } = false;
    public string Strategy { get; set; } = "service-scope"; // service-scope | changed-files
    public bool TreatNoOpAsSuccess { get; set; } = true;
}
```

### 3.3 develop-settings.json Extensions

```jsonc
{
  "workspaceMode": "inPlace",
  "existingRepoPath": "C:\\src\\BigProject",
  "worktreeRoot": "C:\\src\\.vdt-worktrees",
  "sparseCheckoutPaths": ["src/", "build/", "*.sln"],
  "requireCleanHostTree": true,

  "largeProject": {
    "enabled": true,
    "services": [
      {
        "name": "auth-api",
        "displayName": "Auth API",
        "path": "src/services/auth",
        "buildCommand": "dotnet build Auth.csproj",
        "testCommand": "dotnet test Auth.Tests.csproj",
        "port": 5001,
        "healthUrl": "http://localhost:5001/health",
        "useExistingDevServer": true,
        "techStack": "dotnet",
        "additionalSparsePaths": ["src/shared", "src/common"],
        "expertiseTags": ["dotnet", "auth", "security"]
      },
      {
        "name": "frontend",
        "displayName": "Frontend",
        "path": "src/web",
        "buildCommand": "npm run build",
        "testCommand": "npm test",
        "port": 3000,
        "healthUrl": "http://localhost:3000/health",
        "useExistingDevServer": true,
        "techStack": "react+typescript",
        "expertiseTags": ["typescript", "react", "ui"]
      }
    ],
    "devServer": {
      "baseUrl": "http://localhost:3000",
      "healthUrl": "http://localhost:3000/health"
    }
  }
}
```

---

## 4. Core Implementation Changes

### 4.1 SharedCloneManager (New Singleton)

Central coordinator for the shared `.git` object store. Serializes worktree create/remove to avoid `.git/config.lock` races (Lesson #5).

```csharp
public class SharedCloneManager
{
    private readonly SemaphoreSlim _hostGitLock = new(1, 1);
    private DateTime _lastFetch = DateTime.MinValue;
    private readonly TimeSpan _fetchCooldown = TimeSpan.FromMinutes(5);

    public Task<string> EnsureReadyAsync(CancellationToken ct);
    public Task<string> CreateWorktreeAsync(string branch, string agentSlug,
        IReadOnlyList<string>? sparseAdditions, CancellationToken ct);
    public Task RemoveWorktreeAsync(string worktreePath, CancellationToken ct);
    public Task PruneStaleWorktreesAsync(CancellationToken ct);
}
```

Worktree creation flow:
1. Acquire `_hostGitLock`
2. `git branch -f {branch} {baseRef}` in host repo
3. `git worktree add --no-checkout {path} {branch}` — avoids materializing 100GB
4. Release lock
5. Configure sparse checkout in the new worktree (no lock needed)
6. `git checkout {branch}` — only materializes sparse cone files

### 4.2 IAgentWorkspace Interface (Extract from LocalWorkspace)

```csharp
public interface IAgentWorkspace
{
    string RepoPath { get; }
    WorkspaceMode Mode { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task SyncWithMainAsync(CancellationToken ct = default);
    Task CreateBranchAsync(string branchName, CancellationToken ct = default);
    Task CheckoutBranchAsync(string branchName, CancellationToken ct = default);
    Task CommitAsync(string message, CancellationToken ct = default);
    Task PushAsync(string branchName, CancellationToken ct = default);
    Task<string> GetHeadShaAsync(string @ref = "HEAD", CancellationToken ct = default);
    Task RevertUncommittedChangesAsync(CancellationToken ct = default);
    Task CleanupAsync();
    Task NukeAndRecloneAsync(string branchName, CancellationToken ct = default);
    // ... other methods from LocalWorkspace
}
```

Two implementations:
- `LocalWorkspace : IAgentWorkspace` — existing clone-based behavior (default)
- `WorktreeWorkspace : IAgentWorkspace` — worktree-based for Worktree/InPlace modes

### 4.3 LocalWorkspace Mode Dispatch

```csharp
public async Task InitializeAsync(CancellationToken ct)
{
    switch (_config.WorkspaceMode)
    {
        case WorkspaceMode.Clone:
            await InitializeAsCloneAsync(ct);      // existing code unchanged
            break;
        case WorkspaceMode.Worktree:
        case WorkspaceMode.InPlace:
            await InitializeAsWorktreeAsync(ct);   // new path via SharedCloneManager
            break;
    }
}
```

### 4.4 EngineerAgentBase Lifecycle Changes

```csharp
// OnInitializeAsync — branch on workspace mode
if (Config.Workspace.IsInPlaceMode)
{
    // Skip clone probe — check worktree existence instead
    // Skip "already complete" cold-start clone optimization
    Workspace = new WorktreeWorkspace(sharedCloneManager, Identity.Id, branch, Logger);
}
else
{
    Workspace = new LocalWorkspace(Config.Workspace, Identity.Id, repoUrl, branch, Logger);
}
await Workspace.InitializeAsync(ct);
```

### 4.5 NukeAndRecloneAsync for Worktree Mode

```csharp
// WorktreeWorkspace — recovery is FASTER than clone mode
public async Task NukeAndRecloneAsync(string branchName, CancellationToken ct)
{
    // 1. Remove the worktree (force)
    await _sharedCloneManager.RemoveWorktreeAsync(_worktreePath, ct);
    // 2. Delete any leftover directory
    if (Directory.Exists(_worktreePath)) ForceDeleteDirectory(_worktreePath);
    // 3. Re-create fresh worktree from latest base
    _worktreePath = await _sharedCloneManager.CreateWorktreeAsync(branchName, _agentSlug, _sparsePaths, ct);
    // ~2s vs ~30-120s for full re-clone
}
```

---

## 5. Strategy Framework Adaptation

### 5.1 Candidate Worktrees from Shared .git

`GitWorktreeManager.CreateAsync` already creates worktrees — the only change is **where they're rooted**:

```csharp
var candidatesRoot = workspace.Mode == WorkspaceMode.Clone
    ? Path.Combine(agentRepoPath, candidateDirName)                    // inside clone
    : Path.Combine(worktreeRoot, "candidates", candidateDirName);      // outside checkout
```

### 5.2 Narrowed Sparse for Candidates

Strategy candidates know which paths the patch will touch (from the SE's task plan). Use only those paths + shared build files as the sparse cone. A candidate worktree can be **10-50 MB**.

### 5.3 Evaluation Modes

```csharp
public enum CandidateEvaluationMode
{
    FullWorktree,    // Current: checkout + build + run + screenshot
    SparseWorktree,  // Checkout only changed paths + build files
    PatchOnly,       // LLM judge only (no build, no run) — cheapest
}
```

`PatchOnly` applies the patch to an in-memory diff for LLM judging without creating a working tree. Useful for code-only (non-visual) tasks in massive repos where build is expensive.

---

## 6. Build & Test Integration

### 6.1 Service-Aware Command Resolution

```csharp
public interface ICommandResolver
{
    BuildSpec GetBuildSpec(string workspacePath, ServiceDefinition? service);
    BuildSpec GetTestSpec(string workspacePath, ServiceDefinition? service, TestTier tier);
    BuildSpec GetRunSpec(string workspacePath, ServiceDefinition? service);
}

public record BuildSpec(
    string Command,
    string WorkingDirectory,
    IDictionary<string, string>? Env,
    int TimeoutSeconds);
```

Resolution order:
1. `ServiceDefinition.BuildCommand` (per-service explicit)
2. `WorkspaceConfig.BuildCommand` (global explicit)
3. `ProjectTypeDetector` heuristic (auto-detect from `WorkingDirectory`)

### 6.2 Incremental Build Support

For `changed-files` strategy:
```
git diff --name-only HEAD~1 → map to build targets via ServiceDefinition.Path
→ build only affected services
```

For `service-scope` strategy:
```
Always build the assigned service only (fastest, least safe)
```

### 6.3 Dependency Cache Reuse

- **.NET:** `bin/`/`obj/` live in the worktree — inherently isolated. NuGet cache is user-scoped.
- **Node:** Symlink `node_modules` from host repo if package-lock is consistent; otherwise `npm ci --prefer-offline`.
- **Docker:** Share BuildKit cache via user-scoped `~/.docker/buildx`.

---

## 7. Playwright & UI Capture

### 7.1 External App Attach Mode

New concept: instead of launching the app, connect to an already-running dev server.

```csharp
public class AppLauncher
{
    public async Task<AppHandle> LaunchOrAttachAsync(string workspace, ServiceDefinition? svc, CancellationToken ct)
    {
        if (svc?.UseExistingDevServer == true && svc.HealthUrl is not null)
        {
            await WaitForHealthAsync(svc.HealthUrl, ct);
            return AppHandle.Attached($"http://localhost:{svc.Port}");  // no process to manage
        }
        // Fall through to existing launch logic
    }
}
```

### 7.2 Implications

- **No port juggling** — use configured service ports
- **No process management** — no RunnerProcessJob enrollment for external apps
- **Hot reload caveat** — candidate changes in a worktree don't appear in the host's dev server. For per-candidate visual scoring:
  - Option A: Disable visual scoring for external server tasks
  - Option B: Configure dev server to watch the worktree (advanced)
  - Option C: Launch a per-candidate dev server from the worktree (falls back to current behavior)
- **Dashboard banner**: "In-place mode: screenshots reflect the running dev server, not candidate diffs"

### 7.3 Multi-Service URL Routing

```json
"services": [
  { "name": "frontend", "port": 3000, "healthUrl": "http://localhost:3000/health" },
  { "name": "api", "port": 5000, "healthUrl": "http://localhost:5000/health" }
]
```

Playwright targets the service's URL based on the task's service scope. MCP exploration works unchanged — it's already URL-agnostic.

---

## 8. PR/Branch Management

### 8.1 Branch Strategy

Same as today: `agent/{runScope}/{agentSlug}/{taskSlug}`. Branches are created in the shared `.git` via worktrees — each worktree is on its own branch.

### 8.2 Monorepo PR Enhancements

- **Path-scoped PRs**: Pre-push guard blocks out-of-scope file changes
- **CODEOWNERS integration**: Parse owners for changed paths; auto-request reviewers
- **Target branch per service**: `ServiceBranchMap` (e.g., `payments/* → release/2026.06`)
- **Draft-first workflow**: Create Draft PR immediately; mark ready after self-assessment + CI
- **Stacked PRs**: `DependsOnPR[]` metadata for cross-service dependencies
- **Integration branch**: Per-initiative merge branch for multi-service coordination

### 8.3 Path Scoping Enforcement

At commit time, validate changed files are within the assigned `ServiceDefinition.Path`:

```csharp
var outOfScope = changedFiles.Where(f => !f.StartsWith(service.Path, StringComparison.OrdinalIgnoreCase));
if (outOfScope.Any())
{
    _logger.LogWarning("Agent modified files outside service scope: {Files}", outOfScope);
    // enforcement: "off" | "warn" | "block"
}
```

---

## 9. Dashboard & Monitoring Adaptations

### 9.1 New Dashboard Elements

- **Workspace mode badge**: "In-Place" / "Worktree" / "Clone" in header
- **Service navigator**: Filter agents/tasks/PRs by service
- **Service health panel**: Dev server status per service (✅/❌)
- **Worktree hygiene panel**: Count, disk usage, stale worktree warnings
- **Per-service build/test indicators**
- **Agent-to-service assignment visualization**

### 9.2 Timeline Enhancements

- **Service grouping**: Group tasks by service in addition to phase
- **Service dependency graph**: Visualize cross-service task dependencies

### 9.3 FlowMonitor Detectors

New detectors for large project mode:
- `worktree-stale` — Agent worktree exists but agent is idle/stopped
- `service-devserver-down` — External dev server health check failed
- `sparse-checkout-miss` — Build failed with "file not found" in sparse worktree
- `worktree-disk-pressure` — Worktree root disk usage exceeds threshold
- `host-dirty` — Operator's checkout has uncommitted changes (if RequireCleanHostTree=false)

### 9.4 Metrics

- Worktree creation/removal time
- Per-service build/test duration
- Disk usage per worktree
- Sparse checkout miss rate
- External dev server uptime

---

## 10. Safety & Recovery

### 10.1 Safety Invariants

1. **Never write to the host's working tree** — All git ops in the host go through `_hostGitLock` and are limited to `branch`, `worktree add/remove/prune`, `fetch`, `for-each-ref`
2. **Pre-flight checks**: Host path exists, is git repo, clean tree (if required), has needed remotes, disk free > threshold, git version ≥ 2.25 (sparse-checkout support)
3. **Crash recovery**: At runner startup, `git worktree prune` + remove stale `.vdt-worktrees` dirs
4. **Force-push protection**: Refuse push to host's default branch
5. **Auth**: `gh auth token` / PAT still resolves — worktrees inherit the host's `origin` remote

### 10.2 Recovery Strategies

| Failure | Recovery |
|---------|----------|
| Bad commit | `git reset --hard HEAD~1` in worktree (isolated) |
| Merge conflict | `git merge --abort` + remove worktree + recreate (~2s) |
| Corrupted state | Remove worktree + `git worktree add` fresh (~2s) |
| Dirty working tree | `git clean -fd && reset --hard` in worktree |
| .git/config.lock | Jittered retry 3-5× with backoff (existing pattern) |
| Disk space | Prune stale worktrees; warn operator |

### 10.3 Cleanup Script

```powershell
# scripts/cleanup-orphan-worktrees.ps1
# Mirrors kill-orphan-runner-procs.ps1 discipline
git -C <hostRepoPath> worktree list --porcelain
# Prune worktrees under .vdt-worktrees whose lock matches an exited agent
git -C <hostRepoPath> worktree prune
```

---

## 11. Develop Wizard Changes

### 11.1 New "Project Structure" Step

After "Repo & Auth", before "What to Build":

1. **Workspace Mode selector**: Clone (default) | Worktree | In-Place
2. **Existing repo path picker** (Worktree/InPlace): Browse + validate
3. **Service registry builder**: Editable table with Name/Path/BuildCmd/TestCmd/Port
4. **Sparse checkout editor**: Pattern list with match preview
5. **External dev server config**: URLs and health check endpoints

### 11.2 Wizard Validation

```powershell
# Pre-flight checks (run immediately, show green/red pills):
git -C <path> rev-parse --show-toplevel    # is valid git repo?
git -C <path> status --porcelain           # is clean?
git -C <path> worktree list                # existing worktrees?
# Disk space check
# git version check (≥ 2.25 for sparse-checkout)
```

---

## 12. Implementation Phases

### Phase 1: Foundation (Core abstractions)
- [ ] `IAgentWorkspace` interface extraction from `LocalWorkspace`
- [ ] `WorkspaceMode` enum + `WorkspaceConfig` extensions
- [ ] `SharedCloneManager` singleton
- [ ] `WorktreeWorkspace` implementation
- [ ] `EngineerAgentBase` workspace mode dispatch
- [ ] DI registration in Runner + StandaloneServiceRegistration (Lesson #3)
- [ ] Develop wizard "Workspace Mode" step

### Phase 2: Service Registry
- [ ] `LargeProjectConfig` + `ServiceDefinition` classes
- [ ] `ServiceContextResolver` for build/test command routing
- [ ] `ServiceIssueParser` for service scope extraction from issues
- [ ] `BuildRunner`/`TestRunner` service-aware routing
- [ ] Architect prompt augmentation with service list
- [ ] develop-settings.json extensions

### Phase 3: Playwright & Strategy Adaptation
- [ ] `AppLauncher.LaunchOrAttachAsync` external server mode
- [ ] `PlaywrightRunner` external URL targeting
- [ ] Strategy framework worktree routing for InPlace mode
- [ ] `CandidateEvaluationMode.PatchOnly` for cheap evaluation
- [ ] Sparse checkout for candidate worktrees

### Phase 4: Dashboard & Monitoring
- [ ] Workspace mode badge in header
- [ ] Service navigator/filter
- [ ] Service health panel
- [ ] Worktree hygiene monitoring
- [ ] FlowMonitor detectors for large project mode
- [ ] Timeline service grouping

### Phase 5: Polish & Advanced
- [ ] CODEOWNERS integration for PR reviewers
- [ ] Path scoping enforcement (warn/block)
- [ ] Incremental build support
- [ ] Stacked PR support for cross-service dependencies
- [ ] Cleanup script for orphan worktrees
- [ ] Documentation (`docs/InPlaceMode.md`)

---

## 13. Files to Modify (Impact Assessment)

| File | Change Type | Description |
|------|------------|-------------|
| **NEW** `Core/Workspace/IAgentWorkspace.cs` | New interface | Extracted from LocalWorkspace |
| **NEW** `Core/Workspace/WorktreeWorkspace.cs` | New class | Worktree-based workspace |
| **NEW** `Core/Workspace/SharedCloneManager.cs` | New singleton | Worktree lifecycle coordinator |
| **NEW** `Core/Workspace/WorkspaceMode.cs` | New enum | Clone/Worktree/InPlace |
| **NEW** `Core/Configuration/LargeProjectConfig.cs` | New config | Services, incremental build, dev server |
| **NEW** `Core/Workspace/ServiceContextResolver.cs` | New helper | Build/test command routing |
| **NEW** `Core/Agents/Decisions/ServiceIssueParser.cs` | New parser | Service scope from issues |
| **NEW** `scripts/cleanup-orphan-worktrees.ps1` | New script | Worktree cleanup |
| `Core/Workspace/WorkspaceConfig.cs` | Modified | Add workspace mode, LocalCheckoutPath, sparse patterns |
| `Core/Workspace/LocalWorkspace.cs` | Modified | Implement IAgentWorkspace, add mode dispatch |
| `Core/Workspace/BuildRunner.cs` | Modified | Accept BuildSpec with service-aware routing |
| `Core/Workspace/TestRunner.cs` | Modified | Accept service scope |
| `Core/Workspace/AppLauncher.cs` | Modified | LaunchOrAttachAsync |
| `Core/Workspace/PlaywrightRunner.cs` | Modified | External URL targeting |
| `Core/Strategies/GitWorktreeManager.cs` | Modified | Route candidates to worktree root |
| `Core/Strategies/StrategyOrchestrator.cs` | Modified | Use IAgentWorkspace |
| `Core/AI/CopilotCliProcessManager.cs` | Modified | Allow .vdt-worktrees paths |
| `Agents/EngineerAgentBase.cs` | Modified | Workspace mode dispatch, service context |
| `Agents/SoftwareEngineerAgent.cs` | Modified | Workspace type change |
| `Agents/SpecialistEngineerAgent.cs` | Modified | Workspace type change |
| `Agents/TestEngineerAgent.cs` | Modified | Workspace type change |
| `Core/Configuration/DevelopSettings.cs` | Modified | New fields |
| `Dashboard/Components/Pages/Develop.razor` | Modified | Workspace mode step |
| `Dashboard/Components/Pages/Configuration.razor` | Modified | Service registry UI |
| `Runner/Program.cs` | Modified | Register SharedCloneManager |
| `Dashboard.Host/StandaloneServiceRegistration.cs` | Modified | Register SharedCloneManager |

**No changes needed to:** Message bus, DevPlatform interfaces (IPullRequestService etc.), prompt templates, FlowMonitor core, PrLifecycleCalculator, Orchestrator core flow, agent message types.

---

## 14. Key Design Decisions & Rationale

| Decision | Rationale |
|----------|-----------|
| Worktrees over in-place branch switching | Branch switching in a 100GB repo is destructive and slow. Worktrees provide isolation without cost. |
| Sparse checkout by default for InPlace | Without sparse, `git worktree add` checks out ALL files. In a 100GB repo, this defeats the purpose. |
| Service registry as explicit config | Auto-detection is unreliable for large monorepos. Explicit services let operators define boundaries. |
| External dev server attach instead of launch | Large projects have complex startup. Launching from a sparse worktree would fail. Connect to what's already running. |
| `IAgentWorkspace` interface extraction | Enables workspace mode to be swapped without touching 20+ call sites. Clean abstraction boundary. |
| `SharedCloneManager` with `SemaphoreSlim` | `.git/config.lock` races are a known issue (Lesson #5). Serialize worktree ops centrally. |
| PatchOnly evaluation mode | For large repos where build takes 30+ min, LLM-only judging is the practical option. |
| Never modify operator's working tree | This is the #1 safety invariant. Violating it would lose developer's uncommitted work. |

---

## 15. Open Questions for Implementation

1. **GVFS/VFS for Git**: Do `git worktree` commands work correctly with GVFS-enabled repos? Azure DevOps repos often use Scalar/GVFS — need validation.
2. **Binary assets in sparse checkout**: Game engine repos may have `.fbx`, `.png` in tracked LFS. Should sparse patterns include/exclude LFS objects?
3. **Docker Compose services**: Some monorepos require Docker Compose for local dev. Should VDT manage Docker services, or just assume they're running?
4. **CI/CD pipeline awareness**: Large repos have path-scoped CI. Should VDT wait for CI checks before marking PRs ready?
5. **Token/auth for monorepos**: Some monorepos restrict branch creation by path/policy. Need validation before worktree creation.
6. **Worktree limits**: Git has a practical limit on concurrent worktrees. Need to test with 10+ worktrees from a 100GB repo.
7. **Windows MAX_PATH**: Even with `core.longpaths=true`, some Windows tools (MSBuild, npm) may fail with deep worktree paths.
8. **Cross-service integration testing**: When Task A (auth-api) affects Task B (frontend), how do agents coordinate integration tests?
9. **Git LFS lock contention**: Multiple agents editing LFS-tracked files could hit lock contention.
10. **Develop wizard complexity**: Adding Workspace Mode + Service Registry makes the wizard significantly more complex. Should this be a separate "Advanced Setup" wizard?

---

## 16. Rubber-Duck Validation Findings (3 Models × 10 Questions)

> Reviews performed by GPT-5.5, Claude Opus 4.7, and Claude Sonnet 4.6. Each answered 10 challenge questions with specific code references.

### 🔴 Critical Issues (Must Resolve Before Implementation)

| # | Finding | Models | Impact | Resolution |
|---|---------|--------|--------|------------|
| C1 | **`GitWorktreeManager._repoLocks` key is per-worktree-path, not per-.git** — In InPlace mode, each agent's worktree has a different path → different lock keys → `.git/config.lock` protection evaporates. Concurrent candidate worktree creation will race. | Sonnet, GPT-5.5 | Correctness regression — the exact race Lesson #5 documents | Lock key must use `git rev-parse --git-common-dir` to resolve the shared `.git` directory. All code paths (`GitWorktreeManager`, `CandidateEvaluator`, `SharedCloneManager`) must converge on the same lock for the same `.git`. |
| C2 | **`WinnerApplyService` has no mode-aware guard** — It runs `git reset --hard` + `git clean -fd` on whatever `agentRepoPath` is passed. A config bug or stale path cache could destroy the operator's working tree (violates AC1). | Opus | Catastrophic data loss | Stamp every VDT-created worktree with a `.vdt-worktree-id` marker file. `WinnerApplyService` must refuse mutating git ops on paths lacking this marker when `WorkspaceMode != Clone`. Also reject paths equal to `LocalCheckoutPath`. |
| C3 | **`IAgentWorkspace` interface is incomplete** — The plan shows 9 methods + `// ...`. Actual usage across 4 agent files calls 15+ methods not listed: `GetStatusAsync`, `RevertFilesAsync`, `GetDiffFileListVsMainAsync`, `GetChangedFilePathsAsync`, `ReadFileAsync`, `WriteFileAsync`, `GetRemoteShaAsync`, `PullRebaseAsync`, `MergeMainIntoBranchAsync`, `ForcePushAsync`, `GetCurrentBranchAsync`. | GPT-5.5, Sonnet | Won't compile — blocks Phase 1 | Before any code, grep ALL `Workspace.` / `Workspace?.` / `_workspace.` calls across all agent files. Build the interface from actual call sites, not from a sketch. |
| C4 | **`TestEngineerAgent` has its own separate `LocalWorkspace? _workspace` field** (line 58) — not the base class field. It constructs `new LocalWorkspace(...)` directly. The plan's impact table doesn't account for this independent workspace lifecycle. | Sonnet | TE won't work in InPlace mode | Explicitly design TE's workspace behavior: own worktree via `SharedCloneManager`, or shared with SE. Update TE initialization path separately. |
| C5 | **`SoftwareEngineerAgent.cs:3819` strategy guard says "requires LocalWorkspace"** — After refactoring `Workspace` to `IAgentWorkspace`, this guard's semantics change. If left as-is with `is null` check on a non-null `WorktreeWorkspace`, the strategy path works — but the comment and surrounding logic encode Clone-mode assumptions. | Sonnet | Strategy framework silently skipped if guard logic is wrong | Explicitly verify and update this guard. Add a test that exercises the strategy path with `WorktreeWorkspace`. |
| C6 | **`ValidateRepoPathSafety` rejects InPlace worktrees when dogfooding VDT on VDT** — The regex rejects paths containing `VirtualDevTeam.{Project}`. When developing VDT itself in InPlace mode, all worktree paths match. | Opus | First-day blocker for dogfooding | Accept paths containing `/.vdt-worktrees/` in the validation, or replace with marker-file check (see C2). |
| C7 | **`WorktreeRoot` default "sibling of checkout" conflicts with `RequireCleanHostTree`** — If inside the repo, it pollutes `git status`. If outside, may not be writable on corp-managed machines. | Opus | Day-one setup friction | Default to `%LOCALAPPDATA%\VDT\worktrees\{repoHash}`. Add `.gitignore` guidance as fallback for in-repo placement. |

### 🟡 Important Issues (Address During Implementation)

| # | Finding | Models | Resolution |
|---|---------|--------|------------|
| I1 | **Sparse checkout dependency recovery** — Build fails silently when transitive deps are missing from sparse cone. Only reactive detector proposed. | GPT-5.5 | Add build-failure classifier + automatic sparse expansion + retry loop. Always include root build files (`Directory.Build.props`, `global.json`, `*.sln`, lockfiles). |
| I2 | **Visual scoring disabled in external dev server mode** — Core differentiating feature silently neutered for InPlace mode's most common use case. | GPT-5.5, Sonnet | Make this an explicit, visible decision. Dashboard must clearly show "Visual scoring: unavailable (external server)". Require opt-in selection, not silent downgrade. |
| I3 | **Path scoping enforcement too weak (commit-time only)** — Out-of-scope edits can affect builds/tests/prompts before being caught. | GPT-5.5 | Multi-layer enforcement: `WriteFileAsync` rejects out-of-scope paths; pre-commit guard blocks staged files; post-CLI-edit scan reverts violations. Use normalized path boundary checks (not `StartsWith`). |
| I4 | **Agent branches pollute operator's `git branch` output** — 50+ `agent/` branches in shared `.git` clutter IDE and CLI. | Opus | Use `refs/vdt/agents/` namespace locally; push as `refs/heads/agent/` to remote. Add auto-prune for merged/closed branches. |
| I5 | **Windows MAX_PATH** — Worktree paths can exceed 260 chars. | GPT-5.5 | Short default root (`C:\vdt-wt`); hash/shorten slugs; set `core.longpaths=true`; preflight path-length budget check. |
| I6 | **Cone mode can't express single-file cross-service dependencies** | Sonnet | Document directory-only constraint; add wizard validation to reject file patterns; accept broader directory includes as cost. |
| I7 | **Concurrent same-service builds share NuGet cache** — possible corruption | Sonnet | Add per-service build semaphore, or isolate `NUGET_PACKAGES` per-worktree. |
| I8 | **PR review flow with sparse worktrees** — Architect/PM can't browse all files | GPT-5.5 | Use changed-files API context for lightweight reviews; expanded sparse for deep reviews. |
| I9 | **No test strategy specified** — Feature has tricky invariants (locks, paths, cleanup) with no testing design. | Sonnet | Document minimum test suite: real `.git` fixture for `SharedCloneManager`, lock-key resolution test, `NukeAndReclone` retry test. |
| I10 | **Dashboard.Host data flow unspecified** — New service-level UI needs API endpoints for standalone mode. | Opus | Add `/api/services`, `/api/workspace/mode`, `/api/worktrees` endpoints. Register data services in `StandaloneServiceRegistration`. |
| I11 | **Migration safety** — Missing `workspaceMode` field in old config must default to `Clone`. | Opus | Explicit deserialization default; fail-closed to `Clone`; log resolved mode at startup. |
| I12 | **`NukeAndRecloneAsync` Windows file-lock retry** — `git worktree remove --force` fails with antivirus/VS locks. | Sonnet | Retry loop with jittered backoff; fallback to `prune` + `ForceDeleteDirectory`. |
| I13 | **Prompt augmentation mechanism unspecified** — "Architect prompt with service list" needs concrete contract. | Opus | Use template `{{serviceList}}` variable in `prompts/architect/*.md`; populate from `LargeProjectConfig.Services`. |
| I14 | **TE needs wider sparse cone for integration tests** | Opus | Add `ServiceDefinition.TestSparsePaths`; TE worktrees get broader coverage than SE worktrees. |

