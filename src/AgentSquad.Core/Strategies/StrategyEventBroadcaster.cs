using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using AgentSquad.Core.Strategies.Contracts;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// Abstraction for pushing strategy events over SignalR (or any other transport).
/// Implemented in the Runner against <c>IHubContext&lt;AgentHub&gt;</c>; no-op in tests.
/// </summary>
public interface IStrategyBroadcaster
{
    Task BroadcastAsync(string eventName, object payload, CancellationToken ct);
}

public sealed class NullStrategyBroadcaster : IStrategyBroadcaster
{
    public static readonly NullStrategyBroadcaster Instance = new();
    public Task BroadcastAsync(string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// <see cref="IStrategyEventSink"/> implementation that updates <see cref="CandidateStateStore"/>
/// and rebroadcasts the event to connected dashboard clients. Activity events are throttled
/// to prevent overwhelming the UI with high-frequency updates.
/// </summary>
public sealed class StrategyEventBroadcaster : IStrategyEventSink, IDisposable
{
    private readonly ILogger<StrategyEventBroadcaster> _logger;
    private readonly CandidateStateStore _store;
    private readonly IStrategyBroadcaster _broadcaster;

    /// <summary>Activity broadcast throttle: buffer per (runId, taskId, strategyId), flush every 500ms.</summary>
    private readonly ConcurrentDictionary<(string, string, string), CandidateActivityEvent> _activityBuffer = new();
    private readonly Timer? _activityFlushTimer;
    private static readonly TimeSpan ActivityThrottleInterval = TimeSpan.FromMilliseconds(500);

    public StrategyEventBroadcaster(
        ILogger<StrategyEventBroadcaster> logger,
        CandidateStateStore store,
        IStrategyBroadcaster? broadcaster = null)
    {
        _logger = logger;
        _store = store;
        _broadcaster = broadcaster ?? NullStrategyBroadcaster.Instance;
        _activityFlushTimer = new Timer(FlushActivityBuffer, null, ActivityThrottleInterval, ActivityThrottleInterval);
    }

    public async Task EmitAsync(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            switch (eventName)
            {
                case StrategyEvents.CandidateStarted when payload is CandidateStartedEvent s:
                    _store.RecordStarted(s);
                    break;
                case StrategyEvents.CandidateCompleted when payload is CandidateCompletedEvent c:
                    _store.RecordCompleted(c);
                    break;
                case StrategyEvents.CandidateEvaluated when payload is CandidateEvaluatedEvent ev:
                    _store.RecordEvaluated(ev);
                    break;
                case StrategyEvents.CandidateScored when payload is CandidateScoredEvent sc:
                    _store.RecordScored(sc);
                    break;
                case StrategyEvents.CandidateDetail when payload is CandidateDetailEvent dt:
                    _store.RecordDetail(dt);
                    break;
                case StrategyEvents.CandidateActivity when payload is CandidateActivityEvent act:
                    _store.RecordActivity(act);
                    // Buffer for throttled broadcast instead of immediate send.
                    _activityBuffer[(act.RunId, act.TaskId, act.StrategyId)] = act;
                    return; // Skip the immediate broadcast below — flushed by timer.
                case StrategyEvents.WinnerSelected when payload is WinnerSelectedEvent w:
                    _store.RecordWinner(w);
                    break;
                default:
                    // Gate events + unknown events passed through to broadcaster but not
                    // persisted in the store.
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let store errors break the orchestration loop.
            _logger.LogWarning(ex, "CandidateStateStore update failed for event {Event}", eventName);
        }

        try
        {
            await _broadcaster.BroadcastAsync(eventName, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Strategy event broadcast failed for {Event}", eventName);
        }
    }

    private void FlushActivityBuffer(object? state)
    {
        if (_activityBuffer.IsEmpty) return;

        // Snapshot and clear: swap out all buffered entries atomically per key.
        var entries = new List<CandidateActivityEvent>();
        foreach (var key in _activityBuffer.Keys.ToArray())
        {
            if (_activityBuffer.TryRemove(key, out var evt))
                entries.Add(evt);
        }

        foreach (var evt in entries)
        {
            try
            {
                _broadcaster.BroadcastAsync(StrategyEvents.CandidateActivity, evt, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Activity broadcast flush failed for {Strategy}", evt.StrategyId);
            }
        }
    }

    public void Dispose()
    {
        _activityFlushTimer?.Dispose();
    }
}
