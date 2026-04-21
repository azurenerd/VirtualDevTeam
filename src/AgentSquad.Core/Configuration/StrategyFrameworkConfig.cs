namespace AgentSquad.Core.Configuration;

/// <summary>
/// Configuration for the multi-strategy code generation framework described in
/// docs/InteractiveCLIPlan.md. For every SE task, when <see cref="Enabled"/> is true
/// the <c>StrategyOrchestrator</c> runs each entry in <see cref="EnabledStrategies"/>
/// in parallel, scores their patches, and applies the winning patch to the PR branch.
/// </summary>
public class StrategyFrameworkConfig
{
    /// <summary>
    /// Master switch. When false (default), SE agents use the legacy single-shot path
    /// and the strategy framework is fully bypassed. Default is intentionally false until
    /// the baseline strategy stops being a marker-file stub (see p1-baseline-contract).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Which strategies are active. Defaults to baseline + mcp-enhanced.
    /// agentic-delegation is opt-in because --allow-all requires the Phase 3
    /// sandbox hardening (GIT_CONFIG_NOSYSTEM, scrubbed HOME/XDG, realpath allowlist,
    /// symlink/junction rejection) to be safe. Order is ignored; the orchestrator
    /// runs enabled strategies in parallel.
    /// </summary>
    public List<string> EnabledStrategies { get; set; } = new();

    /// <summary>
    /// Per-strategy display names keyed by strategy ID. Falls back to built-in names
    /// for baseline/mcp-enhanced/agentic-delegation, or the raw ID for unknown strategies.
    /// External frameworks (Squad, Claude Code, etc.) register their display name here.
    /// </summary>
    public Dictionary<string, string> DisplayNames { get; set; } = new();

    /// <summary>
    /// How often strategies run.
    /// - <c>always</c>: run every enabled strategy on every task (highest cost, most data).
    /// - <c>high-complexity-only</c>: only when task complexity >= 3.
    /// - <c>sampled-20</c>: on ~20% of tasks, selected deterministically by task id hash.
    /// - <c>first-wave-only</c>: only during the first ParallelDevelopment wave of a run.
    /// </summary>
    public string SamplingPolicy { get; set; } = "always";

    /// <summary>
    /// Review flow after a winner is merged into the PR branch.
    /// - <c>full-review</c>: Architect -> PM -> TE (existing pipeline, recommended).
    /// - <c>fast-track</c>: PR opens ready-for-review without extra architect pass.
    /// - <c>auto-merge</c>: fastforward merge if all gates pass (not recommended).
    /// </summary>
    public string PostWinnerFlow { get; set; } = "full-review";

    /// <summary>Per-strategy hard timeouts (wall clock).</summary>
    public TimeoutsConfig Timeouts { get; set; } = new();

    /// <summary>Concurrency pools for the Copilot CLI process layer.</summary>
    public ConcurrencyConfig Concurrency { get; set; } = new();

    /// <summary>Per-run cost ceiling; the circuit breaker skips expensive strategies when near budget.</summary>
    public BudgetConfig Budget { get; set; } = new();

    /// <summary>Agentic-delegation runtime limits (watchdog thresholds, tool-call caps).</summary>
    public AgenticConfig Agentic { get; set; } = new();

    /// <summary>
    /// Phase 5: adaptive strategy selection based on historical win/survival rate.
    /// OFF by default; intended to be turned on AFTER <c>val-e2e</c> has produced
    /// enough real experiment data to make demotion decisions statistically sound.
    /// </summary>
    public AdaptiveConfig Adaptive { get; set; } = new();

    /// <summary>Evaluator configuration: gates, LLM judge, reserved paths.</summary>
    public EvaluatorConfig Evaluator { get; set; } = new();

    /// <summary>Experiment ndjson output root. Resolved relative to the runner's cwd.</summary>
    public string ExperimentDataDirectory { get; set; } = "experiment-data";

    /// <summary>
    /// Root folder name for candidate worktrees, resolved relative to each SE agent's
    /// <c>Workspace.RepoPath</c> so agents don't collide on a shared path.
    /// </summary>
    public string CandidateDirectoryName { get; set; } = ".candidates";

    /// <summary>
    /// Optional absolute path to the <c>AgentSquad.McpServer.dll</c> used by
    /// <c>McpEnhancedStrategy</c>. When null/empty, <c>DefaultMcpServerLocator</c>
    /// probes well-known locations relative to <see cref="AppContext.BaseDirectory"/>.
    /// Setting this is the recommended production mode — probing is dev-only.
    /// </summary>
    public string? McpServerDllPath { get; set; }
}

public class TimeoutsConfig
{
    public int BaselineSeconds { get; set; } = 180;
    public int McpSeconds { get; set; } = 240;
    public int AgenticSeconds { get; set; } = 600;
    public int BuildGateSeconds { get; set; } = 120;
    public int AppStartGateSeconds { get; set; } = 30;
    public int EvaluatorTestsSeconds { get; set; } = 180;

