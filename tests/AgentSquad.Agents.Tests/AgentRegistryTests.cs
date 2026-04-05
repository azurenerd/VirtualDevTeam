using AgentSquad.Core.Agents;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.Agents.Tests;

public class AgentRegistryTests : IDisposable
{
    private readonly AgentRegistry _registry;

    public AgentRegistryTests()
    {
        _registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
    }

    private static TestAgent CreateTestAgent(AgentRole role = AgentRole.Researcher, string? id = null)
    {
        var identity = new AgentIdentity
        {
            Id = id ?? $"test-{Guid.NewGuid():N}",
            DisplayName = $"Test {role}",
            Role = role,
            ModelTier = "standard"
        };
        return new TestAgent(identity, NullLogger<AgentBase>.Instance);
    }

    [Fact]
    public async Task RegisterAsync_AddsAgent()
    {
        var agent = CreateTestAgent();

        await _registry.RegisterAsync(agent);

        var result = _registry.GetAgent(agent.Identity.Id);
        Assert.NotNull(result);
        Assert.Equal(agent.Identity.Id, result.Identity.Id);
    }

    [Fact]
    public async Task GetAllAgents_ReturnsRegisteredAgents()
    {
        var agent1 = CreateTestAgent(AgentRole.Researcher);
        var agent2 = CreateTestAgent(AgentRole.Architect);

        await _registry.RegisterAsync(agent1);
        await _registry.RegisterAsync(agent2);

        var all = _registry.GetAllAgents();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAgentsByRole_FiltersCorrectly()
    {
        var researcher1 = CreateTestAgent(AgentRole.Researcher);
        var architect = CreateTestAgent(AgentRole.Architect);
        var researcher2 = CreateTestAgent(AgentRole.Researcher);

        await _registry.RegisterAsync(researcher1);
        await _registry.RegisterAsync(architect);
        await _registry.RegisterAsync(researcher2);

        var researchers = _registry.GetAgentsByRole(AgentRole.Researcher);
        Assert.Equal(2, researchers.Count);
        Assert.All(researchers, a => Assert.Equal(AgentRole.Researcher, a.Identity.Role));
    }

    [Fact]
    public async Task UnregisterAsync_RemovesAgent()
    {
        var agent = CreateTestAgent();
        await _registry.RegisterAsync(agent);

        await _registry.UnregisterAsync(agent.Identity.Id);

        Assert.Null(_registry.GetAgent(agent.Identity.Id));
        Assert.Empty(_registry.GetAllAgents());
    }

    [Fact]
    public async Task StatusChanged_EventFires()
    {
        var agent = CreateTestAgent();
        await _registry.RegisterAsync(agent);

        AgentStatusChangedEventArgs? eventArgs = null;
        _registry.AgentStatusChanged += (_, e) => eventArgs = e;

        // Trigger a status change via InitializeAsync (Requested -> Initializing -> Online)
        await agent.InitializeAsync();

        Assert.NotNull(eventArgs);
    }

    private class TestAgent : AgentBase
    {
        public TestAgent(AgentIdentity identity, ILogger<AgentBase> logger)
            : base(identity, logger) { }

        protected override Task RunAgentLoopAsync(CancellationToken ct)
            => Task.CompletedTask;
    }
}
