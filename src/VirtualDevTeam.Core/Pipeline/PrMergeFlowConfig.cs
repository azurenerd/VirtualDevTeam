namespace VirtualDevTeam.Core.Pipeline;

/// <summary>
/// Configuration for the PR Merge-Flow Timeline subsystem. Bound from
/// appsettings.json "PrMergeFlow" section.
/// </summary>
public sealed class PrMergeFlowConfig
{
    /// <summary>
    /// Server-side snapshot cache TTL in seconds. Default 10s — balances freshness
    /// with avoiding hot-path recompute on every operator click.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 10;

    /// <summary>
    /// Per-step stuck-warning thresholds in seconds. Per R6 from rubber-duck —
    /// different steps have very different normal durations.
    /// </summary>
    public Dictionary<string, int> StuckThresholdsSeconds { get; set; } = new()
    {
        ["self-assessment"] = 300,
        ["architect-review"] = 600,
        ["pm-review"] = 600,
        ["se-peer-review"] = 600,
        ["te-inline-tests"] = 1200,    // tests can legitimately run 15-20min
        ["security-audit"] = 1200,
        ["human-gate"] = 86400,        // human can take hours; only warn at 24h
        ["mergeable-ci"] = 180,
        ["merge"] = 60,
    };

    /// <summary>
    /// Fallback stuck threshold in seconds for steps not listed in
    /// <see cref="StuckThresholdsSeconds"/>.
    /// </summary>
    public int DefaultStuckThresholdSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum log lines to include in PrMergeFlowStep.RelatedLogLines. Trim heavy
    /// snapshots so the JSON payload stays small.
    /// </summary>
    public int MaxLogLinesPerStep { get; set; } = 5;
}