    /// <summary>
    /// Per-strategy timeout overrides keyed by strategy ID. Used by the orchestrator
    /// to look up timeouts without hardcoding strategy IDs. Falls back to
    /// <see cref="BaselineSeconds"/> for unknown strategies.
    /// Auto-populated from the named properties when empty.
    /// </summary>
    public Dictionary<string, int> PerStrategy { get; set; } = new();

    /// <summary>Resolve the timeout for a given strategy ID.</summary>
    public TimeSpan GetTimeout(string strategyId)
    {
        if (PerStrategy.TryGetValue(strategyId, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        // Fallback to named properties for backward compatibility
        return strategyId switch
        {
            "agentic-delegation" => TimeSpan.FromSeconds(AgenticSeconds),
            "mcp-enhanced" => TimeSpan.FromSeconds(McpSeconds),
            _ => TimeSpan.FromSeconds(BaselineSeconds),
        };
    }
}

public class ConcurrencyConfig
{
    /// <summary>Hard upper bound across all pools. Prevents 9-process overload on dev laptops.</summary>
    public int GlobalMaxConcurrentProcesses { get; set; } = 6;
    public int SingleShotSlots { get; set; } = 4;
    public int CandidateSlots { get; set; } = 3;
    public int AgenticSlots { get; set; } = 2;

    /// <summary>
    /// On provider 429 or backoff signals, degrade A/B/C -> B/C -> C.
    /// Re-enables when budget and rate limits recover.
    /// </summary>
    public bool AutoDegradeOnRateLimit { get; set; } = true;
}

public class BudgetConfig
{
    /// <summary>Total token ceiling for a single kickoff run across all strategies. 0 = unlimited.</summary>
    public long MaxTokensPerRun { get; set; } = 2_000_000;

    /// <summary>Estimated minimum tokens needed for an agentic session. Skip agentic if below.</summary>
    public long AgenticMinimumTokens { get; set; } = 60_000;

    /// <summary>Estimated minimum tokens needed for an MCP-enhanced session.</summary>
    public long McpMinimumTokens { get; set; } = 30_000;
}

/// <summary>
/// Phase 5 adaptive selection. When <see cref="Enabled"/> is true, the
/// <c>AdaptiveStrategySelector</c> reads historical ndjson experiment records
/// and may drop strategies whose survival-rate over the recent window is below
/// <see cref="MinSurvivalRate"/>. Baseline is always kept.
/// </summary>
public class AdaptiveConfig
{
    public bool Enabled { get; set; } = false;
    public int WindowSize { get; set; } = 50;
    public int MinObservations { get; set; } = 10;
    public double MinSurvivalRate { get; set; } = 0.3;
}

public class EvaluatorConfig
{
    /// <summary>Relative path within the SE repo that no candidate patch may touch. Fails Gate2.</summary>
    public string ReservedPathPrefix { get; set; } = "tests/.evaluator-reserved/";

    /// <summary>Model tier to use for the LLM judge (premium/standard/budget/local).</summary>
    public string JudgeModelTier { get; set; } = "standard";

    /// <summary>Max patch characters passed to the LLM judge (truncated with a marker if exceeded).</summary>
    public int MaxJudgePatchChars { get; set; } = 40_000;

    /// <summary>Skip the LLM judge when only one candidate survives hard gates.</summary>
    public bool SkipJudgeOnSingleSurvivor { get; set; } = true;
}

/// <summary>
/// Agentic-delegation runtime limits. Applies only to the <c>agentic-delegation</c>
/// strategy (or any other call explicitly routed through
/// <c>CopilotCliProcessManager.ExecuteAgenticSessionAsync</c>).
/// </summary>
public class AgenticConfig
{
    /// <summary>
    /// Kill the session if no stdout activity is observed for this long. The
    /// watchdog resets its timer on every non-empty line of stdout. Default 180s.
    /// </summary>
    public int StuckSeconds { get; set; } = 180;

    /// <summary>
    /// Maximum number of tool-call events tolerated per session. Only enforced
    /// when JSON output mode is active (no stdout-regex fallback). When JSON is
    /// disabled, tool-call enforcement is off but the wall-clock timeout still
    /// applies. Default 500.
    /// </summary>
    public int ToolCallCap { get; set; } = 500;

    /// <summary>
    /// When <c>true</c>, post-run validation hashes the host user's global
    /// gitconfig before/after each session and fails the candidate if it changed.
    /// Stays on in Phase 3 as a belt-and-braces check against GIT_CONFIG_GLOBAL
    /// scrub bugs.
    /// </summary>
    public bool ValidateHostGitconfigUnchanged { get; set; } = true;

    /// <summary>
    /// Max process memory (bytes) applied to the Windows Job Object. 0 = no
    /// limit. Default 4 GiB.
    /// </summary>
    public long JobObjectMemoryLimitBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Max active processes in the Job Object tree. Default 64.
    /// </summary>
    public int JobObjectActiveProcessLimit { get; set; } = 64;
}
