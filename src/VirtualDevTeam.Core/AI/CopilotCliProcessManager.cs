using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Frameworks;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.AI;

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
    private readonly CopilotCliConfig _initialConfig;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _configMonitor;
    private readonly StrategyFrameworkConfig _frameworkConfig;
    private readonly ILogger<CopilotCliProcessManager> _logger;
    private readonly RunnerProcessJob? _runnerJob;
    /// <summary>
    /// Optional. When present, every spawned Copilot CLI process gets the Azure OpenAI image-gen
    /// env vars (endpoint + api-version + deployments + api-key OR bearer) so prompts can issue
    /// REST calls directly. Replaces the prior MCP-wrapper approach which failed to bind in
    /// piped-stdin CLI sessions. Null disables image gen for child processes.
    /// </summary>
    private readonly IAzureImageAuthProvider? _imageAuth;
    private readonly AgentCliLogService? _agentLogService;
    private readonly SemaphoreSlim _singleShotPool;
    private readonly SemaphoreSlim _candidatePool;
    private readonly SemaphoreSlim _agenticPool;
    private readonly SemaphoreSlim _reviewPool;
    private readonly StrategyConcurrencyGate _globalGate;
    private readonly CliInteractiveWatchdog _watchdog;
    /// <summary>
    /// Circuit breaker for the wrapper command. After consecutive
    /// startup failures (session never produced output), the wrapper is bypassed and
    /// copilot is called directly. Auto-recovers via half-open probe after cooldown.
    /// </summary>
    private readonly WrapperCircuitBreaker _wrapperBreaker = new();
    /// <summary>
    /// Tracks all copilot processes spawned by this manager. On shutdown/dispose,
    /// every tracked process is killed to prevent orphaned Electron processes from
    /// leaking 200-400MB each. Only processes WE start are tracked — never system-wide scans.
    /// </summary>
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();
    private static readonly string ProcessManifestPath = Path.Combine(
        Path.GetTempPath(), "virtualdevteam-process-manifest.json");

    /// <summary>
    /// The path the Runner is built/published from. Computed once at startup by walking up
    /// from <see cref="AppContext.BaseDirectory"/> looking for <c>VirtualDevTeam.sln</c>.
    /// Any Copilot CLI invocation whose working directory equals (or matches the source-tree
    /// shape of) this path is REJECTED — otherwise the CLI would write target-project
    /// files into the Runner's own repo, polluting the VDT source tree.
    /// Null when the file marker isn't found (e.g. unit tests, packaged installs).
    /// </summary>
    private static readonly Lazy<string?> _runnerRepoRoot = new(ResolveRunnerRepoRoot);

    private bool _copilotAvailable;
    private bool _disposed;

    private static string? ResolveRunnerRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "VirtualDevTeam.sln")))
                    return dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                dir = dir.Parent;
            }
        }
        catch { /* ignore — guard is best-effort */ }
        return null;
    }

    /// <summary>
    /// Hard-guard against the workspace-leak bug seen 2026-05-11 where agent-written
    /// target-project files landed at <c>C:\Git\VirtualDevTeam\src\GridGuardians.Api\</c>.
    /// Root cause: an empty working directory let the spawned CLI inherit the Runner's CWD,
    /// which (because <c>start-runner.ps1</c> didn't pass <c>-WorkingDirectory</c>) was the
    /// operator's shell CWD = VDT repo root. We now FAIL LOUDLY rather than silently corrupt
    /// the source tree.
    /// </summary>
    private void ValidateWorkingDirectory(string? workingDir, string callSite)
    {
        var err = ValidateWorkingDirectoryCore(workingDir, _runnerRepoRoot.Value, callSite);
        if (err is null) return;
        _logger.LogError(err);
        throw new InvalidOperationException(err);
    }

    /// <summary>
    /// Last-resort fallback CWD: a per-agent scratch directory under the configured
    /// <see cref="WorkspaceConfig.RootPath"/>. Used by non-engineer agents (Researcher,
    /// Architect, PM, FlowMonitor detectors, etc.) that don't own a worktree and don't push
    /// their own CWD. The directory is created on demand and is namespaced by
    /// <see cref="AgentCallContext.CurrentAgentId"/> so concurrent agents don't collide.
    /// Returns null when the workspace root isn't configured (the validate guard will then
    /// surface a loud error — desired for non-agent callers).
    /// </summary>
    private string? TryResolveAgentScratchDir()
    {
        // _configMonitor is the parent VirtualDevTeamConfig; Workspace.RootPath is on it.
        var workspaceRoot = _configMonitor?.CurrentValue.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;

        var agentId = AgentCallContext.CurrentAgentId;
        var subdir = string.IsNullOrEmpty(agentId) ? "anonymous" : SanitizeForPath(agentId);
        var scratchDir = Path.Combine(workspaceRoot, ".scratch", subdir);

        try
        {
            Directory.CreateDirectory(scratchDir);
            return scratchDir;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create CLI scratch directory '{ScratchDir}' for agent '{AgentId}'. Validate guard will surface a loud error.",
                scratchDir, agentId ?? "(none)");
            return null;
        }
    }

    private static string SanitizeForPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Injects Azure OpenAI image-gen environment variables into the child process startup info
    /// when the project has image-gen configured. The agent's prompt fragments (see
    /// <c>prompts/_shared/image-gen-rest.md</c>) consume these to issue REST calls directly,
    /// replacing the prior MCP-wrapper approach that did not bind in piped-stdin sessions.
    /// </summary>
    /// <remarks>
    /// Best-effort: any failure (auth fault, network glitch on token acquisition, etc.) is logged
    /// at debug level and the variables are simply not injected. The downstream agent will then
    /// either skip the image step or report it as a blocker — both are acceptable outcomes that
    /// don't compromise the rest of the CLI session.
    /// Runs synchronously (with a short timeout) because <see cref="ProcessStartInfo"/> doesn't
    /// support async population.
    /// </remarks>
    private void ApplyImageGenEnvVars(ProcessStartInfo psi, CancellationToken ct)
    {
        if (_imageAuth is null) return;

        // Skip image-gen env vars when the caller explicitly opted out (judge/review paths
        // never generate images — avoiding 8s DefaultAzureCredential timeout per process).
        var ctx = AgentCallContext.CurrentInvocationContext;
        if (ctx?.SuppressImageGenEnv == true) return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            // Task.Run escapes Blazor Server's single-threaded SynchronizationContext.
            // Without this, .GetResult() deadlocks the circuit thread.
            var env = Task.Run(() => _imageAuth.GetEnvironmentForChildProcessAsync(timeoutCts.Token), timeoutCts.Token)
                .GetAwaiter().GetResult();
            if (env is null) return;
            foreach (var (k, v) in env)
            {
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;
                psi.Environment[k] = v;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inject image-gen env vars — agent image gen may fail.");
        }
    }

    /// <summary>
    /// Pure validation logic — returns a non-null error message if the working directory
    /// would leak into the VDT runner repo, else null. Internal for direct unit testing.
    /// </summary>
    internal static string? ValidateWorkingDirectoryCore(string? workingDir, string? runnerRoot, string callSite)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            return $"Copilot CLI invocation from '{callSite}' has no working directory. " +
                "Refusing to spawn — without an explicit CWD, the child process would " +
                "inherit the Runner's CWD and write target-project files into the VDT " +
                "source tree. Pass an explicit workspace path via OverrideWorkingDirectory " +
                "(ExecutePromptAsync) or options.WorkingDirectory (ExecuteAgenticSessionAsync), " +
                "or set CopilotCli.WorkingDirectory in appsettings.";
        }

        if (string.IsNullOrEmpty(runnerRoot)) return null; // can't validate — accept

        var normalized = Path.GetFullPath(workingDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootNormalized = runnerRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp = StringComparison.OrdinalIgnoreCase;

        if (string.Equals(normalized, rootNormalized, cmp))
        {
            return $"Copilot CLI invocation from '{callSite}' targets the VDT runner repo root ('{normalized}'). " +
                "Refusing — writing here would pollute the VirtualDevTeam source tree (see GridGuardians.Api leak " +
                "incident, 2026-05-11). Use an agent workspace path under .agents/ instead.";
        }

        var srcPrefix = Path.Combine(rootNormalized, "src") + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(srcPrefix, cmp))
        {
            var relative = normalized.Substring(srcPrefix.Length);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            // Allow paths that descend through .agents/ or .candidates/ — those are
            // legitimate workspaces under src\VirtualDevTeam.Runner\.
            var agentsSeg = $"{Path.DirectorySeparatorChar}.agents{Path.DirectorySeparatorChar}";
            var candidatesSeg = $"{Path.DirectorySeparatorChar}.candidates{Path.DirectorySeparatorChar}";
            if (relative.Contains(agentsSeg, cmp) || relative.Contains(candidatesSeg, cmp))
                return null;

            if (firstSegment.StartsWith("VirtualDevTeam.", cmp))
            {
                return $"Copilot CLI invocation from '{callSite}' targets a VDT source project ('{normalized}'). " +
                    "Refusing — this would pollute the VirtualDevTeam source tree. Use an agent workspace path " +
                    "under .agents/ or .candidates/ instead.";
            }
        }

        return null;
    }

    /// <summary>Gets the current config — hot-reloaded if IOptionsMonitor was provided.</summary>
    private CopilotCliConfig _config => _configMonitor?.CurrentValue.CopilotCli ?? _initialConfig;

    public CopilotCliProcessManager(
        IOptions<VirtualDevTeamConfig> config,
        ILogger<CopilotCliProcessManager> logger)
        : this(config, Options.Create(new StrategyFrameworkConfig()), NewDefaultGate(), logger, null)
    {
    }

    public CopilotCliProcessManager(
        IOptions<VirtualDevTeamConfig> config,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        ILogger<CopilotCliProcessManager> logger)
        : this(config, frameworkConfig, NewDefaultGate(frameworkConfig.Value), logger, null)
    {
    }

    public CopilotCliProcessManager(
        IOptions<VirtualDevTeamConfig> config,
        IOptions<StrategyFrameworkConfig> frameworkConfig,
        StrategyConcurrencyGate globalGate,
        ILogger<CopilotCliProcessManager> logger,
        IOptionsMonitor<VirtualDevTeamConfig>? configMonitor = null,
        RunnerProcessJob? runnerJob = null,
        IAzureImageAuthProvider? imageAuth = null,
        AgentCliLogService? agentLogService = null)
    {
        _initialConfig = config.Value.CopilotCli;
        _configMonitor = configMonitor;
        _frameworkConfig = frameworkConfig.Value;
        _logger = logger;
        _globalGate = globalGate;
        _runnerJob = runnerJob;
        _imageAuth = imageAuth;
        _agentLogService = agentLogService;

        // Per-pool sizing. SingleShot honours the legacy CopilotCli.MaxConcurrentRequests
        // when the strategy framework is off (preserves pre-existing behaviour), otherwise
        // takes the framework value. Candidate/Agentic come from the framework config.
        var concurrency = _frameworkConfig.Concurrency;
        var singleShotSize = concurrency.SingleShotSlots > 0
            ? concurrency.SingleShotSlots
            : _initialConfig.MaxConcurrentRequests;
        _singleShotPool = new SemaphoreSlim(singleShotSize, singleShotSize);
        _candidatePool = new SemaphoreSlim(
            Math.Max(1, concurrency.CandidateSlots),
            Math.Max(1, concurrency.CandidateSlots));
        _agenticPool = new SemaphoreSlim(
            Math.Max(1, concurrency.AgenticSlots),
            Math.Max(1, concurrency.AgenticSlots));
        _reviewPool = new SemaphoreSlim(
            Math.Max(1, concurrency.ReviewSlots),
            Math.Max(1, concurrency.ReviewSlots));

        _watchdog = new CliInteractiveWatchdog(logger, _initialConfig.AutoApprovePrompts);
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

        var effectivePath = !string.IsNullOrWhiteSpace(_config.WrapperCommand)
            ? $"{_config.WrapperCommand} copilot"
            : _config.ExecutablePath;

        if (_copilotAvailable)
            _logger.LogInformation(
                "Copilot CLI available at '{Path}'. Max concurrent requests: {Max}",
                effectivePath, _config.MaxConcurrentRequests);
        else
            _logger.LogWarning(
                "Copilot CLI not found at '{Path}'. Agents will use API-key fallback",
                effectivePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Execute a prompt via the copilot CLI in non-interactive mode.
    /// Spawns a fresh process, pipes the prompt via stdin, reads the response from stdout.
    /// </summary>
    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        CancellationToken ct = default,
        IProgress<FrameworkActivityEvent>? activitySink = null)
    {
        return await ExecutePromptAsync(prompt, modelOverride: null, ct: ct, activitySink: activitySink);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        CancellationToken ct = default,
        IProgress<FrameworkActivityEvent>? activitySink = null)
    {
        return await ExecutePromptAsync(prompt, modelOverride, sessionId: null, ct: ct, activitySink: activitySink);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        string? sessionId,
        CancellationToken ct = default,
        IProgress<FrameworkActivityEvent>? activitySink = null,
        bool forceNoWrapper = false)
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
            return await RunProcessAsync(prompt, modelOverride, sessionId, ct, activitySink, forceNoWrapper);
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
            return await RunProcessAsync(prompt, options.ModelOverride, options.SessionId, ct, options.ActivitySink);
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

        _logger.LogInformation("ExecuteAgenticSessionAsync: entered (pool={Pool}, promptLen={Len})", options.Pool, prompt.Length);

        if (options.Pool is not (CopilotCliPool.Agentic or CopilotCliPool.Review))
            throw new ArgumentException(
                $"ExecuteAgenticSessionAsync requires Pool=Agentic or Pool=Review, got {options.Pool}",
                nameof(options));

        if (!_copilotAvailable)
        {
            _logger.LogWarning("ExecuteAgenticSessionAsync: copilot not available!");
            return AgenticSessionResult.Unavailable("Copilot CLI is not available");
        }

        // Agentic and Review calls acquire their respective pool first, then the global gate.
        var pool = options.Pool == CopilotCliPool.Review ? _reviewPool : _agenticPool;
        await pool.WaitAsync(ct);
        try
        {
            using var _ = await _globalGate.AcquireAsync(ct);
            return await RunAgenticSessionAsync(prompt, options, ct);
        }
        finally
        {
            pool.Release();
        }
    }

    private async Task<CopilotCliResult> RunProcessAsync(
        string prompt,
        string? modelOverride,
        string? sessionId,
        CancellationToken ct,
        IProgress<FrameworkActivityEvent>? activitySink = null,
        bool forceNoWrapper = false)
    {
        _logger.LogInformation("RunProcessAsync: ENTERED (promptLen={Len})", prompt.Length);

        // Agent log viewer: mark call boundary and create line tap
        var agentId = AgentCallContext.CurrentAgentId;
        var callId = Guid.NewGuid().ToString("N")[..12];
        if (_agentLogService is not null && !string.IsNullOrEmpty(agentId))
        {
            _agentLogService.MarkCallBoundary(agentId, new CallBoundaryInfo(
                callId, prompt.Length > 80 ? prompt[..80] + "…" : prompt,
                modelOverride ?? _config.ModelName, null, sessionId));
        }
        Action<string>? stdoutLineTap = (_agentLogService is not null && !string.IsNullOrEmpty(agentId))
            ? line =>
            {
                var result = CliLineClassifier.ClassifyFull(line);
                if (!string.IsNullOrEmpty(result.DisplayText))
                    _agentLogService.Append(agentId, result.DisplayText, result.Classification, callId,
                        result.ToolName, result.ToolSuccess, result.ToolOutput);
            }
            : null;
        Action<string>? stderrLineTap = (_agentLogService is not null && !string.IsNullOrEmpty(agentId))
            ? line =>
            {
                var (cls, display) = CliLineClassifier.ClassifyStderr(line);
                if (!string.IsNullOrEmpty(display))
                    _agentLogService.Append(agentId, display, cls, callId);
            }
            : null;
        var argList = BuildArguments(modelOverride, sessionId);
        _logger.LogInformation("RunProcessAsync: args built ({Count} args)", argList.Count);
        var (effectiveExe, effectiveArgs) = ApplyWrapper(argList, forceNoWrapper);
        _logger.LogInformation("RunProcessAsync: wrapper applied, exe={Exe}, forceNoWrapper={NoWrapper}", effectiveExe, forceNoWrapper);

        var psi = new ProcessStartInfo
        {
            FileName = effectiveExe,
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
        foreach (var a in effectiveArgs)
            psi.ArgumentList.Add(a);

        // Resolve PATH from Windows registry so tools installed at runtime (e.g.,
        // gh or copilot via winget) are found without restarting the Runner (Lesson #36).
        FreshPathResolver.ApplyFreshPath(psi);

        // Per-invocation CWD override (e.g. a candidate worktree root) takes precedence
        // over the global default. Fall back to whatever is configured globally.
        var invocation = AgentCallContext.CurrentInvocationContext;
        var workingDir = !string.IsNullOrEmpty(invocation?.OverrideWorkingDirectory)
            ? invocation!.OverrideWorkingDirectory
            : _config.WorkingDirectory;

        // Last-resort fallback: per-agent scratch dir under the configured workspace.
        // Triggered when neither the per-invocation override nor the global default is set —
        // typically non-engineer agents (Researcher / Architect / PM / FlowMonitor detectors)
        // that don't run inside a worktree. Done BEFORE the validate guard so the spawn-time
        // check sees a real path while still rejecting the dangerous "no CWD at all" case.
        if (string.IsNullOrEmpty(workingDir))
        {
            workingDir = TryResolveAgentScratchDir();
        }

        ValidateWorkingDirectory(workingDir, nameof(ExecutePromptAsync));
        psi.WorkingDirectory = workingDir;

        // Environment overrides
        psi.Environment["NO_COLOR"] = "1";

        // Inject Azure OpenAI image-gen env vars when configured. The agent reads these from
        // its own process environment in its REST recipe (see prompts/_shared/image-gen-rest.md).
        // Best-effort; failures here never block the call.
        ApplyImageGenEnvVars(psi, ct);

        using var timeoutCts = new CancellationTokenSource();
        if (_config.RequestTimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();
            _logger.LogInformation("RunProcessAsync: process started PID={Pid}", process.Id);
            _runnerJob?.Assign(process);
            _activeProcesses.TryAdd(process.Id, process);
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
                // Start reading stderr BEFORE stdin write — if the CLI crashes on startup
                // (e.g., bad MCP config, auth failure), we capture the real error message
                // instead of just "The pipe is being closed".
                var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

                // Pipe the prompt via stdin and close to signal EOF.
                // Guarded: if the CLI exits before we finish writing (e.g., MCP server
                // init failure), the pipe closes and WriteAsync throws IOException.
                // In that case, drain stderr for the real error message.
                try
                {
                    _logger.LogInformation("RunProcessAsync: writing prompt to stdin ({Len} chars)...", prompt.Length);
                    await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token);
                    process.StandardInput.Close();
                }
                catch (IOException stdinEx)
                {
                    // CLI process died before we finished writing — drain stderr for the real reason
                    _logger.LogWarning("Stdin pipe closed early (CLI may have crashed on startup): {Error}", stdinEx.Message);
                    try { process.StandardInput.Close(); } catch { /* already broken */ }

                    string earlyStderr;
                    try
                    {
                        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                        drainCts.CancelAfter(TimeSpan.FromSeconds(5));
                        earlyStderr = await stderrTask.WaitAsync(drainCts.Token);
                    }
                    catch { earlyStderr = "(stderr drain failed)"; }

                    KillProcessSafely(process);
                    var reason = !string.IsNullOrWhiteSpace(earlyStderr)
                        ? $"CLI crashed on startup: {earlyStderr}"
                        : $"Pipe closed: {stdinEx.Message}";
                    return CopilotCliResult.Failure(reason);
                }

                var useActivityMonitor = activitySink is not null && _config.JsonOutput;
                var stdoutBuffer = new StringBuilder();
                using var killSource = new CancellationTokenSource();
                Task<string> stdoutTask = Task.FromResult(string.Empty);
                Task monitorTask = Task.CompletedTask;
                AgenticOutputMonitor? singleShotMonitor = null;

                // Agentic allow-all sessions (rework, self-assessment) should have stuck
                // detection even when no activity sink is supplied. Without this, a CLI
                // session that trickles output runs forever (SE stuck 48+ min on rework).
                var isAgenticAllowAll = invocation?.AgenticAllowAll == true;
                var needsStuckDetection = isAgenticAllowAll && _frameworkConfig.Agentic.StuckSeconds > 0;

                // Read stdout concurrently with stderr (already started above). When a sink is
                // supplied in JSON mode, reuse the agentic JSONL monitor so the caller gets a
                // live stream of structured activity events while preserving the raw stdout log.
                if (useActivityMonitor || needsStuckDetection)
                {
                    var singleShotAgenticConfig = new AgenticConfig
                    {
                        // Enable stuck detection for agentic allow-all sessions (rework, etc.)
                        StuckSeconds = needsStuckDetection ? _frameworkConfig.Agentic.StuckSeconds : 0,
                        ToolCallCap = 0,
                        ValidateHostGitconfigUnchanged = _frameworkConfig.Agentic.ValidateHostGitconfigUnchanged,
                        JobObjectMemoryLimitBytes = _frameworkConfig.Agentic.JobObjectMemoryLimitBytes,
                        JobObjectActiveProcessLimit = _frameworkConfig.Agentic.JobObjectActiveProcessLimit,
                    };
                    singleShotMonitor = new AgenticOutputMonitor(singleShotAgenticConfig, _logger, jsonMode: _config.JsonOutput);
                    monitorTask = singleShotMonitor.RunAsync(process.StandardOutput, stdoutBuffer, killSource, linked.Token, activitySink, stdoutLineTap);
                }
                else
                {
                    stdoutTask = ReadOutputWithWatchdogAsync(process, process.StandardOutput, linked.Token, stdoutLineTap);
                }

                using var processWait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, killSource.Token);
                _logger.LogInformation("RunProcessAsync: waiting for process exit (useActivityMonitor={UseMonitor})...", useActivityMonitor);
                // .NET's WaitForExitAsync with redirected streams waits for ALL stream
                // readers to complete, not just the process to exit. When MCP child processes
                // inherit stdout/stderr handles, the wait hangs forever. Use HasExited polling
                // with the bounded drain below to avoid this deadlock.
                while (!process.HasExited && !processWait.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(200, processWait.Token); } catch (OperationCanceledException) { break; }
                }
                if (!process.HasExited)
                {
                    KillProcessSafely(process);
                }

                // Kill the process tree to release pipe handles held by MCP child processes.
                // Without this, --output-format json hangs forever because child node processes
                // inherit stdout/stderr and keep the pipes open after copilot exits.
                KillProcessSafely(process);

                // Bounded drain: monitor may hang if child processes keep stdout open (Lesson #44).
                // Give it 5s after process exit, then cancel and move on.
                try
                {
                    using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await monitorTask.WaitAsync(drainCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("RunProcessAsync: monitor drain timed out after process exit — continuing");
                    killSource.Cancel(); // signal monitor to stop
                }
                catch { }

                // Bounded drain of stdout and stderr after process exit (Lesson #44).
                // Child processes may hold pipes open; don't hang forever.
                string stdout;
                if (useActivityMonitor || needsStuckDetection)
                {
                    stdout = stdoutBuffer.ToString();
                }
                else
                {
                    try { stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch { stdout = ""; }
                }

                string stderr;
                try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { stderr = ""; }

                // If the monitor killed the process due to stuck detection, report it clearly
                if (singleShotMonitor?.FailureReason == AgenticFailureReason.StuckNoOutput)
                {
                    _logger.LogWarning(
                        "RunProcessAsync: agentic session killed — no meaningful stdout for {Seconds}s (stuck detection)",
                        _frameworkConfig.Agentic.StuckSeconds);
                    return new CopilotCliResult
                    {
                        IsSuccess = false,
                        Error = $"CLI session stuck: no stdout for {_frameworkConfig.Agentic.StuckSeconds}s. Partial output ({stdout.Length} chars).",
                        ExitCode = -1,
                        FailureReason = CliFailureReason.StuckNoOutput,
                        HadAnyOutput = stdout.Length > 0,
                    };
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Copilot process exited with code {Code}. stderr: {Stderr}",
                        process.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);

                    // Still return stdout if there's content — partial responses can be useful
                    if (!string.IsNullOrWhiteSpace(stdout))
                        return CopilotCliResult.Success(stdout, process.ExitCode);

                    return CopilotCliResult.Failure(
                        $"Copilot exited with code {process.ExitCode}: {stderr}",
                        CliFailureReason.ProcessCrash);
                }

                return CopilotCliResult.Success(stdout, process.ExitCode);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcessSafely(process);
                return CopilotCliResult.Failure(
                    $"Copilot request timed out after {_config.RequestTimeoutSeconds}s",
                    CliFailureReason.Timeout);
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
            finally
            {
                _activeProcesses.TryRemove(process.Id, out _);
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
        _logger.LogInformation("RunAgenticSessionAsync: ENTERED (promptLen={Len})", prompt.Length);

        // Agent log viewer: mark call boundary and create line tap
        var agentId = AgentCallContext.CurrentAgentId;
        var callId = Guid.NewGuid().ToString("N")[..12];
        if (_agentLogService is not null && !string.IsNullOrEmpty(agentId))
        {
            _agentLogService.MarkCallBoundary(agentId, new CallBoundaryInfo(
                callId, prompt.Length > 80 ? prompt[..80] + "…" : prompt,
                options.ModelOverride ?? _config.ModelName, options.WorkingDirectory, options.SessionId));
        }
        Action<string>? stdoutLineTap = (_agentLogService is not null && !string.IsNullOrEmpty(agentId))
            ? line =>
            {
                var result = CliLineClassifier.ClassifyFull(line);
                if (!string.IsNullOrEmpty(result.DisplayText))
                    _agentLogService.Append(agentId, result.DisplayText, result.Classification, callId,
                        result.ToolName, result.ToolSuccess, result.ToolOutput);
            }
            : null;
        var sw = Stopwatch.StartNew();
        var argList = BuildAgenticArguments(options);
        var (effectiveExe, effectiveArgs) = ApplyWrapper(argList, options.ForceNoWrapper);
        if (options.ForceNoWrapper)
            _logger.LogInformation("RunAgenticSessionAsync: ForceNoWrapper=true, calling copilot directly");
        _logger.LogInformation("RunAgenticSessionAsync: exe={Exe}, building PSI...", effectiveExe);

        var psi = new ProcessStartInfo
        {
            FileName = effectiveExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in effectiveArgs)
            psi.ArgumentList.Add(a);

        FreshPathResolver.ApplyFreshPath(psi);

        var workingDir = !string.IsNullOrEmpty(options.WorkingDirectory)
            ? options.WorkingDirectory
            : _config.WorkingDirectory;
        if (string.IsNullOrEmpty(workingDir))
        {
            workingDir = TryResolveAgentScratchDir();
        }
        _logger.LogInformation("RunAgenticSessionAsync: workingDir={Dir}, validating...", workingDir);
        ValidateWorkingDirectory(workingDir, nameof(ExecuteAgenticSessionAsync));
        psi.WorkingDirectory = workingDir;

        psi.Environment["NO_COLOR"] = "1";

        ApplyImageGenEnvVars(psi, ct);

        if (options.EnvironmentOverrides is { Count: > 0 })
        {
            _logger.LogInformation("RunAgenticSessionAsync: applying {Count} env overrides", options.EnvironmentOverrides.Count);
            foreach (var (k, v) in options.EnvironmentOverrides)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (v is null)
                    psi.Environment.Remove(k);
                else
                    psi.Environment[k] = v;
            }
            ApplyImageGenEnvVars(psi, ct);
        }

        var wallClock = options.Timeout
            ?? TimeoutsConfig.ToTimeSpan(_frameworkConfig.Timeouts.AgenticSeconds);
        using var timeoutCts = new CancellationTokenSource();
        if (wallClock != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(wallClock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        Win32JobObject? jobObject = null;
        try
        {
            process = new Process { StartInfo = psi };
            _logger.LogInformation("RunAgenticSessionAsync: about to Start() exe={Exe}, cwd={Cwd}", effectiveExe, psi.WorkingDirectory);
            process.Start();
            _logger.LogInformation("RunAgenticSessionAsync: started PID={Pid}", process.Id);
            _runnerJob?.Assign(process);
            _activeProcesses.TryAdd(process.Id, process);

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

            // Notify caller so it can create an intervention sink for stdin writes
            try { options.OnProcessCreated?.Invoke(process); }
            catch (Exception cbEx) { _logger.LogDebug(cbEx, "OnProcessCreated callback failed"); }

            // Start wrapper-child liveness watchdog BEFORE stdin write — if the wrapper
            // hangs during MCP init and never reads stdin, the watchdog detects the missing
            // child process and kills it. Previously this was after the stdin write, so a
            // WriteAsync deadlock prevented the watchdog from ever starting.
            Task wrapperLivenessTask = Task.CompletedTask;
            var isWrapped = !string.IsNullOrWhiteSpace(_config.WrapperCommand);
            if (isWrapped && !process.HasExited)
            {
                wrapperLivenessTask = MonitorWrapperChildLivenessAsync(
                    process, killSource, linked.Token);
            }

            try
            {
                // Pipe prompt via stdin; flush but DO NOT close. Agentic sessions
                // keep stdin open for watchdog responses and future multi-turn input.
                // CRITICAL: WriteAsync can deadlock if the wrapper isn't reading stdin
                // yet (still initializing MCP servers, spawning copilot). The Windows
                // anonymous pipe buffer is ~64KB; prompts can be 150KB+. A 90s timeout
                // prevents pinning the candidate slot forever if the process hangs.
                using var stdinCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                stdinCts.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    await process.StandardInput.WriteAsync(prompt.AsMemory(), stdinCts.Token);
                    await process.StandardInput.FlushAsync(stdinCts.Token);
                }
                catch (OperationCanceledException) when (stdinCts.IsCancellationRequested && !linked.IsCancellationRequested)
                {
                    _logger.LogWarning(
                       "Agentic session PID {Pid}: stdin write timed out after 90s (prompt {PromptLen} chars, pipe buffer likely full — process not reading). Killing.",
                        process.Id, prompt.Length);
                    KillProcessSafely(process);
                    sw.Stop();
                    return new AgenticSessionResult
                    {
                        Succeeded = false,
                        FailureReason = AgenticFailureReason.LaunchFailed,
                        ExitCode = -1,
                        WallClock = sw.Elapsed,
                        ErrorMessage = $"stdin write deadlock: process not draining stdin after 90s (prompt {prompt.Length} chars)",
                    };
                }
                if (options.CloseStdinAfterPrompt)
                    process.StandardInput.Close();

                Task<string> stdoutFallbackTask = Task.FromResult(string.Empty);
                Task monitorTask = Task.CompletedTask;
                var useMonitor = options.WatchdogMode == CopilotCliWatchdogMode.Agentic;

                if (useMonitor)
                {
                    // JSONL watchdog: stuck detector + tool-call cap. Always emitted with
                    // JSON mode (BuildAgenticArguments forces --output-format json).
                    var agenticCfg = _frameworkConfig.Agentic;
                    if (options.ToolCallCapOverride is > 0 || options.StuckSecondsOverride is not null)
                    {
                        // Per-request overrides: T-FINAL needs higher tool-call headroom; long-running
                        // live scenario verification overrides the stall window (StuckSeconds) and removes
                        // the wall-clock entirely, so stall detection is the only stopping condition.
                        agenticCfg = new AgenticConfig
                        {
                            StuckSeconds = options.StuckSecondsOverride ?? agenticCfg.StuckSeconds,
                            ToolCallCap = options.ToolCallCapOverride is > 0 ? options.ToolCallCapOverride.Value : agenticCfg.ToolCallCap,
                            ValidateHostGitconfigUnchanged = agenticCfg.ValidateHostGitconfigUnchanged,
                            JobObjectMemoryLimitBytes = agenticCfg.JobObjectMemoryLimitBytes,
                            JobObjectActiveProcessLimit = agenticCfg.JobObjectActiveProcessLimit,
                        };
                    }
                    monitor = new AgenticOutputMonitor(agenticCfg, _logger, jsonMode: true);
                    monitorTask = monitor.RunAsync(process.StandardOutput, logBuffer, killSource, linked.Token, options.ActivitySink, stdoutLineTap);
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
                    // WaitForExitAsync can hang indefinitely when child processes (MCP servers)
                    // inherit stdout/stderr pipe handles. Even after the main copilot process
                    // exits, the pipe stays open. Guard with the wall-clock timeout from the
                    // linked CTS (which includes the strategy timeout) plus a hard safety cap.
                    await process.WaitForExitAsync(processWait.Token);
                }
                catch (OperationCanceledException) when (killSource.IsCancellationRequested)
                {
                    // Watchdog asked us to kill. Tear down the process tree and fall
                    // through to classify the failure from the monitor's FailureReason.
                    KillProcessSafely(process);
                    try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    // Wall-clock timeout or external cancellation. Kill and proceed.
                    _logger.LogWarning("Agentic session PID {Pid} hit wall-clock timeout — killing process tree", process.Id);
                    KillProcessSafely(process);
                    try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                }

                // SAFETY NET: if the process exited but WaitForExitAsync returned
                // successfully yet stdout/stderr tasks are still blocked (child handles
                // holding pipes open), force-kill the entire process tree after 30s grace.
                if (process.HasExited && (!monitorTask.IsCompleted || !stderrTask.IsCompleted))
                {
                    using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await Task.WhenAll(
                            monitorTask.IsCompleted ? Task.CompletedTask : Task.Delay(Timeout.Infinite, drainCts.Token),
                            stderrTask.IsCompleted ? Task.CompletedTask : Task.Delay(Timeout.Infinite, drainCts.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning(
                            "Agentic session PID {Pid} exited but stream readers hung for 30s (child processes holding pipes). Force-killing descendants.",
                            process.Id);
                        // Kill any remaining children holding the pipes open
                        try { process.Kill(entireProcessTree: true); } catch { }
                    }
                }

                // CLEANUP-RACE LAYER 2 (2026-05-12): drain descendant processes (MCP
                // servers, child copilot.exe sessions, etc.) before we hand control
                // back to the orchestrator. Without this, the worktree-cleanup that
                // follows hits a file-lock race against still-draining children and
                // partially destroys the candidate's working tree (Squad sprite loss
                // incident). Best-effort grace delay; doesn't block on PIDs.
                try
                {
                    if (_runnerJob is not null)
                        await _runnerJob.WaitForDescendantsAsync(process.Id, TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch (Exception drainEx)
                {
                    _logger.LogDebug(drainEx, "CLI descendant drain raised — proceeding anyway");
                }

                // Drain the watchdog/stdout tasks. Swallow their exceptions — classification
                // happens on the monitor's FailureReason or the process exit code.
                try { await monitorTask; } catch { }
                try { await wrapperLivenessTask; } catch { }
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
                AgenticFailureReason? detectedFailure = monitor?.FailureReason;

                // Check wrapper-child liveness watchdog result (set via killSource)
                if (detectedFailure is null && isWrapped && killSource.IsCancellationRequested)
                    detectedFailure = AgenticFailureReason.WrapperChildExited;

                if (detectedFailure is { } watchdogFailure)
                {
                    // Track wrapper startup failures for circuit breaker
                    if (isWrapped && !(monitor?.HasReceivedAnyOutput ?? false)
                        && (watchdogFailure is AgenticFailureReason.StuckNoOutput
                            or AgenticFailureReason.WrapperChildExited))
                    {
                        _wrapperBreaker.RecordStartupFailure();
                        _logger.LogWarning(
                            "Wrapper startup failure recorded ({Failures} consecutive). " +
                            "Breaker trips at {Threshold}.",
                            _wrapperBreaker.ConsecutiveFailures, _wrapperBreaker.FailureThreshold);
                    }

                    string errorMessage;
                    if (watchdogFailure == AgenticFailureReason.StuckNoOutput)
                    {
                        var stderrTail = string.IsNullOrEmpty(stderr)
                            ? "(no stderr)"
                            : stderr.Length > 800 ? stderr[..800] : stderr;
                        errorMessage = $"Copilot CLI session stuck: no stdout for {_frameworkConfig.Agentic.StuckSeconds}s. stderr: {stderrTail}";
                    }
                    else if (watchdogFailure == AgenticFailureReason.WrapperChildExited)
                    {
                        errorMessage = "Wrapper process alive but child CLI exited — likely transient auth/network failure during startup";
                    }
                    else
                    {
                        var effectiveCap = options.ToolCallCapOverride ?? _frameworkConfig.Agentic.ToolCallCap;
                        errorMessage = $"Copilot CLI session exceeded tool-call cap of {effectiveCap}";
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
                        "Copilot CLI process exited with code {Code}. stderr: {Stderr}",
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

                // Wrapper circuit breaker: successful session resets the failure counter
                if (isWrapped)
                    _wrapperBreaker.RecordSuccess();

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
                    ErrorMessage = $"Copilot CLI session timed out after {wallClock.TotalSeconds:F0}s",
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
            finally
            {
                _activeProcesses.TryRemove(process.Id, out _);
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
            // Skip --allow-all from base — we control it via options.AllowAll above
            if (a == "--allow-all")
                continue;
            // For agentic sessions, ALLOW custom instructions so the CLI reads
            // the target project's .github/copilot-instructions.md. CWD for agentic
            // sessions is always the candidate worktree (target project), not VDT.
            // This gives agents project-specific context (conventions, build commands,
            // patterns) automatically. The non-agentic BuildArguments path still
            // includes --no-custom-instructions because its CWD may be VDT.
            if (a == "--no-custom-instructions")
                continue;
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
        Process process, StreamReader stdout, CancellationToken ct, Action<string>? onLine = null)
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

            // Check each line in the chunk for interactive prompts + agent log tap
            var lines = chunk.Split('\n');
            foreach (var line in lines)
            {
                // Agent log viewer tap
                if (onLine is not null && !string.IsNullOrWhiteSpace(line))
                {
                    try { onLine(line); } catch { /* best-effort */ }
                }

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
    /// Wraps the executable and argument list with the CLI wrapper when configured.
    /// Returns the effective FileName and argument list for ProcessStartInfo.
    /// </summary>
    internal (string FileName, IReadOnlyList<string> Args) ApplyWrapper(IReadOnlyList<string> args, bool forceNoWrapper = false)
    {
        if (!string.IsNullOrWhiteSpace(_config.WrapperCommand) && !forceNoWrapper)
        {
            // Circuit breaker: if the wrapper has failed N consecutive times at startup
            // (session never produced output), bypass it and call copilot directly.
            if (_wrapperBreaker.ShouldBypass())
            {
                _logger.LogWarning(
                    "Wrapper circuit breaker OPEN — bypassing '{Wrapper}' after {Failures} consecutive startup failures. " +
                    "Calling copilot directly. Will probe wrapper again after cooldown.",
                    _config.WrapperCommand, _wrapperBreaker.ConsecutiveFailures);
                return (_config.ExecutablePath, args);
            }

            var wrapped = new List<string> { "copilot" };
            wrapped.AddRange(args);
            return (_config.WrapperCommand, wrapped);
        }
        return (_config.ExecutablePath, args);
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
        args.Add("--allow-all");
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

        // Context window tier. The CLI no longer encodes the 1M window as a "-1m"
        // model suffix; it's now selected via --context long_context. Omit when unset
        // so the CLI falls back to its persisted/default tier.
        if (!string.IsNullOrWhiteSpace(_config.ContextTier))
        {
            args.Add("--context");
            args.Add(_config.ContextTier);
        }

        // Reasoning effort (skip for models that don't support it).
        var effectiveModel = modelOverride ?? _config.ModelName;
        var supportsReasoning = effectiveModel == null
            || (!effectiveModel.Contains("haiku", StringComparison.OrdinalIgnoreCase)
                && !effectiveModel.Equals("claude-sonnet-4.5", StringComparison.OrdinalIgnoreCase));
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

        // MCP servers referenced by name from the agent-role config.
        // The CLI's mcp.json file is NOT loaded in piped-stdin mode, so we must pass
        // server definitions inline via --additional-mcp-config for each agent call.
        var mcpServers = AgentCallContext.McpServers;
        if (mcpServers is { Count: > 0 } && _configMonitor is not null)
        {
            var globalMcpServers = _configMonitor.CurrentValue.McpServers;
            _logger.LogDebug("Agent {AgentId} has {McpCount} MCP servers in context: [{Servers}]. Global registry has: [{GlobalServers}]",
                AgentCallContext.CurrentAgentId, mcpServers.Count, string.Join(", ", mcpServers),
                string.Join(", ", globalMcpServers.Keys));

            var mcpEntries = new Dictionary<string, object>();
            foreach (var serverName in mcpServers)
            {
                if (string.IsNullOrWhiteSpace(serverName)) continue;
                if (globalMcpServers.TryGetValue(serverName, out var def) && !string.IsNullOrEmpty(def.Command))
                {
                    mcpEntries[serverName] = new
                    {
                        type = def.Transport.ToString().ToLowerInvariant(),
                        command = def.Command,
                        args = def.Args,
                        env = def.Env.Count > 0 ? def.Env : null
                    };
                }
                else
                {
                    _logger.LogWarning("MCP server '{ServerName}' not found in global config or has no Command", serverName);
                }
            }

            if (mcpEntries.Count > 0)
            {
                var mcpConfigJson = JsonSerializer.Serialize(
                    new { mcpServers = mcpEntries },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                args.Add("--additional-mcp-config");
                args.Add(mcpConfigJson);
                _logger.LogInformation("Injecting --additional-mcp-config for agent {AgentId} with servers: [{Servers}]",
                    AgentCallContext.CurrentAgentId, string.Join(", ", mcpEntries.Keys));
            }
        }
        else if (mcpServers is { Count: > 0 } && _configMonitor is null)
        {
            _logger.LogWarning("Agent has {McpCount} MCP servers but _configMonitor is null — cannot inject inline MCP config", mcpServers.Count);
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

            if (invocation.Attachments is { Count: > 0 })
            {
                foreach (var path in invocation.Attachments)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        args.Add("--attachment");
                        args.Add(path);
                    }
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
            var (verifyExe, verifyArgs) = ApplyWrapper(new[] { "--version" });
            var psi = new ProcessStartInfo
            {
                FileName = verifyExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in verifyArgs)
                psi.ArgumentList.Add(a);

            process = new Process { StartInfo = psi };
            process.Start();

            // Read stdout/stderr concurrently to avoid pipe-buffer deadlock.
            // If we call WaitForExit first and the process fills the stderr buffer (~4KB),
            // WaitForExit blocks forever because the process is blocked on the write.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeoutCts.Token);
                process.WaitForExit(TimeSpan.FromSeconds(5)); // should already be exited
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Copilot CLI verification timed out after 30s — treating as unavailable");
                KillProcessSafely(process);
                return false;
            }

            if (process.ExitCode == 0)
            {
                var output = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
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

    /// <summary>
    /// Monitors whether a wrapper process still has meaningful child processes.
    /// When a CLI wrapper is used, the wrapped CLI can die silently on transient
    /// errors while the wrapper stays alive, producing no output. This watchdog
    /// detects the "parent alive, no children" state and signals kill.
    /// General-purpose: ignores only console host processes as infrastructure —
    /// any other child counts as "meaningful."
    /// </summary>
    private async Task MonitorWrapperChildLivenessAsync(
        Process wrapperProcess,
        CancellationTokenSource killSource,
        CancellationToken ct)
    {
        const int graceSeconds = 30;
        const int checkIntervalSeconds = 10;
        const int consecutiveFailsToKill = 3;

        try
        {
            _logger.LogInformation(
                "Wrapper liveness watchdog: started for PID {Pid}, grace={Grace}s, kill after {Fails} empty checks",
                wrapperProcess.Id, graceSeconds, consecutiveFailsToKill);

            await Task.Delay(TimeSpan.FromSeconds(graceSeconds), ct);

            int consecutiveEmpty = 0;
            bool probeAvailable = true;
            // Try pwsh first (PS 7), fall back to powershell (PS 5.1)
            string psExe = "pwsh";

            while (!ct.IsCancellationRequested && !killSource.IsCancellationRequested)
            {
                if (wrapperProcess.HasExited) return;
                if (!probeAvailable) return;

                bool hasMeaningfulChild = false;
                bool probeSucceeded = false;
                try
                {
                    var psi = new ProcessStartInfo(psExe, "-NoProfile -NonInteractive -Command " +
                        $"\"Get-CimInstance Win32_Process -Filter 'ParentProcessId={wrapperProcess.Id}' | Select-Object -ExpandProperty Name\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var probe = Process.Start(psi);
                    if (probe is not null)
                    {
                        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        probeCts.CancelAfter(TimeSpan.FromSeconds(8));
                        var output = await probe.StandardOutput.ReadToEndAsync(probeCts.Token);
                        await probe.WaitForExitAsync(probeCts.Token);
                        probeSucceeded = true;

                        hasMeaningfulChild = output
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Any(n => !n.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
                                   && !n.Equals("conhost", StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch (System.ComponentModel.Win32Exception) when (psExe == "pwsh")
                {
                    // pwsh not found — try powershell (PS 5.1)
                    _logger.LogInformation("Wrapper liveness watchdog: pwsh not found, falling back to powershell");
                    psExe = "powershell";
                    continue;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Neither pwsh nor powershell available — can't monitor child processes.
                    // Kill the wrapper as a failsafe rather than letting it run indefinitely
                    // with no liveness monitoring (a wrapper with no watchdog is a hang risk).
                    _logger.LogWarning(
                        "Wrapper liveness watchdog: neither pwsh nor powershell available — killing wrapper PID {Pid} as failsafe",
                        wrapperProcess.Id);
                    killSource.Cancel();
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Wrapper liveness probe timed out — skipping this check");
                    await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);
                    continue;
                }

                if (!probeSucceeded)
                {
                    await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);
                    continue;
                }

                if (hasMeaningfulChild)
                {
                    consecutiveEmpty = 0;
                }
                else
                {
                    consecutiveEmpty++;
                    _logger.LogInformation(
                        "Wrapper liveness: PID {Pid} has no meaningful children ({Count}/{Needed})",
                        wrapperProcess.Id, consecutiveEmpty, consecutiveFailsToKill);

                    if (consecutiveEmpty >= consecutiveFailsToKill)
                    {
                        _logger.LogWarning(
                            "Wrapper process {Pid} has no meaningful child processes for {Seconds}s — killing (child CLI likely never started or exited silently)",
                            wrapperProcess.Id, consecutiveEmpty * checkIntervalSeconds);
                        killSource.Cancel();
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wrapper child liveness monitor failed for PID {Pid} — continuing without liveness checks", wrapperProcess.Id);
        }
    }

    private void KillProcessSafely(Process process)
    {
        try
        {
            // Always attempt tree kill — even if the parent has exited, MCP child
            // processes may still be alive holding stdout/stderr pipes open.
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

        // Kill all in-flight copilot processes to prevent orphaned sessions
        foreach (var kvp in _activeProcesses)
        {
            if (_activeProcesses.TryRemove(kvp.Key, out var process))
                KillProcessSafely(process);
        }

        _singleShotPool.Dispose();
        _candidatePool.Dispose();
        _agenticPool.Dispose();
        _reviewPool.Dispose();
    }

    /// <summary>
    /// Lightweight circuit breaker for the wrapper command.
    /// Tracks consecutive startup failures (sessions that never produced output)
    /// and bypasses the wrapper after a threshold. Uses half-open probe for recovery:
    /// after cooldown, allows ONE wrapped call; re-trips on failure, resets on success.
    /// Thread-safe via lock.
    /// </summary>
    internal sealed class WrapperCircuitBreaker
    {
        private readonly object _lock = new();
        private int _consecutiveFailures;
        private DateTimeOffset _bypassUntil = DateTimeOffset.MinValue;
        private bool _probeInProgress;

        /// <summary>Number of consecutive startup failures before bypassing the wrapper.</summary>
        public int FailureThreshold { get; set; } = 2;

        /// <summary>Minutes to wait before probing the wrapper again after trip.</summary>
        public int CooldownMinutes { get; set; } = 30;

        /// <summary>True when the wrapper should be bypassed for this call.</summary>
        public bool ShouldBypass()
        {
            lock (_lock)
            {
                if (_consecutiveFailures < FailureThreshold)
                    return false;

                // Cooldown expired — allow one probe
                if (DateTimeOffset.UtcNow >= _bypassUntil && !_probeInProgress)
                {
                    _probeInProgress = true;
                    return false; // Let this one call use the wrapper as a probe
                }

                return true; // Still in bypass mode
            }
        }

        /// <summary>Report a wrapper startup failure (session never produced output).</summary>
        public void RecordStartupFailure()
        {
            lock (_lock)
            {
                _probeInProgress = false;
                _consecutiveFailures++;
                if (_consecutiveFailures >= FailureThreshold)
                    _bypassUntil = DateTimeOffset.UtcNow.AddMinutes(CooldownMinutes);
            }
        }

        /// <summary>Report a successful wrapped call (produced output). Resets the breaker.</summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
                _probeInProgress = false;
                _bypassUntil = DateTimeOffset.MinValue;
            }
        }

        public bool IsOpen => _consecutiveFailures >= FailureThreshold;
        public int ConsecutiveFailures => _consecutiveFailures;
    }
}

/// <summary>Result of a copilot CLI execution.</summary>
/// <summary>Classifies why a CLI call failed, enabling structured retry decisions.</summary>
public enum CliFailureReason
{
    /// <summary>No failure or unknown cause.</summary>
    None,
    /// <summary>Process killed because no meaningful stdout for StuckSeconds.</summary>
    StuckNoOutput,
    /// <summary>Wall-clock timeout exceeded.</summary>
    Timeout,
    /// <summary>Auth/credential error (401, 403).</summary>
    Auth,
    /// <summary>Rate limited (429).</summary>
    RateLimit,
    /// <summary>Process crashed (non-zero exit, signal).</summary>
    ProcessCrash,
    /// <summary>Stale --resume session not found by CLI.</summary>
    StaleSession,
}

public class CopilotCliResult
{
    public bool IsSuccess { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public int ExitCode { get; init; }

    /// <summary>Structured failure reason for retry escalation decisions.</summary>
    public CliFailureReason FailureReason { get; init; }

    /// <summary>Whether the process produced any stdout before failing.</summary>
    public bool HadAnyOutput { get; init; }

    public static CopilotCliResult Success(string output, int exitCode = 0) =>
        new() { IsSuccess = true, Output = output, ExitCode = exitCode, HadAnyOutput = output.Length > 0 };

    public static CopilotCliResult Failure(string error, CliFailureReason reason = CliFailureReason.None) =>
        new() { IsSuccess = false, Error = error, ExitCode = -1, FailureReason = reason };
}

/// <summary>Thrown when the copilot CLI encounters an unrecoverable error (e.g., credential prompt).</summary>
public class CopilotCliException : Exception
{
    public CopilotCliException(string message) : base(message) { }
    public CopilotCliException(string message, Exception inner) : base(message, inner) { }

    /// <summary>If set, indicates the error is MCP-related with a specific category.</summary>
    public McpErrorCategory? McpError { get; init; }

    /// <summary>User-friendly fix suggestion for MCP errors.</summary>
    public string? McpFixSuggestion { get; init; }

    /// <summary>
    /// Creates a CopilotCliException with MCP error details parsed from the error message.
    /// </summary>
    public static CopilotCliException FromCliError(string rawError)
    {
        var (category, suggestion) = ClassifyMcpError(rawError);
        return new CopilotCliException(
            category.HasValue ? $"MCP Error ({category}): {rawError}" : $"Copilot CLI request failed: {rawError}")
        {
            McpError = category,
            McpFixSuggestion = suggestion
        };
    }

    private static (McpErrorCategory?, string?) ClassifyMcpError(string error)
    {
        if (string.IsNullOrEmpty(error)) return (null, null);

        var lower = error.ToLowerInvariant();
        if (lower.Contains("eula") || lower.Contains("license agreement") || lower.Contains("accept"))
            return (McpErrorCategory.EulaNotAccepted,
                "Accept the WorkIQ EULA: run 'copilot' interactively and accept when prompted, or visit https://github.com/microsoft/work-iq");

        if (lower.Contains("npx") && (lower.Contains("not found") || lower.Contains("not recognized") || lower.Contains("enoent")))
            return (McpErrorCategory.NpxNotFound,
                "Install Node.js (v18+) and ensure 'npx' is on your PATH");

        if (lower.Contains("auth") || lower.Contains("unauthorized") || lower.Contains("401") || lower.Contains("credential"))
            return (McpErrorCategory.AuthFailure,
                "MCP server authentication failed. Ensure you are signed in (run 'copilot auth login' or check Microsoft 365 auth)");

        if (lower.Contains("mcp") && (lower.Contains("connect") || lower.Contains("spawn") || lower.Contains("timeout") || lower.Contains("crash")))
            return (McpErrorCategory.ServerStartupFailure,
                "MCP server failed to start. Check that the server package is accessible and Node.js is working");

        return (null, null);
    }
}

/// <summary>Categories of MCP-related errors for structured error surfacing.</summary>
public enum McpErrorCategory
{
    /// <summary>WorkIQ EULA has not been accepted.</summary>
    EulaNotAccepted,
    /// <summary>npx command not found on PATH.</summary>
    NpxNotFound,
    /// <summary>Authentication/authorization failure with MCP server.</summary>
    AuthFailure,
    /// <summary>MCP server process failed to start or crashed.</summary>
    ServerStartupFailure
}
