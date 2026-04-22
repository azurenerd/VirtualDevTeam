using System.Diagnostics;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Phase-3 strategy: instead of running a single-pass LLM generator, it delegates
/// the whole task to a sandboxed agentic <c>copilot --allow-all</c> session running
/// inside the candidate worktree. The CLI does its own planning + tool-calling
/// (read, write, run, git add/commit) until it self-reports done, exhausts the
/// tool-call cap, goes stuck, or hits the wall-clock budget.
///
/// <para>
/// Trust model: process-level containment only. Uses env scrubbing
/// (<see cref="CopilotCliAgenticScope"/>), a Windows Job Object for atomic
/// descendant-kill (<see cref="Win32JobObject"/>), and per-worktree git config
/// isolation. Does NOT protect against network exfil, reads of host-readable
/// files via absolute paths, or human-targeted prompt injection. Ship opt-in;
/// never default.
/// </para>
/// </summary>
public class AgenticDelegationStrategy : ICodeGenerationStrategy
{
    public string Id => "copilot-cli";

    private readonly ILogger<AgenticDelegationStrategy> _logger;
    private readonly CopilotCliProcessManager _processManager;
    private readonly StrategyFrameworkConfig _frameworkConfig;
    private readonly AgenticPromptBuilder _promptBuilder;

    public AgenticDelegationStrategy(
        ILogger<AgenticDelegationStrategy> logger,
        CopilotCliProcessManager processManager,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        AgenticPromptBuilder? promptBuilder = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _frameworkConfig = (frameworkConfig ?? throw new ArgumentNullException(nameof(frameworkConfig))).Value;
        _promptBuilder = promptBuilder ?? new AgenticPromptBuilder();
    }

    public async Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sink = invocation.ActivitySink;

        // Build sandbox scope. Worktree must already exist — strategy orchestrator
        // owns worktree creation; we just materialize the sandbox dirs inside it.
        CopilotCliAgenticScope scope;
        try
        {
            scope = CopilotCliAgenticScope.Prepare(invocation.WorktreePath);
            sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Sandbox scope prepared"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticDelegationStrategy sandbox prepare failed for task {Task}", invocation.Task.TaskId);
            return Fail(sw, $"sandbox-prepare: {ex.GetType().Name}: {ex.Message}");
        }

        var prompt = _promptBuilder.Build(invocation);
        sink?.Report(new Frameworks.FrameworkActivityEvent("init", "Prompt built, launching agentic session"));

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            // Close stdin after piping the prompt. Leaving stdin open historically
            // caused the agentic session to hang before emitting any output (the
            // CLI waits on EOF on the initial prompt before it starts processing).
            // Symptom: stuck-no-output at exactly StuckSeconds, 0 tool calls, empty
            // log buffer. Multi-turn stdin is not used today; flip back to false
            // only when we add a real interactive protocol.
            CloseStdinAfterPrompt = true,
            WatchdogMode = CopilotCliWatchdogMode.Agentic,
            WorkingDirectory = invocation.WorktreePath,
            EnvironmentOverrides = scope.EnvironmentOverrides,
            Timeout = invocation.Timeout,
        };

        // Snapshot host state BEFORE launching the CLI. The validator compares
        // post-session state against this to catch sandbox escapes that the
        // in-process containment missed (e.g. a ~/.gitconfig write that slipped
        // past the GIT_CONFIG_GLOBAL redirect).
        var validator = new SandboxPostRunValidator(_logger);
        var snapshot = SandboxPostRunValidator.TakeSnapshot(invocation.WorktreePath);

        try
        {
            var result = await _processManager.ExecuteAgenticSessionAsync(prompt, options, ct);

            sink?.Report(new Frameworks.FrameworkActivityEvent("complete",
                $"Session finished: {(result.Succeeded ? "succeeded" : "failed")} " +
                $"(tool-calls: {result.ToolCallCount}, wall: {result.WallClock.TotalSeconds:F1}s)"));

            if (!result.Succeeded)
            {
                var reason = result.FailureReason switch
                {
                    AgenticFailureReason.StuckNoOutput => "stuck-no-output",
                    AgenticFailureReason.ToolCallCap => "tool-call-cap",
                    AgenticFailureReason.Timeout => "timeout",
                    AgenticFailureReason.Canceled => "canceled",
                    AgenticFailureReason.ExitNonzero => $"exit-nonzero: {result.ExitCode}",
                    AgenticFailureReason.LaunchFailed => $"launch-failed: {result.ErrorMessage}",
                    AgenticFailureReason.Unavailable => "cli-unavailable",
                    _ => result.ErrorMessage ?? "unknown-agentic-failure",
                };
                _logger.LogWarning(
                    "AgenticDelegationStrategy failed for task {Task}: {Reason} (tool-calls: {ToolCalls}, wall: {Wall}s)",
                    invocation.Task.TaskId, reason, result.ToolCallCount, result.WallClock.TotalSeconds);
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = reason,
                    Elapsed = sw.Elapsed,
                    Log = new[] { TruncateLog(result.LogBuffer) },
                };
            }

            // Post-run sandbox validation. Any violation demotes the candidate to
            // failed — better to drop a suspect patch than ship an escaped one.
            var violations = validator.Validate(
                invocation.WorktreePath, snapshot, scope.SandboxGitconfigPath);
            if (violations.Count > 0)
            {
                var codes = string.Join(",", violations.Select(v => v.Code));
                _logger.LogError(
                    "AgenticDelegationStrategy sandbox violations for task {Task}: {Codes}",
                    invocation.Task.TaskId, codes);
                return new StrategyExecutionResult
                {
                    StrategyId = Id,
                    Succeeded = false,
                    FailureReason = $"sandbox-violation: {codes}",
                    Elapsed = sw.Elapsed,
                    Log = new[] { TruncateLog(result.LogBuffer) },
                };
            }

            _logger.LogInformation(
                "AgenticDelegationStrategy succeeded for task {Task} (tool-calls: {ToolCalls}, wall: {Wall}s)",
                invocation.Task.TaskId, result.ToolCallCount, result.WallClock.TotalSeconds);

            // Diagnostic: persist agentic session log for forensic analysis. Agentic
            // can report "succeeded" but still produce an empty patch (the CLI ran
            // its tool calls but wrote nothing to the worktree); without the log we
            // can't see WHY. File is best-effort — don't fail the strategy on IO error.
            TryPersistAgenticLog(invocation, result);

            return new StrategyExecutionResult
            {
                StrategyId = Id,
                Succeeded = true,
                Elapsed = sw.Elapsed,
                Log = new[] { TruncateLog(result.LogBuffer) },
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticDelegationStrategy threw for task {Task}", invocation.Task.TaskId);
            return Fail(sw, $"strategy-exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private StrategyExecutionResult Fail(Stopwatch sw, string reason) => new()
    {
        StrategyId = Id,
        Succeeded = false,
        FailureReason = reason,
        Elapsed = sw.Elapsed,
    };

    private static string TruncateLog(string log)
    {
        const int MaxLogChars = 8 * 1024;
        return log.Length <= MaxLogChars ? log : log[..MaxLogChars] + "\n… [truncated]";
    }

    private void TryPersistAgenticLog(StrategyInvocation invocation, AgenticSessionResult result)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "experiment-data");
            Directory.CreateDirectory(dir);
            var safeTask = new string((invocation.Task.TaskId ?? "unknown")
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var fileName = $"{stamp}-{safeTask}-agentic.log";
            var fullPath = Path.Combine(dir, fileName);
            var header = $"# Agentic session log\n" +
                         $"# stamp: {stamp}\n" +
                         $"# task: {invocation.Task.TaskId} — {invocation.Task.TaskTitle}\n" +
                         $"# worktree: {invocation.WorktreePath}\n" +
                         $"# succeeded: {result.Succeeded}\n" +
                         $"# tool-calls: {result.ToolCallCount}\n" +
                         $"# wall: {result.WallClock.TotalSeconds:F1}s\n" +
                         $"# exit: {result.ExitCode}\n" +
                         $"# ----- stdout/stderr begin -----\n";
            File.WriteAllText(fullPath, header + (result.LogBuffer ?? ""));
            _logger.LogInformation("Persisted agentic session log: {Path}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist agentic session log for task {Task}", invocation.Task.TaskId);
        }
    }
}

