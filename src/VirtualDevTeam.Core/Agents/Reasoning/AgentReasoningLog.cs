using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Agents.Reasoning;

/// <summary>
/// Thread-safe in-memory reasoning log with event notifications for real-time dashboard updates.
/// Retains the last N events per agent to bound memory usage.
/// </summary>
public class AgentReasoningLog : IAgentReasoningLog
{
    private readonly ConcurrentDictionary<string, List<AgentReasoningEvent>> _events = new();
    private readonly ILogger<AgentReasoningLog> _logger;
    private readonly object _trimLock = new();

    /// <summary>Max events retained per agent before oldest are trimmed.</summary>
    private const int MaxEventsPerAgent = 200;

    /// <summary>Max total events across all agents before global trim.</summary>
    private const int MaxTotalEvents = 2000;

    public event Action<AgentReasoningEvent>? OnEvent;

    public AgentReasoningLog(ILogger<AgentReasoningLog> logger)
    {
        _logger = logger;
    }

    public void Log(AgentReasoningEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var list = _events.GetOrAdd(evt.AgentId, _ => new List<AgentReasoningEvent>());
        lock (list)
        {
            list.Add(evt);
            if (list.Count > MaxEventsPerAgent)
                list.RemoveRange(0, list.Count - MaxEventsPerAgent);
        }

        _logger.LogDebug(
            "[{AgentName}] {EventType}: {Summary}",
            evt.AgentDisplayName, evt.EventType, evt.Summary);

        try
        {
            OnEvent?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in reasoning event handler");
        }
    }

    public IReadOnlyList<AgentReasoningEvent> GetEvents(string agentId)
    {
        if (!_events.TryGetValue(agentId, out var list))
            return Array.Empty<AgentReasoningEvent>();

        lock (list)
        {
            return list.ToList();
        }
    }

    public IReadOnlyList<AgentReasoningEvent> GetEventsSince(string agentId, DateTime since)
    {
        if (!_events.TryGetValue(agentId, out var list))
            return Array.Empty<AgentReasoningEvent>();

        lock (list)
        {
            return list.Where(e => e.Timestamp > since).ToList();
        }
    }

    public IReadOnlyList<AgentReasoningEvent> GetRecentEvents(int count = 50)
    {
        var all = new List<AgentReasoningEvent>();
        foreach (var kvp in _events)
        {
            lock (kvp.Value)
            {
                all.AddRange(kvp.Value);
            }
        }

        return all
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
    }

    public IReadOnlyList<string> GetAgentIds()
    {
        return _events.Keys.ToList();
    }

    public void Clear(string agentId)
    {
        if (_events.TryGetValue(agentId, out var list))
        {
            lock (list) { list.Clear(); }
        }
    }

    public void ClearAll()
    {
        foreach (var kvp in _events)
        {
            lock (kvp.Value) { kvp.Value.Clear(); }
        }
        _events.Clear();
    }
}
