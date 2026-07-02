namespace VirtualDevTeam.Core.Configuration;

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
    /// When true, the orchestrator checks for recovery checkpoints at the start of
    /// <c>RunCandidatesAsync</c>. If completed-but-unjudged candidates exist from a
    /// prior runner session (same baseSha), they are re-evaluated without re-executing
    /// the strategies from scratch. Default: true.
    /// </summary>
    public bool RecoverOrphanedCandidates { get; set; } = true;

    /// <summary>
    /// Which strategies are active. Defaults to baseline + mcp-enhanced.
    /// copilot-cli is opt-in because --allow-all requires the Phase 3
    /// sandbox hardening (GIT_CONFIG_NOSYSTEM, scrubbed HOME/XDG, realpath allowlist,
    /// symlink/junction rejection) to be safe. Order is ignored; the orchestrator
    /// runs enabled strategies in parallel.
    /// </summary>
    public List<string> EnabledStrategies { get; set; } = new();

    /// <summary>
    /// Per-strategy display names keyed by strategy ID. Falls back to built-in names
    /// for baseline/mcp-enhanced/copilot-cli, or the raw ID for unknown strategies.
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

    /// <summary>Revision round: judge-feedback-fix cycle configuration.</summary>
    public RevisionRoundConfig RevisionRound { get; set; } = new();

    /// <summary>Gate retry: gives gate-failed candidates a second attempt from scratch.</summary>
    public GateRetryConfig GateRetry { get; set; } = new();

    /// <summary>Experiment ndjson output root. Resolved relative to the runner's cwd.</summary>
    public string ExperimentDataDirectory { get; set; } = "experiment-data";

    /// <summary>
    /// When true, T-FINAL integration tasks bypass the strategy framework entirely and
    /// go straight to single-pass legacy implementation. When false (default), T-FINAL
    /// runs through the strategy framework with a focused prompt (build→test→fix-only),
    /// pre-computed focus files from merged PRs, and a higher ToolCallCap override (750).
    /// </summary>
    public bool SkipStrategiesForFinalIntegration { get; set; } = false;

    /// <summary>
    /// When true, the SE agent ignores a previously-merged integration PR during recovery
    /// and re-runs T-FINAL from scratch. Useful for iterative testing of T-FINAL prompt/strategy
    /// changes without requiring a full pipeline reset. The old integration branch is deleted
    /// and recreated. Default false (normal recovery behaviour).
    /// </summary>
    public bool ForceRedoFinalIntegration { get; set; } = false;

    /// <summary>
    /// How candidates are evaluated: FullWorktree (build + run + screenshot),
    /// SparseWorktree (changed paths only + build files), PatchOnly (LLM judge only — no build/run).
    /// PatchOnly is fastest for large repos where builds take 30+ minutes.
    /// </summary>
    public CandidateEvaluationMode EvaluationMode { get; set; } = CandidateEvaluationMode.FullWorktree;

    /// <summary>
    /// Root folder name for candidate worktrees, resolved relative to each SE agent's
    /// <c>Workspace.RepoPath</c> so agents don't collide on a shared path.
    /// </summary>
    public string CandidateDirectoryName { get; set; } = ".candidates";

    /// <summary>
    /// Optional absolute path to the <c>VirtualDevTeam.McpServer.dll</c> used by
    /// <c>McpEnhancedStrategy</c>. When null/empty, <c>DefaultMcpServerLocator</c>
    /// probes well-known locations relative to <see cref="AppContext.BaseDirectory"/>.
    /// Setting this is the recommended production mode — probing is dev-only.
    /// </summary>
    public string? McpServerDllPath { get; set; }
}

public class TimeoutsConfig
{
    public int BaselineSeconds { get; set; } = 0;
    public int McpSeconds { get; set; } = 0;
    public int AgenticSeconds { get; set; } = 0;
    /// <summary>Squad spawns sub-agents that each invoke copilot CLI, needing 20-30min total.</summary>
    public int SquadSeconds { get; set; } = 0;
    public int BuildGateSeconds { get; set; } = 0;
    public int AppStartGateSeconds { get; set; } = 0;
    public int EvaluatorTestsSeconds { get; set; } = 0;

