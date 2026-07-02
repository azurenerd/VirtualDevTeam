namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Configuration for the orchestrator's <c>HealthMonitor</c> service. Bound from
/// <c>VirtualDevTeam:HealthMonitor</c> in appsettings.json / develop-settings.json.
/// </summary>
/// <remarks>
/// Distinct from <see cref="FlowMonitorConfig"/> (the watchdog-detector service).
/// HealthMonitor is the lower-level liveness/auto-signal layer; FlowMonitor sits above
/// it and runs the IFlowDetector pipeline.
/// </remarks>
public sealed class HealthMonitorConfig
{
    /// <summary>
    /// Kill-switch for the workflow signal auto-detection heuristic in
    /// <c>HealthMonitor.AutoDetectSignals</c>. When false, the timer keeps running for
    /// stuck-agent detection but NO <c>research.*</c>/<c>architecture.*</c>/
    /// <c>engineering.*</c> signals are inferred from agent status reasons — the workflow
    /// will only advance when agents publish explicit <c>StatusUpdateMessage</c>s on the bus.
    ///
    /// Default: <c>true</c>. Toggle to <c>false</c> if the heuristic misbehaves on a project
    /// (see Lesson #23 for the historical class of false-positive bugs this gates).
    /// </summary>
    public bool AutoDetectSignals { get; set; } = true;

    /// <summary>
    /// Cooldown in seconds between platform file-existence checks for the same phase
    /// (Research, Architecture). Prevents the auto-detect heuristic from blowing the
    /// GitHub/ADO API budget when called from the agent-status-changed event (which can
    /// fire many times per second).
    ///
    /// Default: 60s. Lower in tests if needed.
    /// </summary>
    public int DocCheckCooldownSeconds { get; set; } = 60;
}
