using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Records timestamped pipeline spans for the Flow Timeline page.
/// Span model (start+end) — not point-in-time milestones.
/// Thread-safe, idempotent, with per-entity duration computation.
///
/// Instrumentation: ~80% derived from existing signals (AgentTaskTracker,
/// WorkflowStateMachine, IMessageBus, PrLifecycleCalculator). Only ~10
/// direct RecordStart/RecordComplete calls needed for milestones without
/// existing signals.
/// </summary>
public sealed class FlowTimelineTracker
{
    private readonly ConcurrentDictionary<string, FlowSpan> _spans = new();
    private readonly ILogger<FlowTimelineTracker> _logger;
    private int _sequence;

    public FlowTimelineTracker(ILogger<FlowTimelineTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a new span. Returns the span ID for later completion.
    /// Idempotent — duplicate keys return the existing span ID.
    /// </summary>
    public string RecordStart(
        string eventType,
        string description,
        string? agentId = null,
        string? phase = null,
        MilestoneCategory category = MilestoneCategory.Work,
        string? entityType = null,
        string? entityId = null,
        string? parentSpanId = null,
        string? waveId = null,
        int attempt = 1,
        DateTimeOffset? startedAtUtc = null)
    {
        var key = $"{eventType}:{entityId ?? "global"}:{attempt}";
        var span = new FlowSpan
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Sequence = Interlocked.Increment(ref _sequence),
            EventType = eventType,
            Description = description,
            AgentId = agentId,
            Phase = phase,
            Category = category,
            EntityType = entityType,
            EntityId = entityId,
            ParentSpanId = parentSpanId,
            WaveId = waveId,
            StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow,
            IdempotencyKey = key,
        };

        if (_spans.TryAdd(key, span))
        {
            _logger.LogDebug("Flow span started: {EventType} — {Description}", eventType, description);
            OnSpanChanged?.Invoke(span);
            return span.Id;
        }

        // Already exists — return existing ID
        return _spans[key].Id;
    }

    /// <summary>
    /// Complete a span by its idempotency key or span ID.
    /// </summary>
    public void RecordComplete(string eventTypeOrSpanId, string? entityId = null, int attempt = 1)
    {
        var key = $"{eventTypeOrSpanId}:{entityId ?? "global"}:{attempt}";

        // Try by key first
        if (_spans.TryGetValue(key, out var span))
        {
            CompleteSpan(span);
            return;
        }

        // Try by span ID
        var byId = _spans.Values.FirstOrDefault(s => s.Id == eventTypeOrSpanId);
        if (byId is not null)
        {
            CompleteSpan(byId);
        }
    }

    /// <summary>
    /// Record a point-in-time event (instantly completed span).
    /// Convenience for milestones that don't have meaningful duration.
    /// </summary>
    public void RecordEvent(
        string eventType,
        string description,
        string? agentId = null,
        string? phase = null,
        MilestoneCategory category = MilestoneCategory.Handoff,
        string? entityType = null,
        string? entityId = null,
        string? parentSpanId = null,
        int attempt = 1,
        DateTimeOffset? occurredAtUtc = null)
    {
        var now = occurredAtUtc ?? DateTimeOffset.UtcNow;
        var key = $"{eventType}:{entityId ?? "global"}:{attempt}";
        var span = new FlowSpan
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Sequence = Interlocked.Increment(ref _sequence),
            EventType = eventType,
            Description = description,
            AgentId = agentId,
            Phase = phase,
            Category = category,
            EntityType = entityType,
            EntityId = entityId,
            ParentSpanId = parentSpanId,
            StartedAtUtc = now,
            CompletedAtUtc = now,
            IdempotencyKey = key,
        };

        if (_spans.TryAdd(key, span))
        {
            _logger.LogDebug("Flow event: {EventType} — {Description}", eventType, description);
            OnSpanChanged?.Invoke(span);
        }
    }

    /// <summary>Get all spans as a hierarchical timeline grouped by phase/entity.</summary>
    public IReadOnlyList<FlowSpanView> GetTimeline()
    {
        var ordered = _spans.Values
            .OrderBy(s => s.StartedAtUtc)
            .ThenBy(s => s.Sequence)
            .ToList();

        if (ordered.Count == 0) return Array.Empty<FlowSpanView>();

        var firstStart = ordered[0].StartedAtUtc;

        return ordered.Select(s => new FlowSpanView
        {
            Id = s.Id,
            Sequence = s.Sequence,
            EventType = s.EventType,
            Description = s.Description,
            AgentId = s.AgentId,
            Phase = s.Phase,
            Category = s.Category,
            EntityType = s.EntityType,
            EntityId = s.EntityId,
            ParentSpanId = s.ParentSpanId,
            WaveId = s.WaveId,
            StartedAtUtc = s.StartedAtUtc,
            CompletedAtUtc = s.CompletedAtUtc,
            Duration = s.CompletedAtUtc.HasValue ? s.CompletedAtUtc.Value - s.StartedAtUtc : null,
            IsInProgress = !s.CompletedAtUtc.HasValue,
            ElapsedSinceStart = DateTimeOffset.UtcNow - s.StartedAtUtc,
            TotalElapsed = s.StartedAtUtc - firstStart,
        }).ToList();
    }

    /// <summary>Get spans grouped by phase for column rendering.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FlowSpanView>> GetTimelineByPhase()
    {
        var timeline = GetTimeline();
        return timeline
            .GroupBy(s => s.Phase ?? "Other")
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FlowSpanView>)g.ToList());
    }

    /// <summary>Clear all spans (called on reset).</summary>
    public void Clear()
    {
        _spans.Clear();
        _sequence = 0;
    }

    /// <summary>Fired when a span is started or completed. Used for SignalR push.</summary>
    public event Action<FlowSpan>? OnSpanChanged;

    private void CompleteSpan(FlowSpan span)
    {
        if (span.CompletedAtUtc.HasValue) return; // already completed
        var updated = span with { CompletedAtUtc = DateTimeOffset.UtcNow };
        _spans[span.IdempotencyKey] = updated;
        _logger.LogDebug("Flow span completed: {EventType} — {Description}", span.EventType, span.Description);
        OnSpanChanged?.Invoke(updated);
    }
}

/// <summary>Stored span record.</summary>
public sealed record FlowSpan
{
    public required string Id { get; init; }
    public required int Sequence { get; init; }
    public required string EventType { get; init; }
    public required string Description { get; init; }
    public string? AgentId { get; init; }
    public string? Phase { get; init; }
    public MilestoneCategory Category { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? WaveId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public required string IdempotencyKey { get; init; }
}

/// <summary>Span with computed display fields.</summary>
public sealed record FlowSpanView
{
    public required string Id { get; init; }
    public required int Sequence { get; init; }
    public required string EventType { get; init; }
    public required string Description { get; init; }
    public string? AgentId { get; init; }
    public string? Phase { get; init; }
    public MilestoneCategory Category { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? WaveId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool IsInProgress { get; init; }
    public TimeSpan ElapsedSinceStart { get; init; }
    public TimeSpan TotalElapsed { get; init; }
}

/// <summary>Category for duration semantics.</summary>
public enum MilestoneCategory
{
    Work,
    Wait,
    Handoff,
    FastForward,
    HumanGate,
    LLM,
    Platform,
}