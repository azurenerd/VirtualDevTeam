# Interactive CLI A/B/C Testing Framework — Comprehensive Implementation Plan

## Executive Summary

This plan describes a **multi-strategy code generation framework** for VirtualDevTeam where each engineering task can be implemented by multiple code generation approaches running **in parallel**, scored by an evaluation pipeline, and the **best result** merged into the PR. The framework is extensible — new strategies can be added without modifying the evaluation or integration logic.

### Three Initial Strategies

| Strategy | Approach | CLI Flags | Key Differentiator |
|----------|----------|-----------|-------------------|
| **A: Agentic Delegation** | Full autonomous CLI session with all tools enabled | `--allow-all` | Self-healing build loop, autonomous iteration |
| **B: MCP-Enhanced Generation** | Single-shot generation with read-only MCP tools for codebase context | `--mcp-server` (read-only tools) | Context-aware generation, predictable cost |
| **C: Baseline (Current)** | Single-shot flattened prompt, no tools | `--excluded-tools` (current behavior) | Fast, cheap, deterministic baseline |

> **Design Decision**: Options A and B are intentionally **orthogonal** — one is fully agentic (multi-turn, tool-using), the other is enhanced single-shot (one-turn, context-aware). This makes results interpretable. A "hybrid" option was removed because it would be too similar to A to produce meaningful comparison data.

---

## Architecture Overview

### High-Level Flow

```
┌──────────────────────────────────────────────────────────────┐
│  SE Agent receives task T1 from engineering plan              │
│  Creates PR branch: agent/se-1/implement-auth                │
│  Records base SHA for reproducibility                         │
└──────────────┬───────────────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────┐
│  StrategyOrchestrator.RunCandidatesAsync(taskContext)         │
│                                                              │
│  For each enabled strategy (A, B, C) in parallel:            │
│    1. Create git worktree from base SHA                      │
│    2. Launch strategy in isolated worktree                   │
│    3. Strategy produces code changes                         │
│    4. Extract git diff/patch artifact from worktree          │
│    5. Cleanup worktree                                       │
│                                                              │
│  Returns: List<CandidateResult> with patches + metadata      │
└──────────────┬───────────────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────┐
│  CandidateEvaluator.EvaluateAsync(candidates, taskContext)    │
│                                                              │
│  Phase 1 — Hard Gates (automatic disqualification):          │
│    ✗ No output produced                                      │
│    ✗ Build fails (dotnet build)                              │
│    ✗ App doesn't start (if web task)                         │
│    ✗ Evaluator-owned tests fail                              │
│                                                              │
│  Phase 2 — Scoring (LLM-judged, survivors only):             │
│    Primary: Acceptance criteria adherence (0-100)            │
│    Secondary: Design fidelity (UI tasks only, 0-100)         │
│    Tie-break: Cost, time, file count                         │
│                                                              │
│  Returns: Ranked list with scores + winner                   │
└──────────────┬───────────────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────────────┐
│  Winner Integration                                          │
│    1. Apply winning patch to PR branch (git apply)           │
│    2. Commit with metadata: strategy used, scores, reasoning │
│    3. Continue with configurable downstream:                 │
│       Option 1: Full review pipeline (Architect + PM + TE)   │
│       Option 2: Tests only + auto-merge (skip peer review)   │
│    4. Log experiment data for learning                        │
└──────────────────────────────────────────────────────────────┘
```

### Candidate Isolation: Git Worktrees (Not Folder Copies)

