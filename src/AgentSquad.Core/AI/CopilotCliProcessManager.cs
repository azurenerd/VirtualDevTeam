using System.Diagnostics;
using System.Text;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.AI;

/// <summary>
/// Manages execution of copilot CLI processes in non-interactive mode.
/// Each AI request spawns a fresh <c>copilot -p</c> process with auto-permissions.
/// Concurrency is layered: a per-pool <see cref="SemaphoreSlim"/> (SingleShot /
/// Candidate / Agentic) throttles the specific call site, then the global
/// <see cref="StrategyConcurrencyGate"/> caps total concurrent processes across
/// pools. Pool-first ordering prevents agentic slots from starving baseline.
/// </summary>
public sealed class CopilotCliProcessManager : IHostedService, IDisposable
{
    private readonly CopilotCliConfig _config;
    private readonly StrategyFrameworkConfig _frameworkConfig;
    private readonly ILogger<CopilotCliProcessManager> _logger;
    private readonly SemaphoreSlim _singleShotPool;
    private readonly SemaphoreSlim _candidatePool;
    private readonly SemaphoreSlim _agenticPool;
    private readonly StrategyConcurrencyGate _globalGate;
    private readonly CliInteractiveWatchdog _watchdog;
    private bool _copilotAvailable;
    private bool _disposed;

    public CopilotCliProcessManager(
        IOptions<AgentSquadConfig> config,
        ILogger<CopilotCliProcessManager> logger)
        : this(config, Options.Create(new StrategyFrameworkConfig()), NewDefaultGate(), logger)
    {
    }

    public CopilotCliProcessManager(
        IOptions<AgentSquadConfig> config,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        ILogger<CopilotCliProcessManager> logger)
        : this(config, frameworkConfig, NewDefaultGate(frameworkConfig.Value), logger)
    {
    }

    public CopilotCliProcessManager(
        IOptions<AgentSquadConfig> config,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        StrategyConcurrencyGate globalGate,
        ILogger<CopilotCliProcessManager> logger)
    {
        _config = config.Value.CopilotCli;
        _frameworkConfig = frameworkConfig.Value;
        _logger = logger;
        _globalGate = globalGate;

        // Per-pool sizing. SingleShot honours the legacy CopilotCli.MaxConcurrentRequests
        // when the strategy framework is off (preserves pre-existing behaviour), otherwise
        // takes the framework value. Candidate/Agentic come from the framework config.
        var concurrency = _frameworkConfig.Concurrency;
        var singleShotSize = concurrency.SingleShotSlots > 0
            ? concurrency.SingleShotSlots
            : _config.MaxConcurrentRequests;
        _singleShotPool = new SemaphoreSlim(singleShotSize, singleShotSize);
        _candidatePool = new SemaphoreSlim(
            Math.Max(1, concurrency.CandidateSlots),
            Math.Max(1, concurrency.CandidateSlots));
        _agenticPool = new SemaphoreSlim(
            Math.Max(1, concurrency.AgenticSlots),
            Math.Max(1, concurrency.AgenticSlots));

        _watchdog = new CliInteractiveWatchdog(logger, _config.AutoApprovePrompts);
    }

    private static StrategyConcurrencyGate NewDefaultGate(StrategyFrameworkConfig? cfg = null)
    {
        cfg ??= new StrategyFrameworkConfig();
        var monitor = new StaticOptionsMonitor<StrategyFrameworkConfig>(cfg);
        return new StrategyConcurrencyGate(monitor);
    }

    private sealed class StaticOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Whether the copilot CLI was detected and is available for use.</summary>
    public bool IsAvailable => _copilotAvailable && !_disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _copilotAvailable = await VerifyCopilotInstalledAsync(cancellationToken);

