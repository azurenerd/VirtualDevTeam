# Interactive Agent Mode — Architecture Plan

## Overview

Replace the current "LLM-as-completion-endpoint" pattern with a fully agentic model where the underlying CLI (or any agentic backend) handles filesystem operations, builds, and git — and VirtualDevTeam becomes a thin orchestration/UI layer.

## Current Architecture (Completion Mode)

```
VirtualDevTeam Orchestration (C#)
  ├── Builds prompt (system + context + step instructions)
  ├── Spawns `copilot` process (stdin pipe, stdout collect, exit)
  ├── Parses response text → CodeFileParser extracts FILE: markers
  ├── Resolves paths, filters scope
  ├── Writes files to local workspace
  ├── Runs `dotnet build` + `dotnet test`
  ├── Fix loop (re-prompts LLM with errors, up to N retries)
  └── `git commit` + `git push`
```

**Pros:** Full control, deterministic, auditable, scope enforcement.
**Cons:** Fragile text parsing, context bloat (full rebuild per step), no tool use by AI, orchestration complexity (~3000 lines in EngineerAgentBase).

## Proposed Architecture (Interactive Agent Mode)

```
VirtualDevTeam Orchestration (C#) — thin layer
  ├── Prepares task context (issue, PR, design spec, repo structure)
  ├── Starts agentic session (persistent process or API)
  ├── Sends task prompt: "Implement step N. You have access to the workspace at {path}."
  ├── Agent autonomously:
  │     ├── Reads existing files as needed
  │     ├── Creates/edits files via its own tool calls
  │     ├── Runs build commands, reads errors, fixes them
  │     ├── Runs tests, reads failures, fixes them
  │     └── Signals completion
  ├── VirtualDevTeam validates output (scope filter, file count, build green)
  └── `git commit` + `git push` (VirtualDevTeam retains git control)
```

**Pros:** AI can iterate autonomously (read→write→build→fix loop), no text parsing needed, smaller orchestration codebase, leverages tool-calling intelligence.
**Cons:** Less deterministic, harder to audit mid-flight, sandboxing/security concerns, need to constrain which tools/paths the agent can access.

## Implementation Phases

### Phase 1: Define the Agent Backend Interface

Create an abstraction that decouples VirtualDevTeam from any specific agentic backend:

```csharp
public interface IAgentBackend
{
    /// Start a persistent agent session with a workspace directory
    Task<IAgentSession> StartSessionAsync(AgentSessionConfig config, CancellationToken ct);
}

public interface IAgentSession : IAsyncDisposable
{
    /// Send a task to the agent and get structured results
    Task<AgentTaskResult> ExecuteTaskAsync(string prompt, CancellationToken ct);
    
    /// Send a follow-up message in the same session (maintains context)
    Task<AgentTaskResult> SendFollowUpAsync(string message, CancellationToken ct);
    
    /// Get the list of files modified during the session
    Task<IReadOnlyList<string>> GetModifiedFilesAsync(CancellationToken ct);
}

public record AgentSessionConfig
{
    public required string WorkspacePath { get; init; }
    public required string BranchName { get; init; }
    public IReadOnlyList<string>? AllowedPaths { get; init; }  // scope enforcement
    public IReadOnlyList<string>? AllowedCommands { get; init; }  // sandbox
    public string? ModelId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

public record AgentTaskResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<FileChange> FilesChanged { get; init; } = [];
    public BuildResult? BuildResult { get; init; }
    public TestResult? TestResult { get; init; }
    public IReadOnlyList<AgentStep> Steps { get; init; } = [];  // for observability
}

public record AgentStep
{
    public string Tool { get; init; } = "";      // "create", "edit", "powershell", etc.
    public string Description { get; init; } = "";
    public DateTime Timestamp { get; init; }
}
```

### Phase 2: Copilot CLI Interactive Backend

Implement `IAgentBackend` using Copilot CLI in interactive/agentic mode:

1. **Start persistent process** — launch `copilot` without `--no-ask-user` and `--silent`, keeping stdin/stdout streams open.
2. **Send structured prompts** — include workspace path, allowed files, and task description.
3. **Monitor tool calls** — parse the CLI's tool-call output (it uses a structured format) to track what files are being created/edited and what commands are run.
4. **Scope enforcement** — intercept file operations and reject any outside `AllowedPaths`. Two strategies:
   - **Pre-check**: Set the workspace to a scoped subdirectory so the agent physically can't escape.
   - **Post-check**: After completion, diff the workspace and reject out-of-scope changes.
5. **Collect results** — when the agent signals done (or times out), capture modified files, build status, test results.
6. **Session reuse** — for multi-step PRs, keep the session alive so the agent retains context from prior steps (eliminates context rebuild bloat).

Key CLI flags for interactive mode:
```bash
copilot --allow-all --no-auto-update --no-custom-instructions --model claude-sonnet-4.6
# Note: NO --no-ask-user, NO --silent — these enable agentic behavior
```

### Phase 3: Simplify EngineerAgentBase

