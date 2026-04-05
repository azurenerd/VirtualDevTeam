using AgentSquad.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Integration.Tests;

public class WorkflowStateMachineTests
{
    private readonly AgentRegistry _registry;
    private readonly WorkflowStateMachine _workflow;

    public WorkflowStateMachineTests()
    {
        _registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        _workflow = new WorkflowStateMachine(_registry, NullLogger<WorkflowStateMachine>.Instance);
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
}
