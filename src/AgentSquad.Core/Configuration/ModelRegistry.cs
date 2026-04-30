namespace AgentSquad.Core.Configuration;

using AgentSquad.Core.AI;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps model tier names (premium, standard, budget, local) to configured Kernel instances.
/// When Copilot CLI is enabled, uses it as the default provider with API-key fallback.
/// </summary>
public class ModelRegistry
{
    private readonly Dictionary<string, ModelConfig> _modelConfigs;
    private readonly Dictionary<string, Kernel> _kernelCache = new();
    private readonly Dictionary<string, string> _agentModelOverrides = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CopilotCliConfig _cliConfig;
    private readonly CopilotCliProcessManager? _processManager;
    private readonly AgentUsageTracker _usageTracker;
    private readonly ActiveLlmCallTracker _llmCallTracker;
    private readonly HashSet<string> _cliFallbackTiers = new();
    private readonly object _overrideLock = new();
    // Guards _kernelCache and _cliFallbackTiers. These are hit concurrently by parallel
    // strategy candidates (e.g. baseline + mcp-enhanced running in Task.WhenAll), so
    // a plain Dictionary read/write is unsafe without synchronization.
    private readonly object _kernelCacheLock = new();

    /// <summary>Well-known models available via Copilot CLI.</summary>
    public static readonly IReadOnlyList<string> AvailableCopilotModels =
    [
        "gpt-5.4-mini",
        "gpt-5-mini",
        "gpt-4.1",
        "gpt-5.1",
        "gpt-5.2",
        "gpt-5.4",
        "claude-haiku-4.5",
        "claude-sonnet-4",
        "claude-sonnet-4.5",
        "claude-sonnet-4.6",
        "claude-opus-4.5",
        "claude-opus-4.6",
        "claude-opus-4.7",
    ];

    public ModelRegistry(
        AgentSquadConfig config,
        ILoggerFactory loggerFactory,
        AgentUsageTracker usageTracker,
        ActiveLlmCallTracker llmCallTracker,
        CopilotCliProcessManager? processManager = null)
    {
        _modelConfigs = config.Models;
        _loggerFactory = loggerFactory;
        _usageTracker = usageTracker;
        _llmCallTracker = llmCallTracker;
        _cliConfig = config.CopilotCli;
        _processManager = processManager;
    }

    /// <summary>Fired when a tier falls back from Copilot CLI to API-key provider.</summary>
    public event EventHandler<FallbackTriggeredEventArgs>? FallbackTriggered;

    /// <summary>Per-agent usage tracker for estimated cost display.</summary>
    public AgentUsageTracker UsageTracker => _usageTracker;

    /// <summary>Get or create a Kernel instance for the given model tier.</summary>
    public Kernel GetKernel(string modelTier) => GetKernel(modelTier, agentId: null);

