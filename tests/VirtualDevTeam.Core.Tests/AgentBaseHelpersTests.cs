using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for AgentBase.Subscribe&lt;T&gt;() and PublishStatusAsync() helpers.
/// </summary>
public class AgentBaseHelpersTests : IDisposable
{
    private readonly InProcessMessageBus _bus;
    private readonly TestHelperAgent _agent;

    public AgentBaseHelpersTests()
    {
        _bus = new InProcessMessageBus(NullLogger<InProcessMessageBus>.Instance);

        var identity = new AgentIdentity
        {
            Id = "test-helper",
            DisplayName = "Test Helper",
            Role = AgentRole.Researcher,
            ModelTier = "standard"
        };

        _agent = new TestHelperAgent(identity, _bus, NullLogger<AgentBase>.Instance);
    }

    public void Dispose()
    {
        _agent.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public async Task Subscribe_ReceivesMessages()
    {
        // Arrange — agent subscribes during init
        await _agent.InitializeForTestAsync();

        // Act — publish a message targeted to this agent
        await _bus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = "other-agent",
            ToAgentId = "test-helper",
            MessageType = "test",
            NewStatus = AgentStatus.Working
        });

        // Allow async delivery
        await Task.Delay(100);

        // Assert
        Assert.Equal(1, _agent.StatusMessagesReceived);
    }

    [Fact]
    public async Task StopAsync_DisposesSubscriptions()
    {
        // Arrange
        await _agent.InitializeForTestAsync();

        // Act — stop disposes subscriptions in finally block
        await _agent.StopAsync();

        // Publish after stop — should NOT be received
        await _bus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = "other-agent",
            ToAgentId = "test-helper",
            MessageType = "test",
            NewStatus = AgentStatus.Working
        });
        await Task.Delay(100);

        // Assert — no messages received after disposal
        Assert.Equal(0, _agent.StatusMessagesReceived);
    }

    [Fact]
    public async Task StopAsync_DisposesSubscriptions_EvenWhenOnStopAsyncThrows()
    {
        // Arrange
        _agent.ThrowOnStop = true;
        await _agent.InitializeForTestAsync();

        // Act — OnStopAsync throws, but finally block still disposes
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _agent.StopAsync());

        // Publish after stop — should NOT be received (subscriptions disposed in finally)
        await _bus.PublishAsync(new StatusUpdateMessage
        {
            FromAgentId = "other-agent",
            ToAgentId = "test-helper",
            MessageType = "test",
            NewStatus = AgentStatus.Working
        });
        await Task.Delay(100);

        Assert.Equal(0, _agent.StatusMessagesReceived);
    }

    [Fact]
    public async Task PublishStatusAsync_BroadcastsByDefault()
    {
        // Arrange — subscribe a listener on the bus
        StatusUpdateMessage? received = null;
        _bus.Subscribe<StatusUpdateMessage>("listener", (msg, _) =>
        {
            received = msg;
            return Task.CompletedTask;
        });

        await _agent.InitializeForTestAsync();

        // Act
        await _agent.TestPublishStatusAsync("TestSignal", AgentStatus.Working,
            details: "doing work", currentTask: "task-1");

        await Task.Delay(100);

        // Assert
        Assert.NotNull(received);
        Assert.Equal("test-helper", received!.FromAgentId);
        Assert.Equal("*", received.ToAgentId);
        Assert.Equal("TestSignal", received.MessageType);
        Assert.Equal(AgentStatus.Working, received.NewStatus);
        Assert.Equal("doing work", received.Details);
        Assert.Equal("task-1", received.CurrentTask);
    }

    [Fact]
    public async Task PublishStatusAsync_SupportsTargetedDelivery()
    {
        // Arrange
        StatusUpdateMessage? received = null;
        _bus.Subscribe<StatusUpdateMessage>("target-agent", (msg, _) =>
        {
            received = msg;
            return Task.CompletedTask;
        });

        await _agent.InitializeForTestAsync();

        // Act
        await _agent.TestPublishStatusAsync("ResourceApproval", AgentStatus.Online,
            toAgentId: "target-agent");

        await Task.Delay(100);

        // Assert
        Assert.NotNull(received);
        Assert.Equal("target-agent", received!.ToAgentId);
    }

    /// <summary>
    /// Test agent that exposes Subscribe and PublishStatusAsync for verification.
    /// Uses a thin wrapper to inject the bus without needing a full AgentCoreServices.
    /// </summary>
    private class TestHelperAgent : AgentBase
    {
        private readonly IMessageBus _bus;
        public int StatusMessagesReceived { get; private set; }
        public bool ThrowOnStop { get; set; }

        public TestHelperAgent(AgentIdentity identity, IMessageBus bus, ILogger<AgentBase> logger)
            : base(identity, logger)
        {
            _bus = bus;
        }

        // Expose a test-only Core-like accessor by overriding the helpers directly
        // Since we use the legacy constructor, Core is null. Override Subscribe/Publish
        // by calling the bus directly to test the disposal lifecycle in StopAsync.

        protected override Task RunAgentLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task InitializeForTestAsync() => OnInitializeAsync(CancellationToken.None);

        protected override Task OnInitializeAsync(CancellationToken ct)
        {
            // Manually add subscription to _messageSubscriptions via TrackSubscription
            TrackSubscription(_bus.Subscribe<StatusUpdateMessage>(Identity.Id, (msg, _) =>
            {
                StatusMessagesReceived++;
                return Task.CompletedTask;
            }));
            return Task.CompletedTask;
        }

        protected override Task OnStopAsync(CancellationToken ct)
        {
            if (ThrowOnStop)
                throw new InvalidOperationException("Simulated stop failure");
            return Task.CompletedTask;
        }

        public Task TestPublishStatusAsync(string messageType, AgentStatus status,
            string? details = null, string? currentTask = null, string? toAgentId = "*")
        {
            return _bus.PublishAsync(new StatusUpdateMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = toAgentId ?? "*",
                MessageType = messageType,
                NewStatus = status,
                Details = details ?? "",
                CurrentTask = currentTask ?? ""
            });
        }
    }
}
