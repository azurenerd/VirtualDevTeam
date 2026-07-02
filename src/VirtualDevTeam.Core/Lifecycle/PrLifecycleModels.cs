namespace VirtualDevTeam.Core.Lifecycle;

/// <summary>
/// Status of a single PR lifecycle stage.
/// </summary>
public enum StageStatus
{
    /// <summary>Stage hasn't started yet — prerequisites not met.</summary>
    NotStarted,
    /// <summary>Stage is actively in progress — waiting for the responsible agent.</summary>
    InProgress,
    /// <summary>Stage completed successfully.</summary>
    Complete,
    /// <summary>Stage was skipped (e.g., TE disabled, no peer reviewers, gate disabled).</summary>
    Skipped
}

/// <summary>
/// Well-known lifecycle stage identifiers. Used for machine-readable stage matching
/// across UI, FlowMonitor, and agent consumers.
/// </summary>
public static class StageIds
{
    public const string Development = "development";
    public const string ArchitectReview = "architect-review";
    public const string PeerReview = "peer-review";
    public const string Testing = "testing";
    public const string SecurityAudit = "security-audit";
    public const string PmReview = "pm-review";
    public const string Merge = "merge";
}

/// <summary>
/// A single stage in the PR merge lifecycle. Built dynamically from project configuration
/// so the stage list adapts to enabled/disabled gates, review agents, and workflow mode.
/// </summary>
public sealed record PrLifecycleStage
{
    /// <summary>Machine-readable identifier (see <see cref="StageIds"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name (e.g., "Architect Review").</summary>
    public required string Name { get; init; }

    /// <summary>Emoji icon for dashboard rendering.</summary>
    public required string Icon { get; init; }

    /// <summary>Current status of this stage.</summary>
    public StageStatus Status { get; init; } = StageStatus.NotStarted;

    /// <summary>When this stage became active (entered InProgress). Null if not started.</summary>
    public DateTimeOffset? EnteredAt { get; init; }

    /// <summary>When this stage completed. Null if not complete.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Display name of the agent/role that completed this stage (e.g., "Architect").</summary>
    public string? Actor { get; init; }

    /// <summary>Why this stage was skipped (e.g., "TE disabled in config"). Null unless Skipped.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Order index for rendering (0-based).</summary>
    public int Order { get; init; }
}

/// <summary>
/// Complete lifecycle state for a single PR. Computed by <see cref="PrLifecycleCalculator"/>
/// from labels, comments, and project configuration. Consumed by Timeline UI, FlowMonitor
/// detectors, diagnostic enrichers, and agent merge logic.
/// </summary>
public sealed record PrLifecycle
{
    /// <summary>PR number this lifecycle describes.</summary>
    public required int PrNumber { get; init; }

    /// <summary>Ordered list of applicable stages (built from config).</summary>
    public required IReadOnlyList<PrLifecycleStage> Stages { get; init; }

    /// <summary>The currently active stage (first InProgress stage), or null if all complete/not started.</summary>
    public PrLifecycleStage? CurrentStage =>
        Stages.FirstOrDefault(s => s.Status == StageStatus.InProgress);

    /// <summary>
    /// The stage that appears stuck (InProgress for too long or blocked by a prerequisite).
    /// Null if nothing is stuck.
    /// </summary>
    public PrLifecycleStage? BlockedStage { get; init; }

    /// <summary>Human-readable explanation of why the lifecycle is blocked. Null if not blocked.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>The agent role that should act next to advance the lifecycle.</summary>
    public string? NextRequiredActor { get; init; }

    /// <summary>What's missing to advance to the next stage.</summary>
    public IReadOnlyList<string> MissingRequirements { get; init; } = Array.Empty<string>();

    /// <summary>Whether all stages are complete (PR is ready to merge or already merged).</summary>
    public bool IsReadyForMerge => Stages.All(s => s.Status is StageStatus.Complete or StageStatus.Skipped);

    /// <summary>Whether the PR has been merged.</summary>
    public bool IsMerged => Stages.Any(s => s.Id == StageIds.Merge && s.Status == StageStatus.Complete);

    /// <summary>When this lifecycle was computed.</summary>
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
}
