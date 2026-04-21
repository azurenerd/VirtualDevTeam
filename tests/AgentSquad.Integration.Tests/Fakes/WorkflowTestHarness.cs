namespace AgentSquad.Integration.Tests.Fakes;

using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Lightweight test harness that wires up the core AgentSquad DI container
/// with <see cref="InMemoryGitHubService"/> and configurable fakes.
/// Provides workflow state machine, agent registry, and message bus
/// for integration tests that need orchestrator-level behavior.
/// </summary>
public sealed class WorkflowTestHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private bool _disposed;

    public InMemoryGitHubService GitHub { get; }
    public IMessageBus MessageBus => _provider.GetRequiredService<IMessageBus>();
    public WorkflowStateMachine Workflow => _provider.GetRequiredService<WorkflowStateMachine>();
    public AgentRegistry Registry => _provider.GetRequiredService<AgentRegistry>();
    public AgentSpawnManager SpawnManager => _provider.GetRequiredService<AgentSpawnManager>();
    public AgentStateStore StateStore => _provider.GetRequiredService<AgentStateStore>();
    public IServiceProvider Services => _provider;

    private WorkflowTestHarness(ServiceProvider provider, InMemoryGitHubService github)
    {
        _provider = provider;
        GitHub = github;
    }

    /// <summary>Build a harness with default configuration suitable for most tests.</summary>
    public static WorkflowTestHarness Create(Action<AgentSquadConfig>? configureOptions = null)
    {
        var services = new ServiceCollection();

        // Logging — NullLogger to keep tests quiet
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Configuration
        var config = DefaultConfig();
        configureOptions?.Invoke(config);
        services.AddSingleton(Options.Create(config));
        services.AddSingleton<IOptionsMonitor<AgentSquadConfig>>(
            new OptionsMonitorStub<AgentSquadConfig>(config));
        services.AddSingleton(Options.Create(config.Limits));
        services.AddSingleton(Options.Create(new StrategyFrameworkConfig()));

        // InMemoryGitHubService replaces real GitHub
        var github = new InMemoryGitHubService
        {
            RepositoryFullName = config.Project.GitHubRepo
        };
        services.AddSingleton<IGitHubService>(github);

        // Message bus (real in-process — it's lightweight and tests need real pub/sub)
        services.AddInProcessMessageBus();

        // Persistence — use temp file so tests don't collide
        var dbPath = Path.Combine(Path.GetTempPath(), $"harness_{Guid.NewGuid():N}.db");
        services.AddSingleton(new AgentStateStore(dbPath));
        services.AddSingleton(new AgentMemoryStore(dbPath));

        // ProjectFileManager
        services.AddSingleton<ProjectFileManager>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            return new ProjectFileManager(
                sp.GetRequiredService<IGitHubService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>(),
                cfg.Project.DefaultBranch);
        });

        // Orchestrator components
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<DeadlockDetector>();
        services.AddSingleton<AgentSpawnManager>();
        services.AddSingleton<IGateCheckService, GateCheckService>();
        services.AddSingleton<WorkflowStateMachine>();

        var provider = services.BuildServiceProvider();
        return new WorkflowTestHarness(provider, github);
    }

    /// <summary>
    /// Advance the workflow state machine by polling until
    /// the predicate is satisfied or timeout is reached.
    /// </summary>
    public async Task<bool> AdvanceUntilAsync(
        Func<WorkflowStateMachine, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (predicate(Workflow))
                return true;
            await Task.Delay(50, ct);
        }
        return predicate(Workflow);
    }

    /// <summary>
    /// Send a signal to the workflow state machine and try to advance.
    /// </summary>
    public void Signal(string signalName)
    {
        Workflow.Signal(signalName);
        Workflow.TryAdvancePhase(out _);
    }

    /// <summary>Register a fake agent so workflow gates that check AgentRegistry pass.</summary>
    public async Task RegisterFakeAgentAsync(AgentRole role, string? name = null)
    {
        var identity = new AgentIdentity
        {
            Id = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            Role = role,
            DisplayName = name ?? role.ToString(),
            ModelTier = "standard"
        };
        var agent = new StubAgent(identity, NullLogger<AgentBase>.Instance);
        await Registry.RegisterAsync(agent);
        await agent.InitializeAsync();
    }

    private static AgentSquadConfig DefaultConfig() => new()
    {
        Project = new ProjectConfig
        {
            GitHubRepo = "test-owner/test-repo",
            Description = "Test project for integration tests",
            DefaultBranch = "main"
        },
        Limits = new LimitsConfig
        {
            MaxConcurrentAgents = 3,
            GitHubPollIntervalSeconds = 1
        }
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
    }
}

/// <summary>Minimal IOptionsMonitor stub that returns a fixed value with no change tracking.</summary>
internal sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
{
    public OptionsMonitorStub(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>No-op agent for satisfying workflow gates in tests.</summary>
internal sealed class StubAgent : AgentBase
{
    public StubAgent(AgentIdentity identity, ILogger<AgentBase> logger) : base(identity, logger) { }
    protected override Task RunAgentLoopAsync(CancellationToken ct) => Task.CompletedTask;
}