    /// <summary>Get or create a Kernel for a tier, with optional per-agent model override.</summary>
    public Kernel GetKernel(string modelTier, string? agentId)
    {
        // Check for per-agent model override
        string? modelOverride = null;
        if (agentId is not null)
        {
            lock (_overrideLock)
            {
                _agentModelOverrides.TryGetValue(agentId, out modelOverride);
            }
        }

        // Cache key includes override model so different agents can have different kernels
        var cacheKey = modelOverride is not null ? $"{modelTier}:{modelOverride}" : modelTier;

        lock (_kernelCacheLock)
        {
            if (_kernelCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        Kernel? kernel = null;

        bool cliFallback;
        lock (_kernelCacheLock)
        {
            cliFallback = _cliFallbackTiers.Contains(modelTier);
        }

        // Try Copilot CLI first if enabled and available
        if (_cliConfig.Enabled && _processManager?.IsAvailable == true && !cliFallback)
        {
            kernel = BuildCopilotCliKernel(modelTier, modelOverride);
        }

        // Fall back to API-key provider
        kernel ??= BuildApiKeyKernel(modelTier);

        lock (_kernelCacheLock)
        {
            // Another thread may have raced us; prefer the existing entry so callers
            // observe a single canonical Kernel per cache key.
            if (_kernelCache.TryGetValue(cacheKey, out var raced))
                return raced;
            _kernelCache[cacheKey] = kernel;
        }
        return kernel;
    }

    /// <summary>Get the ModelConfig for a tier (for reading settings like temperature).</summary>
    public ModelConfig? GetModelConfig(string modelTier)
    {
        return _modelConfigs.TryGetValue(modelTier, out var config) ? config : null;
    }

    /// <summary>List available model tiers.</summary>
    public IReadOnlyList<string> GetAvailableTiers() => _modelConfigs.Keys.ToList().AsReadOnly();

    /// <summary>Get the effective model name for a specific agent (override or default).</summary>
    public string GetEffectiveModel(string agentId)
    {
        lock (_overrideLock)
        {
            if (_agentModelOverrides.TryGetValue(agentId, out var overrideModel))
                return overrideModel;
        }

        // Respect FastMode: when enabled, the actual model used is FastModeModel
        return _cliConfig.FastMode && !string.IsNullOrEmpty(_cliConfig.FastModeModel)
            ? _cliConfig.FastModeModel
            : _cliConfig.ModelName;
    }

    /// <summary>Set a per-agent model override. Takes effect on the agent's next AI call.</summary>
    public void SetAgentModelOverride(string agentId, string modelName)
    {
        lock (_overrideLock)
        {
            _agentModelOverrides[agentId] = modelName;
        }

        var logger = _loggerFactory.CreateLogger<ModelRegistry>();
        logger.LogInformation("Model override set for agent '{AgentId}': {Model}", agentId, modelName);
        ModelOverrideChanged?.Invoke(this, new ModelOverrideChangedEventArgs
        {
            AgentId = agentId,
            NewModel = modelName
        });
    }

    /// <summary>Clear a per-agent model override (reverts to default).</summary>
    public void ClearAgentModelOverride(string agentId)
    {
        lock (_overrideLock)
        {
            _agentModelOverrides.Remove(agentId);
        }
    }

    /// <summary>Fired when a per-agent model override is changed.</summary>
    public event EventHandler<ModelOverrideChangedEventArgs>? ModelOverrideChanged;

    /// <summary>
    /// Mark a tier as needing API-key fallback. Called when Copilot CLI fails at runtime.
    /// Clears the kernel cache for that tier so the next call rebuilds with API keys.
    /// </summary>
    public void TriggerFallback(string modelTier, string reason)
    {
        lock (_kernelCacheLock)
        {
            _cliFallbackTiers.Add(modelTier);
            _kernelCache.Remove(modelTier);
        }

        var logger = _loggerFactory.CreateLogger<ModelRegistry>();
        logger.LogWarning("Copilot CLI fallback triggered for tier '{Tier}': {Reason}", modelTier, reason);

        FallbackTriggered?.Invoke(this, new FallbackTriggeredEventArgs
        {
            ModelTier = modelTier,
            Reason = reason
        });
    }

    private Kernel BuildCopilotCliKernel(string modelTier, string? modelOverride = null)
    {
        var effectiveModel = modelOverride ?? _cliConfig.ModelName;
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(_loggerFactory);

        var cliService = new CopilotCliChatCompletionService(
            _processManager!,
            _cliConfig,
            _usageTracker,
            _llmCallTracker,
            _loggerFactory.CreateLogger<CopilotCliChatCompletionService>());

        builder.Services.AddKeyedSingleton<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(
            effectiveModel, cliService);

        var logger = _loggerFactory.CreateLogger<ModelRegistry>();
        logger.LogInformation("Tier '{Tier}' using Copilot CLI provider (model: {Model})",
            modelTier, effectiveModel);

        return builder.Build();
    }

    private Kernel BuildApiKeyKernel(string modelTier)
    {
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
                builder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: config.ApiKey,
                    endpoint: new Uri(string.IsNullOrWhiteSpace(config.Endpoint) ? "https://api.anthropic.com/v1" : config.Endpoint));
                break;

            case "ollama":
                builder.AddOpenAIChatCompletion(
                    modelId: config.Model,
                    apiKey: "ollama",
                    endpoint: new Uri(string.IsNullOrWhiteSpace(config.Endpoint) ? "http://localhost:11434/v1" : config.Endpoint));
                break;

            default:
                throw new ArgumentException($"Unknown provider: {config.Provider}");
        }

        return builder.Build();
    }
}

public class FallbackTriggeredEventArgs : EventArgs
{
    public required string ModelTier { get; init; }
    public required string Reason { get; init; }
}

public class ModelOverrideChangedEventArgs : EventArgs
{
    public required string AgentId { get; init; }
    public required string NewModel { get; init; }
}
