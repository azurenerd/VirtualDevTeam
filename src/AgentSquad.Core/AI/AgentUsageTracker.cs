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

    /// <summary>Get usage stats for a specific agent.</summary>
    public AgentUsageStats GetStats(string agentId) =>
        _stats.GetValueOrDefault(agentId) ?? new AgentUsageStats();

    /// <summary>Get usage stats for all agents.</summary>
    public IReadOnlyDictionary<string, AgentUsageStats> GetAllStats() =>
        _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>Get total estimated cost across all agents.</summary>
    public decimal GetTotalCost() =>
        _stats.Values.Sum(s => s.EstimatedCost);

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
