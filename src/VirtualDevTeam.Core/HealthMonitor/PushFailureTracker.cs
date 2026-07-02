namespace VirtualDevTeam.Core.HealthMonitor;

using System.Collections.Concurrent;

/// <summary>
/// Thread-safe singleton that records git push/rebase failures from agent workspaces.
/// The <see cref="Detectors.PushFailureDetector"/> reads from this tracker each tick.
/// </summary>
public sealed class PushFailureTracker
{
    private readonly ConcurrentQueue<PushFailureEntry> _failures = new();
    private const int MaxEntries = 100;

    /// <summary>Record a push failure from an agent workspace.</summary>
    public void RecordFailure(string agentId, string? displayName, string? branch, string error)
    {
        _failures.Enqueue(new PushFailureEntry(
            AgentId: agentId,
            DisplayName: displayName,
            Branch: branch,
            Error: error.Length > 300 ? error[..300] : error,
            OccurredAt: DateTimeOffset.UtcNow));

        // Trim to bounded size
        while (_failures.Count > MaxEntries)
            _failures.TryDequeue(out _);
    }

    /// <summary>Get all failures since a given time.</summary>
    public IReadOnlyList<PushFailureEntry> GetFailuresSince(DateTimeOffset since)
    {
        return _failures.Where(f => f.OccurredAt >= since).ToList();
    }

    /// <summary>Get failures for a specific agent since a given time.</summary>
    public IReadOnlyList<PushFailureEntry> GetFailuresForAgent(string agentId, DateTimeOffset since)
    {
        return _failures
            .Where(f => f.OccurredAt >= since && string.Equals(f.AgentId, agentId, StringComparison.Ordinal))
            .ToList();
    }
}

/// <summary>A single push failure event.</summary>
public sealed record PushFailureEntry(
    string AgentId,
    string? DisplayName,
    string? Branch,
    string Error,
    DateTimeOffset OccurredAt);
