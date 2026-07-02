namespace VirtualDevTeam.Core.AI;

using VirtualDevTeam.Core.Frameworks;

/// <summary>
/// Which of the three concurrency pools a CLI call belongs to. The actual
/// per-pool semaphores are wired in <see cref="CopilotCliProcessManager"/> by the
/// <c>p3-semaphore-split</c> todo; the enum ships first so downstream call
/// sites can be stamped with intent without waiting for the pool split.
/// </summary>
public enum CopilotCliPool
{
    /// <summary>Short lived advisory calls (e.g. agent chat, planning, MCP helpers).</summary>
    SingleShot = 0,
    /// <summary>Candidate generation under a strategy (baseline / mcp-enhanced patch producers).</summary>
    Candidate = 1,
    /// <summary>Agentic sessions with <c>--allow-all</c> running inside a sandboxed worktree.</summary>
    Agentic = 2,
    /// <summary>CLI-native review sessions with <c>--allow-all</c> for judge/peer code review.</summary>
    Review = 3,
}

/// <summary>Which watchdog state machine to attach to a CLI call's stdout stream.</summary>
public enum CopilotCliWatchdogMode
{
    /// <summary>The regex-based <see cref="CliInteractiveWatchdog"/> (current legacy behaviour).</summary>
    Default = 0,
    /// <summary>The JSONL-driven <c>AgenticOutputMonitor</c> (attached by <c>p3-agentic-watchdog</c>).</summary>
    Agentic = 1,
    /// <summary>No watchdog; stdout is streamed raw (wall-clock timeout still applies).</summary>
    Disabled = 2,
}

/// <summary>
/// Per-call invocation parameters for the copilot CLI. Replaces the growing chain
/// of <c>ExecutePromptAsync</c> overloads. The record carries values the ambient
/// <see cref="AgentCallContext.CurrentInvocationContext"/> does not — pool choice,
/// explicit timeout, watchdog mode, stdin lifecycle, agentic mode flag.
/// </summary>
/// <remarks>
/// The existing <see cref="CopilotCliInvocationContext"/> AsyncLocal stays for
/// MCP scope. When <see cref="CopilotCliProcessManager.BuildArguments"/> reads
/// both, request-options values win on overlap.
/// </remarks>
public sealed record CopilotCliRequestOptions
{
    /// <summary>Target pool. Legacy callers default to <see cref="CopilotCliPool.SingleShot"/>.</summary>
    public CopilotCliPool Pool { get; init; } = CopilotCliPool.SingleShot;

    /// <summary>Override the configured default model (e.g. <c>claude-opus-4.6</c>).</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Copilot CLI <c>--resume</c> session id for conversational continuity.</summary>
    public string? SessionId { get; init; }

    /// <summary>Wall-clock timeout for the whole process lifetime. Null = use config default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Override the process working directory (e.g. a candidate worktree path).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables to set on the child process. A <c>null</c> value for a
    /// given key removes that variable from the child's environment instead of
    /// setting it to an empty string. Used by the agentic sandbox scope to scrub
    /// host credentials.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }

    /// <summary>Which watchdog to attach. Default = legacy regex monitor.</summary>
    public CopilotCliWatchdogMode WatchdogMode { get; init; } = CopilotCliWatchdogMode.Default;

    /// <summary>
    /// Whether to close stdin immediately after writing the prompt. Legacy default
    /// is <c>true</c> — many copilot invocations rely on stdin EOF to begin
    /// streaming. Agentic sessions override this to <c>false</c> so the watchdog's
    /// response path (and future multi-turn stdin pokes) can actually write to
    /// stdin without EOF-ing the process.
    /// </summary>
    public bool CloseStdinAfterPrompt { get; init; } = true;

    /// <summary>
    /// Emit <c>--allow-all</c>. Only honoured when <see cref="Pool"/> is
    /// <see cref="CopilotCliPool.Agentic"/> or <see cref="CopilotCliPool.Review"/>
    /// — the process manager ignores it in any other pool to keep the blast radius narrow.
    /// </summary>
    public bool AllowAll { get; init; } = false;

