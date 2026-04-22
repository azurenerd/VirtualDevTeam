namespace AgentSquad.Core.Strategies;

/// <summary>
/// Normalizes strategy IDs to their canonical form.
/// Maps legacy <c>"agentic-delegation"</c> to <c>"copilot-cli"</c> so that
/// old configs, experiment data, and API calls keep working transparently.
/// </summary>
public static class StrategyIdNormalizer
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agentic-delegation"] = "copilot-cli",
    };

    /// <summary>
    /// Returns the canonical strategy ID, mapping known aliases.
    /// Unknown IDs are returned unchanged.
    /// </summary>
    public static string Normalize(string strategyId)
        => Aliases.TryGetValue(strategyId, out var canonical) ? canonical : strategyId;

    /// <summary>
    /// Normalizes all strategy IDs in a list, preserving order and deduplicating.
    /// </summary>
    public static List<string> NormalizeAll(IEnumerable<string> strategyIds)
        => strategyIds.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