With `IAgentBackend` handling implementation, EngineerAgentBase shrinks dramatically:

**Remove (~1500 lines):**
- `CodeFileParser` and all FILE: marker parsing
- `ResolveFilePathsAsync` path correction
- `CommitViaLocalWorkspaceAsync` build/test/fix loop
- `RunSelfReviewAsync` (agent does this natively)
- Step-by-step ChatHistory construction
- Context rebuild per step

**Keep:**
- Task assignment and PR lifecycle (create PR, assign issue, labels)
- Scope definition (which files this task should touch)
- Git operations (commit, push, branch management)
- Quality gates (post-validation: build green, tests pass, scope respected)
- Dashboard/observability integration

**New flow in ImplementAndCommitAsync:**
```csharp
async Task ImplementInteractiveAsync(PR pr, Issue issue, List<string> steps, CancellationToken ct)
{
    using var session = await _agentBackend.StartSessionAsync(new AgentSessionConfig
    {
        WorkspacePath = Workspace.RepoPath,
        BranchName = pr.Head.Ref,
        AllowedPaths = ExtractAllowedFilesFromDescription(pr.Body, issue.Body),
        ModelId = _modelRegistry.GetModelId(Identity.ModelTier),
        Timeout = TimeSpan.FromMinutes(15)
    }, ct);

    // Single prompt with all steps — agent handles iteration
    var prompt = BuildTaskPrompt(pr, issue, steps);
    var result = await session.ExecuteTaskAsync(prompt, ct);

    // Record observability data
    foreach (var step in result.Steps)
        _taskTracker.RecordSubStep(taskId, $"{step.Tool}: {step.Description}");

    // Validate and commit
    if (result.Success && result.BuildResult?.Green == true)
    {
        await Workspace.CommitAndPushAsync(result.FilesChanged, commitMessage, ct);
    }
    else
    {
        // Follow-up: ask agent to fix
        var fixResult = await session.SendFollowUpAsync(
            $"Build failed: {result.BuildResult?.Error}. Please fix.", ct);
        // ... retry logic
    }
}
```

### Phase 4: Alternative Backends

The `IAgentBackend` interface enables plugging in other agentic solutions:

| Backend | Implementation | Pros |
|---------|---------------|------|
| **Copilot CLI** | Interactive process, stdin/stdout | Free with GitHub, good tool use |
| **Claude Code** | `claude` CLI in agentic mode | Strong coding, native tool use |
| **Aider** | `aider` CLI with git integration | Purpose-built for code editing |
| **OpenAI Codex** | API with tool definitions | Structured tool calling |
| **Custom MCP** | Local MCP server with file/build tools | Full control over tool definitions |

Each implements `IAgentBackend` → `IAgentSession` and VirtualDevTeam doesn't care which one is active.

### Phase 5: Observability & Safety

1. **Step Streaming** — As the agent works, stream its tool calls to the Dashboard via SignalR so you can watch in real-time what files it's touching.
2. **Scope Guard** — Hard filesystem boundary (symlink or chroot the workspace) + soft validation post-completion.
3. **Token Budget** — Set max tokens per session to prevent runaway costs.
4. **Timeout Escalation** — If agent exceeds timeout, kill session, capture partial work, log for human review.
5. **Audit Trail** — Every tool call, file change, and command execution logged with timestamps. This naturally solves the `visible-orchestration-steps` TODO since the agent's own tool calls ARE the steps.

## Migration Strategy

1. **Feature flag**: `AgentConfigs.InteractiveAgentMode` (default: false)
2. **Side-by-side**: Both completion and interactive modes coexist, switchable per agent
3. **Gradual rollout**: Start with one SME agent in interactive mode, compare output quality
4. **Fallback**: If interactive session fails/times out, fall back to completion mode

## Configuration

```json
{
  "VirtualDevTeam": {
    "Agents": {
      "InteractiveAgentMode": false,
      "InteractiveBackend": "CopilotCli",
      "InteractiveTimeout": "00:15:00",
      "InteractiveMaxTokens": 100000,
      "InteractiveAllowedTools": ["create", "edit", "view", "powershell"],
      "InteractiveBlockedCommands": ["rm -rf", "format", "del /s"]
    }
  }
}
```

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| Agent writes outside scope | Filesystem sandboxing + post-validation |
| Agent runs destructive commands | Command allowlist, blocked patterns |
| Non-deterministic output | Post-validation gates (build, test, scope) remain |
| Harder to debug failures | Full tool-call audit trail + dashboard streaming |
| CLI interactive mode instability | Timeout + fallback to completion mode |
| Cost (longer sessions = more tokens) | Token budget per session, model tier selection |

## Estimated Impact

- **EngineerAgentBase**: ~3000 lines → ~800 lines (remove parsing, build loop, context rebuild)
- **Context efficiency**: Session persists context → no per-step rebuild → ~60% token reduction for multi-step PRs
- **Quality**: AI can read its own output, verify builds, self-correct → fewer broken commits
- **Flexibility**: Swap backends without touching agent logic
