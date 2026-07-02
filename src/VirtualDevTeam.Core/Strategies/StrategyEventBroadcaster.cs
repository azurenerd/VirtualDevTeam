using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Strategies.Contracts;

namespace VirtualDevTeam.Core.Strategies;

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
    private readonly ConcurrentDictionary<(string, string, string), ConcurrentBag<CandidateActivityEvent>> _activityBuffer = new();
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
                    // Buffer for throttled broadcast — add to bag, not overwrite.
                    var bag = _activityBuffer.GetOrAdd(
                        (act.RunId, act.TaskId, act.StrategyId),
                        _ => new ConcurrentBag<CandidateActivityEvent>());
                    bag.Add(act);
                    return; // Skip the immediate broadcast below — flushed by timer.
                case StrategyEvents.WinnerSelected when payload is WinnerSelectedEvent w:
                    _store.RecordWinner(w);
                    break;
                case StrategyEvents.CandidateInitialScored when payload is CandidateInitialScoredEvent isc:
                    _store.RecordInitialScored(isc);
                    break;
                case StrategyEvents.CandidateRevisionStarted when payload is CandidateRevisionStartedEvent rs:
                    _store.RecordRevisionStarted(rs);
                    break;
                case StrategyEvents.CandidateRevisionCompleted when payload is CandidateRevisionCompletedEvent rc:
                    _store.RecordRevisionCompleted(rc);
                    break;
                case StrategyEvents.EvaluationProgress when payload is EvaluationProgressEvent ep:
                    _store.RecordProgress(ep);
                    break;
                case StrategyEvents.CandidateRetryStarted when payload is CandidateRetryStartedEvent rts:
                    _store.RecordRetryStarted(rts);
                    break;
                case StrategyEvents.CandidateRetryCompleted when payload is CandidateRetryCompletedEvent rtc:
                    _store.RecordRetryCompleted(rtc);
                    break;
                case StrategyEvents.OrchestrationCancelled when payload is OrchestrationCancelledEvent oc:
                    _store.RecordCancelled(oc);
                    break;
                case StrategyEvents.CandidateVideoReady when payload is CandidateVideoReadyEvent vr:
                    _store.RecordVideoReady(vr);
                    break;
                case StrategyEvents.MediaCaptureProgress when payload is MediaCaptureProgressEvent mp:
                    _store.RecordMediaCaptureProgress(mp);
                    break;
                case StrategyEvents.TaskPrLinked when payload is TaskPrLinkedEvent prl:
                    _store.RecordTaskPrLinked(prl);
                    break;
                case StrategyEvents.CandidateAnalyzerUpdate when payload is CandidateAnalyzerUpdateEvent au:
                    _store.RecordAnalyzerUpdate(au);
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

        // Snapshot and clear: swap out all buffered bags atomically per key.
        var batches = new List<CandidateActivityEvent>();
        foreach (var key in _activityBuffer.Keys.ToArray())
        {
            if (_activityBuffer.TryRemove(key, out var bag))
            {
                // Drain all events from the bag
                while (bag.TryTake(out var evt))
                    batches.Add(evt);
            }
        }

        foreach (var evt in batches)
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
