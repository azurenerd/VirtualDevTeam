using AgentSquad.Core.Configuration;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSquad.Integration.Tests;

public class WorkflowStateMachineTests : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly AgentStateStore _stateStore;
    private readonly WorkflowStateMachine _workflow;

    public WorkflowStateMachineTests()
    {
        _registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"workflow-test-{Guid.NewGuid():N}.db");
        _stateStore = new AgentStateStore(dbPath);
        _workflow = new WorkflowStateMachine(_registry, _stateStore,
            new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);
    }

    public void Dispose()
    {
        _stateStore.Dispose();
    }

    [Fact]
    public void InitialPhase_IsInitialization()
    {
        Assert.Equal(ProjectPhase.Initialization, _workflow.CurrentPhase);
    }

    [Fact]
    public void TryAdvancePhase_FailsWithoutPrerequisites()
    {
        var result = _workflow.TryAdvancePhase(out var blockerReason);

        Assert.False(result);
        Assert.NotNull(blockerReason);
        Assert.Contains("Program Manager", blockerReason);
    }

    [Fact]
    public void ForcePhase_AdvancesToTargetPhase()
    {
        _workflow.ForcePhase(ProjectPhase.Research, "Testing override");

        Assert.Equal(ProjectPhase.Research, _workflow.CurrentPhase);
    }

    [Fact]
    public void ForcePhase_FiresPhaseChangedEvent()
    {
        PhaseTransitionEventArgs? eventArgs = null;
        _workflow.PhaseChanged += (_, e) => eventArgs = e;

        _workflow.ForcePhase(ProjectPhase.Architecture, "Test override");

        Assert.NotNull(eventArgs);
        Assert.Equal(ProjectPhase.Initialization, eventArgs.OldPhase);
        Assert.Equal(ProjectPhase.Architecture, eventArgs.NewPhase);
        Assert.Equal("Test override", eventArgs.Reason);
    }

    [Fact]
    public void ForcePhase_RecordsTransitionHistory()
    {
        _workflow.ForcePhase(ProjectPhase.Research, "phase1");
        _workflow.ForcePhase(ProjectPhase.Architecture, "phase2");

        var history = _workflow.GetTransitionHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal(ProjectPhase.Initialization, history[0].OldPhase);
        Assert.Equal(ProjectPhase.Research, history[0].NewPhase);
        Assert.Equal(ProjectPhase.Research, history[1].OldPhase);
        Assert.Equal(ProjectPhase.Architecture, history[1].NewPhase);
    }

    [Fact]
    public void Signal_And_HasSignal_WorkCorrectly()
    {
        Assert.False(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete));

        _workflow.Signal(WorkflowStateMachine.Signals.ResearchComplete);

        Assert.True(_workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete));
    }

    [Fact]
    public void HasReachedPhase_ReflectsCurrentPhase()
    {
        Assert.True(_workflow.HasReachedPhase(ProjectPhase.Initialization));
        Assert.False(_workflow.HasReachedPhase(ProjectPhase.Research));

        _workflow.ForcePhase(ProjectPhase.Architecture, "test");

        Assert.True(_workflow.HasReachedPhase(ProjectPhase.Research));
        Assert.True(_workflow.HasReachedPhase(ProjectPhase.Architecture));
        Assert.False(_workflow.HasReachedPhase(ProjectPhase.EngineeringPlanning));
    }

    [Fact]
    public async Task RecoverAsync_RestoresPhaseAndSignals()
    {
        // Arrange: advance phase and add signals, then checkpoint
        _workflow.ForcePhase(ProjectPhase.ParallelDevelopment, "test");
        _workflow.Signal(WorkflowStateMachine.Signals.ResearchComplete);
        _workflow.Signal(WorkflowStateMachine.Signals.ArchitectureComplete);
        await _workflow.CheckpointAsync();

        // Create a new workflow instance (simulates crash + restart)
        var recovered = new WorkflowStateMachine(
            _registry, _stateStore,
            new Mock<IGateCheckService>().Object, NullLogger<WorkflowStateMachine>.Instance);

        // Act
        var result = await recovered.RecoverAsync();

        // Assert
        Assert.True(result);
        Assert.Equal(ProjectPhase.ParallelDevelopment, recovered.CurrentPhase);
        Assert.True(recovered.HasSignal(WorkflowStateMachine.Signals.ResearchComplete));
        Assert.True(recovered.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete));
    }

    [Fact]
    public async Task RecoverAsync_ReturnsFalse_WhenNoCheckpoint()
    {
        var result = await _workflow.RecoverAsync();
        Assert.False(result);
        Assert.Equal(ProjectPhase.Initialization, _workflow.CurrentPhase);
    }
}
