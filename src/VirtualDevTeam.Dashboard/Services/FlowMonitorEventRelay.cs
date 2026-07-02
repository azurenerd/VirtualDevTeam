using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Dashboard.Hubs;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Drains <see cref="FlowMonitorEventBus"/> and fans events out to all SignalR
/// clients connected to <see cref="FlowMonitorHub"/>.
///
/// <para>
/// Single-reader design: only one instance of this service should be registered per
/// process. The bus is configured with <c>SingleReader = true</c> so the channel
/// can use lock-free wakeups. If the dashboard is hosted in a different process
/// than the FlowMonitor (standalone mode), this relay simply has nothing to drain.
/// </para>
/// </summary>
public sealed class FlowMonitorEventRelay : BackgroundService
{
    private readonly FlowMonitorEventBus _bus;
    private readonly IHubContext<FlowMonitorHub> _hubContext;
    private readonly ILogger<FlowMonitorEventRelay> _logger;

    public FlowMonitorEventRelay(
        FlowMonitorEventBus bus,
        IHubContext<FlowMonitorHub> hubContext,
        ILogger<FlowMonitorEventRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("FlowMonitorEventRelay started — fanning bus events out to /hubs/flowmonitor clients");
        try
        {
            await foreach (var evt in _bus.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    // Single fan-out method name — the JS / .NET client subscribes to
                    // "FlowMonitorEvent" and inspects evt.Kind locally to colour-code.
                    await _hubContext.Clients.All
                        .SendAsync("FlowMonitorEvent", evt, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Broadcast failure must never starve the channel — log + drop.
                    _logger.LogDebug(ex, "FlowMonitorEventRelay: broadcast failed (non-fatal — event dropped)");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        _logger.LogInformation(
            "FlowMonitorEventRelay stopped — published={Published}, dropped={Dropped}",
            _bus.PublishedCount, _bus.DroppedCount);
    }
}
