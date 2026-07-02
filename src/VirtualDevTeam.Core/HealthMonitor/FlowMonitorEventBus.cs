using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// In-process pub/sub for <see cref="FlowMonitorEvent"/>s.
///
/// <para>
/// Uses a bounded <see cref="Channel{T}"/> with <see cref="BoundedChannelFullMode.DropOldest"/>
/// so the FlowMonitor (writer) is guaranteed never to block on a slow / disconnected
/// dashboard. When the buffer overflows, the oldest event is dropped — viewers will
/// see a small gap in the live log, which is acceptable for a debug stream.
/// </para>
///
/// <para>
/// A dashboard-side relay (e.g., a <c>BackgroundService</c> that calls
/// <c>IHubContext&lt;FlowMonitorHub&gt;.Clients.All.SendAsync</c>) drains
/// <see cref="Reader"/> and fans events out to all SignalR-subscribed clients.
/// In standalone-dashboard mode (no in-process FlowMonitor) the bus is registered
/// but stays empty — no crash, no events.
/// </para>
/// </summary>
public sealed class FlowMonitorEventBus
{
    /// <summary>
    /// Hard-coded buffer size. Empirical: at MED verbosity the FlowMonitor
    /// emits ~4-8 events per tick (every 30s by default). 200 gives us ~10 minutes
    /// of headroom even if a relay stalls. Tuning would only matter under unrealistic
    /// load — the channel is single-process / single-machine.
    /// </summary>
    private const int Capacity = 200;

    private readonly Channel<FlowMonitorEvent> _channel;
    private readonly ILogger<FlowMonitorEventBus> _logger;
    private long _published;
    private long _dropped;

    public FlowMonitorEventBus(ILogger<FlowMonitorEventBus> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _channel = Channel.CreateBounded<FlowMonitorEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Reader the relay drains — one consumer per process.</summary>
    public ChannelReader<FlowMonitorEvent> Reader => _channel.Reader;

    /// <summary>Total events successfully written (pre-drop accounting).</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>Total events dropped because the buffer was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Publish an event without blocking. Auto-tags <see cref="FlowMonitorEvent.AgentId"/>
    /// and <see cref="FlowMonitorEvent.SessionId"/> from <see cref="AgentCallContext"/>
    /// when the caller hasn't already supplied them.
    /// </summary>
    public void Publish(FlowMonitorEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Auto-tag from AsyncLocal context if not supplied
        if (evt.AgentId is null || evt.SessionId is null)
        {
            evt = evt with
            {
                AgentId = evt.AgentId ?? AgentCallContext.CurrentAgentId,
                SessionId = evt.SessionId ?? AgentCallContext.CurrentSessionId,
            };
        }

        // TryWrite on a DropOldest-bounded channel always returns true — but we still
        // check defensively in case the channel is completed.
        if (!_channel.Writer.TryWrite(evt))
        {
            // Should be unreachable while the channel is open.
            Interlocked.Increment(ref _dropped);
            return;
        }
        Interlocked.Increment(ref _published);
    }

    /// <summary>
    /// Convenience: publish a raw message at <see cref="FlowMonitorEventKind.Info"/>.
    /// Use the typed helpers below for lifecycle / detector / finding / action events.
    /// </summary>
    public void Info(string source, string message, string? detail = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Info,
            Source = source,
            Message = message,
            Detail = detail,
        });

    /// <summary>Publish a lifecycle event (tick boundary, service start/stop).</summary>
    public void Lifecycle(string message, string? detail = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Lifecycle,
            Source = "service",
            Message = message,
            Detail = detail,
        });

    /// <summary>Publish a detector event (detector start / finish).</summary>
    public void Detector(string detectorId, string message, string? detail = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Detector,
            Source = detectorId,
            Message = message,
            Detail = detail,
        });

    /// <summary>Publish a finding event (a detector emitted an observation).</summary>
    public void Finding(FlowFinding finding, string message, bool suppressed = false)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Finding,
            Source = finding.DetectorId,
            Message = message,
            FindingId = finding.Id,
            AgentId = finding.TargetAgentId,
            Severity = finding.Severity,
            Detail = suppressed ? "suppressed by dedup window" : finding.Rationale,
        });

    /// <summary>Publish an action-started event.</summary>
    public void ActionStarted(string actionType, string? findingId, string? target, string message)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Action,
            Source = actionType,
            Message = message,
            FindingId = findingId,
            AgentId = target,
        });

    /// <summary>Publish an action-completed event with its outcome.</summary>
    public void ActionCompleted(FlowAction action, string? message = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.ActionResult,
            Source = action.ActionType,
            Message = message ?? $"{action.ActionType} → {action.Result}",
            FindingId = action.FindingId,
            ActionId = action.Id,
            AgentId = action.Target,
            ActionResult = action.Result,
            Detail = action.Detail,
        });

    /// <summary>Publish an error event (caught exception at the FlowMonitor scope).</summary>
    public void Error(string source, string message, string? detail = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.Error,
            Source = source,
            Message = message,
            Detail = detail,
        });

    /// <summary>Publish a status-change event so dashboard clients know to re-fetch findings.</summary>
    public void StatusChange(string findingId, string message, string? detail = null)
        => Publish(new FlowMonitorEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = FlowMonitorEventKind.StatusChange,
            Source = "persistence",
            Message = message,
            FindingId = findingId,
            Detail = detail,
        });
}
