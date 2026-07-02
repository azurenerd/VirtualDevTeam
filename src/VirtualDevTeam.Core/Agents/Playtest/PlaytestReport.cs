using System.Text.Json.Serialization;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// One entry in the evidence trace: records what the adapter observed for a single action.
/// </summary>
public sealed record EvidenceEntry
{
    /// <summary>The action's step index within the plan.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Human-readable description of the action executed (e.g. <c>page.click('.build-tower')</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The raw evidence object returned by the adapter.</summary>
    public required IPlaytestEvidence Evidence { get; init; }

    /// <summary>
    /// Filename of a screenshot taken during this step, if any.
    /// Only set for <c>page.screenshot</c> actions and UI assertion steps.
    /// </summary>
    public string? ScreenshotHandle { get; init; }

    /// <summary>Whether the action's assertion passed (convenience accessor).</summary>
    public bool AssertionPassed => Evidence.Passed;
}

/// <summary>
/// Layer-3 narrative assessment produced by the <c>report-narrative.md</c> Copilot CLI call.
/// Parsed from the JSON verdict returned by the LLM judge.
/// </summary>
public sealed record Layer3NarrativeAssessment
{
    [JsonPropertyName("layer3_verdict")]
    public string Layer3Verdict { get; init; } = "inconclusive";

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("operator_review_required")]
    public bool OperatorReviewRequired { get; init; }

    [JsonPropertyName("ambiguity_note")]
    public string? AmbiguityNote { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }

    [JsonPropertyName("narrative_coherence")]
    public NarrativeCoherence? NarrativeCoherence { get; init; }
}

/// <summary>Narrative coherence sub-object from the Layer-3 judge verdict.</summary>
public sealed record NarrativeCoherence
{
    [JsonPropertyName("coherent")]
    public bool Coherent { get; init; }

    [JsonPropertyName("breaks")]
    public IReadOnlyList<string> Breaks { get; init; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

/// <summary>
/// The per-scenario verdict produced by <see cref="IAppPlaytester.RunAsync"/>.
/// Aggregates evidence from the three-layer verification stack
/// (deterministic Layer 1, LLM-vision Layer 2, narrative-judge Layer 3).
/// </summary>
public sealed record PlaytestReport
{
    /// <summary>The scenario identifier (e.g. <c>S03</c>).</summary>
    public required string ScenarioId { get; init; }

    /// <summary>The scenario title.</summary>
    public required string Title { get; init; }

    /// <summary>The journey kind string (lowercase, underscore-separated).</summary>
    public required string JourneyKind { get; init; }

    /// <summary>The scenario priority string (e.g. <c>critical</c>, <c>important</c>).</summary>
    public string Priority { get; init; } = "important";

    /// <summary>Aggregated verdict (most conservative of all three layers).</summary>
    public VerificationStatus Verdict { get; init; } = VerificationStatus.Inconclusive;

    /// <summary>Confidence in the verdict (0.0–1.0). Aggregated from all three layers.</summary>
    public double Confidence { get; init; }

    /// <summary>Whether a human should manually review this report.</summary>
    public bool OperatorReviewRequired { get; init; }

    /// <summary>Plain-language note for the operator when review is required.</summary>
    public string? AmbiguityNote { get; init; }

    /// <summary>The action plan that was executed; null if planning failed.</summary>
    public PlaytestActionPlan? ActionPlanExecuted { get; init; }

    /// <summary>Ordered trace of all actions executed and their evidence.</summary>
    public IReadOnlyList<EvidenceEntry> Evidence { get; init; } = [];

    /// <summary>List of observation surface kinds that failed or were inconclusive.</summary>
    public IReadOnlyList<string> FailedSurfaces { get; init; } = [];

    /// <summary>Layer-2 vision assessment note; null when Layer 2 was skipped or inconclusive.</summary>
    public string? Layer2VisionNote { get; init; }

    /// <summary>Layer-3 narrative judge output; null when Layer 3 was skipped.</summary>
    public Layer3NarrativeAssessment? NarrativeAssessment { get; init; }

    /// <summary>Error encountered during planning or execution; null on success.</summary>
    public string? ExecutionError { get; init; }

    /// <summary>Layer-1 result before aggregation (used for conservative-merge logic).</summary>
    public VerificationStatus Layer1Result { get; init; } = VerificationStatus.Inconclusive;

    /// <summary>Layer-2 result before aggregation (Inconclusive when skipped).</summary>
    public VerificationStatus Layer2Result { get; init; } = VerificationStatus.Inconclusive;

    /// <summary>Layer-3 result before aggregation (Inconclusive when skipped).</summary>
    public VerificationStatus Layer3Result { get; init; } = VerificationStatus.Inconclusive;
}
