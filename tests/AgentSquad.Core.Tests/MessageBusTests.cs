using AgentSquad.Core.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Core.Tests;

public class MessageBusTests : IDisposable
{
    private readonly InProcessMessageBus _bus;

    public MessageBusTests()
    {
        _bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);
    }

    public void Dispose()
    {
        _bus.Dispose();
    }

    private class TestMessage
    {
        public string ToAgentId { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private class OtherMessage
    {
        public string ToAgentId { get; set; } = "";
        public string Data { get; set; } = "";
    }

    [Fact]
    public async Task PublishAsync_DeliversToTargetedSubscriber()
    {
        TestMessage? received = null;
        var tcs = new TaskCompletionSource();

        _bus.Subscribe<TestMessage>("agent-1", (msg, ct) =>
        {
            received = msg;
            tcs.SetResult();
            return Task.CompletedTask;
        });

        var message = new TestMessage { ToAgentId = "agent-1", Content = "hello" };
        await _bus.PublishAsync(message);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Equal("hello", received.Content);
    }

    [Fact]
    public async Task PublishAsync_Broadcast_DeliversToAllSubscribers()
    {
        var received1 = new TaskCompletionSource<TestMessage>();
        var received2 = new TaskCompletionSource<TestMessage>();

        _bus.Subscribe<TestMessage>("agent-1", (msg, ct) =>
        {
            received1.SetResult(msg);
            return Task.CompletedTask;
        });

        _bus.Subscribe<TestMessage>("agent-2", (msg, ct) =>
        {
            received2.SetResult(msg);
            return Task.CompletedTask;
        });

        var message = new TestMessage { ToAgentId = "*", Content = "broadcast" };
        await _bus.PublishAsync(message);

        var msg1 = await received1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var msg2 = await received2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("broadcast", msg1.Content);
        Assert.Equal("broadcast", msg2.Content);
    }

    [Fact]
    public async Task Subscribe_OnlyReceivesCorrectMessageType()
    {
        var receivedTest = new TaskCompletionSource<TestMessage>();
        bool receivedOther = false;

        _bus.Subscribe<TestMessage>("agent-1", (msg, ct) =>
        {
            receivedTest.SetResult(msg);
            return Task.CompletedTask;
        });

        _bus.Subscribe<OtherMessage>("agent-1", (msg, ct) =>
        {
            receivedOther = true;
            return Task.CompletedTask;
        });

        await _bus.PublishAsync(new TestMessage { ToAgentId = "agent-1", Content = "test" });
        await receivedTest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Allow time for any stray delivery
        await Task.Delay(200);

        Assert.False(receivedOther);
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        int count = 0;
        var firstReceived = new TaskCompletionSource();

        var sub = _bus.Subscribe<TestMessage>("agent-1", (msg, ct) =>
        {
            Interlocked.Increment(ref count);
            if (count == 1) firstReceived.TrySetResult();
            return Task.CompletedTask;
        });

        await _bus.PublishAsync(new TestMessage { ToAgentId = "agent-1", Content = "msg1" });
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sub.Dispose();

        await _bus.PublishAsync(new TestMessage { ToAgentId = "agent-1", Content = "msg2" });
        await Task.Delay(300);

        Assert.Equal(1, count);
    }

    [Fact]
    public void GetPendingCount_ReturnsZeroForUnknownAgent()
    {
        Assert.Equal(0, _bus.GetPendingCount("nonexistent"));
    }
}
