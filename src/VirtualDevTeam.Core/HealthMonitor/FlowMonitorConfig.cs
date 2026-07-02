using System.Collections.Generic;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Configuration for the FlowMonitor watchdog service. Bound from
/// VirtualDevTeam:FlowMonitor in appsettings.json (or develop-settings.json).
/// </summary>
public sealed class FlowMonitorConfig
{
    /// <summary>Master switch. When false, the service still runs but does nothing per tick.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the service ticks. Floored at 10s to avoid thrash.
    /// 90s is sufficient — agent-stuck detection has a 30-minute threshold,
    /// so checking every 90s detects within 1.5 min of the threshold.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 90;

    /// <summary>
    /// Hard ceiling on how many corrective actions the FlowMonitor may take in a rolling
    /// 1-hour window. Prevents runaway action loops if a detector misfires repeatedly.
    /// Pipeline-unblocking actions (gate-stuck, pr-approval-stuck) are exempt from this
    /// limit since they are idempotent label writes that must always fire to prevent stalls.
    /// </summary>
    public int MaxActionsPerHour { get; set; } = 30;

    /// <summary>
    /// How long an open finding's dedup-key suppresses re-creation of the same finding.
    /// E.g., if AgentStuck for agent X fires once, we won't re-fire for the same agent for
    /// this many minutes — stops the audit log from filling with the same row.
    /// Increased to 60 from 15 because the old value caused repeated findings for the same
    /// agent within a single strategy evaluation cycle (~30-45 min).
    /// </summary>
    public int DedupWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Confidence threshold for AI-recommended actions (0.0-1.0). Currently only consulted
    /// when an action handler asks an LLM for a fix recommendation. Below this threshold,
    /// the finding is logged but no action is taken — operator decides via dashboard.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.75;

    /// <summary>
    /// Initial wait before the FlowMonitor starts ticking on runner boot. Default 15s
    /// keeps log noise tidy at startup. Set to 0 in tests to avoid slow spin-up. (T0.5)
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 15;

    /// <summary>
    /// Retention window for SQLite tables (flow_findings, flow_actions, flow_monitor_ticks).
    /// Records older than this are pruned daily. Set to 0 to disable pruning. Default 14d
    /// balances "operator can investigate yesterday" with "DB doesn't grow to 500MB". (T0.9)
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Threshold (minutes) for the AgentStuckDetector — when an agent is in Working state
    /// without a status-reason change for at least this long, the detector fires.
    /// Default 45m: legitimate Strategy-framework + Squad/Copilot CLI candidates + LLM Judge +
    /// Playwright eval cycles can take 30-45m on complex tasks (T1 Project Foundation observed
    /// at 33m wall-clock during the 2026-05-10 run). 30m caused chronic false positives.
    /// NOTE: this value is captured at AgentStuckDetector construction; restart required for
    /// changes to take effect (it's not hot-reloadable). Set in appsettings.json under
    /// VirtualDevTeam:FlowMonitor:StuckThresholdMinutes.
    /// </summary>
    public int StuckThresholdMinutes { get; set; } = 45;

    /// <summary>
    /// Threshold (minutes) for the UnmergedApprovedPrDetector — when a PR has both
    /// architect-approved AND pm-approved labels (plus tests-added if inline test workflow)
    /// but hasn't been merged after this many minutes idle, the detector fires and the
    /// paired merge-approved-pr action attempts a safety-net merge.
    ///
    /// Default 5m: SE Leader's normal merge loop runs every ~15s during WorkOnOwnTasksAsync,
    /// so 5m is well past any legitimate delay. Background: in the 2026-05-10 multi-PR run
    /// (cdbb396b), PR #1394 sat fully-approved for 30+ minutes while the SE was busy running
    /// Strategy candidates for a different task and couldn't poll its merge loop. This action
    /// is the catcher's mitt for that bottleneck — see post-run3-merge-bottleneck. Captured at
    /// detector construction; restart required for changes to take effect.
    /// </summary>
    public int MergeApprovedPrStuckMinutes { get; set; } = 5;

    /// <summary>
    /// Per-detector enable map. Key = DetectorId. Default true if not present.
    /// Allows turning off individual detectors that misfire on a project without
    /// disabling the whole service.
    /// </summary>
    public Dictionary<string, bool> Detectors { get; set; } = new();

    /// <summary>
    /// Per-action enable map. Key = ActionType. Default true if not present.
    /// </summary>
    public Dictionary<string, bool> Actions { get; set; } = new();

    /// <summary>
    /// Minutes before FlowMonitor auto-approves any pending gate or decision.
    /// 0 = disabled (never auto-approve). Default 30.
    /// When a gate or decision has been pending longer than this threshold,
    /// the GateStuckDetector fires and AutoApproveGateAction resolves it.
    /// </summary>
    public int AutoApprovalMinutes { get; set; } = 30;

    /// <summary>
    /// Minutes before a partially-approved (not all reviewers) PR triggers a
    /// merge escalation finding. 0 = disabled. Default 90.
    /// </summary>
    public int PrMergeEscalationMinutes { get; set; } = 90;

    /// <summary>
    /// When true, FlowMonitor can auto-merge fully-approved PRs that are stuck.
    /// Default true. Set false to require human intervention for all merges.
    /// </summary>
    public bool EnableAutoMerge { get; set; } = true;

    /// <summary>
    /// Minutes a fully-approved PR must be idle before FlowMonitor auto-merges it.
    /// Only used when EnableAutoMerge is true. Default 5.
    /// </summary>
    public int PrMergeAutoApprovalMinutes { get; set; } = 5;

    /// <summary>
    /// Minutes before FlowMonitor auto-approves a stuck PR review label
    /// (architect-approved, tests-added, pm-approved). When a review stage has been
    /// pending longer than this threshold, AutoApproveReviewAction adds the missing
    /// label directly. 0 = disabled (never auto-approve reviews). Default 45.
    ///
    /// Agents review sequentially with 60s poll intervals and complex reviews can take
    /// 10-30 minutes (strategy framework + LLM analysis). The old 15-min default caused
    /// FlowMonitor to add labels before agents finished, flooding the Approvals page
    /// with auto-approval audit entries.
    /// </summary>
    public int ReviewAutoApprovalMinutes { get; set; } = 45;

    /// <summary>
    /// Threshold (minutes) for a PR in the `in-progress` (development) phase.
    /// When a PR has `in-progress` label but no `ready-for-review` for longer than
    /// this, the LabelTransitionTimeoutDetector fires. Default 60m — most PRs
    /// complete implementation in 30-45min; 60m catches genuinely stuck agents
    /// without false-firing on strategy-framework runs.
    /// </summary>
    public int DevelopmentPhaseThresholdMinutes { get; set; } = 60;

    /// <summary>
    /// Configuration for the proactive AI pipeline assessment loop.
    /// The assessment service is a separate BackgroundService that polls like a human
    /// checking the dashboard — not a detector (which has a 2s budget).
    /// </summary>
    public AssessmentConfig Assessment { get; set; } = new();

    public bool IsDetectorEnabled(string detectorId) =>
        !Detectors.TryGetValue(detectorId, out var enabled) || enabled;

    public bool IsActionEnabled(string actionType) =>
        !Actions.TryGetValue(actionType, out var enabled) || enabled;
}
