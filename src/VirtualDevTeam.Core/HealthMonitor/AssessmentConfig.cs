namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Configuration for the proactive AI pipeline assessment loop.
/// Nested under <see cref="FlowMonitorConfig.Assessment"/>.
/// Live-reloadable via <c>IOptionsMonitor&lt;FlowMonitorConfig&gt;</c>.
/// </summary>
public sealed class AssessmentConfig
{
    /// <summary>Master switch. When false, the assessment BackgroundService idles.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base polling interval in seconds. Default 5 minutes.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Minimum interval floor (seconds) for adaptive cadence speedup.
    /// Prevents runaway polling when issues are detected.
    /// </summary>
    public int MinIntervalSeconds { get; set; } = 90;

    /// <summary>Maximum interval ceiling (seconds) for adaptive cadence slowdown.</summary>
    public int MaxIntervalSeconds { get; set; } = 600;

    /// <summary>LLM timeout in seconds. Budget tier needs ~10-15s, premium ~30s.</summary>
    public int LlmTimeoutSeconds { get; set; } = 30;

    /// <summary>Model tier for assessment LLM calls. Default "budget" for cost-effective frequent polling.</summary>
    public string ModelTier { get; set; } = "budget";

    /// <summary>
    /// Minimum confidence threshold for creating FlowFindings from AI issues.
    /// Issues below this threshold are still persisted in assessments but don't
    /// create findings visible to the escalation ladder.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// When true, high-confidence AI issues create FlowFindings (Warning-capped).
    /// When false, assessments are persist-only with no FlowFinding side effects.
    /// </summary>
    public bool CreateFindingsOnIssues { get; set; } = true;

    /// <summary>
    /// Daily cap on total assessments to prevent runaway adaptive cadence.
    /// When hit, interval drops to 30-minute floor for the rest of the day.
    /// </summary>
    public int MaxAssessmentsPerDay { get; set; } = 200;

    /// <summary>
    /// Grace period (seconds) after a workflow phase transition before resuming assessments.
    /// Prevents false positives during expected turbulence.
    /// </summary>
    public int PhaseTransitionGraceSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum characters for the pipeline snapshot context sent to the LLM.
    /// Timeline spans are tiered/pruned to stay within this budget.
    /// </summary>
    public int ContextBudgetChars { get; set; } = 40000;
}
