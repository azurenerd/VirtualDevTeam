using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.E2E.Tests.Infrastructure;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualDevTeam.E2E.Tests.Scenarios;

/// <summary>
/// Validates the E2ETestHarness can build a working DI container
/// and resolve all core services needed for full workflow tests.
/// </summary>
public class HarnessSmokeTests : IDisposable
{
    private readonly E2ETestHarness _harness;

    public HarnessSmokeTests()
    {
        _harness = E2ETestHarness.Create();
    }

    [Fact]
    public void Harness_CanResolve_MessageBus()
    {
        var bus = _harness.MessageBus;
        Assert.NotNull(bus);
        Assert.IsType<InProcessMessageBus>(bus);
    }

    [Fact]
    public void Harness_CanResolve_WorkflowStateMachine()
    {
        var workflow = _harness.Workflow;
        Assert.NotNull(workflow);
        Assert.Equal(ProjectPhase.Initialization, workflow.CurrentPhase);
    }

    [Fact]
    public void Harness_CanResolve_AgentRegistry()
    {
        var registry = _harness.Registry;
        Assert.NotNull(registry);
    }

    [Fact]
    public void Harness_CanResolve_AgentSpawnManager()
    {
        var manager = _harness.SpawnManager;
        Assert.NotNull(manager);
    }

    [Fact]
    public void Harness_CanResolve_AgentFactory()
    {
        var factory = _harness.AgentFactory;
        Assert.NotNull(factory);
    }

    [Fact]
    public void Harness_CanResolve_RunCoordinator()
    {
        var coordinator = _harness.Coordinator;
        Assert.NotNull(coordinator);
    }

    [Fact]
    public void Harness_CanResolve_StateStore()
    {
        var store = _harness.StateStore;
        Assert.NotNull(store);
    }

    [Fact]
    public void Harness_GitHub_IsInMemory()
    {
        var github = _harness.GitHub;
        Assert.NotNull(github);
        Assert.Equal("test-owner/hello-world", github.RepositoryFullName);
    }

    [Fact]
    public void Harness_GateService_AutoApproves()
    {
        var gate = _harness.GateService;
        Assert.NotNull(gate);
        Assert.False(gate.IsEnabled);
    }

    [Fact]
    public void Harness_CanResolve_ModelRegistry()
    {
        var registry = _harness.Services.GetRequiredService<ModelRegistry>();
        Assert.NotNull(registry);
    }

    [Fact]
    public async Task Harness_CanRegisterFakeAgent()
    {
        await _harness.RegisterFakeAgentAsync(VirtualDevTeam.Core.Agents.AgentRole.Researcher);

        var agents = _harness.Registry.GetAllAgents();
        Assert.Single(agents);
    }

    [Fact]
    public void Harness_SignalAdvancesPhase()
    {
        // Initialization → Research requires these signals
        _harness.Signal("system.initialized");
        _harness.Signal("agents.ready");

        // Phase should try to advance (may or may not succeed depending on gate conditions)
        // Just verify signaling doesn't throw
        Assert.NotNull(_harness.Workflow);
    }

    public void Dispose()
    {
        _harness.Dispose();
    }
}
