namespace AgentSquad.Core.Services;

/// <summary>
/// Thread-safe singleton class that tracks metrics for SME agent operations.
/// Uses <see cref="Interlocked.Increment"/> for all counter updates to ensure
/// thread safety across concurrent agent operations.
/// </summary>
public sealed class SmeMetrics
{
    private int _smeAgentsSpawned;
    private int _smeAgentsRetired;
    private int _mcpServerErrors;
    private int _knowledgeFetchSuccesses;
    private int _knowledgeFetchFailures;

    /// <summary>
    /// Increments the SME agents spawned counter.
    /// Thread-safe via Interlocked.
    /// </summary>
    public void IncrementSmeAgentsSpawned()
    {
        Interlocked.Increment(ref _smeAgentsSpawned);
    }

    /// <summary>
    /// Increments the SME agents retired counter.
    /// Thread-safe via Interlocked.
    /// </summary>
    public void IncrementSmeAgentsRetired()
    {
        Interlocked.Increment(ref _smeAgentsRetired);
    }

    /// <summary>
    /// Increments the MCP server errors counter.
    /// Thread-safe via Interlocked.
    /// </summary>
    public void IncrementMcpServerErrors()
    {
        Interlocked.Increment(ref _mcpServerErrors);
    }

    /// <summary>
    /// Increments the knowledge fetch successes counter.
    /// Thread-safe via Interlocked.
    /// </summary>
    public void IncrementKnowledgeFetchSuccesses()
    {
        Interlocked.Increment(ref _knowledgeFetchSuccesses);
    }

    /// <summary>
    /// Increments the knowledge fetch failures counter.
    /// Thread-safe via Interlocked.
    /// </summary>
    public void IncrementKnowledgeFetchFailures()
    {
        Interlocked.Increment(ref _knowledgeFetchFailures);
    }

    /// <summary>
    /// Returns a snapshot of all current metric values.
    /// </summary>
    public SmeMetricsSnapshot GetSnapshot()
    {
        return new SmeMetricsSnapshot(
            SmeAgentsSpawned: _smeAgentsSpawned,
            SmeAgentsRetired: _smeAgentsRetired,
            McpServerErrors: _mcpServerErrors,
            KnowledgeFetchSuccesses: _knowledgeFetchSuccesses,
            KnowledgeFetchFailures: _knowledgeFetchFailures
        );
    }
}

/// <summary>
/// Immutable snapshot of SME metrics at a point in time.
/// </summary>
public sealed record SmeMetricsSnapshot(
    int SmeAgentsSpawned,
    int SmeAgentsRetired,
    int McpServerErrors,
    int KnowledgeFetchSuccesses,
    int KnowledgeFetchFailures
);
