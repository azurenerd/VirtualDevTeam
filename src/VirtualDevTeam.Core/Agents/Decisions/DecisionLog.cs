using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// Thread-safe in-memory decision log with event notifications for real-time dashboard updates.
/// Persists each decision to the activity_log SQLite table (event_type='decision', JSON payload)
/// so the log survives runner restarts. Call <see cref="RestoreFromDatabaseAsync"/> on startup
/// to reload decisions from the prior run.
/// Follows the same pattern as <see cref="Reasoning.AgentReasoningLog"/>.
/// </summary>
public class DecisionLog : IDecisionLog
{
    private readonly ConcurrentDictionary<string, AgentDecision> _decisionsById = new();
    private readonly ConcurrentDictionary<string, List<AgentDecision>> _decisionsByAgent = new();
    private readonly AgentStateStore? _stateStore;
    private readonly ILogger<DecisionLog> _logger;

    /// <summary>Max decisions retained per agent before oldest are trimmed.</summary>
    private const int MaxDecisionsPerAgent = 200;

    /// <summary>Max total decisions across all agents before global trim.</summary>
    private const int MaxTotalDecisions = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public event Action<AgentDecision>? OnDecisionChanged;

    public DecisionLog(ILogger<DecisionLog> logger, AgentStateStore? stateStore = null)
    {
        _logger = logger;
        _stateStore = stateStore;
    }

    public void Log(AgentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        _decisionsById[decision.Id] = decision;

        var list = _decisionsByAgent.GetOrAdd(decision.AgentId, _ => new List<AgentDecision>());
        lock (list)
        {
            // Prevent duplicate entries by ID (can happen if same decision is logged twice)
            if (!list.Any(d => d.Id == decision.Id))
            {
                list.Add(decision);
                if (list.Count > MaxDecisionsPerAgent)
                    list.RemoveRange(0, list.Count - MaxDecisionsPerAgent);
            }
        }

        TrimGlobalIfNeeded();

        _logger.LogInformation(
            "[{AgentName}] Decision [{Impact}]: {Title}",
            decision.AgentDisplayName, decision.ImpactLevel, decision.Title);

        // Persist to activity_log for cross-restart recovery (fire-and-forget).
        if (_stateStore is not null)
        {
            var json = JsonSerializer.Serialize(decision, JsonOptions);
            _ = _stateStore.LogActivityAsync(decision.AgentId, "decision", json);
        }

        RaiseEvent(decision);
    }

    public void Update(string decisionId, DecisionStatus status, string? humanFeedback = null, string? approvedBy = null)
    {
        if (!_decisionsById.TryGetValue(decisionId, out var existing))
        {
            _logger.LogWarning("Decision {Id} not found for update", decisionId);
            return;
        }

        // Atomic state transition: only resolve from Pending state
        if (existing.Status is not DecisionStatus.Pending and not DecisionStatus.AutoApproved
            && status is DecisionStatus.Approved or DecisionStatus.Rejected)
        {
            _logger.LogInformation("Decision {Id} already resolved as {Status} — skipping {NewStatus}",
                decisionId, existing.Status, status);
            return;
        }

        var updated = existing with
        {
            Status = status,
            ResolvedAt = DateTime.UtcNow,
            HumanFeedback = humanFeedback ?? existing.HumanFeedback,
            ApprovedBy = approvedBy ?? existing.ApprovedBy,
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

    public IReadOnlyList<AgentDecision> GetDecisionsForPr(int prNumber)
    {
        return GetAllDecisions().Where(d => d.AssociatedPrNumber == prNumber).ToList();
    }

    public IReadOnlyList<AgentDecision> GetDecisionsBySourceQuestion(string question)
    {
        return GetAllDecisions()
            .Where(d => !string.IsNullOrEmpty(d.SourceQuestion) &&
                        d.SourceQuestion.Equals(question, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

    public async Task RestoreFromDatabaseAsync(CancellationToken ct = default)
    {
        if (_stateStore is null) return;

        IReadOnlyList<ActivityLogEntry> entries;
        try
        {
            entries = await _stateStore.GetDecisionActivitiesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read decision entries from activity log — skipping restore");
            return;
        }

        var restored = 0;

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;

            AgentDecision? decision;
            try
            {
                decision = JsonSerializer.Deserialize<AgentDecision>(entry.Details, JsonOptions);
            }
            catch (JsonException)
            {
                // Legacy plain-text entries (e.g., PM gate-bypass messages) cannot be round-tripped.
                _logger.LogDebug(
                    "Skipping non-JSON decision activity entry {EntryId} — legacy plain-text format",
                    entry.Id);
                continue;
            }

            if (decision is null) continue;

            // Idempotent: skip if this decision is already in memory (e.g., logged this run).
            if (_decisionsById.ContainsKey(decision.Id)) continue;

            _decisionsById[decision.Id] = decision;
            var list = _decisionsByAgent.GetOrAdd(decision.AgentId, _ => new List<AgentDecision>());
            lock (list)
            {
                if (!list.Any(d => d.Id == decision.Id))
                    list.Add(decision);
            }

            restored++;
        }

        if (restored > 0)
            _logger.LogInformation("Restored {Count} decisions from activity log", restored);
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
