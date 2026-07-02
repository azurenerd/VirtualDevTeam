using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Dashboard.Hubs;
using VirtualDevTeam.Dashboard.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace VirtualDevTeam.Dashboard.Unit.Tests;

public class SignalRStrategyBroadcasterTests
{
    [Fact]
    public async Task BroadcastAsync_sends_StrategyEvent_to_all_clients_with_event_name_and_payload()
    {
        var all = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.SetupGet(c => c.All).Returns(all.Object);

        var hub = new Mock<IHubContext<AgentHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        var bcast = new SignalRStrategyBroadcaster(hub.Object);
        var payload = new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow);

        await bcast.BroadcastAsync(StrategyEvents.CandidateStarted, payload, CancellationToken.None);

        all.Verify(a => a.SendCoreAsync(
            "StrategyEvent",
            It.Is<object?[]>(args => args.Length == 2
                && (string?)args[0] == StrategyEvents.CandidateStarted
                && ReferenceEquals(args[1], payload)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NullStrategyBroadcaster_instance_is_noop()
    {
        await NullStrategyBroadcaster.Instance.BroadcastAsync("any", new object(), CancellationToken.None);
    }
}
