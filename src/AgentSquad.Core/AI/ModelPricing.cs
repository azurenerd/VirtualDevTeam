namespace AgentSquad.Core.AI;

/// <summary>
/// MSRP pricing estimates for AI models used through the Copilot CLI.
/// Prices are per 1M tokens (input/output) based on publicly listed API pricing.
/// These are rough estimates since the Copilot CLI doesn't return exact token counts —
/// we estimate tokens from character count (~4 characters per token for English text).
/// </summary>
public static class ModelPricing
{
    /// <summary>Approximate characters per token for English text.</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>Estimate token count from character length.</summary>
    public static int EstimateTokens(int charCount) =>
        (int)Math.Ceiling(charCount / CharsPerToken);

    /// <summary>
    /// Get the MSRP pricing for a model. Returns (inputPricePerMillionTokens, outputPricePerMillionTokens).
    /// Prices sourced from public API pricing pages as of mid-2025.
    /// </summary>
    public static (decimal InputPerMillion, decimal OutputPerMillion) GetPricing(string modelName)
    {
        // Normalize: lowercase, trim
        var model = modelName.Trim().ToLowerInvariant();

        return model switch
        {
            // Anthropic Claude models
            "claude-opus-4.7" or "claude-opus-4.6" or "claude-opus-4.5" => (15.00m, 75.00m),
            "claude-sonnet-4.6" or "claude-sonnet-4.5" or "claude-sonnet-4" => (3.00m, 15.00m),
            "claude-haiku-4.5" => (0.80m, 4.00m),

            // OpenAI GPT models
            "gpt-5.4" or "gpt-5.2" or "gpt-5.1" => (2.50m, 10.00m),
            "gpt-5.4-mini" or "gpt-5-mini" => (0.40m, 1.60m),
            "gpt-4.1" => (2.00m, 8.00m),

            // Local models (free)
            var m when m.Contains("ollama") || m.Contains("local") => (0m, 0m),

            // Default: assume mid-tier pricing
            _ => (3.00m, 15.00m)
        };
    }

    /// <summary>
    /// Calculate estimated cost for a single AI call based on prompt and response character lengths.
    /// </summary>
    public static decimal EstimateCost(string modelName, int promptChars, int responseChars)
    {
        var (inputPrice, outputPrice) = GetPricing(modelName);
        var promptTokens = EstimateTokens(promptChars);
        var responseTokens = EstimateTokens(responseChars);

        return (promptTokens * inputPrice / 1_000_000m) +
               (responseTokens * outputPrice / 1_000_000m);
    }
}
