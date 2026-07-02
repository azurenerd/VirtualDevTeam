namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Importance classification of a scenario, used to determine gate requirements:
/// T-FINAL must pass ≥ 95% of <see cref="Critical"/> scenarios before emitting
/// <c>scenarios.all_critical_verified</c>.
/// </summary>
public enum ScenarioPriority
{
    /// <summary>Core user journey — failure here means the app does not deliver its promise.</summary>
    Critical,

    /// <summary>Significant but non-blocking scenario; failure degrades UX without breaking core flow.</summary>
    Important,

    /// <summary>Enhancement or edge-case scenario; acceptable to defer to a follow-up sprint.</summary>
    NiceToHave,
}
