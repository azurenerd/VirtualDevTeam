using System.Collections.Concurrent;

namespace AgentSquad.Core.AI;

/// <summary>
/// Tracks which agents currently have in-flight LLM calls.
/// Thread-safe singleton read by the Dashboard to show AI activity indicators
/// without replacing the agent's descriptive status reason.
/// </summary>
public sealed class ActiveLlmCallTracker
{
    private readonly ConcurrentDictionary<string, LlmCallInfo> _activeCalls = new();

    /// <summary>Record that an agent has started an LLM call.</summary>
    public void NotifyCallStarted(string agentId, string modelName)
    {
        _activeCalls[agentId] = new LlmCallInfo
        {
            AgentId = agentId,
            ModelName = modelName,
            StartedAt = DateTime.UtcNow
        };
    }

    /// <summary>Record that an agent's LLM call has completed.</summary>
    public void NotifyCallCompleted(string agentId)
    {
        _activeCalls.TryRemove(agentId, out _);
    }

    /// <summary>Check if a specific agent has an active LLM call.</summary>
    public bool IsCallActive(string agentId) => _activeCalls.ContainsKey(agentId);

    /// <summary>Get the call info for a specific agent, or null if no active call.</summary>
    public LlmCallInfo? GetActiveCall(string agentId) =>
        _activeCalls.TryGetValue(agentId, out var info) ? info : null;

    /// <summary>Get all currently active LLM calls.</summary>
    public IReadOnlyCollection<LlmCallInfo> GetAllActiveCalls() =>
        _activeCalls.Values.ToList().AsReadOnly();
}

/// <summary>Information about an in-flight LLM call for a specific agent.</summary>
public sealed record LlmCallInfo
{
    public required string AgentId { get; init; }
    public required string ModelName { get; init; }
    public required DateTime StartedAt { get; init; }
}