        if (_copilotAvailable)
            _logger.LogInformation(
                "Copilot CLI available at '{Path}'. Max concurrent requests: {Max}",
                _config.ExecutablePath, _config.MaxConcurrentRequests);
        else
            _logger.LogWarning(
                "Copilot CLI not found at '{Path}'. Agents will use API-key fallback",
                _config.ExecutablePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Execute a prompt via the copilot CLI in non-interactive mode.
    /// Spawns a fresh process, pipes the prompt via stdin, reads the response from stdout.
    /// </summary>
    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        CancellationToken ct = default)
    {
        return await ExecutePromptAsync(prompt, modelOverride: null, ct);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        CancellationToken ct = default)
    {
        return await ExecutePromptAsync(prompt, modelOverride, sessionId: null, ct);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        string? sessionId,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_copilotAvailable)
            return CopilotCliResult.Failure("Copilot CLI is not available");

        // Legacy callers land in the SingleShot pool. Acquire the per-pool slot first,
        // then the global gate — this ordering prevents a burst of agentic calls from
        // stealing every global permit and starving SingleShot. See StrategyConcurrencyGate.
        await _singleShotPool.WaitAsync(ct);
        try
        {
            using var _ = await _globalGate.AcquireAsync(ct);
            return await RunProcessAsync(prompt, modelOverride, sessionId, ct);
        }
        finally
        {
            _singleShotPool.Release();
        }
    }

    /// <summary>
    /// Pool-routed overload of <see cref="ExecutePromptAsync(string, CancellationToken)"/>.
    /// Callers that know which pool their call belongs to (e.g. strategy patch-producers
    /// routing to <see cref="CopilotCliPool.Candidate"/>) should use this overload.
    /// <see cref="CopilotCliPool.Agentic"/> is rejected — agentic sessions must go
    /// through <see cref="ExecuteAgenticSessionAsync"/> because their lifecycle differs
    /// (stdin stays open, allow-all flag, JSONL watchdog).
    /// </summary>
    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        CopilotCliRequestOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (options.Pool == CopilotCliPool.Agentic)
            throw new ArgumentException(
                "Pool=Agentic is not valid for ExecutePromptAsync. Use ExecuteAgenticSessionAsync.",
                nameof(options));

        if (!_copilotAvailable)
            return CopilotCliResult.Failure("Copilot CLI is not available");

        var pool = options.Pool switch
        {
            CopilotCliPool.Candidate => _candidatePool,
            CopilotCliPool.SingleShot => _singleShotPool,
            _ => _singleShotPool,
        };

        await pool.WaitAsync(ct);
        try
        {
            using var _ = await _globalGate.AcquireAsync(ct);
            return await RunProcessAsync(prompt, options.ModelOverride, options.SessionId, ct);
        }
        finally
        {
            pool.Release();
        }
    }

    /// <summary>
    /// Execute an agentic session via the copilot CLI (Pool=Agentic, --allow-all).
    /// Unlike <see cref="ExecutePromptAsync(string, CancellationToken)"/>, this lifecycle
    /// keeps stdin open until process exit so future watchdog responses and multi-turn
    /// stdin input work. The watchdog itself, the per-pool semaphore split, the
    /// Windows Job Object containment, and the sandbox env scrub are layered on by
    /// subsequent todos (p3-agentic-watchdog, p3-semaphore-split, p3-cleanup-impl,
    /// p3-real-sandbox). This method establishes the process-lifecycle skeleton that
    /// those todos extend without reshaping the API.
    /// </summary>
    public async Task<AgenticSessionResult> ExecuteAgenticSessionAsync(
        string prompt,
        CopilotCliRequestOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (options.Pool != CopilotCliPool.Agentic)
            throw new ArgumentException(
                $"ExecuteAgenticSessionAsync requires Pool=Agentic, got {options.Pool}",
                nameof(options));

        if (!_copilotAvailable)
            return AgenticSessionResult.Unavailable("Copilot CLI is not available");

        // Agentic calls acquire the Agentic pool first, then the global gate. The
        // per-pool semaphore bounds parallel agentic sessions (default 2); the global
        // gate bounds total concurrent processes across all pools (default 6).
        await _agenticPool.WaitAsync(ct);
        try
        {
            using var _ = await _globalGate.AcquireAsync(ct);
            return await RunAgenticSessionAsync(prompt, options, ct);
        }
        finally
        {
            _agenticPool.Release();
        }
    }

    private async Task<CopilotCliResult> RunProcessAsync(string prompt, string? modelOverride, string? sessionId, CancellationToken ct)
    {
        var argList = BuildArguments(modelOverride, sessionId);

        var psi = new ProcessStartInfo
        {
            FileName = _config.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList bypasses manual Windows command-line quoting. Essential for args
        // that may contain JSON with embedded quotes (e.g. --additional-mcp-config).
        foreach (var a in argList)
            psi.ArgumentList.Add(a);

        // Per-invocation CWD override (e.g. a candidate worktree root) takes precedence
        // over the global default. Fall back to whatever is configured globally.
        var invocation = AgentCallContext.CurrentInvocationContext;
        var workingDir = !string.IsNullOrEmpty(invocation?.OverrideWorkingDirectory)
            ? invocation!.OverrideWorkingDirectory
            : _config.WorkingDirectory;
        if (!string.IsNullOrEmpty(workingDir))
            psi.WorkingDirectory = workingDir;

        // Environment overrides
        psi.Environment["NO_COLOR"] = "1";

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start copilot process");
            return CopilotCliResult.Failure($"Failed to start copilot: {ex.Message}");
        }

        using (process)
        {
            try
            {
                // Pipe the prompt via stdin and close to signal EOF
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token);
                process.StandardInput.Close();

                // Read stdout and stderr concurrently
                var stdoutTask = ReadOutputWithWatchdogAsync(
                    process, process.StandardOutput, linked.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

                // Wait for process to exit
                await process.WaitForExitAsync(linked.Token);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Copilot process exited with code {Code}. stderr: {Stderr}",
                        process.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);

                    // Still return stdout if there's content — partial responses can be useful
                    if (!string.IsNullOrWhiteSpace(stdout))
                        return CopilotCliResult.Success(stdout, process.ExitCode);

                    return CopilotCliResult.Failure(
                        $"Copilot exited with code {process.ExitCode}: {stderr}");
                }

                return CopilotCliResult.Success(stdout, process.ExitCode);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcessSafely(process);
                return CopilotCliResult.Failure(
                    $"Copilot request timed out after {_config.RequestTimeoutSeconds}s");
            }
            catch (OperationCanceledException)
            {
                KillProcessSafely(process);
                throw; // Caller-initiated cancellation — propagate
            }
            catch (Exception ex)
            {
                KillProcessSafely(process);
                _logger.LogError(ex, "Error during copilot process execution");
                return CopilotCliResult.Failure($"Process error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Agentic lifecycle: starts copilot with --allow-all, keeps stdin open, streams
    /// stdout to a log buffer, enforces wall-clock timeout, and kills the whole
    /// process tree on cancel/timeout. Job Object containment (p3-cleanup-impl),
    /// JSONL watchdog (p3-agentic-watchdog), and sandbox env scrub (p3-real-sandbox)
    /// are layered in by subsequent todos without changing this method's signature.
    /// </summary>
    private async Task<AgenticSessionResult> RunAgenticSessionAsync(
        string prompt,
        CopilotCliRequestOptions options,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var argList = BuildAgenticArguments(options);

        var psi = new ProcessStartInfo
        {
            FileName = _config.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argList)
            psi.ArgumentList.Add(a);

        // Working directory precedence: options override > CopilotCli default.
        var workingDir = !string.IsNullOrEmpty(options.WorkingDirectory)
            ? options.WorkingDirectory
            : _config.WorkingDirectory;
        if (!string.IsNullOrEmpty(workingDir))
            psi.WorkingDirectory = workingDir;

        psi.Environment["NO_COLOR"] = "1";

        // Environment overrides. A null value removes the variable entirely — this
        // is the scrub path used by the sandbox scope (p3-real-sandbox) to strip
        // host credentials such as GITHUB_TOKEN, SSH_AUTH_SOCK, etc.
        if (options.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var (k, v) in options.EnvironmentOverrides)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (v is null)
                    psi.Environment.Remove(k);
                else
                    psi.Environment[k] = v;
            }
        }

        var wallClock = options.Timeout
            ?? TimeSpan.FromSeconds(_frameworkConfig.Timeouts.AgenticSeconds);
        using var timeoutCts = new CancellationTokenSource(wallClock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        Win32JobObject? jobObject = null;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();

            // Assign to a Job Object for atomic descendant-kill on close.
            // KILL_ON_JOB_CLOSE + BreakawayOK=false means every grandchild is
            // terminated when we dispose the job handle at the end of this
            // session — no orphaned git/node/shell processes on timeout or crash.
            // Cross-platform: on non-Windows, IsSupported returns false, jobObject
            // stays null, and we fall through to Process.Kill(entireProcessTree:true).
            if (Win32JobObject.IsSupported)
            {
                try
                {
                    jobObject = new Win32JobObject(
                        _logger,
                        _frameworkConfig.Agentic.JobObjectMemoryLimitBytes,
                        _frameworkConfig.Agentic.JobObjectActiveProcessLimit);
                    if (!jobObject.AssignProcess(process))
                    {
                        jobObject.Dispose();
                        jobObject = null;
                    }
                }
                catch (Exception jobEx)
                {
                    _logger.LogWarning(jobEx, "Failed to create/assign Job Object; agentic session will rely on tree-kill fallback");
                    jobObject?.Dispose();
                    jobObject = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start copilot agentic process");
            return AgenticSessionResult.LaunchFailed($"Failed to start copilot: {ex.Message}");
        }

        using (process)
        using (jobObject)
        {
            var logBuffer = new StringBuilder();
            // killSource lets the watchdog signal a kill request (stuck or tool-cap
            // violation) while the main lifecycle is awaiting WaitForExitAsync.
            using var killSource = new CancellationTokenSource();
            AgenticOutputMonitor? monitor = null;
            try
            {
                // Pipe prompt via stdin; flush but DO NOT close. Agentic sessions
                // keep stdin open for watchdog responses and future multi-turn input.
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token);
                await process.StandardInput.FlushAsync(linked.Token);
                if (options.CloseStdinAfterPrompt)
                    process.StandardInput.Close();

                Task<string> stdoutFallbackTask = Task.FromResult(string.Empty);
                Task monitorTask = Task.CompletedTask;
                var useMonitor = options.WatchdogMode == CopilotCliWatchdogMode.Agentic;

                if (useMonitor)
                {
                    // JSONL watchdog: stuck detector + tool-call cap. Always emitted with
                    // JSON mode (BuildAgenticArguments forces --output-format json).
                    monitor = new AgenticOutputMonitor(_frameworkConfig.Agentic, _logger, jsonMode: true);
                    monitorTask = monitor.RunAsync(process.StandardOutput, logBuffer, killSource, linked.Token);
                }
                else
                {
                    // Legacy path: raw stdout-to-string (no stuck detection, no tool-cap).
                    stdoutFallbackTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                }

                var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

                // Compose a cancellation that also reacts to watchdog kill signals.
                using var processWait = CancellationTokenSource.CreateLinkedTokenSource(
                    linked.Token, killSource.Token);

                try
                {
                    await process.WaitForExitAsync(processWait.Token);
                }
                catch (OperationCanceledException) when (killSource.IsCancellationRequested)
                {
                    // Watchdog asked us to kill. Tear down the process tree and fall
                    // through to classify the failure from the monitor's FailureReason.
                    KillProcessSafely(process);
                    try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                }

                // Drain the watchdog/stdout tasks. Swallow their exceptions — classification
                // happens on the monitor's FailureReason or the process exit code.
                try { await monitorTask; } catch { }
                string stdout = string.Empty;
                try { stdout = await stdoutFallbackTask; } catch { }
                string stderr = string.Empty;
                try { stderr = await stderrTask; } catch { }

                if (!useMonitor)
                    logBuffer.Append(stdout);
                if (!string.IsNullOrEmpty(stderr))
                    logBuffer.Append("\n---- stderr ----\n").Append(stderr);

                sw.Stop();

                // Watchdog-detected violation wins over exit-code classification — the
                // session was killed BECAUSE of the violation, so exit code is noise.
                if (monitor?.FailureReason is { } watchdogFailure)
                {
                    string errorMessage;
                    if (watchdogFailure == AgenticFailureReason.StuckNoOutput)
                    {
                        // Surface stderr tail so operators can see real failure cause
                        // (e.g. auth failure, MCP server startup crash) when the CLI
                        // hangs before emitting any stdout. Without this, the experiment
                        // record only says "stuck" with no clue what went wrong.
                        var stderrTail = string.IsNullOrEmpty(stderr)
                            ? "(no stderr)"
                            : stderr.Length > 800 ? stderr[..800] : stderr;
                        errorMessage = $"Agentic session stuck: no stdout for {_frameworkConfig.Agentic.StuckSeconds}s. stderr: {stderrTail}";
                    }
                    else
                    {
                        errorMessage = $"Agentic session exceeded tool-call cap of {_frameworkConfig.Agentic.ToolCallCap}";
                    }
                    return new AgenticSessionResult
                    {
                        Succeeded = false,
                        FailureReason = watchdogFailure,
                        ExitCode = process.HasExited ? process.ExitCode : -1,
                        WallClock = sw.Elapsed,
                        ToolCallCount = monitor.ToolCallCount,
                        LogBuffer = logBuffer.ToString(),
                        ErrorMessage = errorMessage,
                    };
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Agentic copilot process exited with code {Code}. stderr: {Stderr}",
                        process.ExitCode,
                        stderr.Length > 500 ? stderr[..500] : stderr);
                    return new AgenticSessionResult
                    {
                        Succeeded = false,
                        FailureReason = AgenticFailureReason.ExitNonzero,
                        ExitCode = process.ExitCode,
                        WallClock = sw.Elapsed,
                        ToolCallCount = monitor?.ToolCallCount ?? 0,
                        LogBuffer = logBuffer.ToString(),
                        ErrorMessage = $"Copilot exited with code {process.ExitCode}",
                    };
                }

                return new AgenticSessionResult
                {
                    Succeeded = true,
                    FailureReason = AgenticFailureReason.None,
                    ExitCode = process.ExitCode,
                    WallClock = sw.Elapsed,
                    ToolCallCount = monitor?.ToolCallCount ?? 0,
                    LogBuffer = logBuffer.ToString(),
                };
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcessSafely(process);
                sw.Stop();
                return new AgenticSessionResult
                {
                    Succeeded = false,
                    FailureReason = AgenticFailureReason.Timeout,
                    ExitCode = -1,
                    WallClock = sw.Elapsed,
                    ToolCallCount = monitor?.ToolCallCount ?? 0,
                    LogBuffer = logBuffer.ToString(),
                    ErrorMessage = $"Agentic session timed out after {wallClock.TotalSeconds:F0}s",
                };
            }
            catch (OperationCanceledException)
            {
                KillProcessSafely(process);
                sw.Stop();
                return new AgenticSessionResult
                {
                    Succeeded = false,
                    FailureReason = AgenticFailureReason.Canceled,
                    ExitCode = -1,
                    WallClock = sw.Elapsed,
                    ToolCallCount = monitor?.ToolCallCount ?? 0,
                    LogBuffer = logBuffer.ToString(),
                    ErrorMessage = "Caller cancelled the agentic session",
                };
            }
            catch (Exception ex)
            {
                KillProcessSafely(process);
                sw.Stop();
                _logger.LogError(ex, "Error during agentic copilot process execution");
                return new AgenticSessionResult
                {
                    Succeeded = false,
                    FailureReason = AgenticFailureReason.LaunchFailed,
                    ExitCode = -1,
                    WallClock = sw.Elapsed,
                    ToolCallCount = monitor?.ToolCallCount ?? 0,
                    LogBuffer = logBuffer.ToString(),
                    ErrorMessage = $"Process error: {ex.Message}",
                };
            }
        }
    }

    /// <summary>
    /// Build argv for an agentic session. Layers over <see cref="BuildArguments"/> by
    /// prepending <c>--allow-all</c> when <see cref="CopilotCliRequestOptions.AllowAll"/>
    /// is true and forcing JSON output mode so the watchdog (p3-agentic-watchdog) can
    /// count tool-call events reliably.
    /// </summary>
    internal IReadOnlyList<string> BuildAgenticArguments(CopilotCliRequestOptions options)
    {
        var baseArgs = BuildArguments(options.ModelOverride, options.SessionId);
        var args = new List<string>();

        if (options.AllowAll)
            args.Add("--allow-all");

        var jsonAlreadyPresent = false;
        for (var i = 0; i < baseArgs.Count; i++)
        {
            var a = baseArgs[i];
            args.Add(a);
            if (a == "--output-format" && i + 1 < baseArgs.Count && baseArgs[i + 1] == "json")
                jsonAlreadyPresent = true;
        }

        if (!jsonAlreadyPresent)
        {
            args.Add("--output-format");
            args.Add("json");
        }

        return args;
    }

    /// <summary>
    /// Reads stdout while monitoring for interactive prompts via the watchdog.
    /// If a prompt is detected, auto-responds via stdin.
    /// </summary>
    private async Task<string> ReadOutputWithWatchdogAsync(
        Process process, StreamReader stdout, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            var readTask = stdout.ReadAsync(buffer, ct);
            int charsRead;

            try
            {
                charsRead = await readTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (charsRead == 0)
                break; // EOF

            var chunk = new string(buffer, 0, charsRead);
            output.Append(chunk);

            // Check each line in the chunk for interactive prompts
            var lines = chunk.Split('\n');
            foreach (var line in lines)
            {
                var action = _watchdog.DetectPrompt(line);
                if (action == null) continue;

                if (action.Type == WatchdogActionType.FailFast)
                {
                    _logger.LogError("Watchdog fail-fast: {Reason}", action.Reason);
                    KillProcessSafely(process);
                    throw new CopilotCliException(action.Reason);
                }

                if (action.Type == WatchdogActionType.Respond && !process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync(
                            action.Response.AsMemory(), ct);
                        await process.StandardInput.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to write watchdog response to stdin");
                    }
                }
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Build the argv for a single CLI invocation. Returns a pre-tokenised list; each
    /// element becomes one argv entry with no further shell interpretation. This is the
    /// ONLY safe way to pass inline JSON (for <c>--additional-mcp-config</c>) on Windows.
    /// </summary>
    /// <remarks>
    /// Per-invocation values (inline MCP config, allow-tool permissions, override CWD)
    /// are read from <see cref="AgentCallContext.CurrentInvocationContext"/>. The method
    /// is <c>internal</c> so strategy-framework tests can assert the emitted argv without
    /// having to spawn a real CLI process.
    /// </remarks>
    internal IReadOnlyList<string> BuildArguments(string? modelOverride = null, string? sessionId = null)
    {
        var args = new List<string>();

        // Core flags for non-interactive autonomous operation.
        // NOTE: --allow-all is intentionally omitted. For strategies that need to invoke
        // read-only MCP tools, explicit --allow-tool flags are emitted below via the
        // per-invocation context — this is tighter than --allow-all, which would also
        // permit filesystem writes and shell execution.
        args.Add("--no-ask-user");
        args.Add("--no-auto-update");
        args.Add("--no-custom-instructions");

        // Session resume for conversational continuity across calls.
        if (!string.IsNullOrEmpty(sessionId))
            args.Add($"--resume={sessionId}");

        if (_config.SilentMode)
            args.Add("--silent");

        args.Add("--no-color");

        if (_config.JsonOutput)
        {
            args.Add("--output-format");
            args.Add("json");
        }

        // Model selection (per-agent override takes precedence).
        var model = modelOverride ?? _config.ModelName;
        args.Add("--model");
        args.Add(model);

        // Reasoning effort (skip for models that don't support it, e.g. haiku).
        var effectiveModel = modelOverride ?? _config.ModelName;
        var supportsReasoning = effectiveModel == null
            || !effectiveModel.Contains("haiku", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_config.ReasoningEffort) && supportsReasoning)
        {
            args.Add("--effort");
            args.Add(_config.ReasoningEffort);
        }

        // Excluded tools.
        foreach (var tool in _config.ExcludedTools)
        {
            args.Add("--excluded-tools");
            args.Add(tool);
        }

        // Legacy free-form additional args. Passed verbatim as a single argv entry when
        // safe; rejected if the value contains quote/escape characters we cannot
        // faithfully reproduce without a real shell tokeniser. New callers should use
        // AdditionalArgList instead.
        if (!string.IsNullOrEmpty(_config.AdditionalArgs))
        {
            if (ContainsShellEscapeChars(_config.AdditionalArgs))
            {
                throw new InvalidOperationException(
                    "CopilotCli.AdditionalArgs contains quote or backslash-quote characters " +
                    "that cannot be safely tokenised without shell semantics. Migrate to " +
                    "CopilotCli.AdditionalArgList (string[]) — each list entry is passed as " +
                    "a single argv element with no further interpretation.");
            }
            // No embedded quotes; safe to split on whitespace to mirror prior single-string
            // semantics under Windows command-line parsing for the quote-free case.
            foreach (var tok in _config.AdditionalArgs.Split(
                         (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add(tok);
            }
        }

        // Pre-tokenised additional args (preferred API).
        foreach (var a in _config.AdditionalArgList)
        {
            if (!string.IsNullOrEmpty(a))
                args.Add(a);
        }

        // MCP servers referenced by name from the agent-role config (legacy path).
        var mcpServers = AgentCallContext.McpServers;
        if (mcpServers is { Count: > 0 })
        {
            foreach (var server in mcpServers)
            {
                if (!string.IsNullOrWhiteSpace(server))
                {
                    args.Add("--mcp-server");
                    args.Add(server);
                }
            }
        }

        // Per-invocation MCP additions (inline config + tool permissions). These come
        // from strategies that opt into workspace-reader or similar scoped servers;
        // they flow via AsyncLocal so the entire call chain — ProcessManager AND the
        // chat completion service's prompt flattener — sees a consistent state.
        var invocation = AgentCallContext.CurrentInvocationContext;
        if (invocation is not null)
        {
            if (!string.IsNullOrEmpty(invocation.AdditionalMcpConfigJson))
            {
                args.Add("--additional-mcp-config");
                args.Add(invocation.AdditionalMcpConfigJson);
            }

            if (invocation.AllowedMcpTools is { Count: > 0 })
            {
                foreach (var tool in invocation.AllowedMcpTools)
                {
                    if (!string.IsNullOrWhiteSpace(tool))
                        args.Add($"--allow-tool={tool}");
                }
            }
        }

        return args;
    }

    private static bool ContainsShellEscapeChars(string s)
    {
        foreach (var c in s)
        {
            if (c == '"' || c == '\'' || c == '\\' || c == '`')
                return true;
        }
        return false;
    }

    private async Task<bool> VerifyCopilotInstalledAsync(CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = psi };
            process.Start();

            // Use WaitForExit with a timeout rather than async + CancellationToken,
            // because cancelling ReadToEndAsync doesn't kill the process, and
            // Process.Dispose() blocks waiting for exit — causing a deadlock.
            var exited = process.WaitForExit(TimeSpan.FromSeconds(10));
            if (!exited)
            {
                _logger.LogWarning("Copilot CLI verification timed out after 10s — treating as unavailable");
                KillProcessSafely(process);
                return false;
            }

            if (process.ExitCode == 0)
            {
                var output = process.StandardOutput.ReadToEnd();
                _logger.LogDebug("Copilot CLI version: {Version}", output.Trim());
                return true;
            }

            _logger.LogDebug("copilot --version exited with code {Code}", process.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copilot CLI not found at '{Path}'", _config.ExecutablePath);
            if (process is { HasExited: false })
                KillProcessSafely(process);
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void KillProcessSafely(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing copilot process");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _singleShotPool.Dispose();
        _candidatePool.Dispose();
        _agenticPool.Dispose();
    }
}

/// <summary>Result of a copilot CLI execution.</summary>
public class CopilotCliResult
{
    public bool IsSuccess { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public int ExitCode { get; init; }

    public static CopilotCliResult Success(string output, int exitCode = 0) =>
        new() { IsSuccess = true, Output = output, ExitCode = exitCode };

    public static CopilotCliResult Failure(string error) =>
        new() { IsSuccess = false, Error = error, ExitCode = -1 };
}

/// <summary>Thrown when the copilot CLI encounters an unrecoverable error (e.g., credential prompt).</summary>
public class CopilotCliException : Exception
{
    public CopilotCliException(string message) : base(message) { }
    public CopilotCliException(string message, Exception inner) : base(message, inner) { }
}
