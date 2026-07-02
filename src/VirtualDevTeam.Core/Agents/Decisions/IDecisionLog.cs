namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// Captures and stores agent decisions for observability and gating.
/// Thread-safe storage with real-time event notifications for dashboard updates.
/// </summary>
public interface IDecisionLog
{
    /// <summary>Log a new decision.</summary>
    void Log(AgentDecision decision);

    /// <summary>Update an existing decision (e.g., after approval/rejection).</summary>
    void Update(string decisionId, DecisionStatus status, string? humanFeedback = null, string? approvedBy = null);

    /// <summary>Get all decisions for a specific agent, ordered by timestamp.</summary>
    IReadOnlyList<AgentDecision> GetDecisions(string agentId);

    /// <summary>Get all decisions across all agents, ordered by timestamp descending.</summary>
    IReadOnlyList<AgentDecision> GetAllDecisions();

    /// <summary>Get all decisions associated with a specific PR number.</summary>
    IReadOnlyList<AgentDecision> GetDecisionsForPr(int prNumber);

    /// <summary>Get decisions that answer a specific source question (for deduplication).</summary>
    IReadOnlyList<AgentDecision> GetDecisionsBySourceQuestion(string question);

    /// <summary>Get decisions filtered by impact level (at or above the given level).</summary>
    IReadOnlyList<AgentDecision> GetDecisionsByMinLevel(DecisionImpactLevel minLevel);

    /// <summary>Get pending (gated) decisions awaiting human review.</summary>
    IReadOnlyList<AgentDecision> GetPendingDecisions();

    /// <summary>Get a single decision by ID.</summary>
    AgentDecision? GetDecision(string decisionId);

    /// <summary>Get decision counts by impact level (for overview widget).</summary>
    IReadOnlyDictionary<DecisionImpactLevel, int> GetCountsByLevel();

    /// <summary>Get all agents that have decisions.</summary>
    IReadOnlyList<string> GetAgentIds();

    /// <summary>Clear all decisions.</summary>
    void ClearAll();

    /// <summary>
    /// Restore decisions that were persisted to the activity log in a previous run.
    /// Idempotent — safe to call even if decisions are already loaded.
    /// No-op if the implementation has no backing store.
    /// </summary>
    Task RestoreFromDatabaseAsync(CancellationToken ct = default);

    /// <summary>Fired when a new decision is logged or updated. Subscribe for real-time updates.</summary>
    event Action<AgentDecision>? OnDecisionChanged;
}
