using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Dashboard-facing read model for strategy candidate state. Two impls:
/// - <see cref="InProcessStrategiesDataService"/> when the dashboard is hosted in the Runner.
/// - <see cref="HttpStrategiesDataService"/> when standalone — calls the Runner REST API.
/// </summary>
public interface IStrategiesDataService
{
    Task<IReadOnlyList<TaskSnapshot>> GetActiveTasksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskSnapshot>> GetRecentTasksAsync(int limit = 50, CancellationToken ct = default);
    Task<EnabledStrategiesInfo> GetEnabledAsync(CancellationToken ct = default);
    Task<bool> CancelOrchestrationAsync(string runId, string taskId, CancellationToken ct = default);
    Task<bool> CancelCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default);
    Task<bool> ResetCandidateAsync(string runId, string taskId, string strategyId, CancellationToken ct = default);

    /// <summary>Number of currently active strategy orchestrations. Updated in real-time for in-process mode.</summary>
    int ActiveCount { get; }

    /// <summary>Fires when active count changes. In-process only; standalone implementations may leave this as a no-op.</summary>
    event Action? OnActiveCountChanged;
}

public sealed record EnabledStrategiesInfo(bool MasterEnabled, IReadOnlyList<string> EnabledStrategies);
