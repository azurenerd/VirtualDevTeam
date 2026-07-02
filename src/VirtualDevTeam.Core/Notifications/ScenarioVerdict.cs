using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Notifications;

/// <summary>
/// T-FINAL's per-scenario playtest result attached to an approval gate payload (plan D7).
/// Allows the Approvals page to surface per-scenario confidence + verdict inline without
/// requiring operators to open a separate report.
/// </summary>
public record ScenarioVerdict
{
    /// <summary>Stable short identifier matching <see cref="Scenario.Id"/> (e.g., <c>S01</c>).</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Human-readable scenario title (one line).</summary>
    public required string Title { get; init; }

    /// <summary>T-FINAL's verdict after running the scenario end-to-end.</summary>
    public VerificationStatus Status { get; init; } = VerificationStatus.NotYetVerified;

    /// <summary>
    /// T-FINAL's self-reported confidence in the verdict (0.0–1.0).
    /// A value below 0.5 on a Critical scenario triggers a manual-review banner.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Importance classification; mirrors <see cref="Scenario.Priority"/>.</summary>
    public ScenarioPriority Priority { get; init; } = ScenarioPriority.Important;

    /// <summary>
    /// Optional URL pointing to a screenshot, video, or Playwright trace produced by T-FINAL
    /// as evidence for this specific scenario. Rendered as a "🔗" link in the verdict row.
    /// </summary>
    public string? EvidenceUrl { get; init; }
}