Each candidate runs in a **git worktree** created from the same base SHA. This provides:
- True git isolation (no shared `.git` state conflicts)
- Efficient disk usage (shared object store)
- Clean diff extraction (`git diff` against base)
- No risk of accidental pushes (worktrees don't have push targets by default)

```
workspace/
├── .git/                          # Main repo
├── ReportingDashboard/            # Main working tree (PR branch)
└── .candidates/                   # ← gitignored
    ├── option-a-{taskId}/         # Git worktree (detached at base SHA)
    │   └── ReportingDashboard/    # Strategy A writes code here
    ├── option-b-{taskId}/         # Git worktree (detached at base SHA)
    │   └── ReportingDashboard/    # Strategy B writes code here
    └── option-c-{taskId}/         # Git worktree (detached at base SHA)
        └── ReportingDashboard/    # Strategy C writes code here
```

**Security hardening for agentic strategies (A):**
- Strip git credentials from worktree: `git config --local credential.helper ""`
- Set `git config --local push.default nothing` to block accidental pushes
- The strategy interface returns a `patch` artifact, not direct repo mutation

---

## Core Interfaces

### Strategy Registry (Extensible)

```csharp
/// <summary>
/// A code generation strategy that can be registered and run as a candidate.
/// New strategies can be added by implementing this interface and registering in DI.
/// </summary>
public interface ICodeGenerationStrategy
{
    /// <summary>Unique identifier (e.g., "agentic-delegation", "mcp-enhanced", "baseline").</summary>
    string StrategyId { get; }

    /// <summary>Human-readable name for dashboard/logs.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Execute the strategy in the given workspace directory.
    /// The strategy should write/modify files in workspacePath and return a result.
    /// The caller will extract a git diff after execution.
    /// </summary>
    Task<StrategyExecutionResult> ExecuteAsync(
        TaskContext context,
        string workspacePath,
        CancellationToken ct);
}

public record TaskContext
{
    public required string TaskId { get; init; }
    public required string TaskName { get; init; }
    public required string TaskDescription { get; init; }
    public required string AcceptanceCriteria { get; init; }
    public required string? DesignContext { get; init; }      // HTML design reference (if UI task)
    public required string ArchitectureContext { get; init; }  // Architecture.md summary
    public required string BaseSha { get; init; }              // Git SHA to build from
    public required int IssueNumber { get; init; }
    public required int PrNumber { get; init; }
}

public record StrategyExecutionResult
{
    public required bool Success { get; init; }
    public required string StrategyId { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public int TokensUsed { get; init; }
    public int FilesCreated { get; init; }
    public int FilesModified { get; init; }
    public List<string> ChangedFiles { get; init; } = [];
    public string? AgentLog { get; init; }  // Stdout/stderr from CLI session
}
```

### Candidate Result (After Patch Extraction)

```csharp
public record CandidateResult
{
    public required string StrategyId { get; init; }
    public required string DisplayName { get; init; }
    public required StrategyExecutionResult Execution { get; init; }
    public required string PatchContent { get; init; }  // Git diff (unified format)
    public required string BaseSha { get; init; }
    public List<string> ChangedFiles { get; init; } = [];
}
```

### Evaluation Result

```csharp
public record CandidateScore
{
    public required string StrategyId { get; init; }

    // Hard gates (all must pass to be eligible)
    public bool ProducedOutput { get; init; }
    public bool BuildPasses { get; init; }
    public bool AppStarts { get; init; }         // Web tasks only
    public bool EvaluatorTestsPass { get; init; } // Frozen test suite
    public bool PassedAllGates => ProducedOutput && BuildPasses &&
                                   AppStarts && EvaluatorTestsPass;

    // Soft scores (0-100, only computed for gate-passing candidates)
    public int AcceptanceCriteriaScore { get; init; }  // Primary ranker
    public int? DesignFidelityScore { get; init; }     // UI tasks only
    public TimeSpan ExecutionTime { get; init; }       // Tie-breaker
    public int TokenCost { get; init; }                // Tie-breaker
    public string? EvaluationReasoning { get; init; }  // LLM explanation

    /// <summary>Composite rank (1 = best). Computed by evaluator.</summary>
    public int Rank { get; init; }
}

public record EvaluationResult
{
    public required List<CandidateScore> Scores { get; init; }
    public required string WinnerStrategyId { get; init; }
    public required string Summary { get; init; }  // Human-readable comparison
}
```

---

## Strategy Implementations

### Strategy A: Agentic Delegation

```
Copilot CLI Process:
  copilot --allow-all --no-ask-user --silent --no-color --no-auto-update
          --no-custom-instructions --model claude-sonnet-4.6

Execution Flow:
  1. Set working directory to candidate worktree
  2. Pipe structured task prompt via stdin:
     - Task description + acceptance criteria
     - Architecture context
     - Design reference (if UI task)
     - Instructions: "Implement the task. Build with `dotnet build`. Fix errors. Run tests."
  3. CLI autonomously: creates/edits files, runs builds, fixes errors, iterates
  4. CliInteractiveWatchdog monitors for:
     - Stuck states (no output for 60s)
     - Completion signals ("task_complete" tool call or explicit "done" message)
     - Runaway behavior (>500 tool calls)
  5. Timeout: 10 minutes per task (configurable)
  6. On completion: extract git diff from worktree
```

**Key changes to CopilotCliProcessManager:**
- New `ExecuteAgenticSessionAsync()` method: keeps process alive, streams output, detects completion
- Separate semaphore pool for agentic sessions (don't starve single-shot calls)
- Output buffer captures full session log for debugging

**Sandbox hardening:**
- `--excluded-tools` blocks: `git push`, `gh pr`, any network-mutating tools
- Worktree has no push credentials
- File writes confined to worktree directory (monitored by watchdog)

### Strategy B: MCP-Enhanced Single-Shot

```
Copilot CLI Process:
  copilot --no-ask-user --silent --no-color --no-auto-update
          --no-custom-instructions --model claude-sonnet-4.6
          --mcp-server workspace-reader

Execution Flow:
  1. Start lightweight MCP server exposing read-only tools:
     - read_file(path) — read any file in the worktree
     - search_code(pattern, glob) — ripgrep across worktree
     - list_directory(path) — directory listing
     - read_csproj(path) — parse project references
  2. Pipe enhanced prompt via stdin:
     - Same task context as Strategy A
     - Additional: "You have access to MCP tools to read existing code. Use them to understand
       the project structure before generating your implementation."
  3. CLI makes MCP tool calls during generation to understand codebase
  4. CLI returns text response with code blocks (same as current flow)
  5. Agent extracts code files from response, writes to worktree
  6. Extract git diff
```

**New component: WorkspaceReaderMcpServer**
- Lightweight MCP server (JSON-RPC over stdio)
- Scoped to candidate worktree directory (cannot escape)
- Read-only: no file writes, no shell commands, no network access
- Starts per-candidate, killed after strategy completes

### Strategy C: Baseline (Current Approach)

```
Copilot CLI Process:
  copilot --no-ask-user --silent --no-color --no-auto-update
          --no-custom-instructions --model claude-sonnet-4.6
          --excluded-tools shell --excluded-tools write

Execution Flow:
  1. Flatten ChatHistory into single prompt (existing FormatChatHistoryAsPrompt)
  2. Include task context, architecture, design reference
  3. Single-shot: pipe prompt, read response, kill process
  4. Extract code files from response text
  5. Write files to worktree
  6. Extract git diff
```

This is essentially the **current SE code generation path** wrapped in the strategy interface. Serves as the control group for A/B comparison.

---

## Evaluation Pipeline

### Phase 1: Hard Gates (Automated, No LLM)

Run sequentially — fail fast on first gate failure:

```
Gate 1: Output Produced
  - Candidate patch is non-empty
  - At least 1 file created/modified

Gate 2: Build Success
  - Apply patch to a clean worktree
  - Run: dotnet build --verbosity quiet
  - Must exit with code 0

Gate 3: App Starts (web tasks only)
  - Run: dotnet run --no-launch-profile --urls http://localhost:{port}
  - HTTP GET to / must return 200 within 30s
  - Kill app process

Gate 4: Evaluator Tests Pass
  - Run evaluator-owned test suite (NOT candidate-generated tests)
  - These are frozen tests that the candidate cannot modify
  - Must pass at 100% (candidates that break existing tests are disqualified)
```

**Why evaluator-owned tests?** Candidates (especially Strategy A) can modify or delete tests to inflate pass rates. The evaluator runs its own frozen test suite from a known-good baseline.

### Phase 2: Scoring (LLM-Judged, Survivors Only)

Only candidates that pass all hard gates proceed to scoring. This dramatically reduces LLM evaluation cost (often only 1-2 survivors).

```
Evaluator Prompt (per surviving candidate):

"You are evaluating a code implementation for a software task.

## Task
{TaskName}: {TaskDescription}

## Acceptance Criteria
{AcceptanceCriteria}

## Design Reference (if UI task)
{DesignContext — truncated HTML}

## Code Changes (unified diff)
{PatchContent}

## Changed Files
{list of files with brief content preview}

Score this implementation:

1. **Acceptance Criteria Adherence (0-100)**: How completely does this implementation
   satisfy each acceptance criterion? Score 100 if ALL criteria are fully met.
   Score 0 if none are met. Be specific about which criteria are met/missed.

2. **Design Fidelity (0-100, UI tasks only)**: How closely does the visual output
   match the OriginalDesignConcept.html reference? Consider layout, colors,
   typography, component structure. N/A for non-UI tasks.

Respond in JSON:
{
  "acceptanceCriteriaScore": <0-100>,
  "designFidelityScore": <0-100 or null>,
  "reasoning": "<explanation of scores, criteria met/missed>"
}
"
```

**Ranking algorithm:**
1. Sort by `AcceptanceCriteriaScore` descending (primary)
2. For UI tasks with tied AC scores, break tie by `DesignFidelityScore`
3. For remaining ties, prefer: lower `TokenCost` → lower `ExecutionTime`

### Phase 3: Winner Selection & Reporting

```csharp
// Pseudo-code for winner selection
var eligible = candidates.Where(c => c.PassedAllGates).ToList();

if (eligible.Count == 0)
{
    // All candidates failed gates — fall back to best-effort:
    // Pick the one that got furthest (built but didn't pass tests > didn't build)
    // Or re-run with relaxed gates
    return FallbackSelection(candidates);
}

if (eligible.Count == 1)
    return eligible[0]; // Only one survivor, skip LLM scoring

// Multiple survivors — run LLM evaluation
var scored = await EvaluateWithLlmAsync(eligible, taskContext);
return scored.OrderByDescending(s => s.AcceptanceCriteriaScore)
             .ThenByDescending(s => s.DesignFidelityScore ?? 0)
             .ThenBy(s => s.TokenCost)
             .First();
```

---

## Integration with Existing Pipeline

### Where It Plugs In

The strategy framework replaces the code generation step in `SoftwareEngineerAgent.WorkOnOwnTasksAsync()`. The four existing code generation paths (initial multi-step, initial single-pass, continuation single-pass, continuation multi-step) all funnel into the strategy orchestrator:

```
BEFORE (current):
  SE Agent → BuildImplementationPrompt() → CopilotCli.GetCompletion() → ExtractCodeFiles() → CommitToPR()

AFTER (with framework):
  SE Agent → StrategyOrchestrator.RunCandidatesAsync() → CandidateEvaluator.EvaluateAsync() → ApplyWinnerToPR()
```

### Configuration

```json
{
  "VirtualDevTeam": {
    "StrategyFramework": {
      "Enabled": true,
      "EnabledStrategies": ["agentic-delegation", "mcp-enhanced", "baseline"],
      "ConcurrencyLimit": 3,
      "AgenticTimeoutSeconds": 600,
      "McpTimeoutSeconds": 120,
      "BaselineTimeoutSeconds": 90,
      "EvaluatorModel": "claude-sonnet-4.6",
      "RunAllStrategiesForEveryTask": false,
      "SamplingPolicy": "high-complexity-only",
      "FallbackStrategy": "baseline",
      "PostWinnerFlow": "full-review",
      "ExperimentDataPath": "experiment-data/"
    }
  }
}
```

**`PostWinnerFlow` options:**
- `"full-review"` — Winner goes through existing Architect → PM → TE pipeline (default)
- `"tests-only"` — Skip peer reviews, run TE tests, auto-merge if passing
- `"auto-merge"` — Skip reviews AND tests, merge immediately (use with caution)

### Sampling Policy

Running 3 strategies for every task is expensive. Sampling policies control when to run the full A/B/C comparison:

- `"always"` — Run all strategies for every task (most data, highest cost)
- `"high-complexity-only"` — Full comparison only for Medium/High complexity tasks; Low complexity uses fallback strategy only
- `"sampled-20"` — 20% of tasks get full comparison, rest use fallback (statistically valid with enough tasks)
- `"first-wave-only"` — Full comparison for Wave 1 tasks, use winning strategy for subsequent waves

### Concurrency Management

**Problem**: Current semaphore is 4-5 concurrent CLI calls. Running 3 strategies per task would saturate it.

**Solution**: Separate concurrency pools:

```csharp
// Existing pool for single-shot calls (reviews, planning, etc.)
private readonly SemaphoreSlim _singleShotSemaphore = new(4);

// Dedicated pool for strategy candidates (bounded separately)
private readonly SemaphoreSlim _candidateSemaphore = new(3);

// Agentic sessions get their own pool (they hold slots for minutes)
private readonly SemaphoreSlim _agenticSemaphore = new(2);
```

This ensures strategy candidates don't starve review/planning calls.

---

## Experiment Tracking & Learning

### Per-Run Data Collection

Every strategy execution is logged for analysis:

```csharp
public record ExperimentRecord
{
    public required string RunId { get; init; }           // Pipeline run identifier
    public required string TaskId { get; init; }
    public required string TaskName { get; init; }
    public required string TaskComplexity { get; init; }  // Low/Medium/High
    public required bool IsUiTask { get; init; }
    public required string Wave { get; init; }

    // Per-strategy results
    public required List<StrategyRecord> Strategies { get; init; }

    // Winner
    public required string WinnerStrategyId { get; init; }
    public required string WinnerReason { get; init; }

    // Post-merge outcomes (filled in later)
    public int? ReviewReworkCycles { get; init; }
    public bool? PassedTeTests { get; init; }
    public bool? MergedSuccessfully { get; init; }
}

public record StrategyRecord
{
    public required string StrategyId { get; init; }
    public required bool ProducedOutput { get; init; }
    public required bool BuildPassed { get; init; }
    public required bool TestsPassed { get; init; }
    public required int AcceptanceCriteriaScore { get; init; }
    public required int? DesignFidelityScore { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int TokensUsed { get; init; }
    public required int FilesChanged { get; init; }
    public required int LinesOfCode { get; init; }
}
```

**Storage**: JSON files in `experiment-data/` directory, one per pipeline run. Can be aggregated offline for analysis.

### Learning Insights

After 5+ pipeline runs, the experiment data answers:
- Which strategy wins most often? For which task types?
- Does agentic mode produce fewer rework cycles?
- What's the cost premium of agentic vs baseline?
- Are there task characteristics that predict which strategy will win?
- Should the sampling policy be adjusted?

---

## Implementation Phases

### Phase 1: Foundation (Core interfaces + Baseline strategy)
**Goal**: Build the framework skeleton. Baseline strategy wraps existing code generation.

| Component | Description |
|-----------|-------------|
| `ICodeGenerationStrategy` interface | Strategy contract |
| `StrategyOrchestrator` | Parallel execution, worktree management, patch extraction |
| `CandidateEvaluator` | Hard gates + LLM scoring |
| `BaselineStrategy` | Wraps current `GenerateCodeAsync()` logic |
| `ExperimentTracker` | Logs experiment data |
| Config model | `StrategyFrameworkConfig` with all settings |
| `.candidates/` gitignore | Ensure worktrees are excluded |

### Phase 2: MCP-Enhanced Strategy (Option B)
**Goal**: Build the MCP read-only tool server and integrate it.

| Component | Description |
|-----------|-------------|
| `WorkspaceReaderMcpServer` | JSON-RPC MCP server with read_file, search_code, list_directory |
| `McpEnhancedStrategy` | Launches MCP server, passes `--mcp-server` to CLI, extracts code from response |
| Integration tests | Verify MCP server starts, responds, is killed cleanly |

### Phase 3: Agentic Delegation Strategy (Option A)
**Goal**: Full agentic CLI session with autonomous tool use.

| Component | Description |
|-----------|-------------|
| `CopilotCliProcessManager.ExecuteAgenticSessionAsync()` | Long-lived process, output streaming, completion detection |
| `AgenticDelegationStrategy` | Launches agentic session, monitors progress, extracts results |
| `AgenticWatchdog` | Extended CliInteractiveWatchdog for agentic: stuck detection, runaway prevention, tool call counting |
| Sandbox hardening | Credential stripping, push blocking, file escape detection |
| Separate semaphore pool | Agentic sessions don't starve single-shot |

### Phase 4: Dashboard Integration
**Goal**: Real-time visibility into strategy comparisons on the dashboard.

| Component | Description |
|-----------|-------------|
| `DashboardDataService` extensions | Expose candidate status, scores, winner selection |
| New dashboard cards | Strategy comparison view: side-by-side scores, gate results |
| SignalR events | Real-time updates as candidates complete and scores arrive |

### Phase 5: Adaptive Strategy Selection
**Goal**: Use experiment data to automatically select the best strategy per task type.

| Component | Description |
|-----------|-------------|
| `AdaptiveStrategySelector` | Analyzes historical experiment data, recommends strategy per task characteristics |
| Auto-tuning | Gradually narrow from 3 strategies to 1-2 per task type based on win rates |
| Cost budgeting | Set per-run token budget, allocate across strategies |

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| **Cost explosion** (3x generation per task) | Sampling policy limits full comparison to subset of tasks; fallback strategy for simple tasks |
| **Agentic session runaway** | Hard timeout (10 min), tool call limit (500), watchdog monitoring, separate semaphore pool |
| **Candidate pushes to origin** | Worktrees have credentials stripped, push blocked; strategy returns patch artifact only |
| **Candidate modifies tests to game scoring** | Evaluator runs frozen test suite that candidates cannot access or modify |
| **Patch apply conflicts** | Patches are from same base SHA; if apply fails, re-generate from winner strategy |
| **Disk pressure from worktrees** | Git worktrees share object store (minimal overhead); cleanup in finally block |
| **LLM evaluator inconsistency** | Structured JSON output, temperature=0, same model for all evaluations within a run |
| **Concurrency starvation** | Separate semaphore pools for single-shot, candidates, and agentic sessions |
| **Strategy B and C too similar** | B has MCP tools for context; C is purely prompt-based. Difference is whether LLM can read existing code during generation |

---

## Success Criteria

The framework is successful if:

1. **At least one strategy consistently outperforms baseline** (Strategy C) on acceptance criteria scores
2. **Agentic strategy (A) produces fewer rework cycles** in downstream reviews
3. **The cost premium is justified** — if Strategy A costs 5x more but produces 50% fewer rework cycles and 30% higher AC scores, it's worth it
4. **Framework is stable** — no crashes, leaked processes, or corrupted PR branches across 10+ pipeline runs
5. **Dashboard provides clear visibility** into strategy comparisons for human operators

---

## Appendix: Decision Log

### Why not weighted composite scoring?
Build success and test passing are **hard requirements**, not preferences. A weighted score could rank a candidate that doesn't build above one that does (if it scored higher on other metrics). The hierarchical gating model ensures only viable candidates compete on quality metrics.

### Why git worktrees instead of folder copies?
- Shared object store = minimal disk overhead
- Clean `git diff` extraction for patches
- No `.git` directory duplication
- Native git isolation without filesystem tricks

### Why evaluator-owned tests instead of candidate test pass rate?
Agentic strategies (A) can modify or delete tests to inflate pass rates. The evaluator's frozen test suite is immutable and provides an objective quality signal. Candidate-generated tests are still valuable but are treated as supplementary, not authoritative.

### Why not use existing PM/Architect review as the evaluator?
The existing review pipeline is designed for single implementations. For A/B/C comparison, we need a dedicated evaluator that can:
- Compare multiple implementations side-by-side
- Apply the same rubric consistently
- Score quantitatively (not just approve/request-changes)

However, after the winner is selected, the existing review pipeline runs normally on the winning code.

### Why separate semaphore pools?
Agentic sessions hold CLI slots for 5-10 minutes. If they share a pool with single-shot calls (which take 10-30 seconds), reviews and planning calls would queue behind agentic sessions. Separate pools ensure the non-strategy parts of the pipeline keep running.
