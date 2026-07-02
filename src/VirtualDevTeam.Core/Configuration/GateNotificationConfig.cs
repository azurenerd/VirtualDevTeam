namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Operational thresholds for the gate notification poller. NoMessyCodePlan Theme 8.
///
/// <para>
/// Was previously hardcoded as a <c>static readonly TimeSpan</c> in
/// <c>GateNotificationService</c>. Pulled out so operators can tune the polling
/// cadence per environment (fast CI vs slow CI / over-rate-limited project) without
/// rebuilding. Bound from <c>VirtualDevTeam:GateNotification</c> in appsettings.json
/// (or develop-settings.json overrides).
/// </para>
///
/// <para>
/// Cost model: each tick costs ~1 API call per pending gate. The default 120s gives
/// 30 ticks/hour × N gates ≈ N×30 API calls/hour — well within typical rate limits.
/// Lower the value if you need faster gate-resolution feedback in the dashboard and
/// can spare the API budget.
/// </para>
/// </summary>
public sealed class GateNotificationConfig
{
    /// <summary>
    /// How often the gate poller checks pending GitHub/ADO approval gates. Floored
    /// at 10s to avoid thrashing the platform API.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 120;
}
