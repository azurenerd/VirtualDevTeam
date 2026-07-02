using System.Collections.Concurrent;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Tracks which agents currently have active LLM calls in progress.
/// Used by the dashboard to overlay "Working (AI)" status on agents
/// that are waiting for a Copilot CLI response.
/// </summary>
public sealed class ActiveLlmCallTracker
{
    private readonly ConcurrentDictionary<string, LlmCallInfo> _activeCalls = new();

    /// <summary>Raised when any LLM call starts or completes, enabling real-time dashboard refresh.</summary>
    public event EventHandler<LlmCallChangedEventArgs>? LlmCallChanged;

    public void NotifyCallStarted(string agentId, string modelName, string? context = null)
    {
        _activeCalls[agentId] = new LlmCallInfo(modelName, DateTime.UtcNow, context);
        LlmCallChanged?.Invoke(this, new LlmCallChangedEventArgs(agentId, IsStarted: true));
    }

    public void NotifyCallCompleted(string agentId)
    {
        _activeCalls.TryRemove(agentId, out _);
        LlmCallChanged?.Invoke(this, new LlmCallChangedEventArgs(agentId, IsStarted: false));
    }

    /// <summary>
    /// Returns the active LLM call info for the given agent, or null if no call is in progress.
    /// </summary>
    public LlmCallInfo? GetActiveCall(string agentId)
    {
        return _activeCalls.TryGetValue(agentId, out var info) ? info : null;
    }

    /// <summary>Returns all agents with active LLM calls.</summary>
    public IReadOnlyDictionary<string, LlmCallInfo> GetAllActiveCalls() => _activeCalls;

    /// <summary>LLM call info including optional context describing what the AI is generating.</summary>
    public sealed record LlmCallInfo(string ModelName, DateTime StartedAt, string? Context = null);

    /// <summary>Event args for LLM call start/complete notifications.</summary>
    public sealed record LlmCallChangedEventArgs(string AgentId, bool IsStarted);
}
