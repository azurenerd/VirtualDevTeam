namespace VirtualDevTeam.Core.Merging;

/// <summary>
/// Serializes merge attempts so only one PR merges at a time.
/// Prevents the N² thrash problem where multiple SEs concurrently merge,
/// each success invalidating every other in-flight branch.
///
/// NOT a full queue with persistence — just a semaphore lock.
/// Existing SE poll loops continue; they acquire the lock before merging.
/// </summary>
public interface IMergeCoordinator
{
    /// <summary>
    /// Run a merge action exclusively — only one merge runs at a time.
    /// If another merge is in progress, this call blocks until it completes.
    /// Deduplicates by PR number — if the same PR is already being merged, returns immediately.
    /// </summary>
    Task<MergeCoordinatorResult> RunExclusiveAsync(
        int prNumber,
        string mergerAgentId,
        Func<CancellationToken, Task<MergeOutcome>> mergeAction,
        CancellationToken ct);

    /// <summary>Current merge queue status for dashboard/health snapshot.</summary>
    MergeQueueStatus GetStatus();
}

/// <summary>Outcome of a coordinated merge attempt.</summary>
public enum MergeOutcome
{
    /// <summary>PR merged successfully.</summary>
    Merged,
    /// <summary>Merge conflict detected — resolution needed.</summary>
    ConflictDetected,
    /// <summary>Conflict was resolved by AI and merge succeeded.</summary>
    ConflictResolved,
    /// <summary>PR was already merged or closed.</summary>
    NotOpen,
    /// <summary>Merge failed — conflict unresolvable.</summary>
    Failed,
    /// <summary>Skipped — this PR is already being processed.</summary>
    Skipped,
}

/// <summary>Result from the coordinator wrapping a merge attempt.</summary>
public record MergeCoordinatorResult
{
    public required MergeOutcome Outcome { get; init; }
    public string? Detail { get; init; }
    public int PrNumber { get; init; }

    public static MergeCoordinatorResult Success(int prNumber) =>
        new() { Outcome = MergeOutcome.Merged, PrNumber = prNumber };

    public static MergeCoordinatorResult Conflict(int prNumber, string detail) =>
        new() { Outcome = MergeOutcome.ConflictDetected, PrNumber = prNumber, Detail = detail };

    public static MergeCoordinatorResult Skip(int prNumber, string reason) =>
        new() { Outcome = MergeOutcome.Skipped, PrNumber = prNumber, Detail = reason };
}

/// <summary>Snapshot of the merge queue for dashboard/health API.</summary>
public record MergeQueueStatus
{
    /// <summary>Number of SEs waiting to merge.</summary>
    public int PendingCount { get; init; }

    /// <summary>PR currently being merged (null if idle).</summary>
    public int? ActivePrNumber { get; init; }

    /// <summary>Agent ID performing the active merge.</summary>
    public string? ActiveAgentId { get; init; }

    /// <summary>How long the active merge has been running.</summary>
    public TimeSpan? ActiveDuration { get; init; }

    /// <summary>PRs that failed merge and are awaiting conflict resolution or escalation.</summary>
    public int StuckCount { get; init; }
}
