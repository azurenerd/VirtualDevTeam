using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class StrategyEventBroadcasterTests
{
    private sealed class CapturingBroadcaster : IStrategyBroadcaster
    {
        public readonly List<(string Event, object Payload)> Messages = new();
        public Task BroadcastAsync(string eventName, object payload, CancellationToken ct)
        {
            Messages.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingBroadcaster : IStrategyBroadcaster
    {
        public Task BroadcastAsync(string eventName, object payload, CancellationToken ct)
            => throw new InvalidOperationException("nope");
    }

    [Fact]
    public async Task Events_update_store_and_broadcast()
    {
        var store = new CandidateStateStore();
        var bcast = new CapturingBroadcaster();
        var sink = new StrategyEventBroadcaster(NullLogger<StrategyEventBroadcaster>.Instance, store, bcast);

        var started = new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow);
        await sink.EmitAsync(StrategyEvents.CandidateStarted, started, CancellationToken.None);

        Assert.Single(store.GetActiveTasks());
        Assert.Single(bcast.Messages);
        Assert.Equal(StrategyEvents.CandidateStarted, bcast.Messages[0].Event);
    }

    [Fact]
    public async Task Unknown_event_name_is_broadcast_but_does_not_break_sink()
    {
        var store = new CandidateStateStore();
        var bcast = new CapturingBroadcaster();
        var sink = new StrategyEventBroadcaster(NullLogger<StrategyEventBroadcaster>.Instance, store, bcast);

        await sink.EmitAsync("gate:started", new { foo = "bar" }, CancellationToken.None);

        Assert.Empty(store.GetActiveTasks());
        Assert.Single(bcast.Messages);
        Assert.Equal("gate:started", bcast.Messages[0].Event);
    }

    [Fact]
    public async Task Broadcaster_exception_does_not_propagate()
    {
        var store = new CandidateStateStore();
        var throwing = new ThrowingBroadcaster();
        var sink = new StrategyEventBroadcaster(NullLogger<StrategyEventBroadcaster>.Instance, store, throwing);

        var ex = await Record.ExceptionAsync(() => sink.EmitAsync(
            StrategyEvents.CandidateStarted,
            new CandidateStartedEvent("r", "t", "baseline", DateTimeOffset.UtcNow),
            CancellationToken.None));

        Assert.Null(ex);
        Assert.Single(store.GetActiveTasks());
    }
}
