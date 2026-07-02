using Microsoft.AspNetCore.SignalR;
using VirtualDevTeam.Core.HealthMonitor;

namespace VirtualDevTeam.Dashboard.Hubs;

/// <summary>
/// SignalR hub that pushes <see cref="FlowMonitorEvent"/>s to subscribed dashboards
/// in real time. Companion to <see cref="Services.FlowMonitorEventRelay"/>, which
/// drains the in-process bus and broadcasts to <c>Clients.All</c>.
///
/// <para>
/// Hub method <c>Subscribe()</c> is provided as an explicit handshake the JS client
/// can call after connection — the relay broadcasts unconditionally so the call is
/// purely advisory, but it gives clients a one-shot "you're connected" return that
/// they can wait on.
/// </para>
///
/// <para>
/// In standalone-dashboard mode there is no in-process FlowMonitor, so the bus stays
/// empty and the hub broadcasts nothing — the route is still mapped so a future
/// remote-streaming bridge can plug in without recompiling the dashboard.
/// </para>
/// </summary>
public sealed class FlowMonitorHub : Hub
{
    public Task Subscribe() => Task.CompletedTask;
}
