namespace AgentSquad.Core.Configuration;

using Microsoft.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps model tier names (premium, standard, budget, local) to configured Kernel instances.
/// </summary>
public class ModelRegistry
{
    private readonly Dictionary<string, ModelConfig> _modelConfigs;
    private readonly Dictionary<string, Kernel> _kernelCache = new();
    private readonly ILoggerFactory _loggerFactory;

    public ModelRegistry(AgentSquadConfig config, ILoggerFactory loggerFactory)
    {
        _modelConfigs = config.Models;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Get or create a Kernel instance for the given model tier.</summary>
    public Kernel GetKernel(string modelTier)
    {
        if (_kernelCache.TryGetValue(modelTier, out var cached))
            return cached;

        if (!_modelConfigs.TryGetValue(modelTier, out var config))
            throw new ArgumentException(
                $"Unknown model tier: {modelTier}. Available: {string.Join(", ", _modelConfigs.Keys)}");

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(_loggerFactory);

        switch (config.Provider.ToLowerInvariant())
        {
            case "openai":
                builder.AddOpenAIChatCompletion(config.Model, config.ApiKey);
                break;

            case "azure-openai":
            case "azureopenai":
                if (string.IsNullOrEmpty(config.Endpoint))
                    throw new ArgumentException("Azure OpenAI requires an Endpoint.");
                builder.AddAzureOpenAIChatCompletion(
                    deploymentName: config.Model,
                    endpoint: config.Endpoint,
                    apiKey: config.ApiKey);
                break;

            case "anthropic":
                // Anthropic has no official SK connector yet.
                // Route through an OpenAI-compatible proxy (e.g. LiteLLM) or custom endpoint.
                builder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: config.ApiKey,
                    endpoint: new Uri(config.Endpoint ?? "https://api.anthropic.com/v1"));
                break;

            case "ollama":
                // Ollama exposes an OpenAI-compatible API
                builder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: "ollama", // Ollama doesn't need a real key
                    endpoint: new Uri(config.Endpoint ?? "http://localhost:11434/v1"));
                break;

            default:
                throw new ArgumentException($"Unknown provider: {config.Provider}");
        }

        var kernel = builder.Build();
        _kernelCache[modelTier] = kernel;
        return kernel;
    }

    /// <summary>Get the ModelConfig for a tier (for reading settings like temperature).</summary>
    public ModelConfig? GetModelConfig(string modelTier)
    {
        return _modelConfigs.TryGetValue(modelTier, out var config) ? config : null;
    }

    /// <summary>List available model tiers.</summary>
    public IReadOnlyList<string> GetAvailableTiers() => _modelConfigs.Keys.ToList().AsReadOnly();

}
