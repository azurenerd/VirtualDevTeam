using AgentSquad.Core.Agents;
using AgentSquad.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Agents.Tests;

/// <summary>
/// Tests for the pause/resume lifecycle at both per-agent (AgentBase) and
/// global (RunCoordinator) levels.
/// </summary>
public class PauseResumeTests : IDisposable
{
    private readonly PausableTestAgent _agent;

    public PauseResumeTests()
    {
        var identity = new AgentIdentity
        {
            Id = "test-pause-agent",
            DisplayName = "Test Pause Agent",
            Role = AgentRole.Researcher,
            ModelTier = "standard"
        };
        _agent = new PausableTestAgent(identity, NullLogger<AgentBase>.Instance);
    }

    public void Dispose()
    {
        _agent.Dispose();
    }

    // ──────────────────────────────────────────────────────
    // Per-agent pause via control messages
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleMessage_ControlPause_SetsStatusToPaused()
    {
        await _agent.StartAsync(CancellationToken.None);

        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };

        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);

        Assert.Equal(AgentStatus.Paused, _agent.Status);
    }

    [Fact]
    public async Task HandleMessage_ControlResume_SetsStatusToWorking()
    {
        await _agent.StartAsync(CancellationToken.None);

        // Pause first
        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);
        Assert.Equal(AgentStatus.Paused, _agent.Status);

        // Now resume
        var resumeMsg = new AgentMessage
        {
            MessageType = "control.resume",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(resumeMsg, CancellationToken.None);

        Assert.Equal(AgentStatus.Working, _agent.Status);
    }

    [Fact]
    public async Task HandleMessage_ControlPause_DoesNotDelegateToDerived()
    {
        await _agent.StartAsync(CancellationToken.None);

        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);

        // OnMessageReceivedAsync should NOT have been called for control messages
        Assert.Equal(0, _agent.MessagesReceived);
    }

    [Fact]
    public async Task HandleMessage_RegularMessage_DelegatesToDerived()
    {
        await _agent.StartAsync(CancellationToken.None);

        var msg = new AgentMessage
        {
            MessageType = "task.assignment",
            FromAgentId = "pm",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(msg, CancellationToken.None);

        Assert.Equal(1, _agent.MessagesReceived);
    }

    [Fact]
    public async Task HandleMessage_DoublePause_IsIdempotent()
    {
        await _agent.StartAsync(CancellationToken.None);

        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };

        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);
        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);

        Assert.Equal(AgentStatus.Paused, _agent.Status);

        // Resume once should work (no double-release exception)
        var resumeMsg = new AgentMessage
        {
            MessageType = "control.resume",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(resumeMsg, CancellationToken.None);

        Assert.Equal(AgentStatus.Working, _agent.Status);
    }

    // ──────────────────────────────────────────────────────
    // WaitIfPausedAsync cooperative gate
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task WaitIfPausedAsync_WhenNotPaused_ReturnsImmediately()
    {
        await _agent.StartAsync(CancellationToken.None);

        // Should not block
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _agent.TestWaitIfPausedAsync(cts.Token);
        // If we get here, it returned immediately ✓
    }

    [Fact]
    public async Task WaitIfPausedAsync_WhenPaused_BlocksUntilResumed()
    {
        await _agent.StartAsync(CancellationToken.None);

        // Pause the agent
        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);

        // Start WaitIfPausedAsync in background — it should block
        var waitCompleted = false;
        var waitTask = Task.Run(async () =>
        {
            await _agent.TestWaitIfPausedAsync(CancellationToken.None);
            waitCompleted = true;
        });

        // Give it time to block
        await Task.Delay(200);
        Assert.False(waitCompleted, "WaitIfPausedAsync should be blocking while paused");

        // Resume the agent — unblocks the wait
        var resumeMsg = new AgentMessage
        {
            MessageType = "control.resume",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(resumeMsg, CancellationToken.None);

        // Wait should now complete
        var completed = await Task.WhenAny(waitTask, Task.Delay(5000));
        Assert.True(waitCompleted, "WaitIfPausedAsync should unblock after resume");
    }

    [Fact]
    public async Task WaitIfPausedAsync_WhenPaused_CancelledByCancellationToken()
    {
        await _agent.StartAsync(CancellationToken.None);

        // Pause the agent
        var pauseMsg = new AgentMessage
        {
            MessageType = "control.pause",
            FromAgentId = "orchestrator",
            ToAgentId = _agent.Identity.Id,

        };
        await _agent.HandleMessageAsync(pauseMsg, CancellationToken.None);

        // Cancel should unblock the wait
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _agent.TestWaitIfPausedAsync(cts.Token));
    }

    // ──────────────────────────────────────────────────────
    // Test helper agent
    // ──────────────────────────────────────────────────────

    private class PausableTestAgent : AgentBase
    {
        public int MessagesReceived { get; private set; }

        public PausableTestAgent(AgentIdentity identity, ILogger<AgentBase> logger)
            : base(identity, logger) { }

        protected override Task RunAgentLoopAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task OnMessageReceivedAsync(AgentMessage message, CancellationToken ct)
        {
            MessagesReceived++;
            return Task.CompletedTask;
        }

        /// <summary>Expose WaitIfPausedAsync for testing.</summary>
        public Task TestWaitIfPausedAsync(CancellationToken ct) => WaitIfPausedAsync(ct);
    }
}
