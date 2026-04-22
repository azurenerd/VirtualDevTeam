using System.Collections.Concurrent;
using AgentSquad.Core.Persistence;

namespace AgentSquad.Core.AI;

/// <summary>
/// Tracks estimated token usage and MSRP cost per agent, accumulated across all AI calls.
/// Thread-safe for concurrent agent access. Costs are estimates based on character-to-token
/// conversion since the Copilot CLI doesn't return exact token counts.
/// Persists to SQLite so costs survive restarts.
/// </summary>
public sealed class AgentUsageTracker
{
    private readonly ConcurrentDictionary<string, AgentUsageStats> _stats = new();
    private readonly ConcurrentDictionary<string, AgentUsageStats> _strategyStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentStateStore? _stateStore;

    public AgentUsageTracker() { }

    public AgentUsageTracker(AgentStateStore stateStore)
    {
        _stateStore = stateStore;
        RestoreFromStore();
    }

    /// <summary>
    /// Record a completed AI call for an agent.
    /// </summary>
    public void RecordCall(string agentId, string modelName, int promptChars, int responseChars)
    {
        var promptTokens = ModelPricing.EstimateTokens(promptChars);
        var responseTokens = ModelPricing.EstimateTokens(responseChars);
        var cost = ModelPricing.EstimateCost(modelName, promptChars, responseChars);

        var updated = _stats.AddOrUpdate(
            agentId,
            _ => new AgentUsageStats
            {
                PromptTokens = promptTokens,
                CompletionTokens = responseTokens,
                TotalCalls = 1,
                EstimatedCost = cost,
                LastModel = modelName
            },
            (_, existing) =>
            {
                return new AgentUsageStats
                {
                    PromptTokens = existing.PromptTokens + promptTokens,
                    CompletionTokens = existing.CompletionTokens + responseTokens,
                    TotalCalls = existing.TotalCalls + 1,
                    EstimatedCost = existing.EstimatedCost + cost,
                    LastModel = modelName
                };
            });

        // Persist to SQLite
        _stateStore?.SaveAiUsage(agentId, updated.PromptTokens, updated.CompletionTokens,
            updated.TotalCalls, updated.EstimatedCost, updated.LastModel);
    }

    /// <summary>
    /// Phase 6: attribute a completed strategy candidate's token cost to a
    /// specific strategy id (e.g. "baseline", "mcp-enhanced", "copilot-cli").
    /// Called by <c>StrategyOrchestrator</c> after each candidate completes.
    /// Uses the same estimation model as <see cref="RecordCall"/> but keys by
    /// strategy id instead of agent id, so the dashboard can show
    /// cost-per-strategy rollups.
    /// </summary>
    public void RecordStrategyCall(string strategyId, string modelName, int promptChars, int responseChars)
    {
        var promptTokens = ModelPricing.EstimateTokens(promptChars);
        var responseTokens = ModelPricing.EstimateTokens(responseChars);
        var cost = ModelPricing.EstimateCost(modelName, promptChars, responseChars);

        _strategyStats.AddOrUpdate(
            strategyId,
            _ => new AgentUsageStats
            {
                PromptTokens = promptTokens,
                CompletionTokens = responseTokens,
                TotalCalls = 1,
                EstimatedCost = cost,
                LastModel = modelName,
            },
            (_, existing) => new AgentUsageStats
            {
                PromptTokens = existing.PromptTokens + promptTokens,
                CompletionTokens = existing.CompletionTokens + responseTokens,
                TotalCalls = existing.TotalCalls + 1,
                EstimatedCost = existing.EstimatedCost + cost,
                LastModel = modelName,
            });
    }

    /// <summary>
    /// Phase 6: attribute already-known token counts (not char counts) to a
    /// strategy id. Used when the strategy framework has precise token usage
    /// from <see cref="Strategies.StrategyExecutionResult.TokensUsed"/> and
    /// doesn't need character-based estimation. Cost is computed from the
    /// equivalent character count using <see cref="ModelPricing"/>.
    /// </summary>
    public void RecordStrategyTokens(string strategyId, string modelName, long totalTokens)
    {
        if (totalTokens <= 0) return;
        // ModelPricing estimates at ~4 chars/token; invert to get cost-equivalent chars.
        // Split 70/30 prompt/response as a conservative default since strategies
        // typically read more context than they emit. Clamp to int.MaxValue to
        // protect the arithmetic below.
        var capped = (int)Math.Min(totalTokens, int.MaxValue / 4);
        var totalChars = capped * 4;
        var promptChars = (int)(totalChars * 0.7);
        var respChars = totalChars - promptChars;
        RecordStrategyCall(strategyId, modelName, promptChars, respChars);
    }

    /// <summary>Get usage stats for a specific agent.</summary>
    public AgentUsageStats GetStats(string agentId) =>
        _stats.GetValueOrDefault(agentId) ?? new AgentUsageStats();

    /// <summary>Get usage stats for all agents.</summary>
    public IReadOnlyDictionary<string, AgentUsageStats> GetAllStats() =>
        _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Phase 6: get accumulated usage stats for a specific strategy id.</summary>
    public AgentUsageStats GetStrategyStats(string strategyId) =>
        _strategyStats.GetValueOrDefault(strategyId) ?? new AgentUsageStats();

    /// <summary>Phase 6: get accumulated usage stats for all strategies.</summary>
    public IReadOnlyDictionary<string, AgentUsageStats> GetAllStrategyStats() =>
        _strategyStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Get total estimated cost across all agents.</summary>
    public decimal GetTotalCost() =>
        _stats.Values.Sum(s => s.EstimatedCost);

    /// <summary>Phase 6: total estimated cost across all strategies.</summary>
    public decimal GetTotalStrategyCost() =>
        _strategyStats.Values.Sum(s => s.EstimatedCost);

    private void RestoreFromStore()
    {
        if (_stateStore is null) return;
        try
        {
            var saved = _stateStore.LoadAllAiUsage();
            foreach (var (agentId, data) in saved)
            {
                _stats[agentId] = new AgentUsageStats
                {
                    PromptTokens = data.PromptTokens,
                    CompletionTokens = data.CompletionTokens,
                    TotalCalls = data.TotalCalls,
                    EstimatedCost = data.EstimatedCost,
                    LastModel = data.LastModel
                };
            }
        }
        catch { /* DB may not have the table yet on first run */ }
    }
}

/// <summary>
/// Accumulated usage statistics for a single agent.
/// All values are estimated from character counts.
/// </summary>
public sealed class AgentUsageStats
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public int TotalCalls { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? LastModel { get; init; }
}
