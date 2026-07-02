namespace VirtualDevTeam.Core.AI;

/// <summary>
/// MSRP pricing estimates for AI models used through the Copilot CLI.
/// Prices are per 1M tokens (input/output) based on publicly listed API pricing.
/// These are rough estimates since the Copilot CLI doesn't return exact token counts —
/// we estimate tokens from character count using per-model ratios.
/// </summary>
public static class ModelPricing
{
    /// <summary>Default characters per token for English text.</summary>
    public const double DefaultCharsPerToken = 4.0;

    /// <summary>Get characters-per-token ratio for a model family. Code-heavy models tend toward ~3.5.</summary>
    public static double GetCharsPerToken(string modelName)
    {
        var model = modelName.Trim().ToLowerInvariant();
        return model switch
        {
            // Claude models use byte-pair encoding optimized for code — ~3.5 chars/token
            var m when m.StartsWith("claude") => 3.5,
            // GPT models — ~3.7 chars/token for mixed code/text
            var m when m.StartsWith("gpt") => 3.7,
            // Local models vary widely — use conservative default
            _ => DefaultCharsPerToken
        };
    }

    /// <summary>Estimate token count from character length using per-model ratio.</summary>
    public static int EstimateTokens(int charCount, string? modelName = null)
    {
        var ratio = modelName is not null ? GetCharsPerToken(modelName) : DefaultCharsPerToken;
        return (int)Math.Ceiling(charCount / ratio);
    }

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
            // Anthropic Claude models (match with or without context-window suffix like -1m)
            var m when m.StartsWith("claude-opus") => (15.00m, 75.00m),
            var m when m.StartsWith("claude-sonnet") => (3.00m, 15.00m),
            var m when m.StartsWith("claude-haiku") => (0.80m, 4.00m),

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
        var promptTokens = EstimateTokens(promptChars, modelName);
        var responseTokens = EstimateTokens(responseChars, modelName);

        return (promptTokens * inputPrice / 1_000_000m) +
               (responseTokens * outputPrice / 1_000_000m);
    }
}
