using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Tests for the scenario-pipeline gates added to WorkflowStateMachine:
/// Architecture exit gate (ScenariosArchitectureMapped),
/// EngineeringPlanning exit gate (ScenariosTasksAssigned),
/// and Completion display gates (ScenariosAllCriticalVerified + TestingAppAlive).
/// Also covers the graceful-degrade path when no scenarios are registered.
/// </summary>
public class WorkflowStateMachineScenarioGatesTests : IDisposable
{
    private readonly AgentRegistry _registry;
    private readonly AgentStateStore _stateStore;
    private readonly Mock<IScenarioRegistry> _scenarioRegistry;

    public WorkflowStateMachineScenarioGatesTests()
    {
        _registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"scenario-gates-test-{Guid.NewGuid():N}.db");
        _stateStore = new AgentStateStore(dbPath);
        _scenarioRegistry = new Mock<IScenarioRegistry>();
    }

    public void Dispose() => _stateStore.Dispose();

    private WorkflowStateMachine CreateWorkflow(bool withScenarios = true)
    {
        _scenarioRegistry.Setup(r => r.Current).Returns(
            withScenarios
                ? new[] { MakeScenario("S01") }
                : Array.Empty<Scenario>());

        return new WorkflowStateMachine(
            _registry,
            _stateStore,
            new Mock<IGateCheckService>().Object,
            NullLogger<WorkflowStateMachine>.Instance,
            config: null,
            scenarioRegistry: _scenarioRegistry.Object);
    }

    private static Scenario MakeScenario(string id) => new()
    {
        Id = id,
        Title = $"Test scenario {id}",
        JourneyKind = JourneyKind.UiInteraction,
        Actor = "User",
        Trigger = "Clicks a button",
        Priority = ScenarioPriority.Critical
    };

    // ── Architecture exit gate ───────────────────────────────────────────────

    [Fact]
    public void Architecture_Gate_Blocked_WhenScenariosMappingSignalAbsent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Architecture, "test");
        workflow.Signal(WorkflowStateMachine.Signals.ArchitectureDocReady);
        workflow.Signal(WorkflowStateMachine.Signals.ArchitectureComplete);
        // ScenariosArchitectureMapped NOT signalled

        var gates = workflow.GetCurrentGates();
        var mappingGate = gates.SingleOrDefault(g => g.Name == "Scenarios Architecture Mapped");

        Assert.NotNull(mappingGate);
        Assert.False(mappingGate!.IsMet);
    }

    [Fact]
    public void Architecture_Gate_Met_WhenScenariosMappingSignalPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Architecture, "test");
        workflow.Signal(WorkflowStateMachine.Signals.ScenariosArchitectureMapped);

        var gates = workflow.GetCurrentGates();
        var mappingGate = gates.Single(g => g.Name == "Scenarios Architecture Mapped");

        Assert.True(mappingGate.IsMet);
    }

    [Fact]
    public void Architecture_Gate_AutoSatisfied_WhenNoScenariosRegistered()
    {
        var workflow = CreateWorkflow(withScenarios: false);
        workflow.ForcePhase(ProjectPhase.Architecture, "test");

        var gates = workflow.GetCurrentGates();
        var mappingGate = gates.Single(g => g.Name == "Scenarios Architecture Mapped");

        Assert.True(mappingGate.IsMet);
    }

    // ── EngineeringPlanning exit gate ────────────────────────────────────────

    [Fact]
    public void EngineeringPlanning_Gate_Blocked_WhenTasksAssignedSignalAbsent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.EngineeringPlanning, "test");
        workflow.Signal(WorkflowStateMachine.Signals.EngineeringPlanReady);
        workflow.Signal(WorkflowStateMachine.Signals.SoftwareEngineerReady);
        // ScenariosTasksAssigned NOT signalled

        var gates = workflow.GetCurrentGates();
        var tasksGate = gates.SingleOrDefault(g => g.Name == "Scenarios Tasks Assigned");

        Assert.NotNull(tasksGate);
        Assert.False(tasksGate!.IsMet);
    }

    [Fact]
    public void EngineeringPlanning_Gate_Met_WhenTasksAssignedSignalPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.EngineeringPlanning, "test");
        workflow.Signal(WorkflowStateMachine.Signals.ScenariosTasksAssigned);

        var gates = workflow.GetCurrentGates();
        var tasksGate = gates.Single(g => g.Name == "Scenarios Tasks Assigned");

        Assert.True(tasksGate.IsMet);
    }

    [Fact]
    public void EngineeringPlanning_Gate_AutoSatisfied_WhenNoScenariosRegistered()
    {
        var workflow = CreateWorkflow(withScenarios: false);
        workflow.ForcePhase(ProjectPhase.EngineeringPlanning, "test");

        var gates = workflow.GetCurrentGates();
        var tasksGate = gates.Single(g => g.Name == "Scenarios Tasks Assigned");

        Assert.True(tasksGate.IsMet);
    }

    // ── Completion display gates ─────────────────────────────────────────────

    [Fact]
    public void Completion_Gates_BothBlocked_WhenNoSignalsPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Completion, "test");

        var gates = workflow.GetCurrentGates();

        var criticalVerified = gates.Single(g => g.Name == "All Critical Scenarios Verified");
        var appAlive = gates.Single(g => g.Name == "Application Alive");

        Assert.False(criticalVerified.IsMet);
        Assert.False(appAlive.IsMet);
    }

    [Fact]
    public void Completion_CriticalVerifiedGate_Met_WhenSignalPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Completion, "test");
        workflow.Signal(WorkflowStateMachine.Signals.ScenariosAllCriticalVerified);

        var gates = workflow.GetCurrentGates();
        var gate = gates.Single(g => g.Name == "All Critical Scenarios Verified");

        Assert.True(gate.IsMet);
    }

    [Fact]
    public void Completion_AppAliveGate_Met_WhenSignalPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Completion, "test");
        workflow.Signal(WorkflowStateMachine.Signals.TestingAppAlive);

        var gates = workflow.GetCurrentGates();
        var gate = gates.Single(g => g.Name == "Application Alive");

        Assert.True(gate.IsMet);
    }

    [Fact]
    public void Completion_BothGatesMet_WhenBothSignalsPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Completion, "test");
        workflow.Signal(WorkflowStateMachine.Signals.ScenariosAllCriticalVerified);
        workflow.Signal(WorkflowStateMachine.Signals.TestingAppAlive);

        var gates = workflow.GetCurrentGates();

        Assert.True(gates.All(g => g.IsMet));
    }

    // ── Graceful-degrade path ────────────────────────────────────────────────

    [Fact]
    public void GracefulDegrade_AllGatesAutoSatisfied_WhenNoScenariosAndPastInit()
    {
        var workflow = CreateWorkflow(withScenarios: false);
        workflow.ForcePhase(ProjectPhase.Completion, "test");

        var gates = workflow.GetCurrentGates();

        Assert.True(gates.All(g => g.IsMet),
            $"Expected all gates auto-satisfied. Unmet: {string.Join(", ", gates.Where(g => !g.IsMet).Select(g => g.Name))}");
    }

    [Fact]
    public void GracefulDegrade_DoesNotAutoSatisfy_WhenScenariosPresent()
    {
        var workflow = CreateWorkflow(withScenarios: true);
        workflow.ForcePhase(ProjectPhase.Completion, "test");

        var gates = workflow.GetCurrentGates();

        Assert.False(gates.All(g => g.IsMet),
            "Gates should NOT all be met when scenarios exist but signals are absent.");
    }

    [Fact]
    public void GracefulDegrade_DoesNotApply_WhenInInitializationPhase()
    {
        // Init gate checks PM Online — scenario degrade guard returns false for Init phase.
        var workflow = CreateWorkflow(withScenarios: false);

        var result = workflow.TryAdvancePhase(out var reason);

        Assert.False(result);
        Assert.Contains("Program Manager", reason);
    }

    // ── Backward-compat: no IScenarioRegistry injected ───────────────────────

    [Fact]
    public void WorkflowConstructedWithoutScenarioRegistry_AllScenarioGatesAutoSatisfied()
    {
        var workflow = new WorkflowStateMachine(
            _registry,
            _stateStore,
            new Mock<IGateCheckService>().Object,
            NullLogger<WorkflowStateMachine>.Instance);

        workflow.ForcePhase(ProjectPhase.Completion, "test");
        var gates = workflow.GetCurrentGates();

        Assert.True(gates.All(g => g.IsMet));
    }

    // ── Signal constant consistency ──────────────────────────────────────────

    [Fact]
    public void TopLevelSignalsClass_HasCorrectConstantValues()
    {
        Assert.Equal("scenarios.approved", Signals.ScenariosApproved);
        Assert.Equal("scenarios.architecture.mapped", Signals.ScenariosArchitectureMapped);
        Assert.Equal("scenarios.tasks.assigned", Signals.ScenariosTasksAssigned);
        Assert.Equal("scenarios.all_critical_verified", Signals.ScenariosAllCriticalVerified);
        Assert.Equal("scenarios.drift_detected", Signals.ScenariosDriftDetected);
        Assert.Equal("testing.app.alive", Signals.TestingAppAlive);
    }

    [Fact]
    public void NestedSignalsClass_MatchesTopLevelSignalsClass()
    {
        Assert.Equal(WorkflowStateMachine.Signals.ScenariosApproved, Signals.ScenariosApproved);
        Assert.Equal(WorkflowStateMachine.Signals.ScenariosArchitectureMapped, Signals.ScenariosArchitectureMapped);
        Assert.Equal(WorkflowStateMachine.Signals.ScenariosTasksAssigned, Signals.ScenariosTasksAssigned);
        Assert.Equal(WorkflowStateMachine.Signals.ScenariosAllCriticalVerified, Signals.ScenariosAllCriticalVerified);
        Assert.Equal(WorkflowStateMachine.Signals.ScenariosDriftDetected, Signals.ScenariosDriftDetected);
        Assert.Equal(WorkflowStateMachine.Signals.TestingAppAlive, Signals.TestingAppAlive);
    }
}
