using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Agents.Decisions;

/// <summary>
/// Thread-safe in-memory decision log with event notifications for real-time dashboard updates.
/// Follows the same pattern as <see cref="Reasoning.AgentReasoningLog"/>.
/// </summary>
public class DecisionLog : IDecisionLog
{
    private readonly ConcurrentDictionary<string, AgentDecision> _decisionsById = new();
    private readonly ConcurrentDictionary<string, List<AgentDecision>> _decisionsByAgent = new();
    private readonly ILogger<DecisionLog> _logger;

    /// <summary>Max decisions retained per agent before oldest are trimmed.</summary>
    private const int MaxDecisionsPerAgent = 200;

    /// <summary>Max total decisions across all agents before global trim.</summary>
    private const int MaxTotalDecisions = 2000;

    public event Action<AgentDecision>? OnDecisionChanged;

    public DecisionLog(ILogger<DecisionLog> logger)
    {
        _logger = logger;
    }

    public void Log(AgentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        _decisionsById[decision.Id] = decision;

        var list = _decisionsByAgent.GetOrAdd(decision.AgentId, _ => new List<AgentDecision>());
        lock (list)
        {
            list.Add(decision);
            if (list.Count > MaxDecisionsPerAgent)
                list.RemoveRange(0, list.Count - MaxDecisionsPerAgent);
        }

        TrimGlobalIfNeeded();

        _logger.LogInformation(
            "[{AgentName}] Decision [{Impact}]: {Title}",
            decision.AgentDisplayName, decision.ImpactLevel, decision.Title);

        RaiseEvent(decision);
    }

    public void Update(string decisionId, DecisionStatus status, string? humanFeedback = null)
    {
        if (!_decisionsById.TryGetValue(decisionId, out var existing))
        {
            _logger.LogWarning("Decision {Id} not found for update", decisionId);
            return;
        }

        var updated = existing with
        {
            Status = status,
            ResolvedAt = DateTime.UtcNow,
            HumanFeedback = humanFeedback ?? existing.HumanFeedback,
        };

        _decisionsById[decisionId] = updated;

        // Update in agent list
        var list = _decisionsByAgent.GetOrAdd(existing.AgentId, _ => new List<AgentDecision>());
        lock (list)
        {
            var idx = list.FindIndex(d => d.Id == decisionId);
            if (idx >= 0) list[idx] = updated;
        }

        _logger.LogInformation(
            "Decision {Id} updated to {Status} (feedback: {Feedback})",
            decisionId, status, humanFeedback ?? "none");

        RaiseEvent(updated);
    }

    public IReadOnlyList<AgentDecision> GetDecisions(string agentId)
    {
        if (!_decisionsByAgent.TryGetValue(agentId, out var list))
            return Array.Empty<AgentDecision>();

        lock (list) { return list.ToList(); }
    }

    public IReadOnlyList<AgentDecision> GetAllDecisions()
    {
        var all = new List<AgentDecision>();
        foreach (var kvp in _decisionsByAgent)
        {
            lock (kvp.Value) { all.AddRange(kvp.Value); }
        }
        return all.OrderByDescending(d => d.CreatedAt).ToList();
    }

    public IReadOnlyList<AgentDecision> GetDecisionsByMinLevel(DecisionImpactLevel minLevel)
    {
        return GetAllDecisions().Where(d => d.ImpactLevel >= minLevel).ToList();
    }

    public IReadOnlyList<AgentDecision> GetPendingDecisions()
    {
        return GetAllDecisions().Where(d => d.Status == DecisionStatus.Pending).ToList();
    }

    public AgentDecision? GetDecision(string decisionId)
    {
        _decisionsById.TryGetValue(decisionId, out var decision);
        return decision;
    }

    public IReadOnlyDictionary<DecisionImpactLevel, int> GetCountsByLevel()
    {
        var all = GetAllDecisions();
        return Enum.GetValues<DecisionImpactLevel>()
            .ToDictionary(level => level, level => all.Count(d => d.ImpactLevel == level));
    }

    public IReadOnlyList<string> GetAgentIds()
    {
        return _decisionsByAgent.Keys.ToList();
    }

    public void ClearAll()
    {
        _decisionsById.Clear();
        foreach (var kvp in _decisionsByAgent)
        {
            lock (kvp.Value) { kvp.Value.Clear(); }
        }
        _decisionsByAgent.Clear();
    }

    private void TrimGlobalIfNeeded()
    {
        var totalCount = 0;
        foreach (var kvp in _decisionsByAgent)
        {
            lock (kvp.Value) { totalCount += kvp.Value.Count; }
        }

        if (totalCount <= MaxTotalDecisions) return;

        // Trim oldest from each agent proportionally
        foreach (var kvp in _decisionsByAgent)
        {
            lock (kvp.Value)
            {
                var excess = kvp.Value.Count - MaxDecisionsPerAgent / 2;
                if (excess > 0)
                {
                    var removed = kvp.Value.Take(excess).Select(d => d.Id).ToList();
                    kvp.Value.RemoveRange(0, excess);
                    foreach (var id in removed)
                        _decisionsById.TryRemove(id, out _);
                }
            }
        }
    }

    private void RaiseEvent(AgentDecision decision)
    {
        try
        {
            OnDecisionChanged?.Invoke(decision);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in decision event handler");
        }
    }
}
