using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Represents one low-level action (or assertion) within a <see cref="PlaytestActionPlan"/>.
/// Deserialized from the JSON emitted by the <c>verify-scenario-user.md</c> LLM prompt.
/// </summary>
public sealed record PlaytestAction
{
    /// <summary>Zero-based position of this action within the plan's steps.</summary>
    [JsonPropertyName("step_index")]
    public int StepIndex { get; init; }

    /// <summary>The verbatim scenario step text this action implements.</summary>
    [JsonPropertyName("scenario_step")]
    public string? ScenarioStep { get; init; }

    /// <summary>
    /// The action type string from the action-type reference table
    /// (e.g. <c>page.goto</c>, <c>http.post</c>, <c>cli.run</c>, <c>assert.selectorExists</c>).
    /// Tolerant deserialization: accepts both <c>action_type</c> (snake_case) and
    /// <c>actionType</c> (camelCase) since LLMs inconsistently respect naming conventions.
    /// </summary>
    [JsonPropertyName("action_type")]
    public string ActionType { get; init; } = "";

    /// <summary>
    /// Action-type-specific parameters as a raw JSON object. Adapters parse only the
    /// keys they need; unknown keys are ignored.
    /// </summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement> Params { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, the adapter stores an observable snapshot (DOM value,
    /// counter reading, etc.) under <see cref="SnapshotKey"/> for use by later change-detection
    /// assertions.
    /// </summary>
    [JsonPropertyName("captures_snapshot")]
    public bool CapturesSnapshot { get; init; }

    /// <summary>The key under which the snapshot is stored when <see cref="CapturesSnapshot"/> is true.</summary>
    [JsonPropertyName("snapshot_key")]
    public string? SnapshotKey { get; init; }

    /// <summary>
    /// When non-null, identifies which <c>observation_surfaces[N].kind</c> this action
    /// is asserting (e.g. <c>dom_query</c>, <c>http_response</c>, <c>process_exit_code</c>).
    /// Null for non-assertion actions (navigation, fill, click, wait, etc.).
    /// </summary>
    [JsonPropertyName("surface_verified")]
    public string? SurfaceVerified { get; init; }

    /// <summary>
    /// Convenience: returns the string value of a parameter key, or null if the key is absent
    /// or not a string/number JSON value.
    /// </summary>
    public string? GetParam(string key)
    {
        if (!Params.TryGetValue(key, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }

    /// <summary>Returns an integer parameter value, or <paramref name="fallback"/> when absent or non-numeric.</summary>
    public int GetIntParam(string key, int fallback = 0)
    {
        if (!Params.TryGetValue(key, out var element)) return fallback;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var v) ? v : fallback;
    }

    /// <summary>
    /// Determines the top-level action category (the part before the first <c>.</c>).
    /// E.g. <c>page.click</c> → <c>"page"</c>, <c>assert.selectorExists</c> → <c>"assert"</c>.
    /// </summary>
    public string ActionCategory => ActionType.Contains('.') ? ActionType[..ActionType.IndexOf('.')] : ActionType;

    /// <summary>
    /// The specific verb after the category prefix.
    /// E.g. <c>page.click</c> → <c>"click"</c>.
    /// </summary>
    public string ActionVerb => ActionType.Contains('.') ? ActionType[(ActionType.IndexOf('.') + 1)..] : ActionType;
}
