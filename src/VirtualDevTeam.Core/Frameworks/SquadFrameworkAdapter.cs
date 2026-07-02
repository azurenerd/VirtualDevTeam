using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Frameworks;

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
    private readonly TimeSpan _stuckThreshold;
    private readonly VirtualDevTeam.Core.AI.RunnerProcessJob? _runnerJob;

    // Telemetry events captured during the most recent execution (for snapshot queries).
    private readonly List<FrameworkEvent> _lastRunEvents = new();

    public SquadFrameworkAdapter(
        ILogger<SquadFrameworkAdapter> logger,
        SquadReadinessChecker readiness,
        IOptions<StrategyFrameworkConfig> strategyConfig,
        VirtualDevTeam.Core.AI.RunnerProcessJob? runnerJob = null,
        VirtualDevTeam.Core.AI.IAzureImageAuthProvider? imageAuth = null)
    {
        _logger = logger;
        _readiness = readiness;
        _runnerJob = runnerJob;
        _imageAuth = imageAuth;
        // StuckSeconds <= 0 means disabled (no stuck detection).
        // Squad sub-agents can be silent for 10+ minutes while working internally.
        var stuckSec = strategyConfig.Value.Agentic.StuckSeconds;
        _stuckThreshold = stuckSec > 0
            ? TimeSpan.FromSeconds(stuckSec)
            : TimeSpan.MaxValue; // effectively disabled
    }

    /// <summary>
    /// Image-gen credentials (endpoint + api key OR bearer + deployments list) injected into
    /// every squad-spawned Copilot CLI session so the agent can call the Azure OpenAI REST
    /// endpoint directly from its shell tool. Optional — null disables image-gen for squad runs.
    /// </summary>
    private readonly VirtualDevTeam.Core.AI.IAzureImageAuthProvider? _imageAuth;

    private void ApplyImageGenEnvVars(ProcessStartInfo psi, CancellationToken ct)
    {
        if (_imageAuth is null) return;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            var env = _imageAuth.GetEnvironmentForChildProcessAsync(timeoutCts.Token).GetAwaiter().GetResult();
            if (env is null) return;
            foreach (var (k, v) in env)
            {
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;
                psi.Environment[k] = v;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Squad: failed to inject image-gen env vars (non-fatal)");
        }
    }

    // ── IAgenticFrameworkAdapter ──

    public string Id => "squad";
    public string DisplayName => "Squad";
    public string Description => "Multi-agent team coordination via Squad framework (Brady Gaster)";
    public TimeSpan DefaultTimeout => Timeout.InfiniteTimeSpan;
    public bool SupportsRevision => true;

    public async Task<FrameworkExecutionResult> ExecuteRevisionAsync(
        FrameworkRevisionInvocation invocation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<FrameworkEvent>();
        var sink = invocation.ActivitySink;

        try
        {
            // Squad revision: re-use the same process but with a focused revision prompt.
            // No init/team-config needed — the worktree already has .squad/ from initial run.
            events.Add(Evt(FrameworkEventType.Decision, "squad", "Starting surgical revision"));
            sink?.Report(new FrameworkActivityEvent("revision", "Starting Squad surgical revision"));

            var prompt = SquadPromptBuilder.BuildRevision(invocation);

            events.Add(Evt(FrameworkEventType.SubAgentSpawn, "squad.coordinator",
                "Launching Squad revision via copilot --agent squad --yolo"));
            sink?.Report(new FrameworkActivityEvent("spawn", "Launching Squad revision session"));

            var execResult = await RunSquadProcessAsync(
                invocation.WorktreePath, prompt, invocation.Timeout, events, sink,
                invocation.BaseSha, ct);

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
            _logger.LogError(ex, "Squad revision threw for task {Task}", invocation.TaskId);
            return FailResult(sw, $"squad-revision-exception: {ex.GetType().Name}: {ex.Message}", events);
        }
    }

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
                invocation.WorktreePath, prompt, invocation.Timeout, events, sink,
                invocation.Task.BaseSha, ct);

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
        List<FrameworkEvent> events, IProgress<FrameworkActivityEvent>? sink,
        string? baseSha, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            cts.CancelAfter(timeout);

        // Write prompt to a file — copilot agent mode may not read stdin reliably
        var promptFile = Path.Combine(worktreePath, ".squad", "task-prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
        await File.WriteAllTextAsync(promptFile, prompt, ct);

        // Build the copilot command with -p flag referencing the prompt content
        // We use -p with the actual prompt text (not a file path) since copilot
        // doesn't have a file-input flag. For very long prompts, stdin is the fallback.
        // Allow custom instructions so the CLI reads the target project's
        // .github/copilot-instructions.md. CWD for squad sessions is always
        // the candidate worktree (target project), not VDT.
        var baseArgs = "--agent squad --yolo --no-ask-user --silent --no-color --no-auto-update";

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

        // Image-gen REST credentials so squad-spawned agents can produce binary art
        // assets directly from their shell tool (replaces the prior MCP wrapper approach).
        ApplyImageGenEnvVars(psi, CancellationToken.None);

        _logger.LogInformation("Starting Squad process in {Path}: {FileName} {Args}",
            worktreePath, fileName, arguments);

        // Live artifact watcher — emits FrameworkActivityEvent for each new PNG/JSON/etc
        // Squad writes to the worktree. Lets the operator watch image-gen tasks land
        // assets in real time on the Frameworks dashboard (2026-05-12 ask).
        await using var artifactWatcher = new CandidateArtifactWatcher(_logger);
        artifactWatcher.Start(worktreePath, sink, ct);

        using var process = Process.Start(psi);
        if (process is null)
            return SquadProcessResult.Failed("Failed to start copilot process");

        // Assign to the runner-scoped Job Object so the entire Squad process tree
        // (cmd → copilot → node MCPs) dies atomically when the runner exits. Job
        // Object propagation captures all descendants by default.
        _runnerJob?.Assign(process);

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
        // When _stuckThreshold == TimeSpan.MaxValue, skip stuck detection entirely.
        var stuckCheckTask = _stuckThreshold == TimeSpan.MaxValue
            ? Task.CompletedTask
            : Task.Run(async () =>
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

        // CLEANUP-RACE LAYER 2 (2026-05-12): drain descendant processes (MCP servers,
        // Python sprite-generators, etc.) that Squad spawned. These can briefly outlive
        // the parent and hold file locks on .git/worktrees/{name}/, causing the
        // subsequent `git worktree remove` to retry-and-partially-fail — wiping working-
        // tree contents (lost candidate work) but failing to remove the dir itself.
        // The grace delay lets OS file handles release before cleanup attempts.
        try
        {
            if (_runnerJob is not null)
                await _runnerJob.WaitForDescendantsAsync(process.Id, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Squad descendant drain raised — proceeding with cleanup anyway");
        }

        // Parse metrics from stdout
        var metrics = SquadStdoutParser.ParseMetrics(stdout.ToString());

        var rawSucceeded = process.ExitCode == 0;
        // post-run-squad-crash-retry: classify Windows runtime crashes distinctly from regular
        // failures so the FrameworkExecutionGate post-message and dashboard activity events can
        // differentiate "task failed" (LLM/agent error, partial output may be useful) from
        // "process crashed" (memory corruption, partial output should be discarded). Observed
        // 2026-05-10: squad exited -1073740791 (STATUS_STACK_BUFFER_OVERRUN) on T-FINAL after
        // 12s with 1 file modified — partial output is meaningless. The orchestrator's
        // existing parallel-strategy + legacy-fallback already handles this correctly because
        // copilot-cli ran in parallel and succeeded; the only gap was diagnostic clarity.
        var crashKind = ClassifyWindowsRuntimeCrash(process.ExitCode);

        // squad-exit-code-minus1-loses-real-output: soft-treat non-crash non-zero exits as
        // success when the worktree has post-base committable changes. Observed 2026-05-12:
        // squad exited -1 (NOT a recognized Windows runtime crash) AFTER successfully producing
        // 4 high-quality 400-500 KB PNG sprites. The hard exit-code-0-only gate discarded the
        // real output and let a sibling copilot-cli candidate win with Pillow-stub placeholders.
        // The judge can re-rank based on actual deliverable quality once the partial output is
        // preserved. Runtime crashes still hard-fail (memory corruption -> partial output is
        // untrustworthy); only plain non-zero exits get the soft-treatment.
        var hasCommittableChanges = false;
        var partialOutputNote = "";
        if (!rawSucceeded && crashKind is null)
        {
            try
            {
                hasCommittableChanges = await HasPostBaseCommittableChangesAsync(worktreePath, baseSha, CancellationToken.None);
                if (hasCommittableChanges)
                {
                    partialOutputNote = $"soft-success: exit-code-{process.ExitCode} but worktree has committable changes — judge will rank on output quality";
                    _logger.LogWarning(
                        "Squad exited non-zero (code {Code}) but worktree {Path} has committable changes — promoting to soft-success so the judge can evaluate partial output (was: hard-failure).",
                        process.ExitCode, worktreePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HasPostBaseCommittableChangesAsync probe failed for {Path}; treating as hard failure", worktreePath);
            }
        }

        var succeeded = rawSucceeded || hasCommittableChanges;
        var failureReason = succeeded
            ? (hasCommittableChanges && !rawSucceeded ? partialOutputNote : null)
            : crashKind is not null
                ? $"runtime-crash:{crashKind}:{process.ExitCode}"
                : $"exit-code-{process.ExitCode}";

        if (!succeeded)
        {
            if (crashKind is not null)
            {
                _logger.LogWarning(
                    "Squad process CRASHED with {Crash} (code {Code}) for worktree {Path}. " +
                    "Partial output should be ignored. Stdout ({StdoutLen} chars): {StdoutTail}. Stderr ({StderrLen} chars): {StderrTail}",
                    crashKind, process.ExitCode, worktreePath,
                    stdout.Length, stdout.Length > 500 ? stdout.ToString(stdout.Length - 500, 500) : stdout.ToString(),
                    stderr.Length, stderr.Length > 500 ? stderr.ToString(stderr.Length - 500, 500) : stderr.ToString());
            }
            else
            {
                _logger.LogWarning("Squad process exited with code {Code} for worktree {Path}. " +
                    "Stdout ({StdoutLen} chars): {StdoutTail}. Stderr ({StderrLen} chars): {StderrTail}",
                    process.ExitCode, worktreePath,
                    stdout.Length, stdout.Length > 500 ? stdout.ToString(stdout.Length - 500, 500) : stdout.ToString(),
                    stderr.Length, stderr.Length > 500 ? stderr.ToString(stderr.Length - 500, 500) : stderr.ToString());
            }
        }
        else
        {
            // Distinguish hard-success (exit 0) from soft-success (non-zero exit + committable changes).
            if (rawSucceeded)
            {
                _logger.LogInformation("Squad process completed successfully for worktree {Path} " +
                    "({StdoutLines} stdout lines, {StderrLen} stderr chars)",
                    worktreePath, logLines.Count, stderr.Length);
            }
            else
            {
                _logger.LogWarning(
                    "Squad process exited NON-ZERO (code {Code}) but the worktree {Path} has committable changes — " +
                    "treating as soft-success so the judge can evaluate partial output. " +
                    "Stdout ({StdoutLen} chars): {StdoutTail}",
                    process.ExitCode, worktreePath,
                    stdout.Length, stdout.Length > 500 ? stdout.ToString(stdout.Length - 500, 500) : stdout.ToString());
            }
        }

        return new SquadProcessResult(
            Succeeded: succeeded,
            FailureReason: failureReason,
            TokensUsed: metrics.TotalTokens,
            RequestCount: metrics.RequestCount,
            SubAgentCount: events.Count(e => e.Type == FrameworkEventType.SubAgentSpawn),
            LogLines: TruncateLogLines(logLines));
    }

    /// <summary>
    /// Maps a process exit code to a Windows runtime-crash category, or null if not a recognized
    /// crash. Used to differentiate "framework crashed" from "framework failed" in logs and gates.
    /// </summary>
    private static string? ClassifyWindowsRuntimeCrash(int exitCode) => exitCode switch
    {
        -1073740791 => "STATUS_STACK_BUFFER_OVERRUN",     // 0xC0000409
        -1073741819 => "STATUS_ACCESS_VIOLATION",         // 0xC0000005
        -1073741676 => "STATUS_INTEGER_DIVIDE_BY_ZERO",   // 0xC0000094
        -1073741571 => "STATUS_STACK_OVERFLOW",           // 0xC00000FD
        -1073741670 => "STATUS_NO_MEMORY",                // 0xC0000017
        _ => null,
    };

    /// <summary>
    /// Probes whether <paramref name="worktreePath"/> has tracked or untracked changes vs its
    /// upstream base — i.e. would <c>git diff</c> + <c>git status</c> show any committable files? Used by
    /// the squad-exit-code-minus1-loses-real-output soft-success path: a non-zero exit code is
    /// not enough to discard the run if the agent actually produced files on disk. Returns false
    /// on any error (best-effort probe — caller treats failure as "no committable changes").
    /// </summary>
    /// <remarks>
    /// Checks TWO sources of changes:
    /// 1. Uncommitted changes via <c>git status --porcelain</c>
    /// 2. Committed changes via <c>git rev-list --count baseSha..HEAD</c> — Squad (and copilot CLI)
    ///    often commit their work before exiting, leaving the working tree CLEAN. Without this
    ///    check, a Squad run that committed 52 files + passed all tests + verified all scenarios
    ///    would be discarded as a hard failure just because <c>git status</c> is empty.
    /// </remarks>
    private static async Task<bool> HasPostBaseCommittableChangesAsync(
        string worktreePath, string? baseSha, CancellationToken ct)
    {
        // Check 1: uncommitted changes (original path)
        var (exit, output) = await RunCommandAsync(
            "git",
            "status --porcelain --untracked-files=normal",
            worktreePath,
            TimeSpan.FromSeconds(30),
            ct);
        if (exit == 0)
        {
            foreach (var line in output.Split('\n', '\r'))
            {
                if (!string.IsNullOrWhiteSpace(line)) return true;
            }
        }

        // Check 2: committed changes since base SHA — catches the case where Squad
        // committed all its work (git status is clean but HEAD has moved forward).
        if (!string.IsNullOrWhiteSpace(baseSha))
        {
            var (revExit, revOutput) = await RunCommandAsync(
                "git",
                $"rev-list --count {baseSha}..HEAD",
                worktreePath,
                TimeSpan.FromSeconds(15),
                ct);
            if (revExit == 0 && int.TryParse(revOutput.Trim(), out var commitCount) && commitCount > 0)
                return true;
        }

        return false;
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

    /// <summary>
    /// Counts files this candidate has changed vs the worktree's base SHA, INCLUDING
    /// untracked files. Squad generated PNGs as untracked working-tree files in
    /// 2026-05-12; the original "git diff --name-only" implementation reported only
    /// 5 modified files when the worktree actually held 30+ generated PNG assets.
    /// `git status --porcelain --untracked-files=all` is the only command that gives
    /// the truthful count of all candidate-produced files (modified + untracked + deleted).
    /// </summary>
    private static async Task<int> CountModifiedFilesAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain --untracked-files=all",
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
        if (timeout != Timeout.InfiniteTimeSpan)
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

        if (!string.IsNullOrWhiteSpace(t.ExistingProjectContext))
        {
            sb.AppendLine("> ⚠️ **WARNING: This is an EXISTING project with working code. You are EXTENDING it, not creating it from scratch.**");
            sb.AppendLine("> Do NOT scaffold new project files (Program.cs, .csproj, package.json, tsconfig) if they already exist.");
            sb.AppendLine("> Read existing code FIRST, then make surgical additions that fit the existing architecture.");
            sb.AppendLine();
        }

        sb.AppendLine($"# Task: {t.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine(t.TaskDescription);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(t.ExistingProjectContext))
        {
            sb.AppendLine("## Existing Project Context");
            sb.AppendLine("This task is part of an EXISTING project. The following summary describes the project's current state, structure, patterns, and conventions. **You MUST align your implementation with these existing patterns.**");
            sb.AppendLine();
            sb.AppendLine(t.ExistingProjectContext);
            sb.AppendLine();
        }

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

        sb.AppendLine("## Methodology (CRITICAL — follow this order)");
        sb.AppendLine("**BEFORE writing any code**, you MUST explore the existing project:");
        sb.AppendLine("1. **Read the project structure** — list the top-level directory, understand the layout");
        sb.AppendLine("2. **Read existing related files** — find files related to your task (grep for relevant keywords, read imports, understand patterns already in use)");
        sb.AppendLine("3. **Understand build/test setup** — check package.json scripts, .csproj files, Makefile, etc. to know how to build and test");
        sb.AppendLine("4. **Match existing patterns** — use the same naming conventions, file organization, import style, error handling, and architectural patterns as the existing code");
        sb.AppendLine("5. **Then implement** — write code that fits naturally into the existing project, not code that looks like a standalone example");
        sb.AppendLine("6. **Build and test** — run the project's build command and verify your changes compile and tests pass before finishing");
        sb.AppendLine();
        sb.AppendLine("Common mistakes to avoid:");
        sb.AppendLine("- Do NOT generate boilerplate project scaffolding (Program.cs, package.json, tsconfig.json) if these files already exist — read and extend them");
        sb.AppendLine("- Do NOT use different frameworks/libraries than what the project already uses (e.g., don't add Express if the project uses Fastify)");
        sb.AppendLine("- Do NOT create files in the wrong directory — check where similar files live first");
        sb.AppendLine("- Do NOT ignore existing type definitions, interfaces, or shared utilities — import and reuse them");
        sb.AppendLine();

        sb.AppendLine("## Constraints");
        sb.AppendLine("- Modify files ONLY inside this working directory");
        sb.AppendLine("- Do NOT create GitHub Issues, PRs, or comments");
        sb.AppendLine("- Do NOT run `git push` or any remote-mutating operation");
        sb.AppendLine("- Record all major decisions in `.squad/decisions.md`");
        sb.AppendLine("- Commits are fine and expected; keep them focused");
        sb.AppendLine("- If tests exist, keep them green. If adding tests, ensure they pass");
        sb.AppendLine("- **Parallelize where possible**: run independent commands simultaneously (e.g., backend build + frontend install, multiple test suites). Don't wait for one to finish before starting the next.");
        sb.AppendLine("- Stop when acceptance criteria are met or no forward progress possible");
        sb.AppendLine();
        sb.AppendLine("## Per-Entity Commit Cadence (CRITICAL for resilience)");
        sb.AppendLine("If this task produces multiple independent deliverables (e.g. one PNG/JSON pair per game entity, one component file per UI feature, one config block per data source), commit AFTER EACH DELIVERABLE COMPLETES. Do NOT batch all deliverables into a single end-of-run commit.");
        sb.AppendLine();
        sb.AppendLine("Why this matters: if your session crashes, runs out of tool calls, or is killed for timeout while you're in the middle of entity 3 of 4, the framework will only preserve work that has been COMMITTED. Per-entity commits give the operator partial credit for partial completion. The framework's pre-cleanup auto-commit (added 2026-05-12) is a safety net; explicit per-entity commits are the primary durability mechanism.");
        sb.AppendLine();
        sb.AppendLine("Commit message convention:");
        sb.AppendLine("- `feat({entity}): {what was produced}` (e.g. `feat(goblin): add walk + die animation frames`)");
        sb.AppendLine("- One commit per entity (or per logical batch of < 30 files). Don't cram 4 entities into one commit.");

        return sb.ToString();
    }

    public static string BuildRevision(FrameworkRevisionInvocation invocation)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Surgical Revision — Targeted Fixes Only");
        sb.AppendLine();
        sb.AppendLine($"**Task:** {invocation.TaskTitle}");
        sb.AppendLine();

        sb.AppendLine("## Working Directory");
        sb.AppendLine($"All work must be done in: `{invocation.WorktreePath}`");
        sb.AppendLine();

        // Scores
        sb.AppendLine("## Current Scores (0-10):");
        foreach (var (axis, score) in invocation.InitialScores)
            sb.AppendLine($"- **{axis}**: {score}/10");
        sb.AppendLine();

        // Judge feedback
        if (!string.IsNullOrWhiteSpace(invocation.JudgeFeedback))
        {
            sb.AppendLine("## Judge Feedback (primary — fix these issues):");
            sb.AppendLine(invocation.JudgeFeedback);
            sb.AppendLine();
        }

        // Per-axis feedback
        var axisFeedback = new (string axis, string? feedback)[]
        {
            ("Acceptance Criteria", invocation.AcFeedback),
            ("Design", invocation.DesignFeedback),
            ("Readability", invocation.ReadabilityFeedback),
            ("Visuals", invocation.VisualsFeedback),
        };
        if (axisFeedback.Any(f => !string.IsNullOrWhiteSpace(f.feedback)))
        {
            sb.AppendLine("## Per-Axis Feedback:");
            foreach (var (axis, feedback) in axisFeedback)
            {
                if (!string.IsNullOrWhiteSpace(feedback))
                    sb.AppendLine($"### {axis}\n{feedback}\n");
            }
        }

        // Rubber-duck critique
        if (!string.IsNullOrWhiteSpace(invocation.RubberDuckFeedback))
        {
            sb.AppendLine("## Independent Critique (second opinion):");
            sb.AppendLine(invocation.RubberDuckFeedback);
            sb.AppendLine();
        }

        // File hints
        if (invocation.OriginalFiles.Count > 0)
        {
            sb.AppendLine("## Files from initial implementation (focus here):");
            foreach (var file in invocation.OriginalFiles.Take(20))
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }

        // Instructions
        sb.AppendLine("## Constraints");
        sb.AppendLine("- Modify files ONLY inside this working directory");
        sb.AppendLine("- Do NOT create GitHub Issues, PRs, or comments");
        sb.AppendLine("- Do NOT run `git push` or any remote-mutating operation");
        sb.AppendLine("- Make ONLY targeted fixes for the specific issues in the feedback");
        sb.AppendLine("- Do NOT rewrite, restructure, or regenerate files from scratch");
        sb.AppendLine("- Do NOT add new features unless explicitly requested in feedback");
        sb.AppendLine("- Focus on raising the lowest-scoring axes");
        sb.AppendLine("- Commit your changes when done");
        sb.AppendLine();
        sb.AppendLine("## Begin");
        sb.AppendLine("Read the existing files, apply surgical fixes from the feedback, then commit.");

        return sb.ToString();
    }
}

/// <summary>
/// Parses Squad / Copilot-CLI stdout for token metrics and request counts.
/// Handles the Copilot CLI session summary format that both framework adapters
/// rely on:
/// <c>Tokens    ↑ {input}k · ↓ {output}k · {cached}k (cached)</c>
/// <c>Requests  {count} {tier} ({duration})</c>
/// Public so the AgenticDelegationStrategy can reuse it for copilot-cli token
/// extraction (same CLI, same output format).
/// </summary>
public static class SquadStdoutParser
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
