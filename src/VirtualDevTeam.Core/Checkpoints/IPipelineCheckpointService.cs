namespace VirtualDevTeam.Core.Checkpoints;

/// <summary>
/// Captures and restores pipeline state snapshots at key milestones.
/// Enables quick recovery after fixes/restarts without full pipeline resets.
/// </summary>
public interface IPipelineCheckpointService
{
    /// <summary>Capture a checkpoint of the current pipeline state.</summary>
    Task<CheckpointResult> CaptureAsync(string name, CheckpointTrigger trigger, CancellationToken ct = default);

    /// <summary>Restore pipeline state from a named checkpoint. Runner must be stopped first.</summary>
    Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default);

    /// <summary>List all available checkpoints, newest first.</summary>
    Task<IReadOnlyList<CheckpointInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Delete a specific checkpoint.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>Get the most recent checkpoint, if any.</summary>
    Task<CheckpointInfo?> GetLatestAsync(CancellationToken ct = default);
}

/// <summary>What triggered the checkpoint capture.</summary>
public enum CheckpointTrigger
{
    /// <summary>Manually captured by operator via dashboard or API.</summary>
    Manual,
    /// <summary>Before first strategy run (engineering plan ready).</summary>
    BeforePR,
    /// <summary>After a PR is marked ready-for-review.</summary>
    AfterPRReady,
    /// <summary>After a PR is merged.</summary>
    AfterPRMerge,
    /// <summary>After all tasks in a wave are merged.</summary>
    WaveComplete,
    /// <summary>On workflow phase transition.</summary>
    PhaseTransition,
    /// <summary>Final integration PR merged.</summary>
    PipelineComplete,
}

/// <summary>Metadata about a captured checkpoint.</summary>
public record CheckpointInfo
{
    public required string Name { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required CheckpointTrigger Trigger { get; init; }
    public required string Phase { get; init; }
    public long DiskSizeBytes { get; init; }

    /// <summary>Number of open PRs at capture time.</summary>
    public int OpenPRCount { get; init; }

    /// <summary>Number of open work items at capture time.</summary>
    public int OpenWorkItemCount { get; init; }

    /// <summary>Dev platform kind at capture time (Local, GitHub, AzureDevOps).</summary>
    public string? DevPlatformKind { get; init; }

    /// <summary>Human-readable description of what was happening when captured.</summary>
    public string? Description { get; init; }
}

/// <summary>Result of a checkpoint capture operation.</summary>
public record CheckpointResult
{
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
    public CheckpointInfo? Info { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>If a checkpoint was evicted to make room, its name.</summary>
    public string? EvictedCheckpoint { get; init; }

    public static CheckpointResult Success(CheckpointInfo info, TimeSpan elapsed, string? evicted = null) =>
        new() { Succeeded = true, Info = info, Elapsed = elapsed, EvictedCheckpoint = evicted };

    public static CheckpointResult Failure(string error, TimeSpan elapsed) =>
        new() { Succeeded = false, Error = error, Elapsed = elapsed };
}

/// <summary>Result of a checkpoint restore operation.</summary>
public record RestoreResult
{
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Warnings about state that couldn't be fully restored
    /// (e.g., remote platform state may have diverged).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static RestoreResult Success(TimeSpan elapsed, IReadOnlyList<string>? warnings = null) =>
        new() { Succeeded = true, Elapsed = elapsed, Warnings = warnings ?? Array.Empty<string>() };

    public static RestoreResult Failure(string error, TimeSpan elapsed) =>
        new() { Succeeded = false, Error = error, Elapsed = elapsed };
}
