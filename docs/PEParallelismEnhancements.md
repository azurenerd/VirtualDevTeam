# SE Agent Parallelism Enhancements Plan

> **Purpose:** Comprehensive plan for enhancing the Software Engineer agent's engineering plan generation to maximize task parallelism, enforce file ownership, and adopt fleet-style dependency wave scheduling — reducing merge conflicts and accelerating parallel development.
>
> **Author:** Ben Humphrey (@azurenerd) with Copilot CLI
> **Status:** PLAN — Not yet implemented
> **Last Updated:** 2026-04-15

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Current State Analysis](#2-current-state-analysis)
3. [Enhancement 1: Hard-Reject File Overlaps](#3-enhancement-1-hard-reject-file-overlaps)
4. [Enhancement 2: Parallelism Wave Scheduling](#4-enhancement-2-parallelism-wave-scheduling)
5. [Enhancement 3: Shared File Registry in T1](#5-enhancement-3-shared-file-registry-in-t1)
6. [Enhancement 4: Typed Dependencies](#6-enhancement-4-typed-dependencies)
7. [Enhancement 5: Parallelism Self-Assessment](#7-enhancement-5-parallelism-self-assessment)
8. [Enhancement 6: Critical Path Analysis](#8-enhancement-6-critical-path-analysis)
9. [Data Model Changes](#9-data-model-changes)
10. [Implementation Phases](#10-implementation-phases)
11. [Testing Strategy](#11-testing-strategy)
12. [Risk Assessment](#12-risk-assessment)

---

## 1. Problem Statement

### The Gap

The SE agent's engineering plan generation has solid parallelism *intent* — the system prompt explicitly asks for star-topology dependencies, vertical slicing, and file ownership. However, the **enforcement and optimization** of these goals is weak:

- File overlaps between tasks are **warned but not rejected** — leading to merge conflicts when parallel engineers modify the same file
- Dependencies are **flat and untyped** (`T1,T3`) — making it impossible to distinguish necessary vs. unnecessary blockers
- No **wave scheduling** — the runtime counts parallelizable tasks but doesn't reason about optimal execution order
- No **critical path analysis** — bottleneck tasks aren't identified, so the plan may serialize unnecessarily
- The self-assessment criteria check for architecture coverage and dependency order but **don't evaluate parallelism quality**

### The Vision

Adopt fleet-style parallelism patterns where:
1. The plan explicitly defines **execution waves** (groups of tasks that can run simultaneously)
2. File ownership is **enforced, not advisory** — overlaps cause plan regeneration
3. Dependencies have **types** (file, API, concept) so unnecessary ones can be relaxed
4. A **shared file registry** makes it clear which files are off-limits to parallel tasks
5. A dedicated **parallelism self-assessment** validates that the plan maximizes concurrency

### Comparison: Current vs Fleet-Style

| Aspect | Current SE | Fleet-Style (Target) |
|--------|-----------|---------------------|
| File ownership | Advisory (prompt asks, warns post-hoc) | Enforced (reject + regenerate on overlap) |
| Dependency tracking | Flat IDs (`T1,T3`) | Typed (`T1(files),T3(api)`) |
| Wave scheduling | Implicit (count parallelizable tasks) | Explicit (Wave 1, 2, 3 in plan output) |
| Critical path | None | Calculated, bottleneck tasks identified |
| Shared files | "Put shared stuff in T1" (informal) | Explicit manifest (`SHARED_FILES:` in T1) |
| Parallelism validation | None | Dedicated self-assessment turn |
| Overlap handling | Log warning, continue | Reject plan, request fix from AI |

---

## 2. Current State Analysis

### What Exists Today

#### System Prompt (Lines 454-509 of `SoftwareEngineerAgent.cs`)

The SE's planning system prompt already has strong parallelism guidance:

```
"## CRITICAL — Parallel-Friendly Task Decomposition
Multiple engineers will work on tasks IN PARALLEL. Design tasks to MINIMIZE overlap:
- Separate by component/module boundary
- Vertical slicing over horizontal
- Explicit file ownership: every task's FilePlan must list EXACTLY which files...
- Shared infrastructure in T1
- Minimize cross-task dependencies
- Independent test scoping"
```

**Strength:** The guidance is comprehensive and correct.
**Gap:** It's purely advisory — the AI can and does violate these rules.

#### Task Output Format (Line 544-557)

```
TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>
```

**Strength:** Includes FilePlan with CREATE/MODIFY/USE operations.
**Gap:** No wave assignment, no dependency types, no shared file declaration.

#### Foundation-First Enforcement (`EnsureFoundationFirstPattern`, Lines 3501-3569)

```csharp
// Ensures T1 is first with no dependencies
// Ensures all tasks depend on T1
// Detects file overlaps → LogWarning only
```

**Strength:** Structural integrity enforced programmatically.
**Gap:** File overlap only warned, never acted upon.

#### Parallelism Detection (Lines 2425-2496)

```csharp
var parallelizable = _taskManager.Tasks.Count(t =>
    t.Status == "Pending" && _taskManager.AreDependenciesMet(t));
// If parallelizable > freeWorkers + 1 → request more workers
```

**Strength:** Dynamic worker scaling based on workload.
**Gap:** No wave analysis, no critical path, no optimization.

#### Dependency Checking (`AreDependenciesMet`, `EngineeringTaskIssueManager.cs` Lines 164-174)

```csharp
public bool AreDependenciesMet(EngineeringTask task)
{
    return task.DependencyIssueNumbers.All(depIssueNum =>
    {
        var dep = _cache.FirstOrDefault(t => t.IssueNumber == depIssueNum);
        return dep is null || IsTaskDone(dep);
    });
}
```

**Strength:** Simple, correct for direct dependencies.
**Gap:** No transitive dependency awareness. If A→B→C, task A can start before C is done, but won't know that C indirectly blocks it.

#### Self-Assessment Criteria (`AssessmentCriteria.cs`, Lines 39-47)

```
1. ARCHITECTURE COVERAGE: every component has at least one task
2. DEPENDENCY ORDER: dependencies built before dependents
3. TASK SPECIFICITY: descriptions specific enough to implement
4. DEFINITION OF DONE: clear completion criteria
5. TEST STRATEGY: testing approach defined
6. INTEGRATION POINTS: integration points identified
```

**Gap:** No criteria for parallelism quality, file ownership validation, or wave optimization.

#### EngineeringTask Record (Lines 3674-3694)

```csharp
internal record EngineeringTask
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Complexity { get; init; } = "Medium";
    public string Status { get; init; } = "Pending";
    public string? AssignedTo { get; init; }
    public int? PullRequestNumber { get; init; }
    public int? IssueNumber { get; init; }
    public long? GitHubId { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
    public List<int> DependencyIssueNumbers { get; init; } = new();
    public int? ParentIssueNumber { get; init; }
    public List<string> Labels { get; init; } = new();
}
```

**Gap:** No `Wave`, `OwnedFiles`, `DependencyTypes`, or `SharedFiles` properties.

---

## 3. Enhancement 1: Hard-Reject File Overlaps

### Problem

Currently in `EnsureFoundationFirstPattern()` (line 3551-3568), file overlaps are detected but only logged as warnings:

```csharp
Logger.LogWarning("File overlap detected: '{File}' is created by both {Task1} and {Task2}",
    file, owner, task.Id);
```

This means two parallel engineers can be assigned tasks that create/modify the same file, causing merge conflicts that require SE to close and recreate PRs (`TryCloseAndRecreatePRAsync`).

### Solution

Replace the warn-and-continue behavior with a **detect → reject → regenerate** cycle:

```csharp
private async Task<List<EngineeringTask>> EnsureNoFileOverlapsAsync(
    List<EngineeringTask> tasks,
    IChatCompletionService chat,
    CancellationToken ct)
{
    const int MaxRetries = 2;
    
    for (var attempt = 0; attempt <= MaxRetries; attempt++)
    {
        var overlaps = DetectFileOverlaps(tasks);
        
        if (overlaps.Count == 0)
            return tasks; // Clean — no overlaps
        
        if (attempt == MaxRetries)
        {
            // Last resort: auto-resolve by assigning contested files to the
            // earlier task (lower task ID) and removing from the later task
            Logger.LogWarning(
                "File overlaps persist after {Attempts} regeneration attempts — auto-resolving",
                MaxRetries);
            return AutoResolveOverlaps(tasks, overlaps);
        }
        
        // Ask AI to fix the overlaps
        Logger.LogInformation(
            "Detected {Count} file overlaps — requesting AI fix (attempt {Attempt})",
            overlaps.Count, attempt + 1);
        
        var fixPrompt = BuildOverlapFixPrompt(tasks, overlaps);
        tasks = await RegeneratePlanWithFixAsync(chat, fixPrompt, tasks, ct);
    }
    
    return tasks;
}
```

### File Overlap Detection (Enhanced)

```csharp
private record FileOverlap(string FilePath, string Task1Id, string Task2Id, string Operation);

private List<FileOverlap> DetectFileOverlaps(List<EngineeringTask> tasks)
{
    var overlaps = new List<FileOverlap>();
    var fileOwnership = new Dictionary<string, (string TaskId, string Operation)>(
        StringComparer.OrdinalIgnoreCase);
    
    foreach (var task in tasks)
    {
        var fileOps = ExtractFileOperations(task.Description);
        foreach (var (file, operation) in fileOps)
        {
            // USE references are fine — multiple tasks can USE the same file
            if (operation == "USE") continue;
            
            // CREATE/MODIFY by multiple tasks is a conflict
            if (fileOwnership.TryGetValue(file, out var existing))
            {
                overlaps.Add(new FileOverlap(file, existing.TaskId, task.Id, operation));
            }
            else
            {
                fileOwnership[file] = (task.Id, operation);
            }
        }
    }
    
    return overlaps;
}
```

### AI Fix Prompt

When overlaps are detected, send a targeted prompt back to the AI:

```
"Your engineering plan has file ownership conflicts that will cause merge conflicts
during parallel development. Fix these overlaps:

CONFLICT: Program.cs is CREATED by both T1 and T3
CONFLICT: UserService.cs is MODIFIED by both T2 and T4

Rules:
1. Each file must be owned (CREATE/MODIFY) by exactly ONE task
2. Other tasks that need that file should USE it (read-only dependency)
3. If two tasks both need to modify the same file, either:
   a. Merge the modifications into one task, OR
   b. Split the file into two separate files (one per task), OR
   c. Move the shared modifications to T1 (foundation task)
4. Add a dependency from the non-owning task to the owning task if needed

Rewrite ONLY the affected TASK lines. Keep all other tasks unchanged."
```

### Auto-Resolution Fallback

If AI can't fix overlaps after retries, apply a deterministic rule:

```csharp
private List<EngineeringTask> AutoResolveOverlaps(
    List<EngineeringTask> tasks, 
    List<FileOverlap> overlaps)
{
    // Strategy: the earlier task (lower index) owns the file.
    // The later task gets a dependency on the earlier task added.
    foreach (var overlap in overlaps)
    {
        var laterTask = tasks.First(t => t.Id == overlap.Task2Id);
        if (!laterTask.Dependencies.Contains(overlap.Task1Id))
        {
            laterTask.Dependencies.Add(overlap.Task1Id);
        }
        // Remove the conflicting file operation from the later task's description
        RemoveFileFromTaskDescription(laterTask, overlap.FilePath);
    }
    return tasks;
}
```

### Where to Integrate

In `SoftwareEngineerAgent.cs`, after line 643 (`EnsureFoundationFirstPattern(parsedTasks)`):

```csharp
EnsureFoundationFirstPattern(parsedTasks);

// NEW: Enforce file ownership — reject and regenerate if overlaps exist
parsedTasks = await EnsureNoFileOverlapsAsync(parsedTasks, chat, ct);
```

### Files Changed

| File | Change |
|------|--------|
| `SoftwareEngineerAgent.cs` | Add `EnsureNoFileOverlapsAsync()`, `DetectFileOverlaps()`, `AutoResolveOverlaps()`, `BuildOverlapFixPrompt()` methods. Replace warning-only logic in `EnsureFoundationFirstPattern()`. |
| `EngineeringTaskIssueManager.cs` | Add `ExtractFileOperations()` returning `(file, operation)` tuples (enhanced from existing `ExtractCreateFilesFromDescription()`) |

---

## 4. Enhancement 2: Parallelism Wave Scheduling

### Problem

The SE creates tasks with dependencies but never groups them into execution waves. The runtime discovers parallelism opportunistically (`AreDependenciesMet` count) rather than having it designed into the plan.

### Solution

Add explicit wave assignment to the task output format and the `EngineeringTask` record.

### Updated Task Output Format

```
TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>|<Wave>
```

Example output:
```
TASK|T1|42|Project Foundation & Scaffolding|...|High|NONE|CREATE:.gitignore;...|W1
TASK|T2|43|Implement auth module|...|Medium|T1|CREATE:AuthService.cs;...|W2
TASK|T3|44|Implement user profile|...|Medium|T1|CREATE:UserProfileService.cs;...|W2
TASK|T4|45|Implement notifications|...|Low|T1|CREATE:NotificationService.cs;...|W2
TASK|T5|46|Auth-profile integration|...|Medium|T2,T3|CREATE:ProfileAuthMiddleware.cs;...|W3
TASK|T-FINAL|42|Final Integration|...|High|T2,T3,T4,T5|...|W4
```

### System Prompt Addition

Add to the SE system prompt (after the "Parallel-Friendly Task Decomposition" section):

```
## CRITICAL — Execution Wave Assignment
Group every task into an execution Wave (W1, W2, W3, etc.):
- **W1**: Foundation only (T1) — always solo
- **W2**: All tasks that depend ONLY on T1 — these run fully in parallel
- **W3**: Tasks that depend on W2 tasks — run after W2 completes
- **W4+**: Continue the pattern for deeper dependency chains
- **W-FINAL**: Integration task — always last

Design the plan to MAXIMIZE the number of tasks in W2 (the main parallel wave).
Target: at least 60% of non-foundation tasks should be in W2.
If a task is in W3+, it MUST have a genuine dependency on a W2 task that
cannot be resolved by moving shared code to T1.

After listing all TASK lines, output a wave summary:
WAVES|W1:1|W2:4|W3:1|W-FINAL:1
(format: WAVES|<wave>:<task_count>|...)
```

### Wave Validation Logic

```csharp
private WaveAnalysis ValidateWaves(List<EngineeringTask> tasks)
{
    var waves = tasks.GroupBy(t => t.Wave).OrderBy(g => g.Key).ToList();
    var analysis = new WaveAnalysis();
    
    // W1 should have exactly 1 task (foundation)
    var w1 = waves.FirstOrDefault(w => w.Key == "W1");
    if (w1?.Count() != 1)
        analysis.Warnings.Add("W1 should contain exactly one foundation task");
    
    // W2 should have the most tasks (main parallel wave)
    var w2 = waves.FirstOrDefault(w => w.Key == "W2");
    var nonFoundationNonFinal = tasks.Count(t => t.Wave != "W1" && t.Wave != "W-FINAL");
    if (w2 is not null && nonFoundationNonFinal > 0)
    {
        analysis.W2Percentage = (double)w2.Count() / nonFoundationNonFinal * 100;
        if (analysis.W2Percentage < 60)
            analysis.Warnings.Add(
                $"Only {analysis.W2Percentage:F0}% of tasks are in W2 (target: 60%+). " +
                "Consider moving shared code to T1 to reduce W3+ dependencies.");
    }
    
    // Validate wave ordering matches dependencies
    foreach (var task in tasks.Where(t => t.Dependencies.Count > 0))
    {
        foreach (var depId in task.Dependencies)
        {
            var dep = tasks.FirstOrDefault(t => t.Id == depId);
            if (dep is not null && CompareWaves(task.Wave, dep.Wave) <= 0)
            {
                analysis.Errors.Add(
                    $"Task {task.Id} (Wave {task.Wave}) depends on {dep.Id} (Wave {dep.Wave}) " +
                    "but is in the same or earlier wave");
            }
        }
    }
    
    analysis.MaxParallelism = waves.Max(w => w.Count());
    analysis.TotalWaves = waves.Count;
    
    return analysis;
}

private record WaveAnalysis
{
    public int TotalWaves { get; set; }
    public int MaxParallelism { get; set; }
    public double W2Percentage { get; set; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}
```

### Wave-Aware Worker Scaling

Enhance the resource evaluation (lines 2425-2496) to be wave-aware:

```csharp
private async Task EvaluateResourceNeedsAsync(CancellationToken ct)
{
    // Current wave: the earliest wave with pending tasks
    var currentWave = _taskManager.Tasks
        .Where(t => t.Status == "Pending" && _taskManager.AreDependenciesMet(t))
        .Select(t => t.Wave)
        .OrderBy(w => w)
        .FirstOrDefault();
    
    if (currentWave is null) return;
    
    // Count tasks in the current wave that are ready
    var waveTaskCount = _taskManager.Tasks.Count(t =>
        t.Wave == currentWave &&
        t.Status == "Pending" &&
        _taskManager.AreDependenciesMet(t));
    
    // Request workers to match the wave width
    var freeWorkers = CountFreeWorkers();
    var needed = waveTaskCount - freeWorkers;
    
    if (needed > 0)
    {
        Logger.LogInformation(
            "Wave {Wave} has {TaskCount} ready tasks but only {Free} workers — requesting {Needed} more",
            currentWave, waveTaskCount, freeWorkers, needed);
        // ... existing spawn request logic
    }
}
```

### GitHub Issue Enhancement

Include wave info in issue bodies (`EngineeringTaskIssueManager.BuildIssueBodyWithDeps`):

```markdown
## Metadata
- **Task ID:** T3
- **Complexity:** Medium
- **Execution Wave:** W2 (parallel with T2, T4, T5)
- **Parent Issue:** #42
- **Depends On:** #50 (T1)
```

### Files Changed

| File | Change |
|------|--------|
| `SoftwareEngineerAgent.cs` | Update system prompt to include wave assignment instructions. Add wave parsing from output. Add `ValidateWaves()`. Update `EvaluateResourceNeedsAsync()` for wave-aware scaling. |
| `EngineeringTask` record | Add `public string Wave { get; init; } = "W1";` property |
| `EngineeringTaskIssueManager.cs` | Include wave metadata in issue body. Parse wave from issue body on recovery. |

---

## 5. Enhancement 3: Shared File Registry in T1

### Problem

The system prompt says "shared infrastructure in T1" but doesn't require T1 to declare *which* files are shared. Parallel tasks can't know which files are protected without parsing T1's full description.

### Solution

Require T1 to output a `SHARED_FILES` declaration that acts as a registry. Other tasks can `USE` these files but never `CREATE` or `MODIFY` them.

### System Prompt Addition

Add to the T1 section of the SE system prompt:

```
## CRITICAL — T1 Shared File Registry
Task T1 MUST include a SHARED_FILES declaration listing every file it creates that
other tasks will depend on. Format (as last line of T1's FilePlan):

SHARED_FILES:Program.cs,appsettings.json,Models/BaseEntity.cs,Interfaces/IRepository.cs

Rules:
- Every file listed in SHARED_FILES is OWNED by T1 — no other task may CREATE or MODIFY it
- Other tasks may USE files from SHARED_FILES (read-only import/reference)
- If a later task needs to add to a shared file (e.g., DI registration in Program.cs),
  include ALL necessary registrations in T1 upfront, or make T1 depend on being the last
  to touch that file
- The SHARED_FILES list should include: entry points (Program.cs), DI setup,
  configuration files, shared models/interfaces, base classes, and utility helpers
```

### Shared File Registry Enforcement

```csharp
private void EnforceSharedFileRegistry(List<EngineeringTask> tasks)
{
    var t1 = tasks[0];
    var sharedFiles = ExtractSharedFiles(t1.Description);
    
    if (sharedFiles.Count == 0)
    {
        Logger.LogWarning("T1 has no SHARED_FILES declaration — " + 
            "parallel tasks may conflict on shared files");
        return;
    }
    
    Logger.LogInformation("T1 shared file registry: {Files}", 
        string.Join(", ", sharedFiles));
    
    // Check that no other task CREATEs or MODIFYs a shared file
    foreach (var task in tasks.Skip(1))
    {
        var fileOps = ExtractFileOperations(task.Description);
        foreach (var (file, operation) in fileOps)
        {
            if (operation is "CREATE" or "MODIFY" && 
                sharedFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                Logger.LogError(
                    "Task {TaskId} attempts to {Op} shared file '{File}' owned by T1",
                    task.Id, operation, file);
                // Option A: Hard error → trigger plan regeneration
                // Option B: Auto-fix → convert to USE reference
            }
        }
    }
}

private HashSet<string> ExtractSharedFiles(string description)
{
    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in description.Split('\n'))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("SHARED_FILES:", StringComparison.OrdinalIgnoreCase))
        {
            var fileList = trimmed["SHARED_FILES:".Length..];
            foreach (var file in fileList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                files.Add(file);
            }
        }
    }
    return files;
}
```

### Where to Integrate

After `EnsureFoundationFirstPattern()` and before (or as part of) the overlap check:

```csharp
EnsureFoundationFirstPattern(parsedTasks);
EnforceSharedFileRegistry(parsedTasks);          // NEW
parsedTasks = await EnsureNoFileOverlapsAsync(parsedTasks, chat, ct);  // Enhancement 1
```

### Files Changed

| File | Change |
|------|--------|
| `SoftwareEngineerAgent.cs` | Update T1 system prompt section. Add `EnforceSharedFileRegistry()`, `ExtractSharedFiles()`. Call after `EnsureFoundationFirstPattern()`. |

---

## 6. Enhancement 4: Typed Dependencies

### Problem

Dependencies are flat strings: `"T1,T3"`. The SE and runtime can't distinguish:
- **File dependency**: T3 reads a file T2 creates → hard block
- **API dependency**: T3 calls an interface T2 implements → soft block (interface defined in T1)
- **Concept dependency**: T3 "should happen after" T2 → often unnecessary

This leads to over-constraining the plan. A task listed as depending on T2 because it uses T2's interface might actually be satisfiable if the interface is in T1.

### Solution

Extend the dependency format to include types:

```
Dependencies format: T1(files),T3(api) or NONE

Dependency types:
- files: This task reads/imports files created by the dependency
- api: This task calls interfaces/APIs implemented by the dependency  
- data: This task needs data/schema created by the dependency
- test: This task's tests verify behavior of the dependency
- none: Dependency exists but type is unspecified (treated as hard block)
```

### Updated Task Output Format

```
TASK|T5|46|Auth-profile integration|...|Medium|T2(api),T3(files)|CREATE:ProfileAuth.cs;...|W3
```

### Dependency Relaxation Logic

Typed dependencies enable intelligent relaxation:

```csharp
public bool CanRelaxDependency(EngineeringTask task, EngineeringTask dependency, 
    List<EngineeringTask> allTasks)
{
    var depType = task.DependencyTypes.GetValueOrDefault(dependency.Id, "none");
    
    return depType switch
    {
        // API deps can be relaxed if the interface is defined in T1
        "api" => IsInterfaceAvailableInFoundation(dependency, allTasks),
        
        // File deps are always hard — need the actual file
        "files" => false,
        
        // Data deps can sometimes be relaxed with mock data
        "data" => false, // conservative for now
        
        // Test deps can run with stubs
        "test" => true, // tests can use mocks
        
        // Unknown deps are hard blocks
        _ => false,
    };
}

private bool IsInterfaceAvailableInFoundation(EngineeringTask dep, List<EngineeringTask> allTasks)
{
    var t1 = allTasks[0];
    // Check if T1's shared files include the interface that `dep` implements
    var sharedFiles = ExtractSharedFiles(t1.Description);
    var depFiles = ExtractFileOperations(dep.Description)
        .Where(f => f.Operation == "CREATE" && f.File.Contains("Interface", StringComparison.OrdinalIgnoreCase))
        .Select(f => f.File);
    
    return depFiles.Any(f => sharedFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
}
```

### Enhanced AreDependenciesMet

```csharp
public bool AreDependenciesMet(EngineeringTask task, bool useRelaxation = true)
{
    if (task.DependencyIssueNumbers.Count == 0)
        return true;
    
    return task.DependencyIssueNumbers.All(depIssueNum =>
    {
        var dep = _cache.FirstOrDefault(t => t.IssueNumber == depIssueNum);
        if (dep is null || IsTaskDone(dep))
            return true;
        
        // NEW: Check if this dependency can be relaxed
        if (useRelaxation && CanRelaxDependency(task, dep, _cache))
        {
            _logger.LogDebug(
                "Relaxing {DepType} dependency: {TaskId} → {DepId} (interface available in T1)",
                task.DependencyTypes.GetValueOrDefault(dep.Id, "none"), task.Id, dep.Id);
            return true;
        }
        
        return false;
    });
}
```

### Data Model Change

```csharp
internal record EngineeringTask
{
    // ... existing properties ...
    
    /// <summary>
    /// Typed dependencies: TaskId → dependency type (files, api, data, test).
    /// Enables intelligent dependency relaxation.
    /// </summary>
    public Dictionary<string, string> DependencyTypes { get; init; } = new();
}
```

### Parsing Update

Update the task line parser (around line 604):

```csharp
// Parse dependencies with types: "T1(files),T3(api)" → Dependencies + DependencyTypes
var depsRaw = parts[6].Trim();
var deps = new List<string>();
var depTypes = new Dictionary<string, string>();

if (!depsRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase))
{
    foreach (var dep in depsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var match = Regex.Match(dep, @"^(\w+)(?:\((\w+)\))?$");
        if (match.Success)
        {
            var taskId = match.Groups[1].Value;
            deps.Add(taskId);
            if (match.Groups[2].Success)
                depTypes[taskId] = match.Groups[2].Value;
        }
    }
}
```

### Files Changed

| File | Change |
|------|--------|
| `SoftwareEngineerAgent.cs` | Update system prompt with dependency type format. Update parser for typed deps. Add `CanRelaxDependency()`, `IsInterfaceAvailableInFoundation()`. |
| `EngineeringTask` record | Add `DependencyTypes` dictionary property. |
| `EngineeringTaskIssueManager.cs` | Update `AreDependenciesMet()` with relaxation logic. Include dep types in issue body metadata. Parse dep types from issue body on recovery. |

---

## 7. Enhancement 5: Parallelism Self-Assessment

### Problem

The existing self-assessment criteria (`AssessmentCriteria.SoftwareEngineer`) evaluate architecture coverage, dependency order, task specificity, definition of done, test strategy, and integration points — but **nothing about parallelism quality**.

### Solution

Add parallelism-specific assessment criteria and a dedicated validation AI turn.

### New Assessment Criteria

Add to `AssessmentCriteria.cs`:

```csharp
public const string SoftwareEngineerParallelism = """
    Evaluate the engineering plan's parallelism and conflict-avoidance:
    1. WAVE DISTRIBUTION: Are at least 60% of non-foundation tasks in Wave 2 (the main parallel wave)?
       Plans with most tasks in W3+ are over-serialized — a gap.
    2. FILE OWNERSHIP: Does every task own a distinct set of files with no overlaps?
       Two tasks creating/modifying the same file is a critical gap.
    3. DEPENDENCY JUSTIFICATION: For each dependency beyond T1, is there a clear reason
       (file, API, or data dependency)? Unjustified dependencies reduce parallelism — a gap.
    4. SHARED FILE COVERAGE: Does T1 include ALL shared infrastructure (base classes,
       interfaces, DI registration, config) that parallel tasks will need?
       Parallel tasks that need to create shared infrastructure are a gap.
    5. MERGE CONFLICT RISK: Are there tasks that touch adjacent or related files where
       merge conflicts are likely (e.g., neighboring lines in the same config file)?
       High merge-conflict risk between parallel tasks is a gap.
    6. DEPENDENCY CHAIN DEPTH: Are there long dependency chains (A→B→C→D)?
       Chains longer than 2 levels (beyond T1) indicate over-serialization — a gap.
    """;
```

### Dedicated Parallelism Validation Turn

After generating the plan, add a focused AI turn:

```csharp
// After existing self-assessment (line 589), add parallelism assessment
var parallelismPrompt = $"""
    Review the following engineering plan for PARALLELISM QUALITY.
    
    {FormatTasksForReview(parsedTasks)}
    
    Analysis required:
    1. What percentage of tasks are in Wave 2 (main parallel wave)?
    2. Are there any file ownership conflicts between parallel tasks?
    3. For each task in W3+, can its dependency on W2 tasks be eliminated
       by moving shared code to T1?
    4. What is the critical path (longest dependency chain)?
    5. What is the maximum number of tasks that can execute simultaneously?
    
    If you find tasks that should be in W2 but aren't, explain what change
    would allow moving them earlier.
    
    If the plan scores below 60% W2 tasks, rewrite the TASK lines with
    improved parallelism. Otherwise, output "PARALLELISM_OK".
    """;

history.AddUserMessage(parallelismPrompt);
var parallelismResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

if (!parallelismResponse.Content?.Contains("PARALLELISM_OK") == true)
{
    // Parse the improved tasks from the response
    parsedTasks = ParseImprovedTasks(parallelismResponse.Content, parsedTasks);
    Logger.LogInformation("Plan improved by parallelism assessment — re-validating");
}
```

### Parallelism Metrics Logging

After plan finalization, log parallelism metrics for dashboard/monitoring:

```csharp
private void LogParallelismMetrics(List<EngineeringTask> tasks)
{
    var waves = tasks.GroupBy(t => t.Wave).OrderBy(g => g.Key).ToList();
    var w2Count = waves.FirstOrDefault(w => w.Key == "W2")?.Count() ?? 0;
    var nonFoundation = tasks.Count(t => t.Wave != "W1" && t.Wave != "W-FINAL");
    var w2Pct = nonFoundation > 0 ? (double)w2Count / nonFoundation * 100 : 0;
    var maxParallel = waves.Max(w => w.Count());
    var criticalPath = CalculateCriticalPathLength(tasks);
    
    Logger.LogInformation(
        "Plan parallelism: {W2Pct:F0}% in W2, max parallelism={MaxParallel}, " +
        "critical path={CriticalPath} waves, total waves={TotalWaves}",
        w2Pct, maxParallel, criticalPath, waves.Count);
    
    LogActivity("planning",
        $"📊 Plan parallelism: {w2Pct:F0}% parallel (W2), " +
        $"max {maxParallel} simultaneous tasks, {waves.Count} execution waves");
}
```

### Files Changed

| File | Change |
|------|--------|
| `AssessmentCriteria.cs` | Add `SoftwareEngineerParallelism` criteria string |
| `SoftwareEngineerAgent.cs` | Add parallelism assessment AI turn after plan generation. Add `LogParallelismMetrics()`. Add `CalculateCriticalPathLength()`. |

---

## 8. Enhancement 6: Critical Path Analysis

### Problem

The SE doesn't analyze the dependency graph to find the critical path (longest chain of dependent tasks). This means:
- Bottleneck tasks aren't prioritized for assignment
- Worker scaling doesn't account for future waves
- The plan may have an unnecessarily long critical path that could be shortened

### Solution

Implement a topological-sort-based critical path calculator:

```csharp
private CriticalPathResult AnalyzeCriticalPath(List<EngineeringTask> tasks)
{
    // Build adjacency list (task → tasks that depend on it)
    var graph = new Dictionary<string, List<string>>();
    var inDegree = new Dictionary<string, int>();
    var taskMap = tasks.ToDictionary(t => t.Id);
    
    foreach (var task in tasks)
    {
        graph.TryAdd(task.Id, new List<string>());
        inDegree.TryAdd(task.Id, 0);
    }
    
    foreach (var task in tasks)
    {
        foreach (var dep in task.Dependencies)
        {
            if (graph.ContainsKey(dep))
            {
                graph[dep].Add(task.Id);
                inDegree[task.Id] = inDegree.GetValueOrDefault(task.Id) + 1;
            }
        }
    }
    
    // Topological sort with level tracking (BFS)
    var queue = new Queue<(string TaskId, int Level)>();
    var levels = new Dictionary<string, int>();
    
    foreach (var (taskId, degree) in inDegree.Where(kv => kv.Value == 0))
    {
        queue.Enqueue((taskId, 0));
        levels[taskId] = 0;
    }
    
    var criticalPath = new List<string>();
    var maxLevel = 0;
    
    while (queue.Count > 0)
    {
        var (current, level) = queue.Dequeue();
        levels[current] = level;
        
        if (level > maxLevel)
        {
            maxLevel = level;
        }
        
        foreach (var next in graph[current])
        {
            inDegree[next]--;
            var nextLevel = level + 1;
            
            if (inDegree[next] == 0)
            {
                queue.Enqueue((next, nextLevel));
            }
        }
    }
    
    // Find the critical path (longest chain)
    // Trace back from the deepest level
    var deepestTask = levels.OrderByDescending(kv => kv.Value).First().Key;
    var path = new List<string> { deepestTask };
    var current2 = deepestTask;
    
    while (taskMap[current2].Dependencies.Count > 0)
    {
        var deepestDep = taskMap[current2].Dependencies
            .Where(d => levels.ContainsKey(d))
            .OrderByDescending(d => levels[d])
            .First();
        path.Insert(0, deepestDep);
        current2 = deepestDep;
    }
    
    return new CriticalPathResult
    {
        Path = path,
        Length = maxLevel + 1,
        BottleneckTasks = tasks
            .Where(t => path.Contains(t.Id) && t.Wave != "W1" && t.Wave != "W-FINAL")
            .Select(t => t.Id)
            .ToList(),
        MaxParallelism = levels.GroupBy(kv => kv.Value).Max(g => g.Count()),
        LevelDistribution = levels.GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count())
    };
}

private record CriticalPathResult
{
    public List<string> Path { get; init; } = [];
    public int Length { get; init; }
    public List<string> BottleneckTasks { get; init; } = [];
    public int MaxParallelism { get; init; }
    public Dictionary<int, int> LevelDistribution { get; init; } = new();
}
```

### Critical Path-Aware Task Assignment

Prioritize bottleneck tasks for assignment to the strongest engineers:

```csharp
// In FindNextAssignableTask, add critical path priority
private EngineeringTask? FindNextAssignableTask(string engineerId, AgentRole engineerRole)
{
    var ready = _taskManager.GetAssignableTasks()
        .Where(t => t.Id != IntegrationTaskId);
    
    // NEW: Prioritize critical path tasks (bottlenecks)
    var criticalPathTasks = ready.Where(t => _criticalPath?.BottleneckTasks.Contains(t.Id) == true);
    var nonCriticalTasks = ready.Where(t => _criticalPath?.BottleneckTasks.Contains(t.Id) != true);
    
    // Assign bottleneck tasks first, to the strongest available engineer
    var preferred = criticalPathTasks.Any() ? criticalPathTasks : nonCriticalTasks;
    
    return engineerRole switch
    {
        AgentRole.SoftwareEngineer => preferred.OrderByDescending(ComplexityRank).FirstOrDefault(),
        AgentRole.SoftwareEngineer => preferred.OrderBy(t => Math.Abs(ComplexityRank(t) - 2)).FirstOrDefault(),
        AgentRole.SoftwareEngineer => preferred.OrderBy(ComplexityRank).FirstOrDefault(),
        _ => preferred.FirstOrDefault()
    };
}
```

### Files Changed

| File | Change |
|------|--------|
| `SoftwareEngineerAgent.cs` | Add `AnalyzeCriticalPath()`, `CriticalPathResult` record. Store `_criticalPath` as field. Update `FindNextAssignableTask()` with critical path priority. Log critical path in plan metrics. |

---

## 9. Data Model Changes

### EngineeringTask Record (Updated)

```csharp
internal record EngineeringTask
{
    // Existing properties (unchanged)
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Complexity { get; init; } = "Medium";
    public string Status { get; init; } = "Pending";
    public string? AssignedTo { get; init; }
    public int? PullRequestNumber { get; init; }
    public int? IssueNumber { get; init; }
    public long? GitHubId { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
    public List<int> DependencyIssueNumbers { get; init; } = new();
    public int? ParentIssueNumber { get; init; }
    public List<string> Labels { get; init; } = new();
    
    // NEW: Enhancement 2 — Wave assignment
    public string Wave { get; init; } = "W1";
    
    // NEW: Enhancement 3 — Files this task owns (CREATE/MODIFY)
    public List<string> OwnedFiles { get; init; } = new();
    
    // NEW: Enhancement 4 — Typed dependencies
    public Dictionary<string, string> DependencyTypes { get; init; } = new();
}
```

### New Records

```csharp
/// File overlap between two tasks
internal record FileOverlap(string FilePath, string Task1Id, string Task2Id, string Operation);

/// Wave analysis results
internal record WaveAnalysis
{
    public int TotalWaves { get; set; }
    public int MaxParallelism { get; set; }
    public double W2Percentage { get; set; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

/// Critical path analysis results
internal record CriticalPathResult
{
    public List<string> Path { get; init; } = [];
    public int Length { get; init; }
    public List<string> BottleneckTasks { get; init; } = [];
    public int MaxParallelism { get; init; }
    public Dictionary<int, int> LevelDistribution { get; init; } = new();
}
```

---

## 10. Implementation Phases

### Phase 1: File Overlap Enforcement (Enhancement 1 + 3)

**Goal:** Eliminate file conflicts between parallel tasks.

**Changes:**
- Add `DetectFileOverlaps()` with `FileOverlap` record
- Add `EnsureNoFileOverlapsAsync()` with AI fix prompt + auto-resolution fallback
- Add `EnforceSharedFileRegistry()` and `ExtractSharedFiles()`
- Update system prompt with `SHARED_FILES` declaration for T1
- Replace warning-only logic in `EnsureFoundationFirstPattern()`
- Enhance `ExtractCreateFilesFromDescription()` → `ExtractFileOperations()` returning operation types

**Files:** `SoftwareEngineerAgent.cs`, `EngineeringTaskIssueManager.cs`
**Tests:** Unit tests for overlap detection, shared file parsing, auto-resolution logic
**Risk:** Low — additive logic, doesn't change task output format

### Phase 2: Wave Scheduling (Enhancement 2)

**Goal:** Explicit execution wave assignment in plans.

**Changes:**
- Update system prompt with wave assignment instructions
- Add 9th field (`Wave`) to TASK output format + parse from `WAVES|` summary line
- Add `Wave` property to `EngineeringTask` record
- Add `ValidateWaves()` with W2 percentage target
- Update issue body generation to include wave metadata
- Update issue body parsing to recover wave on restart

**Files:** `SoftwareEngineerAgent.cs`, `EngineeringTask` record, `EngineeringTaskIssueManager.cs`
**Tests:** Unit tests for wave parsing, validation, wave-from-issue-body recovery
**Risk:** Medium — changes task output format (backward compatibility: default Wave="W1" if missing)

### Phase 3: Typed Dependencies (Enhancement 4)

**Goal:** Enable intelligent dependency relaxation.

**Changes:**
- Update system prompt with dependency type format
- Parse typed deps from output (`T1(files),T3(api)`)
- Add `DependencyTypes` to `EngineeringTask` record
- Add `CanRelaxDependency()` logic
- Update `AreDependenciesMet()` with optional relaxation parameter
- Include dep types in issue body metadata

**Files:** `SoftwareEngineerAgent.cs`, `EngineeringTask` record, `EngineeringTaskIssueManager.cs`
**Tests:** Unit tests for typed dep parsing, relaxation logic, backward compat with untyped deps
**Risk:** Medium — changes dependency behavior. Use `useRelaxation = false` flag as safety valve.

### Phase 4: Parallelism Assessment & Critical Path (Enhancement 5 + 6)

**Goal:** AI-validated parallelism quality and bottleneck identification.

**Changes:**
- Add `SoftwareEngineerParallelism` to `AssessmentCriteria.cs`
- Add parallelism validation AI turn after plan generation
- Add `AnalyzeCriticalPath()` with topological sort
- Add `LogParallelismMetrics()` for monitoring
- Update `FindNextAssignableTask()` with critical path priority
- Store `_criticalPath` as SE agent field

**Files:** `SoftwareEngineerAgent.cs`, `AssessmentCriteria.cs`
**Tests:** Unit tests for critical path calculation (DAG test cases), parallelism metric logging
**Risk:** Low — adds AI turn (cost) but doesn't change plan structure. Critical path priority is additive.

### Phase 5: Wave-Aware Worker Scaling

**Goal:** Scale engineers based on wave width, not just parallelizable task count.

**Changes:**
- Update `EvaluateResourceNeedsAsync()` to identify current wave
- Scale worker requests to match wave task count
- Pre-request workers for upcoming waves when current wave is nearly complete

**Files:** `SoftwareEngineerAgent.cs`
**Tests:** Integration test for scaling decisions based on wave width
**Risk:** Low — improves existing scaling logic without changing interfaces

### Phase Dependency Graph

```
Phase 1 (File Overlap Enforcement)
    ↓
Phase 2 (Wave Scheduling)   ←── can start in parallel with Phase 1
    ↓
Phase 3 (Typed Dependencies) ←── depends on Phase 2 for wave context
    ↓
Phase 4 (Assessment + Critical Path) ←── depends on Phase 2 + 3
    ↓
Phase 5 (Wave-Aware Scaling) ←── depends on Phase 2
```

Phases 1 and 2 can run in parallel (different concerns).
Phase 3 benefits from Phase 2 being done first.
Phases 4 and 5 depend on waves being in place.

---

## 11. Testing Strategy

### Unit Tests

| Test | Enhancement | What It Validates |
|------|------------|-------------------|
| `DetectFileOverlaps_NoOverlap_ReturnsEmpty` | 1 | Clean plans pass |
| `DetectFileOverlaps_CreateConflict_DetectsOverlap` | 1 | Two CREATEs caught |
| `DetectFileOverlaps_UseDoesNotConflict` | 1 | USE references are safe |
| `AutoResolveOverlaps_AssignsToEarlierTask` | 1 | Fallback logic correct |
| `ExtractSharedFiles_ParsesFromDescription` | 3 | SHARED_FILES declaration parsed |
| `EnforceSharedFileRegistry_BlocksModify` | 3 | Parallel tasks can't modify shared files |
| `ValidateWaves_HighW2Percentage_Passes` | 2 | Good plans accepted |
| `ValidateWaves_LowW2Percentage_Warns` | 2 | Over-serialized plans flagged |
| `ValidateWaves_DependencyInSameWave_Errors` | 2 | Invalid wave ordering caught |
| `ParseTypedDependencies_WithTypes` | 4 | `T1(files),T3(api)` parsed correctly |
| `ParseTypedDependencies_WithoutTypes_DefaultsToNone` | 4 | Backward compat |
| `CanRelaxDependency_ApiWithInterfaceInT1_ReturnsTrue` | 4 | API deps relaxed when interface available |
| `CanRelaxDependency_FileDep_ReturnsFalse` | 4 | File deps never relaxed |
| `AnalyzeCriticalPath_StarTopology_LengthTwo` | 6 | T1 → parallel wave = length 2 |
| `AnalyzeCriticalPath_ChainTopology_ReportsFullChain` | 6 | A→B→C→D = length 4 |
| `AnalyzeCriticalPath_MaxParallelism_Correct` | 6 | Widest wave width reported |

### Integration Tests

| Test | What It Validates |
|------|-------------------|
| `PlanGeneration_NoFileOverlaps` | End-to-end plan from mock issues has no file overlaps |
| `PlanGeneration_WavesValid` | Generated plan has valid wave assignments |
| `TaskAssignment_CriticalPathFirst` | Bottleneck tasks assigned before non-critical |
| `WorkerScaling_MatchesWaveWidth` | Resource requests scale to wave width |
| `PlanRecovery_ParsesWavesAndTypes` | Issue body round-trip preserves waves and dep types |

---

## 12. Risk Assessment

| Risk | Impact | Likelihood | Mitigation |
|------|--------|-----------|-----------|
| AI doesn't follow new output format (waves, types) | Medium — parse failure | Medium | Graceful fallback: default wave W1, untyped deps. Existing plan still works. |
| File overlap fix prompt produces worse plan | Medium — regression | Low | Max 2 retries then auto-resolve. Compare task count before/after. |
| Dependency relaxation releases task too early | High — broken build | Low | `useRelaxation` flag defaults to false initially. Opt-in per run. |
| Extra AI turn (parallelism assessment) increases cost | Low — ~1 extra API call | High | Only run when wave analysis shows <60% W2. Skip for small plans (<4 tasks). |
| Backward compatibility with existing plans | Medium — recovery fails | Medium | All new fields have defaults (Wave="W1", DependencyTypes=empty). Parse handles missing 8th field. |
| Critical path priority starves simple tasks | Low — Software Engineers idle | Low | Critical path priority only applies to SE/SE. SE assignment unchanged. |

---

*This plan is designed to be implemented incrementally. Each phase adds value independently — you don't need all 5 phases for the system to improve. Phase 1 (file overlap enforcement) alone would eliminate the most common source of merge conflicts in parallel development.*
