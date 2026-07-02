namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Per-model-tier knowledge context budgets. Controls how much knowledge
/// content is injected into agent system prompts based on model capability.
/// </summary>
public static class KnowledgeBudget
{
    /// <summary>
    /// Returns the maximum characters of knowledge context for a model tier.
    /// Premium models handle larger context well; budget models need concise input.
    /// </summary>
    public static int GetMaxKnowledgeChars(string modelTier) => modelTier?.ToLowerInvariant() switch
    {
        "premium" => 8000,
        "standard" => 4000,
        "budget" => 2000,
        "local" => 2000,
        _ => 2500
    };

    /// <summary>
    /// Returns the maximum characters for role description based on model tier.
    /// </summary>
    public static int GetMaxRoleDescriptionChars(string modelTier) => modelTier?.ToLowerInvariant() switch
    {
        "premium" => 3000,
        "standard" => 2000,
        "budget" => 1000,
        "local" => 1000,
        _ => 1500
    };

    /// <summary>
    /// Returns the maximum characters per individual knowledge link summary.
    /// </summary>
    public static int GetMaxPerLinkChars(string modelTier) => modelTier?.ToLowerInvariant() switch
    {
        "premium" => 2000,
        "standard" => 1000,
        "budget" => 500,
        "local" => 500,
        _ => 800
    };
}
