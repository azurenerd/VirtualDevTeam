using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Executes coding tasks by delegating to the Squad framework (Brady Gaster's
/// multi-agent team). Implements the full adapter surface: execution, lifecycle
/// (via <see cref="SquadReadinessChecker"/>), and stdout-based telemetry.
///
/// <para>Execution model:</para>
/// <list type="number">
///   <item>Initialize Squad in the worktree (<c>squad init</c>)</item>
///   <item>Pre-populate <c>.squad/team.md</c> with task-derived team config</item>
///   <item>Run <c>copilot --agent squad --yolo -p &lt;prompt&gt;</c> headlessly</item>
///   <item>Capture stdout for telemetry (sub-agent spawns, token metrics)</item>
///   <item>Return code changes + metrics as <see cref="FrameworkExecutionResult"/></item>
/// </list>
/// </summary>
public sealed class SquadFrameworkAdapter
    : IAgenticFrameworkAdapter, IFrameworkLifecycle, IFrameworkTelemetrySource
{
    private readonly ILogger<SquadFrameworkAdapter> _logger;
    private readonly SquadReadinessChecker _readiness;
    // Squad spawns sub-agents that each make copilot CLI calls (3-5min each).
    // During sub-agent work, the parent process produces no stdout — 120s was too short.
    private readonly TimeSpan _stuckThreshold = TimeSpan.FromSeconds(600);

    // Telemetry events captured during the most recent execution (for snapshot queries).
    private readonly List<FrameworkEvent> _lastRunEvents = new();

    public SquadFrameworkAdapter(
        ILogger<SquadFrameworkAdapter> logger,
        SquadReadinessChecker readiness)
    {
        _logger = logger;
        _readiness = readiness;
    }

    // ── IAgenticFrameworkAdapter ──

    public string Id => "squad";
    public string DisplayName => "Squad";
    public string Description => "Multi-agent team coordination via Squad framework (Brady Gaster)";
    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(600);

    public async Task<FrameworkExecutionResult> ExecuteAsync(
        FrameworkInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<FrameworkEvent>();
        var sink = invocation.ActivitySink;

        try
        {
            // 1. Initialize Squad in the worktree
            events.Add(Evt(FrameworkEventType.Decision, "squad", "Initializing Squad workspace"));
            sink?.Report(new FrameworkActivityEvent("init", "Initializing Squad workspace"));
            var initOk = await InitializeSquadWorkspaceAsync(invocation.WorktreePath, ct);
            if (!initOk.Succeeded)
                return FailResult(sw, $"squad-init-failed: {initOk.Message}", events);

            // 2. Pre-populate team configuration
            events.Add(Evt(FrameworkEventType.Decision, "squad", "Configuring team from task context"));
            sink?.Report(new FrameworkActivityEvent("config", "Configuring team from task context"));
            await WriteTeamConfigAsync(invocation.WorktreePath, invocation.Task);

            // 3. Build the task prompt
            var prompt = SquadPromptBuilder.Build(invocation);

            // 4. Execute Squad headlessly
            events.Add(Evt(FrameworkEventType.SubAgentSpawn, "squad.coordinator",
                "Launching Squad via copilot --agent squad --yolo"));
            sink?.Report(new FrameworkActivityEvent("spawn", "Launching Squad via copilot --agent squad --yolo"));
            var execResult = await RunSquadProcessAsync(
                invocation.WorktreePath, prompt, invocation.Timeout, events, sink, ct);

            // 5. Post-execution: scrape .squad/ for decisions
            var decisions = await ScrapeDecisionsAsync(invocation.WorktreePath);
            foreach (var d in decisions)
                events.Add(Evt(FrameworkEventType.Decision, "squad", d));

            // Store events for telemetry queries
            lock (_lastRunEvents)
            {
                _lastRunEvents.Clear();
                _lastRunEvents.AddRange(events);
            }

            return new FrameworkExecutionResult
            {
                FrameworkId = Id,
                Succeeded = execResult.Succeeded,
                FailureReason = execResult.FailureReason,
                Elapsed = sw.Elapsed,
                TokensUsed = execResult.TokensUsed,
                Log = execResult.LogLines,
                Metrics = new FrameworkMetrics
                {
                    TokensUsed = execResult.TokensUsed,
                    ElapsedTime = sw.Elapsed,
                    SubAgentSpawns = execResult.SubAgentCount,
                    LlmCallsMade = execResult.RequestCount,
                    FilesModified = await CountModifiedFilesAsync(invocation.WorktreePath, ct),
                }
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Squad adapter threw for task {Task}", invocation.Task.TaskId);
            return FailResult(sw, $"squad-exception: {ex.GetType().Name}: {ex.Message}", events);
        }
    }

    // ── IFrameworkLifecycle (delegates to SquadReadinessChecker) ──

    public Task<FrameworkReadinessResult> CheckReadinessAsync(CancellationToken ct) =>
        _readiness.CheckReadinessAsync(ct);

    public Task<FrameworkInstallResult> EnsureInstalledAsync(CancellationToken ct) =>
        _readiness.EnsureInstalledAsync(ct);

    // ── IFrameworkTelemetrySource ──

    public async IAsyncEnumerable<FrameworkEvent> StreamEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Yield a copy of events captured so far. In future, this could be
        // wired to a Channel<T> for true real-time streaming during execution.
        List<FrameworkEvent> snapshot;
        lock (_lastRunEvents)
        {
            snapshot = new List<FrameworkEvent>(_lastRunEvents);
        }

        foreach (var evt in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
        }

        await Task.CompletedTask; // Satisfy async requirement
    }

    public Task<FrameworkActivitySnapshot> GetActivitySnapshotAsync(CancellationToken ct)
    {
        List<FrameworkEvent> snapshot;
        lock (_lastRunEvents)
        {
            snapshot = new List<FrameworkEvent>(_lastRunEvents);
        }

        var agentSpawns = snapshot
            .Where(e => e.Type == FrameworkEventType.SubAgentSpawn)
            .Select(e => new FrameworkAgentStatus(e.AgentName, "agent", e.Description, "completed"))
            .ToList();

        var decisions = snapshot
            .Where(e => e.Type == FrameworkEventType.Decision)
            .Select(e => e.Description)
            .TakeLast(10)
            .ToList();

        return Task.FromResult(new FrameworkActivitySnapshot
        {
            ActiveAgents = 0, // Post-execution, all agents are done
            Agents = agentSpawns,
            RecentDecisions = decisions,
        });
    }

    // ── Squad workspace initialization ──

    private async Task<(bool Succeeded, string Message)> InitializeSquadWorkspaceAsync(
        string worktreePath, CancellationToken ct)
    {
        var squadDir = Path.Combine(worktreePath, ".squad");
        if (Directory.Exists(squadDir))
        {
            _logger.LogDebug("Squad workspace already initialized at {Path}", squadDir);
            return (true, "already-initialized");
        }

        try
        {
            var (exitCode, output) = await RunCommandAsync(
                "squad", "init",
                worktreePath, TimeSpan.FromSeconds(30), ct);

            if (exitCode == 0)
            {
                _logger.LogInformation("Squad init completed in worktree {Path}", worktreePath);
                return (true, "initialized");
            }

            _logger.LogWarning("Squad init failed (exit {Code}): {Output}", exitCode, output);
            return (false, $"exit {exitCode}: {output}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Squad init threw in worktree {Path}", worktreePath);
            return (false, ex.Message);
        }
    }

    private static async Task WriteTeamConfigAsync(string worktreePath, FrameworkTaskContext task)
    {
        var squadDir = Path.Combine(worktreePath, ".squad");
        Directory.CreateDirectory(squadDir);

        // Generate a team.md that routes work based on task complexity/type.
        // Pre-populating team.md bypasses Squad's interactive Init Mode.
        var teamMd = new StringBuilder();
        teamMd.AppendLine("# Squad Team Configuration");
        teamMd.AppendLine();
        teamMd.AppendLine("## Team Members");
        teamMd.AppendLine();

        if (task.Complexity <= 2)
        {
            // Simple tasks: single developer + tester
            teamMd.AppendLine("- **Lead Developer**: Full-stack developer handling implementation");
            teamMd.AppendLine("- **Tester**: Writes and runs tests for the implementation");
        }
        else
        {
            // Complex tasks: full team
            teamMd.AppendLine("- **Lead Developer**: Coordinates implementation, handles architecture");
            if (task.IsWebTask)
                teamMd.AppendLine("- **Frontend Developer**: Handles UI/UX implementation");
            teamMd.AppendLine("- **Backend Developer**: Handles API and business logic");
            teamMd.AppendLine("- **Tester**: Writes and runs comprehensive tests");
        }

        teamMd.AppendLine();
        teamMd.AppendLine("## Routing Rules");
        teamMd.AppendLine();
        teamMd.AppendLine("- UI/frontend tasks → Frontend Developer (if available) or Lead Developer");
        teamMd.AppendLine("- API/backend tasks → Backend Developer or Lead Developer");
        teamMd.AppendLine("- Test tasks → Tester");
        teamMd.AppendLine("- Architecture decisions → Lead Developer");

        await File.WriteAllTextAsync(
            Path.Combine(squadDir, "team.md"),
            teamMd.ToString());
    }

    // ── Squad process execution ──

    private async Task<SquadProcessResult> RunSquadProcessAsync(
        string worktreePath, string prompt, TimeSpan timeout,
        List<FrameworkEvent> events, IProgress<FrameworkActivityEvent>? sink, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Write prompt to a file — copilot agent mode may not read stdin reliably
        var promptFile = Path.Combine(worktreePath, ".squad", "task-prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
        await File.WriteAllTextAsync(promptFile, prompt, ct);

        // Build the copilot command with -p flag referencing the prompt content
        // We use -p with the actual prompt text (not a file path) since copilot
        // doesn't have a file-input flag. For very long prompts, stdin is the fallback.
        var baseArgs = "--agent squad --yolo --no-ask-user --silent --no-color --no-auto-update --no-custom-instructions";

        // On Windows, CLI tools are .cmd shims that require cmd.exe
        string fileName;
        string arguments;
        if (OperatingSystem.IsWindows())
        {
            fileName = "cmd.exe";
            arguments = $"/c copilot {baseArgs}";
        }
        else
        {
            fileName = "copilot";
            arguments = baseArgs;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = worktreePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Containment note: We do NOT blank GH_TOKEN/GITHUB_TOKEN because
        // the copilot CLI itself needs GitHub auth to call the AI model API.
        // Containment is enforced by running in an isolated worktree directory.
        psi.Environment["SQUAD_DEBUG"] = "1";

        _logger.LogInformation("Starting Squad process in {Path}: {FileName} {Args}",
            worktreePath, fileName, arguments);

        using var process = Process.Start(psi);
        if (process is null)
            return SquadProcessResult.Failed("Failed to start copilot process");

        // Pipe prompt via stdin (primary delivery) and close to signal EOF
        await process.StandardInput.WriteAsync(prompt);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        // Capture stdout/stderr with stuck detection
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lastOutputTime = DateTimeOffset.UtcNow;
        var logLines = new List<string>();

        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            {
                stdout.AppendLine(line);
                lastOutputTime = DateTimeOffset.UtcNow;
                logLines.Add(line);

                // Parse real-time events from stdout and report to activity sink
                ParseStdoutLine(line, events);
                if (!string.IsNullOrWhiteSpace(line))
                    sink?.Report(new FrameworkActivityEvent("stdout", line.Trim()));
            }
        }, cts.Token);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cts.Token) is { } line)
            {
                stderr.AppendLine(line);
                lastOutputTime = DateTimeOffset.UtcNow;
            }
        }, cts.Token);

        // Stuck detection: if no output for _stuckThreshold, kill the process
        var stuckCheckTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested && !process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                if (DateTimeOffset.UtcNow - lastOutputTime > _stuckThreshold)
                {
                    _logger.LogWarning("Squad process stuck (no output for {Sec}s), killing",
                        _stuckThreshold.TotalSeconds);
                    try { process.Kill(entireProcessTree: true); } catch { }
                    break;
                }
            }
        }, cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            // Give output tasks a moment to flush
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(5000, CancellationToken.None));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return SquadProcessResult.Failed("timeout", logLines: TruncateLogLines(logLines));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // Parse metrics from stdout
        var metrics = SquadStdoutParser.ParseMetrics(stdout.ToString());

        var succeeded = process.ExitCode == 0;
        if (!succeeded)
        {
            _logger.LogWarning("Squad process exited with code {Code} for worktree {Path}. " +
                "Stdout ({StdoutLen} chars): {StdoutTail}. Stderr ({StderrLen} chars): {StderrTail}",
                process.ExitCode, worktreePath,
                stdout.Length, stdout.Length > 500 ? stdout.ToString(stdout.Length - 500, 500) : stdout.ToString(),
                stderr.Length, stderr.Length > 500 ? stderr.ToString(stderr.Length - 500, 500) : stderr.ToString());
        }
        else
        {
            _logger.LogInformation("Squad process completed successfully for worktree {Path} " +
                "({StdoutLines} stdout lines, {StderrLen} stderr chars)",
                worktreePath, logLines.Count, stderr.Length);
        }

        return new SquadProcessResult(
            Succeeded: succeeded,
            FailureReason: succeeded ? null : $"exit-code-{process.ExitCode}",
            TokensUsed: metrics.TotalTokens,
            RequestCount: metrics.RequestCount,
            SubAgentCount: events.Count(e => e.Type == FrameworkEventType.SubAgentSpawn),
            LogLines: TruncateLogLines(logLines));
    }

    // ── Stdout parsing ──

    private static void ParseStdoutLine(string line, List<FrameworkEvent> events)
    {
        // Detect sub-agent spawns: Squad logs like "Agent: <name> starting..."
        if (line.Contains("Agent:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(Evt(FrameworkEventType.SubAgentSpawn, "squad.agent", line.Trim()));
        }

        // Detect tool calls
        if (line.Contains("tool_call", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Tool:", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(Evt(FrameworkEventType.ToolCall, "squad", line.Trim()));
        }

        // Detect code generation activity
        if (line.Contains("Creating file", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Writing file", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Modifying file", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(Evt(FrameworkEventType.CodeGen, "squad", line.Trim()));
        }
    }

    // ── Post-execution file scraping ──

    private static async Task<IReadOnlyList<string>> ScrapeDecisionsAsync(string worktreePath)
    {
        var decisionsPath = Path.Combine(worktreePath, ".squad", "decisions.md");
        if (!File.Exists(decisionsPath))
            return Array.Empty<string>();

        try
        {
            var content = await File.ReadAllTextAsync(decisionsPath);
            return content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#') && l.Length > 0)
                .Take(50)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<int> CountModifiedFilesAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --name-only",
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return 0;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch
        {
            return 0;
        }
    }

    // ── Helpers ──

    private FrameworkExecutionResult FailResult(Stopwatch sw, string reason, List<FrameworkEvent> events)
    {
        lock (_lastRunEvents)
        {
            _lastRunEvents.Clear();
            _lastRunEvents.AddRange(events);
        }

        return new FrameworkExecutionResult
        {
            FrameworkId = Id,
            Succeeded = false,
            FailureReason = reason,
            Elapsed = sw.Elapsed,
            Log = Array.Empty<string>(),
        };
    }

    private static FrameworkEvent Evt(FrameworkEventType type, string agent, string desc) =>
        new(DateTimeOffset.UtcNow, type, agent, desc);

    private static IReadOnlyList<string> TruncateLogLines(List<string> lines)
    {
        const int MaxLines = 200;
        if (lines.Count <= MaxLines) return lines;
        var result = new List<string>(MaxLines + 1);
        result.AddRange(lines.Take(MaxLines / 2));
        result.Add($"… [{lines.Count - MaxLines} lines omitted] …");
        result.AddRange(lines.Skip(lines.Count - MaxLines / 2));
        return result;
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, string args, string workingDir, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // On Windows, CLI tools like squad/npm/gh are .cmd shims that require cmd.exe
        string fileName;
        string arguments;
        if (OperatingSystem.IsWindows())
        {
            fileName = "cmd.exe";
            arguments = $"/c {command} {args}";
        }
        else
        {
            fileName = command;
            arguments = args;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{command}'");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }

    // ── Inner types ──

    private sealed record SquadProcessResult(
        bool Succeeded,
        string? FailureReason,
        long? TokensUsed = null,
        int RequestCount = 0,
        int SubAgentCount = 0,
        IReadOnlyList<string>? LogLines = null)
    {
        public static SquadProcessResult Failed(string reason, IReadOnlyList<string>? logLines = null) =>
            new(false, reason, LogLines: logLines);
    }
}

/// <summary>
/// Builds the task prompt markdown sent to Squad via stdin.
/// </summary>
internal static class SquadPromptBuilder
{
    public static string Build(FrameworkInvocation invocation)
    {
        var t = invocation.Task;
        var sb = new StringBuilder();

        sb.AppendLine($"# Task: {t.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(t.TaskDescription);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(t.PmSpec))
        {
            sb.AppendLine("## Product Specification (context)");
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
            sb.AppendLine("## UI/Design Context");
            sb.AppendLine(t.DesignContext);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(t.TechStack))
        {
            sb.AppendLine($"## Tech Stack: {t.TechStack}");
            sb.AppendLine();
        }

        sb.AppendLine("## Working Directory");
        sb.AppendLine($"All work must be done in: `{invocation.WorktreePath}`");
        sb.AppendLine($"Branch: `{t.PrBranch}` (based on `{t.BaseSha}`)");
        sb.AppendLine();

        sb.AppendLine("## Constraints");
        sb.AppendLine("- Modify files ONLY inside this working directory");
        sb.AppendLine("- Do NOT create GitHub Issues, PRs, or comments");
        sb.AppendLine("- Do NOT run `git push` or any remote-mutating operation");
        sb.AppendLine("- Record all major decisions in `.squad/decisions.md`");
        sb.AppendLine("- Commits are fine and expected; keep them focused");
        sb.AppendLine("- If tests exist, keep them green. If adding tests, ensure they pass");
        sb.AppendLine("- Stop when acceptance criteria are met or no forward progress possible");

        return sb.ToString();
    }
}

/// <summary>
/// Parses Squad stdout for token metrics and request counts.
/// Handles the Copilot CLI output format:
/// <c>Tokens    ↑ {input}k · ↓ {output}k · {cached}k (cached)</c>
/// <c>Requests  {count} {tier} ({duration})</c>
/// </summary>
internal static class SquadStdoutParser
{
    // Tokens ↑ 620.4k · ↓ 3.2k · 494.7k (cached)
    private static readonly Regex TokenRegex = new(
        @"Tokens\s+↑\s*([\d.]+)k\s*·\s*↓\s*([\d.]+)k",
        RegexOptions.Compiled);

    // Requests  3 Premium (37.5s)
    private static readonly Regex RequestRegex = new(
        @"Requests\s+(\d+)\s+\w+",
        RegexOptions.Compiled);

    public static SquadMetricsSummary ParseMetrics(string stdout)
    {
        long? totalTokens = null;
        int requestCount = 0;

        foreach (var line in stdout.Split('\n'))
        {
            var tokenMatch = TokenRegex.Match(line);
            if (tokenMatch.Success)
            {
                if (double.TryParse(tokenMatch.Groups[1].Value, out var inputK) &&
                    double.TryParse(tokenMatch.Groups[2].Value, out var outputK))
                {
                    totalTokens = (long)((inputK + outputK) * 1000);
                }
            }

            var requestMatch = RequestRegex.Match(line);
            if (requestMatch.Success && int.TryParse(requestMatch.Groups[1].Value, out var count))
            {
                requestCount += count;
            }
        }

        return new SquadMetricsSummary(totalTokens, requestCount);
    }

    public record SquadMetricsSummary(long? TotalTokens, int RequestCount);
}
