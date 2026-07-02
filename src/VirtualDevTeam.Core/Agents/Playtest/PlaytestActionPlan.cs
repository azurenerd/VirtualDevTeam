using System.Text.Json.Serialization;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// A terminal assertion entry from the <c>terminal_assertions</c> array of the
/// LLM-generated action plan. Each entry maps to one <c>observation_surfaces</c> entry
/// in the scenario definition.
/// </summary>
public sealed record TerminalAssertion
{
    /// <summary>Zero-based index into the scenario's <c>observation_surfaces</c> array.</summary>
    [JsonPropertyName("surface_index")]
    public int SurfaceIndex { get; init; }

    /// <summary>The observation surface kind (e.g. <c>dom_query</c>, <c>http_response</c>).</summary>
    [JsonPropertyName("surface_kind")]
    public string SurfaceKind { get; init; } = "";

    /// <summary>The action type used to verify this surface.</summary>
    [JsonPropertyName("action_type")]
    public string ActionType { get; init; } = "";

    /// <summary>Parameters for the assertion action.</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, System.Text.Json.JsonElement> Params { get; init; } = [];
}

/// <summary>
/// The full deterministic action plan produced by the <c>verify-scenario-user.md</c>
/// LLM prompt and executed by the <see cref="IAppPlaytester"/> to verify one scenario.
/// </summary>
public sealed record PlaytestActionPlan
{
    /// <summary>The <see cref="Scenarios.Scenario.Id"/> this plan verifies.</summary>
    [JsonPropertyName("scenario_id")]
    public string ScenarioId { get; init; } = "";

    /// <summary>The <see cref="Scenarios.JourneyKind"/> name (lowercase, underscore-separated).</summary>
    [JsonPropertyName("journey_kind")]
    public string JourneyKind { get; init; } = "";

    /// <summary>
    /// The adapter to use for all actions in this plan
    /// (<c>WebPlaytestAdapter</c>, <c>ApiPlaytestAdapter</c>, or <c>CliPlaytestAdapter</c>).
    /// </summary>
    [JsonPropertyName("adapter")]
    public string Adapter { get; init; } = "";

    /// <summary>
    /// A single assertion expression to verify preconditions before execution begins,
    /// or <see langword="null"/> if there is no precondition check.
    /// </summary>
    [JsonPropertyName("precondition_check")]
    public string? PreconditionCheck { get; init; }

    /// <summary>Ordered list of actions to execute.</summary>
    [JsonPropertyName("actions")]
    public IReadOnlyList<PlaytestAction> Actions { get; init; } = [];

    /// <summary>Terminal assertions — one per <c>observation_surfaces</c> entry in the scenario.</summary>
    [JsonPropertyName("terminal_assertions")]
    public IReadOnlyList<TerminalAssertion> TerminalAssertions { get; init; } = [];

    /// <summary>Filename of the final screenshot, or <see langword="null"/> for non-UI scenarios.</summary>
    [JsonPropertyName("final_screenshot")]
    public string? FinalScreenshot { get; init; }
}
