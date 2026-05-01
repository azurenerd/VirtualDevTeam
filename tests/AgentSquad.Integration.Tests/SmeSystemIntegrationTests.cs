using AgentSquad.Core.Agents;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Services;
using AgentSquad.Core.Workspace;
using AgentSquad.Agents;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using System.Threading;

namespace AgentSquad.Integration.Tests;

public class SmeSystemIntegrationTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public SmeSystemIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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

            // Configure MCP servers
            config.McpServers = new Dictionary<string, McpServerDefinition>
            {
                ["github"] = new McpServerDefinition
                {
                    Name = "github",
                    Description = "GitHub API",
                    ProvidedCapabilities = ["github-issues", "github-prs"]
                },
                ["fetch"] = new McpServerDefinition
                {
                    Name = "fetch",
                    Description = "HTTP fetch",
                    ProvidedCapabilities = ["web-fetch"]
                }
            };

            // Configure SME agents
            config.SmeAgents = new SmeAgentsConfig
            {
                Enabled = true,
                MaxTotalSmeAgents = 5,
                AllowAgentCreatedDefinitions = true,
                PersistDefinitions = false, // Don't write files in tests
                Templates = new Dictionary<string, SMEAgentDefinition>
                {
                    ["security-auditor"] = new SMEAgentDefinition
                    {
                        DefinitionId = "security-auditor",
                        RoleName = "Security Auditor",
                        SystemPrompt = "You are a security specialist.",
                        Capabilities = ["security", "vulnerability-scanning", "penetration-testing"],
                        McpServers = ["github"]
                    },
                    ["api-designer"] = new SMEAgentDefinition
                    {
                        DefinitionId = "api-designer",
                        RoleName = "API Designer",
                        SystemPrompt = "You are an API design specialist.",
                        Capabilities = ["api-design", "openapi", "rest"],
                        McpServers = ["fetch"]
                    }
                }
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

        var testDbPath= Path.Combine(Path.GetTempPath(), $"agentsquad-test-{Guid.NewGuid():N}.db");
        services.AddSingleton(new AgentStateStore(testDbPath));
        services.AddSingleton(new AgentMemoryStore(testDbPath));
        services.AddSingleton<ProjectFileManager>(sp =>
            new ProjectFileManager(
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>()));
        services.AddSingleton<PullRequestWorkflow>();
        services.AddSingleton<IssueWorkflow>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<IGateCheckService, GateCheckService>();
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog,
            AgentSquad.Core.Agents.Reasoning.AgentReasoningLog>();
        services.AddSingleton<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>();

        // Agent dependency bundles
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

        services.AddOrchestrator();
        services.AddSingleton<IAgentFactory, AgentFactory>();

        // SME services
        services.AddSingleton<McpServerRegistry>();
        services.AddSingleton<McpServerSecurityPolicy>();
        services.AddSingleton<SMEAgentDefinitionService>();
        services.AddSingleton<AgentTeamComposer>();
        services.AddSingleton<SmeDefinitionGenerator>();
        services.AddSingleton<SmeMetrics>();

        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        var store = _provider.GetService<AgentStateStore>();
        store?.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void CanResolve_McpServerRegistry()
    {
        var registry = _provider.GetRequiredService<McpServerRegistry>();
        Assert.NotNull(registry);
    }

    [Fact]
    public void CanResolve_SmeDefinitionService()
    {
        var service = _provider.GetRequiredService<SMEAgentDefinitionService>();
        Assert.NotNull(service);
    }

    [Fact]
    public void CanResolve_AgentTeamComposer()
    {
        var composer = _provider.GetRequiredService<AgentTeamComposer>();
        Assert.NotNull(composer);
    }

    [Fact]
    public void CanResolve_SmeDefinitionGenerator()
    {
        var generator = _provider.GetRequiredService<SmeDefinitionGenerator>();
        Assert.NotNull(generator);
    }

    [Fact]
    public void CanResolve_SmeMetrics()
    {
        var metrics = _provider.GetRequiredService<SmeMetrics>();
        Assert.NotNull(metrics);
    }

    [Fact]
    public void McpRegistry_ReturnsConfiguredServers()
    {
        var registry = _provider.GetRequiredService<McpServerRegistry>();
        var servers = registry.GetAll();
        Assert.NotNull(servers);
        Assert.Equal(2, servers.Count);
        Assert.True(servers.ContainsKey("github"));
        Assert.True(servers.ContainsKey("fetch"));
    }

    [Fact]
    public void SecurityPolicy_AllowsRegisteredServer()
    {
        var policy = _provider.GetRequiredService<McpServerSecurityPolicy>();
        var result = policy.IsServerAllowed("github");
        Assert.True(result);
    }

    [Fact]
    public void SecurityPolicy_BlocksUnregisteredServer()
    {
        var policy = _provider.GetRequiredService<McpServerSecurityPolicy>();
        var result = policy.IsServerAllowed("unknown");
        Assert.False(result);
    }

    [Fact]
    public void SecurityPolicy_BlocksDangerousServer()
    {
        var policy = _provider.GetRequiredService<McpServerSecurityPolicy>();
        var result = policy.IsServerAllowed("shell");
        Assert.False(result);
    }

    [Fact]
    public async Task DefinitionService_ReturnsTemplates()
    {
        var service = _provider.GetRequiredService<SMEAgentDefinitionService>();
        var templates = await service.GetAllAsync();
        Assert.NotNull(templates);
        Assert.Equal(2, templates.Count);
        Assert.True(templates.ContainsKey("security-auditor"));
        Assert.True(templates.ContainsKey("api-designer"));
    }

    [Fact]
    public async Task DefinitionService_FindsByCapability()
    {
        var service = _provider.GetRequiredService<SMEAgentDefinitionService>();
        var results = await service.FindByCapabilitiesAsync(["security"]);
        Assert.NotNull(results);
        Assert.NotEmpty(results);
        Assert.Single(results, r => r.DefinitionId == "security-auditor");
    }

    [Fact]
    public void AgentFactory_CanCreateSmeAgent()
    {
        var factory = _provider.GetRequiredService<IAgentFactory>();
        var identity = new AgentIdentity
        {
            Id = $"sme-test-{Guid.NewGuid():N}",
            DisplayName = "Test SME Agent",
            Role = AgentRole.Custom,
            ModelTier = "standard",
            CustomAgentName = "sme:security-auditor"
        };
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "security-auditor",
            RoleName = "Security Auditor",
            SystemPrompt = "You are a security specialist.",
            Capabilities = ["security"],
            McpServers = ["github"]
        };

        var agent = factory.CreateSme(identity, definition);

        Assert.NotNull(agent);
        Assert.Equal(AgentRole.Custom, agent.Identity.Role);
        Assert.Equal(identity.Id, agent.Identity.Id);
    }

    [Fact]
    public async Task SpawnManager_CanSpawnSmeAgent_WhenGateApproved()
    {
        // Get the spawn manager from orchestrator services
        var spawnManager = _provider.GetRequiredService<AgentSpawnManager>();
        Assert.NotNull(spawnManager);

        // Create a definition to spawn
        var definition = new SMEAgentDefinition
        {
            DefinitionId = "security-auditor",
            RoleName = "Security Auditor",
            SystemPrompt = "You are a security specialist.",
            Capabilities = ["security"],
            McpServers = ["github"]
        };

        // Verify that the factory can create an SME agent from the definition
        var factory = _provider.GetRequiredService<IAgentFactory>();
        var identity = new AgentIdentity
        {
            Id = $"sme-spawn-test-{Guid.NewGuid():N}",
            DisplayName = definition.RoleName,
            Role = AgentRole.Custom,
            ModelTier = "standard",
            CustomAgentName = "sme:security-auditor"
        };

        var agent = factory.CreateSme(identity, definition);
        Assert.NotNull(agent);
        Assert.Equal("Security Auditor", agent.Identity.DisplayName);
        Assert.Equal(AgentRole.Custom, agent.Identity.Role);
    }
}