    /// <summary>Wall-clock timeout for each CLI-native review session (judge or peer review).</summary>
    public int CliReviewSeconds { get; set; } = 0;

    /// <summary>
    /// Per-strategy timeout overrides keyed by strategy ID. Used by the orchestrator
    /// to look up timeouts without hardcoding strategy IDs. Falls back to
    /// <see cref="BaselineSeconds"/> for unknown strategies.
    /// Auto-populated from the named properties when empty.
    /// </summary>
    public Dictionary<string, int> PerStrategy { get; set; } = new();

    /// <summary>Resolve the timeout for a given strategy ID. Returns Timeout.InfiniteTimeSpan when 0 (disabled).</summary>
    public TimeSpan GetTimeout(string strategyId)
    {
        if (PerStrategy.TryGetValue(strategyId, out var seconds))
            return ToTimeSpan(seconds);

        // Fallback to named properties for backward compatibility
        var raw = strategyId switch
        {
            "copilot-cli" or "agentic-delegation" => AgenticSeconds,
            "mcp-enhanced" => McpSeconds,
            "squad" => SquadSeconds,
            _ => BaselineSeconds,
        };
        return ToTimeSpan(raw);
    }

    /// <summary>Convert seconds config to TimeSpan. 0 or negative → Timeout.InfiniteTimeSpan (no limit).</summary>
    public static TimeSpan ToTimeSpan(int seconds) =>
        seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
}

public class ConcurrencyConfig
{
    /// <summary>Hard upper bound across all pools. Prevents 9-process overload on dev laptops.</summary>
    public int GlobalMaxConcurrentProcesses { get; set; } = 6;
    public int SingleShotSlots { get; set; } = 4;
    public int CandidateSlots { get; set; } = 3;
    public int AgenticSlots { get; set; } = 2;
    /// <summary>Concurrent CLI-native review sessions (judge + peer review).</summary>
    public int ReviewSlots { get; set; } = 2;

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

    /// <summary>Model tier for the visual (screenshot) judge. Should be a vision-capable model.</summary>
    public string VisualJudgeModelTier { get; set; } = "standard";

    /// <summary>
    /// Max patch characters passed to the LLM judge. 0 = no truncation (recommended for
    /// large-context models like Opus 4.6). Still sanitizes control characters.
    /// </summary>
    public int MaxJudgePatchChars { get; set; } = 0;

    /// <summary>Skip the LLM judge when only one candidate survives hard gates.</summary>
    public bool SkipJudgeOnSingleSurvivor { get; set; } = true;

    /// <summary>
    /// Maximum retry attempts for CLI-native judge per candidate. 1 = no retry (single attempt).
    /// Default 1 — retrying non-transient errors (invalid JSON) wastes 5+ min per attempt
    /// with the same result. Set to 2+ only if transient CLI failures are common.
    /// </summary>
    public int JudgeMaxRetries { get; set; } = 1;

    /// <summary>
    /// When true, use CLI-native judge (launches Copilot CLI pointed at the worktree directory)
    /// instead of the text-based LlmJudge that passes patches through prompts. Eliminates
    /// truncation issues for large patches. Falls back to text-based when CLI is unavailable.
    /// </summary>
    public bool UseCliNativeJudge { get; set; } = true;

    /// <summary>
    /// Timeout in minutes for the media capture phase (Playwright interaction, screenshots,
    /// video, GIF). On timeout, candidate is marked "media-incomplete" but remains eligible
    /// for scoring. 0 = no timeout (legacy behavior). Default 20 min.
    /// </summary>
    public int MediaCaptureTimeoutMinutes { get; set; } = 20;

    /// <summary>
    /// Timeout in minutes for the LLM judge scoring phase (batch-score all survivors).
    /// On timeout, partial scores are kept and unscored candidates get Score=null.
    /// 0 = no timeout (legacy behavior). Default 15 min.
    /// </summary>
    public int JudgeScoringTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Timeout in minutes for the visual (screenshot) judge scoring phase.
    /// On timeout, VisualsScore is set to null for all candidates and evaluation
    /// proceeds without visual scores. 0 = no timeout (legacy behavior). Default 10 min.
    /// </summary>
    public int VisualScoringTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// When true, enables the emergency winner selection fallback. If EvaluateAsync throws
    /// an unrecoverable exception, the system attempts to select the best candidate from
    /// whatever results are available rather than losing all work. Default true.
    /// </summary>
    public bool EmergencyWinnerEnabled { get; set; } = true;

