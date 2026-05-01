using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Workspace;
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

        var mockGitHub = new Mock<IGitHubService>();
        mockGitHub.Setup(g => g.RepositoryFullName).Returns("test-owner/test-repo");
        services.AddSingleton(mockGitHub.Object);

        // Platform interfaces (mock — tests don't exercise real platform calls)
        services.AddSingleton(new Mock<AgentSquad.Core.DevPlatform.Capabilities.IPullRequestService>().Object);
        services.AddSingleton(new Mock<AgentSquad.Core.DevPlatform.Capabilities.IWorkItemService>().Object);
        services.AddSingleton(new Mock<AgentSquad.Core.DevPlatform.Capabilities.IRepositoryContentService>().Object);
        services.AddSingleton(new Mock<AgentSquad.Core.DevPlatform.Capabilities.IReviewService>().Object);
        services.AddSingleton(new Mock<AgentSquad.Core.DevPlatform.Capabilities.IBranchService>().Object);
        services.AddInProcessMessageBus();
        services.AddSingleton<AgentUsageTracker>();
        services.AddSingleton<AgentSquad.Core.AI.ActiveLlmCallTracker>();
        services.AddSemanticKernelModels();
        services.AddSingleton<AgentSquad.Core.AI.IChatCompletionRunner, AgentSquad.Core.AI.ChatCompletionRunner>();

        // Persistence
        var testDbPath = Path.Combine(Path.GetTempPath(), $"agentsquad-test-{Guid.NewGuid():N}.db");
        services.AddSingleton(new AgentStateStore(testDbPath));
        services.AddSingleton(new AgentMemoryStore(testDbPath));
        services.AddSingleton<ProjectFileManager>(sp =>
            new ProjectFileManager(
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>()));

        // GitHub workflows
        services.AddSingleton<PullRequestWorkflow>();
        services.AddSingleton<IssueWorkflow>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<IGateCheckService, GateCheckService>();

        // Reasoning services (needed by PM, Architect, PE, Researcher)
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog, AgentSquad.Core.Agents.Reasoning.AgentReasoningLog>();
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>();

        // Task step tracking
        services.AddSingleton<AgentSquad.Core.Agents.Steps.IAgentTaskTracker, AgentSquad.Core.Agents.Steps.AgentTaskTracker>();

        // Prompt template service (needed by all agents)
        services.AddSingleton<IPromptTemplateService, PromptTemplateService>();

        // Agent dependency bundles (mirrors Runner/Program.cs registrations)
        services.AddSingleton(sp => new AgentSquad.Core.Agents.AgentCoreServices(
            messageBus: sp.GetRequiredService<IMessageBus>(),
            modelRegistry: sp.GetRequiredService<AgentSquad.Core.Configuration.ModelRegistry>(),
            chatRunner: sp.GetRequiredService<AgentSquad.Core.AI.IChatCompletionRunner>(),
            projectFiles: sp.GetRequiredService<ProjectFileManager>(),
            memoryStore: sp.GetRequiredService<AgentMemoryStore>(),
            gateCheck: sp.GetRequiredService<IGateCheckService>(),
            config: sp.GetRequiredService<IOptions<AgentSquadConfig>>(),
            promptService: sp.GetService<IPromptTemplateService>(),
            roleContextProvider: sp.GetService<AgentSquad.Core.AI.RoleContextProvider>(),
            selfAssessment: sp.GetService<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>(),
            reasoningLog: sp.GetService<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog>(),
            taskTracker: sp.GetService<AgentSquad.Core.Agents.Steps.IAgentTaskTracker>(),
            stateStore: sp.GetService<AgentStateStore>()));
        services.AddSingleton(sp => new AgentSquad.Core.Agents.AgentPlatformServices(
            prService: sp.GetRequiredService<IPullRequestService>(),
            workItemService: sp.GetRequiredService<IWorkItemService>(),
            repoContent: sp.GetRequiredService<IRepositoryContentService>(),
            reviewService: sp.GetRequiredService<IReviewService>(),
            prWorkflow: sp.GetRequiredService<PullRequestWorkflow>(),
            branchService: sp.GetService<IBranchService>(),
            issueWorkflow: sp.GetService<IssueWorkflow>(),
            branchProvider: sp.GetService<IRunBranchProvider>(),
            docResolver: sp.GetService<AgentSquad.Core.DevPlatform.Capabilities.IDocumentReferenceResolver>(),
            platformHost: sp.GetService<IPlatformHostContext>()));
        services.AddSingleton(sp => new AgentSquad.Core.Agents.AgentWorkspaceServices(
            buildRunner: sp.GetService<BuildRunner>(),
            testRunner: sp.GetService<TestRunner>(),
            playwrightRunner: sp.GetService<PlaywrightRunner>(),
            metrics: sp.GetService<AgentSquad.Core.Metrics.BuildTestMetrics>()));

        // DevPlatform abstraction (needed by PM, SE, TE for platform-agnostic operations)
        services.Configure<AgentSquad.Core.DevPlatform.Config.DevPlatformConfig>(cfg =>
        {
            cfg.Platform = AgentSquad.Core.DevPlatform.Config.DevPlatformType.GitHub;
        });
        services.AddDevPlatform();

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
            AgentRole.SoftwareEngineer,
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
        Assert.True(spawnManager.CanSpawn(AgentRole.SoftwareEngineer));
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

        var softwareEng = await spawnManager.SpawnAgentAsync(AgentRole.SoftwareEngineer, cts.Token);
        Assert.NotNull(softwareEng);

        var tester = await spawnManager.SpawnAgentAsync(AgentRole.TestEngineer, cts.Token);
        Assert.NotNull(tester);

        Assert.Equal(5, registry.GetAllAgents().Count);
    }
}
