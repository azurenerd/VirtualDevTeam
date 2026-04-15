using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Agents;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentSquad.Integration.Tests;

public class SystemBootstrapTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public SystemBootstrapTests()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging();

        // Configuration
        services.Configure<AgentSquadConfig>(config =>
        {
            config.Project = new ProjectConfig
            {
                Name = "Test Project",
                Description = "Integration test project",
                GitHubRepo = "test/repo",
                GitHubToken = "fake-token-for-test",
                DefaultBranch = "main"
            };
            config.Limits = new LimitsConfig
            {
                MaxAdditionalEngineers = 3,
                AgentTimeoutMinutes = 15,
                GitHubPollIntervalSeconds = 30,
                MaxConcurrentAgents = 10
            };
            config.Models = new Dictionary<string, ModelConfig>
            {
                ["premium"] = new() { Provider = "openai", Model = "gpt-4", ApiKey = "test-key" },
                ["standard"] = new() { Provider = "openai", Model = "gpt-3.5-turbo", ApiKey = "test-key" },
                ["budget"] = new() { Provider = "openai", Model = "gpt-4o-mini", ApiKey = "test-key" },
                ["local"] = new() { Provider = "ollama", Model = "codellama", Endpoint = "http://localhost:11434" }
            };
        });
        services.Configure<LimitsConfig>(limits =>
        {
            limits.MaxAdditionalEngineers = 3;
            limits.AgentTimeoutMinutes = 15;
            limits.GitHubPollIntervalSeconds = 30;
            limits.MaxConcurrentAgents = 10;
        });

        // Mock GitHub service
        var mockGitHub = new Mock<IGitHubService>();
        mockGitHub.Setup(g => g.RepositoryFullName).Returns("test-owner/test-repo");
        services.AddSingleton(mockGitHub.Object);

        // Core services
        services.AddInProcessMessageBus();
        services.AddSingleton<AgentUsageTracker>();
        services.AddSemanticKernelModels();

        // Persistence
        var testDbPath = Path.Combine(Path.GetTempPath(), $"agentsquad-test-{Guid.NewGuid():N}.db");
        services.AddSingleton(new AgentStateStore(testDbPath));
        services.AddSingleton(new AgentMemoryStore(testDbPath));
        services.AddSingleton<ProjectFileManager>(sp =>
            new ProjectFileManager(
                sp.GetRequiredService<IGitHubService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>()));

        // GitHub workflows
        services.AddSingleton<PullRequestWorkflow>(sp =>
            new PullRequestWorkflow(
                sp.GetRequiredService<IGitHubService>(),
                sp.GetRequiredService<ILogger<PullRequestWorkflow>>()));
        services.AddSingleton<IssueWorkflow>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<IGateCheckService, GateCheckService>();

        // Reasoning services (needed by PM, Architect, PE, Researcher)
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog, AgentSquad.Core.Agents.Reasoning.AgentReasoningLog>();
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>();

        // Prompt template service (needed by all agents)
        services.AddSingleton<IPromptTemplateService, PromptTemplateService>();

        // Orchestrator
        services.AddOrchestrator();

        // Agent factory
        services.AddSingleton<IAgentFactory, AgentFactory>();

        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        // Clean up the AgentStateStore (which holds a SQLite connection)
        var store = _provider.GetService<AgentStateStore>();
        store?.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void CanResolve_MessageBus()
    {
        var bus = _provider.GetRequiredService<IMessageBus>();
        Assert.NotNull(bus);
        Assert.IsType<InProcessMessageBus>(bus);
    }

    [Fact]
    public void CanResolve_AgentRegistry()
    {
        var registry = _provider.GetRequiredService<AgentRegistry>();
        Assert.NotNull(registry);
    }

    [Fact]
    public void CanResolve_WorkflowStateMachine()
    {
        var workflow = _provider.GetRequiredService<WorkflowStateMachine>();
        Assert.NotNull(workflow);
    }

    [Fact]
    public void CanResolve_AgentSpawnManager()
    {
        var manager = _provider.GetRequiredService<AgentSpawnManager>();
        Assert.NotNull(manager);
    }

    [Fact]
    public void CanResolve_AgentFactory()
    {
        var factory = _provider.GetRequiredService<IAgentFactory>();
        Assert.NotNull(factory);
        Assert.IsType<AgentFactory>(factory);
    }

    [Fact]
    public void CanResolve_PullRequestWorkflow()
    {
        var workflow = _provider.GetRequiredService<PullRequestWorkflow>();
        Assert.NotNull(workflow);
    }

    [Fact]
    public void CanResolve_IssueWorkflow()
    {
        var workflow = _provider.GetRequiredService<IssueWorkflow>();
        Assert.NotNull(workflow);
    }

    [Fact]
    public void CanResolve_AgentStateStore()
    {
        var store = _provider.GetRequiredService<AgentStateStore>();
        Assert.NotNull(store);
    }

    [Fact]
    public void CanResolve_ModelRegistry()
    {
        var registry = _provider.GetRequiredService<ModelRegistry>();
        Assert.NotNull(registry);
    }

    [Fact]
    public void AgentFactory_CanCreateCoreAgentTypes()
    {
        var factory = _provider.GetRequiredService<IAgentFactory>();

        var roles = new[]
        {
            AgentRole.ProgramManager,
            AgentRole.Researcher,
            AgentRole.Architect,
            AgentRole.PrincipalEngineer,
            AgentRole.TestEngineer
        };

        foreach (var role in roles)
        {
            var identity = new AgentIdentity
            {
                Id = $"test-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                DisplayName = $"Test {role}",
                Role = role,
                ModelTier = "standard"
            };

            var agent = factory.Create(role, identity);
            Assert.NotNull(agent);
            Assert.Equal(role, agent.Identity.Role);
        }
    }

    [Fact]
    public void SpawnManager_ReportsCanSpawnCoreRoles()
    {
        var spawnManager = _provider.GetRequiredService<AgentSpawnManager>();

        Assert.True(spawnManager.CanSpawn(AgentRole.ProgramManager));
        Assert.True(spawnManager.CanSpawn(AgentRole.Researcher));
        Assert.True(spawnManager.CanSpawn(AgentRole.Architect));
        Assert.True(spawnManager.CanSpawn(AgentRole.PrincipalEngineer));
        Assert.True(spawnManager.CanSpawn(AgentRole.TestEngineer));
    }

    [Fact]
    public async Task FullStartupSequence_SpawnsAllCoreAgents()
    {
        var spawnManager = _provider.GetRequiredService<AgentSpawnManager>();
        var registry = _provider.GetRequiredService<AgentRegistry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var pm = await spawnManager.SpawnAgentAsync(AgentRole.ProgramManager, cts.Token);
        Assert.NotNull(pm);
        Assert.Equal(AgentRole.ProgramManager, pm.Role);

        var researcher = await spawnManager.SpawnAgentAsync(AgentRole.Researcher, cts.Token);
        Assert.NotNull(researcher);

        var architect = await spawnManager.SpawnAgentAsync(AgentRole.Architect, cts.Token);
        Assert.NotNull(architect);

        var principal = await spawnManager.SpawnAgentAsync(AgentRole.PrincipalEngineer, cts.Token);
        Assert.NotNull(principal);

        var tester = await spawnManager.SpawnAgentAsync(AgentRole.TestEngineer, cts.Token);
        Assert.NotNull(tester);

        Assert.Equal(5, registry.GetAllAgents().Count);
    }
}
