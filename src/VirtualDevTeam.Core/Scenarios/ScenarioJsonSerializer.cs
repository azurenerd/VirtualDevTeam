using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Handles JSON serialization and deserialization of <see cref="Scenario"/> lists for the
/// <c>scenarios.json</c> sidecar file.
/// </summary>
/// <remarks>
/// <para>
/// All property names are serialized as <c>snake_case</c> (matching the YAML schema).
/// Enum values are serialized as <c>snake_case</c> strings so the sidecar JSON is
/// human-readable and round-trips cleanly with the YAML representation.
/// </para>
/// <para>
/// The sidecar JSON is a deterministic mirror of the PMSpec YAML block. It exists for
/// tool consumption convenience; the YAML block is always the canonical source of truth.
/// </para>
/// </remarks>
public static class ScenarioJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    // Read-only options used for deserialization (same settings; shared for clarity).
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
    };

    /// <summary>
    /// Serialize a list of scenarios to a pretty-printed JSON string.
    /// </summary>
    /// <param name="scenarios">The scenarios to serialize.</param>
    /// <returns>Indented JSON representation.</returns>
    public static string Serialize(IReadOnlyList<Scenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        return JsonSerializer.Serialize(scenarios, Options);
    }

    /// <summary>
    /// Deserialize a JSON string produced by <see cref="Serialize"/> back into a scenario list.
    /// Returns <see langword="null"/> when the input is null or empty.
    /// </summary>
    /// <param name="json">JSON text to deserialize.</param>
    /// <returns>Parsed scenario list, or <see langword="null"/> on empty input.</returns>
    public static IReadOnlyList<Scenario>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<IReadOnlyList<Scenario>>(json, ReadOptions);
    }
}
