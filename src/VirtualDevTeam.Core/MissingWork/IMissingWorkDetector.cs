namespace VirtualDevTeam.Core.MissingWork;

/// <summary>
/// Detector contract for the MissingWorkRecommendation subsystem. Mirrors
/// <c>VirtualDevTeam.Core.HealthMonitor.Detectors.IFlowDetector</c> but emits
/// gap-findings instead of in-flight problem findings.
///
/// <para>Implementation contract:</para>
/// <list type="bullet">
///   <item>Stateless — any cross-tick state lives in <see cref="MissingWorkContext"/>.</item>
///   <item>Must NOT throw — wrap in try/catch internally and log warnings.</item>
///   <item>Must return within ~5 seconds per tick.</item>
///   <item>Must produce stable <see cref="MissingWorkFinding.DedupKey"/>s.</item>
/// </list>
/// </summary>
public interface IMissingWorkDetector
{
    string DetectorId { get; }
    Task<IReadOnlyList<MissingWorkFinding>> DetectAsync(MissingWorkContext ctx, CancellationToken ct);
}

/// <summary>
/// Per-tick context provided to each detector. Carries the project workspace path,
/// snapshots of open + recently-closed issues, and timing info.
/// </summary>
public sealed record MissingWorkContext
{
    /// <summary>Absolute path to the workspace under inspection (typically an agent's repo clone).</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Snapshot of open issues. Pre-fetched so detectors don't pay the API cost individually.</summary>
    public required IReadOnlyList<IssueRef> OpenIssues { get; init; }

    /// <summary>Snapshot of recently-closed issues (last 30 days).</summary>
    public required IReadOnlyList<IssueRef> RecentlyClosedIssues { get; init; }

    public DateTime Now { get; init; } = DateTime.UtcNow;
}

public sealed record IssueRef(int Number, string Title, IReadOnlyList<string> Labels, string Body = "");