/// <summary>
/// Builds the task prompt handed to the agentic copilot session. Extracted so
/// tests can assert prompt shape without needing a real process. Default
/// implementation emits a structured markdown block with acceptance criteria,
/// commit-message convention, and "do not push / do not touch files outside
/// this worktree" safety reminders.
/// </summary>
public class AgenticPromptBuilder
{
    public virtual string Build(StrategyInvocation invocation)
    {
        var t = invocation.Task;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Task {t.TaskId}: {t.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(t.TaskDescription);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(t.PmSpec))
        {
            sb.AppendLine("## Product Spec (context)");
            sb.AppendLine(t.PmSpec);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(t.Architecture))
        {
            sb.AppendLine("## Architecture (context)");
            sb.AppendLine(t.Architecture);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(t.DesignContext))
        {
            sb.AppendLine("## UI / Design context");
            sb.AppendLine(t.DesignContext);
            sb.AppendLine();
        }
        sb.AppendLine("## Working directory");
        sb.AppendLine($"You are running inside the git worktree at `{invocation.WorktreePath}`.");
        sb.AppendLine($"Branch: `{t.PrBranch}` (based on `{t.BaseSha}`).");
        sb.AppendLine();
        sb.AppendLine("## Constraints");
        sb.AppendLine("- Modify files ONLY inside this worktree. Do NOT touch any file outside the working directory.");
        sb.AppendLine("- Do NOT run `git push` or any network-mutating operation. Commits are expected and fine.");
        sb.AppendLine("- Keep commits focused and scoped to this task. Prefer one logical commit per concern.");
        sb.AppendLine("- Write a concise commit message: `{type}({scope}): {title}` body with what/why.");
        sb.AppendLine("- If a test suite exists, try to keep it green. If you add new tests, ensure they pass.");
        sb.AppendLine("- Stop when the acceptance criteria are met or when you cannot make forward progress.");
        sb.AppendLine();
        sb.AppendLine("## Begin");
        sb.AppendLine("Implement this task autonomously. You have --allow-all — use tools freely, but respect the constraints above.");
        return sb.ToString();
    }
}
