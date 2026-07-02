using System.Collections.Concurrent;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.AI;

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
        var promptTokens = ModelPricing.EstimateTokens(promptChars, modelName);
        var responseTokens = ModelPricing.EstimateTokens(responseChars, modelName);
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
        var promptTokens = ModelPricing.EstimateTokens(promptChars, modelName);
        var responseTokens = ModelPricing.EstimateTokens(responseChars, modelName);
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

    /// <summary>
    /// Record premium request count and API duration from Copilot CLI usage data.
    /// Called separately from RecordCall because the JSONL usage data is only
    /// available when the CLI provides it (not all output modes include it).
    /// </summary>
    public void RecordPremiumRequests(string agentId, int premiumRequests, long apiDurationMs)
    {
        _stats.AddOrUpdate(
            agentId,
            _ => new AgentUsageStats { PremiumRequests = premiumRequests, ApiDurationMs = apiDurationMs },
            (_, existing) => new AgentUsageStats
            {
                PromptTokens = existing.PromptTokens,
                CompletionTokens = existing.CompletionTokens,
                TotalCalls = existing.TotalCalls,
                EstimatedCost = existing.EstimatedCost,
                LastModel = existing.LastModel,
                PremiumRequests = existing.PremiumRequests + premiumRequests,
                ApiDurationMs = existing.ApiDurationMs + apiDurationMs
            });

        // Persist updated premium request data
        var stats = _stats[agentId];
        _stateStore?.SaveAiUsage(agentId, stats.PromptTokens, stats.CompletionTokens,
            stats.TotalCalls, stats.EstimatedCost, stats.LastModel,
            stats.PremiumRequests, stats.ApiDurationMs);
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

    /// <summary>
    /// Reload usage data from the underlying store. Call after the store is reconfigured
    /// to a different database file (e.g., branch-scoped DB switch on runner startup).
    /// Clears in-memory stats and re-populates from the new DB so the cost badge reflects
    /// the correct run history immediately after restart.
    /// </summary>
    public void Reload()
    {
        _stats.Clear();
        _strategyStats.Clear();
        RestoreFromStore();
    }

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
                    LastModel = data.LastModel,
                    PremiumRequests = data.PremiumRequests,
                    ApiDurationMs = data.ApiDurationMs
                };
            }
            RestoredRowCount = _stats.Count;
        }
        catch (Exception ex)
        {
            // DB may not have the table yet on first run — but log the failure so we can
            // diagnose the "cost counters reset" symptom (16 DB rows but only 2 show in
            // cost-summary). Silent catch hid this for too long.
            RestoreError = ex.Message;
        }
    }

    /// <summary>
    /// Diagnostic: number of rows restored from SQLite at construction time. Zero when no
    /// store was provided OR when restore failed (see <see cref="RestoreError"/>).
    /// </summary>
    public int RestoredRowCount { get; private set; }

    /// <summary>Diagnostic: exception message from a failed restore, or null on success.</summary>
    public string? RestoreError { get; private set; }

    /// <summary>
    /// Aggregates per-agent stats into one row per AGENT ROLE — derived from the agent_id
    /// prefix (everything up to but not including the GUID suffix). Used by the dashboard
    /// cost summary to give the operator a stable view across runner restarts: instead of
    /// seeing 4 different <c>programmanager-{guid}</c> rows (one per restart), the operator
    /// sees one <c>programmanager</c> row with the cumulative totals.
    /// </summary>
    /// <remarks>
    /// The id-to-role inference is conservative: strip the trailing <c>-{32-hex-chars}</c>
    /// GUID-style suffix if present. SME agents keep their full id because their id IS stable
    /// across restarts (the SME definition id is persisted) and contains semantically useful
    /// information (which SME). Strategy/flow-monitor pseudo-ids pass through unchanged.
    /// </remarks>
    public IReadOnlyDictionary<string, AgentUsageStats> GetAggregatedStatsByRole()
    {
        static string ExtractRole(string agentId)
        {
            // SME ids are already role-prefixed and STABLE (persisted via sme-definitions.json).
            // Don't collapse them — operator wants to see "Game Engine Engineer 1" separate
            // from "Artist SME 1".
            if (agentId.StartsWith("sme-", StringComparison.OrdinalIgnoreCase)) return agentId;
            // Strategy / flow-monitor pseudo-agents — pass through.
            if (agentId.Contains(':')) return agentId;
            // Match trailing GUID-style hex suffix (32 hex chars after final dash).
            var dashIdx = agentId.LastIndexOf('-');
            if (dashIdx > 0 && dashIdx < agentId.Length - 1)
            {
                var tail = agentId[(dashIdx + 1)..];
                if (tail.Length == 32 && tail.All(c => Uri.IsHexDigit(c)))
                    return agentId[..dashIdx];
            }
            return agentId;
        }

        var grouped = new Dictionary<string, AgentUsageStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agentId, stats) in _stats)
        {
            var role = ExtractRole(agentId);
            if (grouped.TryGetValue(role, out var existing))
            {
                grouped[role] = new AgentUsageStats
                {
                    PromptTokens = existing.PromptTokens + stats.PromptTokens,
                    CompletionTokens = existing.CompletionTokens + stats.CompletionTokens,
                    TotalCalls = existing.TotalCalls + stats.TotalCalls,
                    EstimatedCost = existing.EstimatedCost + stats.EstimatedCost,
                    LastModel = stats.LastModel ?? existing.LastModel,
                    PremiumRequests = existing.PremiumRequests + stats.PremiumRequests,
                    ApiDurationMs = existing.ApiDurationMs + stats.ApiDurationMs,
                };
            }
            else
            {
                grouped[role] = stats;
            }
        }
        return grouped;
    }
}

/// <summary>
/// Accumulated usage statistics for a single agent.
/// All token/cost values are estimated from character counts.
/// PremiumRequests comes from Copilot CLI JSONL output when available.
/// </summary>
public sealed class AgentUsageStats
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public int TotalCalls { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? LastModel { get; init; }

    /// <summary>Copilot CLI premium requests consumed (billing units). 0 if not reported by CLI.</summary>
    public int PremiumRequests { get; init; }

    /// <summary>Total API duration in milliseconds as reported by the CLI.</summary>
    public long ApiDurationMs { get; init; }
}