    /// <summary>
    /// Strategy ID substring to prefer as last-resort tiebreaker when no objective signals
    /// (scores, build status, diff size) differentiate emergency winner candidates.
    /// Default "squad". Set to empty string to disable the preference.
    /// </summary>
    public string EmergencyWinnerDefault { get; set; } = "squad";

    /// <summary>
    /// Minutes a single candidate can remain in Running state before
    /// <see cref="Orchestrator.StrategyEvaluationStuckDetector"/> fires a candidate-stuck finding.
    /// Default 60.
    /// </summary>
    public int StuckCandidateMinutes { get; set; } = 60;
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
    /// watchdog resets its timer on every non-empty line of stdout. Default 600s
    /// (10 minutes) — if the CLI is actually working it produces tool-call and
    /// reasoning output; 10 minutes of complete silence means the session is
    /// dead or stuck (MCP init failure, auth hang, pipe deadlock).
    /// Set to 0 to disable (not recommended).
    /// </summary>
    public int StuckSeconds { get; set; } = 600;

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

/// <summary>
/// Configuration for the revision round feature. When enabled, frameworks get one
/// chance to fix their code based on judge feedback before final scoring.
/// Flow: Initial Dev → Initial Judge (scores + feedback) → Revision Dev (same worktree) → Final Judge → Winner
/// </summary>
public class RevisionRoundConfig
{
    /// <summary>
    /// Master switch. When false (default), the flow is identical to today — zero regression risk.
    /// When true, surviving candidates receive judge feedback and get one revision attempt.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true (default), skip the initial LLM judge scoring before revision. Candidates go through
    /// gates only (build + screenshots) initially, then get judged ONCE at the final evaluation.
    /// This halves LLM cost/time. When false, the full initial-judge → revision → final-judge flow runs.
    /// </summary>
    public bool SkipInitialJudgment { get; set; } = true;

    /// <summary>
    /// Hard wall-clock timeout for each strategy's revision attempt (seconds).
    /// 0 means no timeout (infinite). Default: 0 (disabled).
    /// </summary>
    public int MaxRevisionSeconds { get; set; } = 0;

    /// <summary>
    /// Model tier for the rubber-duck adversarial feedback generator.
    /// Uses a DIFFERENT tier than the judge to get genuine perspective diversity.
    /// Default: "standard".
    /// </summary>
    public string FeedbackModelTier { get; set; } = "standard";
}

/// <summary>
/// Gate retry: when a candidate fails a build gate, re-execute the strategy from
/// scratch with a shorter timeout. This helps transient failures (timeouts, process
/// crashes) recover without requiring manual intervention.
/// </summary>
public class GateRetryConfig
{
    /// <summary>
    /// Master switch. When true (default), gate-failed candidates get one retry attempt.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of retries per candidate. Default: 1.
    /// </summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>
    /// Timeout for each retry attempt (seconds). 0 means no timeout. Default: 0 (disabled).
    /// </summary>
    public int RetryTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Only retry candidates that failed with these gate IDs. Empty = retry all gate failures.
    /// Includes "gate1-output" because empty patches from CLI strategies are often transient
    /// (network failures, auth timeouts, tool execution errors the CLI swallowed).
    /// </summary>
    public List<string> RetryableGates { get; set; } = new() { "strategy-failed", "gate2-build", "gate1-output" };
}

/// <summary>
/// How candidates are evaluated after code generation.
/// </summary>
public enum CandidateEvaluationMode
{
    /// <summary>Full checkout + build + run + screenshot (current default).</summary>
    FullWorktree,

    /// <summary>Checkout only changed paths + build files (faster for large repos).</summary>
    SparseWorktree,

    /// <summary>LLM judge only — no build, no run. Cheapest evaluation for massive repos.</summary>
    PatchOnly,
}
