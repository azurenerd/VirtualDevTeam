namespace AgentSquad.Core.Frameworks;

/// <summary>
/// Optional real-time telemetry for frameworks that emit observable events
/// during execution (sub-agent spawns, tool calls, decisions, etc.).
/// Enables the dashboard to show live progress for external framework runs.
/// </summary>
public interface IFrameworkTelemetrySource
{
    /// <summary>
    /// Stream events as they occur during framework execution.
    /// The orchestrator subscribes before calling ExecuteAsync and
    /// forwards events to the dashboard via SignalR.
    /// </summary>
    IAsyncEnumerable<FrameworkEvent> StreamEventsAsync(CancellationToken ct);

    /// <summary>
    /// Point-in-time snapshot of framework activity (active agents, recent decisions, metrics).
    /// Called by the dashboard on demand for the Reasoning/Activity page.
    /// </summary>
    Task<FrameworkActivitySnapshot> GetActivitySnapshotAsync(CancellationToken ct);
}