    /// <summary>
    /// Optional progress callback for streaming real-time activity events to the
    /// dashboard. Set by the strategy layer so the agentic output monitor can
    /// report tool calls, file operations, and planning output as they happen.
    /// </summary>
    public IProgress<FrameworkActivityEvent>? ActivitySink { get; init; }

    /// <summary>
    /// Per-request override for <see cref="Configuration.AgenticConfig.ToolCallCap"/>.
    /// Used by T-FINAL validation which needs higher headroom than normal code-gen tasks.
    /// Null = use the global config default. 0 = disabled (AI monitor handles lifecycle).
    /// </summary>
    public int? ToolCallCapOverride { get; init; }

    /// <summary>
    /// Per-request override for <see cref="Configuration.AgenticConfig.StuckSeconds"/> — the stall
    /// window (seconds of no meaningful stdout) after which the agentic watchdog kills the session.
    /// Used by long-running live scenario verification, which removes the wall-clock timeout and relies
    /// solely on stall detection. Null = use the global config default. 0 = disable stall detection.
    /// </summary>
    public int? StuckSecondsOverride { get; init; }

    /// <summary>
    /// Callback invoked after the child process is created but before the main wait loop.
    /// Receives the <see cref="System.Diagnostics.Process"/> so callers can create an
    /// <see cref="Strategies.IAgenticInterventionSink"/> for mid-session stdin writes.
    /// </summary>
    public Action<System.Diagnostics.Process>? OnProcessCreated { get; init; }

    /// <summary>
    /// When true, bypasses the configured wrapper command for THIS call only.
    /// Used by the strategy reset escalation ladder (rung 2) to retry without the wrapper
    /// after a startup hang. Does not affect the global circuit breaker state.
    /// </summary>
    public bool ForceNoWrapper { get; init; } = false;
}

/// <summary>Why an agentic session ended unsuccessfully.</summary>
public enum AgenticFailureReason
{
    None = 0,
    /// <summary>No stdout activity within the stuck-detector window.</summary>
    StuckNoOutput,
    /// <summary>Wrapper process is alive but has no meaningful child processes.</summary>
    WrapperChildExited,
    /// <summary>Exceeded the configured tool-call cap (JSONL mode only).</summary>
    ToolCallCap,
    /// <summary>Post-run sandbox validation rejected the candidate.</summary>
    SandboxViolation,
    /// <summary>Wall-clock timeout expired.</summary>
    Timeout,
    /// <summary>Process exited with a non-zero code.</summary>
    ExitNonzero,
    /// <summary>Caller cancelled via <see cref="System.Threading.CancellationToken"/>.</summary>
    Canceled,
    /// <summary>Failed to start the process at all.</summary>
    LaunchFailed,
    /// <summary>Strategy framework is disabled or the CLI is not available.</summary>
    Unavailable,
}

/// <summary>Outcome of one agentic CLI session.</summary>
public sealed record AgenticSessionResult
{
    public bool Succeeded { get; init; }
    public AgenticFailureReason FailureReason { get; init; } = AgenticFailureReason.None;
    public int ExitCode { get; init; }
    public TimeSpan WallClock { get; init; }
    public int ToolCallCount { get; init; }
    public string LogBuffer { get; init; } = "";
    public string? ErrorMessage { get; init; }

    public static AgenticSessionResult Unavailable(string reason) => new()
    {
        Succeeded = false,
        FailureReason = AgenticFailureReason.Unavailable,
        ErrorMessage = reason,
        ExitCode = -1,
    };

    public static AgenticSessionResult LaunchFailed(string reason) => new()
    {
        Succeeded = false,
        FailureReason = AgenticFailureReason.LaunchFailed,
        ErrorMessage = reason,
        ExitCode = -1,
    };
}
