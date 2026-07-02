using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.Agents;
using VirtualDevTeam.Integration.Tests.Fakes;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// Full DI test harness for end-to-end workflow tests.
/// Wires up all services with InMemoryGitHubService and ScriptedChatCompletionService,
/// enabling full agent lifecycle testing without real GitHub or LLM calls.
/// </summary>
public sealed class E2ETestHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbPath;
    private bool _disposed;

    public InMemoryGitHubService GitHub { get; }
    public ScriptedChatCompletionService ChatService { get; }
    public AutoApproveGateCheckService GateService { get; }
    public E2ELogSink LogSink { get; }
    public IServiceProvider Services => _provider;

    // Core orchestrator components
    public IMessageBus MessageBus => _provider.GetRequiredService<IMessageBus>();
    public WorkflowStateMachine Workflow => _provider.GetRequiredService<WorkflowStateMachine>();
    public AgentRegistry Registry => _provider.GetRequiredService<AgentRegistry>();
    public AgentSpawnManager SpawnManager => _provider.GetRequiredService<AgentSpawnManager>();
    public AgentStateStore StateStore => _provider.GetRequiredService<AgentStateStore>();
    public IAgentFactory AgentFactory => _provider.GetRequiredService<IAgentFactory>();
    public RunCoordinator Coordinator => _provider.GetRequiredService<RunCoordinator>();
    public HealthMonitor HealthMonitor => _provider.GetRequiredService<HealthMonitor>();

    private E2ETestHarness(
        ServiceProvider provider,
        InMemoryGitHubService github,
        ScriptedChatCompletionService chatService,
        AutoApproveGateCheckService gateService,
        E2ELogSink logSink,
        string dbPath)
    {
        _provider = provider;
        GitHub = github;
        ChatService = chatService;
        GateService = gateService;
        LogSink = logSink;
        _dbPath = dbPath;
    }

    /// <summary>
    /// Build a fully wired E2E harness with InMemoryGitHubService and scripted LLM responses.
    /// </summary>
    public static E2ETestHarness Create(
        Action<VirtualDevTeamConfig>? configureOptions = null,
        ScriptedChatCompletionService? chatService = null)
    {
        var services = new ServiceCollection();

        // Logging — capture warnings/errors to in-memory list
        var logSink = new E2ELogSink();
        services.AddSingleton(logSink);
        services.AddSingleton<ILoggerFactory>(new E2ELoggerFactory(logSink, LogLevel.Information));
        services.AddSingleton(typeof(ILogger<>), typeof(E2ELogger<>));

        // Configuration
        var config = DefaultConfig();
        configureOptions?.Invoke(config);
        services.AddSingleton(Options.Create(config));
        services.AddSingleton<IOptions<VirtualDevTeamConfig>>(Options.Create(config));
        services.AddSingleton<IOptionsMonitor<VirtualDevTeamConfig>>(new E2EOptionsMonitor<VirtualDevTeamConfig>(config));
        services.AddSingleton(Options.Create(config.Limits));
        services.AddSingleton(Options.Create(new StrategyFrameworkConfig()));

        // DevPlatform config
        services.Configure<DevPlatformConfig>(cfg =>
        {
            cfg.Platform = DevPlatformType.GitHub;
            cfg.AuthMethod = DevPlatformAuthMethod.Pat;
        });

        // InMemoryGitHubService replaces real GitHub
        var github = new InMemoryGitHubService
        {
            RepositoryFullName = config.Project.GitHubRepo
        };
        services.AddSingleton<IGitHubService>(github);

        // DevPlatform adapters — wraps IGitHubService for all capability interfaces
        services.AddDevPlatform();

        // Message bus (real in-process)
        services.AddInProcessMessageBus();

        // Persistence — temp DB
        var dbPath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.db");
        services.AddSingleton(new AgentStateStore(dbPath));
        services.AddSingleton(new AgentMemoryStore(dbPath));

        // Project file manager
        services.AddSingleton<ProjectFileManager>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            return new ProjectFileManager(
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>(),
                branch: cfg.Project.DefaultBranch);
        });

        // AI services
        chatService ??= HelloWorldScripts.CreateForAllAgents();
        services.AddSingleton<AgentUsageTracker>();
        services.AddSingleton<ActiveLlmCallTracker>();
        services.AddSingleton<StrategyConcurrencyGate>();

        // CopilotCliProcessManager (needed by ModelRegistry, won't actually be used)
        services.AddSingleton<CopilotCliProcessManager>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>();
            var fwCfg = sp.GetRequiredService<IOptions<StrategyFrameworkConfig>>();
            var gate = sp.GetRequiredService<StrategyConcurrencyGate>();
            var logger = sp.GetRequiredService<ILogger<CopilotCliProcessManager>>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<VirtualDevTeamConfig>>();
            return new CopilotCliProcessManager(cfg, fwCfg, gate, logger, monitor);
        });

        // ModelRegistry — uses ScriptedModelRegistry to inject our mock LLM into all kernels
        services.AddSingleton<ModelRegistry>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var usageTracker = sp.GetRequiredService<AgentUsageTracker>();
            var llmCallTracker = sp.GetRequiredService<ActiveLlmCallTracker>();
            var processManager = sp.GetRequiredService<CopilotCliProcessManager>();
            var monitor = sp.GetRequiredService<IOptionsMonitor<VirtualDevTeamConfig>>();
            return new ScriptedModelRegistry(chatService, cfg, loggerFactory, usageTracker, llmCallTracker, processManager, monitor);
        });

        services.AddSingleton<IChatCompletionRunner>(new ScriptedChatCompletionRunner(chatService));
        services.AddSingleton<VirtualDevTeam.Core.Services.McpServerRegistry>();

        // Gate check — auto-approve everything
        var gateService = new AutoApproveGateCheckService();
        services.AddSingleton<IGateCheckService>(gateService);

        // GitHub workflows
        services.AddSingleton<PullRequestWorkflow>();
        services.AddSingleton<IssueWorkflow>();
        services.AddSingleton<ConflictResolver>();

        // Run branch provider
        services.AddSingleton(new RunBranchProvider(config.Project.DefaultBranch));
        services.AddSingleton<IRunBranchProvider>(sp => sp.GetRequiredService<RunBranchProvider>());

        // DevelopSettingsService — use a temp path that doesn't exist
        // to avoid loading the real develop-settings.json which overwrites test config
        services.AddSingleton<DevelopSettingsService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DevelopSettingsService>>();
            var cfg = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"e2e-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var tempSettingsPath = Path.Combine(tempDir, "develop-settings.json");
            return new DevelopSettingsService(logger, cfg, tempSettingsPath);
        });

        // Candidate state store
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<AgentStateStore>();
            return new CandidateStateStore(store);
        });

        // Reasoning services
        services.AddSingleton<IAgentReasoningLog, AgentReasoningLog>();
        services.AddSingleton<SelfAssessmentService>();
        services.AddSingleton<IAgentTaskTracker, AgentTaskTracker>();
        services.AddSingleton<IPromptTemplateService, PromptTemplateService>();

        // Agent dependency bundles
        services.AddSingleton(sp => new AgentCoreServices(
            messageBus: sp.GetRequiredService<IMessageBus>(),
            modelRegistry: sp.GetRequiredService<ModelRegistry>(),
            chatRunner: sp.GetRequiredService<IChatCompletionRunner>(),
            projectFiles: sp.GetRequiredService<ProjectFileManager>(),
            memoryStore: sp.GetRequiredService<AgentMemoryStore>(),
            gateCheck: sp.GetRequiredService<IGateCheckService>(),
            config: sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>(),
            promptService: sp.GetService<IPromptTemplateService>(),
            roleContextProvider: sp.GetService<RoleContextProvider>(),
            selfAssessment: sp.GetService<SelfAssessmentService>(),
            reasoningLog: sp.GetService<IAgentReasoningLog>(),
            taskTracker: sp.GetService<IAgentTaskTracker>(),
            stateStore: sp.GetService<AgentStateStore>()));

        services.AddSingleton(sp => new AgentPlatformServices(
            prService: sp.GetRequiredService<IPullRequestService>(),
            workItemService: sp.GetRequiredService<IWorkItemService>(),
            repoContent: sp.GetRequiredService<IRepositoryContentService>(),
            reviewService: sp.GetRequiredService<IReviewService>(),
            prWorkflow: sp.GetRequiredService<PullRequestWorkflow>(),
            branchService: sp.GetService<IBranchService>(),
            issueWorkflow: sp.GetService<IssueWorkflow>(),
            branchProvider: sp.GetService<IRunBranchProvider>(),
            docResolver: sp.GetService<IDocumentReferenceResolver>(),
            platformHost: sp.GetService<IPlatformHostContext>()));

        services.AddSingleton(sp => new AgentWorkspaceServices(
            buildRunner: sp.GetService<BuildRunner>(),
            testRunner: sp.GetService<TestRunner>(),
            playwrightRunner: sp.GetService<PlaywrightRunner>(),
            metrics: sp.GetService<VirtualDevTeam.Core.Metrics.BuildTestMetrics>()));

        // Orchestrator
        services.AddOrchestrator();

        // Agent factory
        services.AddSingleton<IAgentFactory, AgentFactory>();

        var provider = services.BuildServiceProvider();
        return new E2ETestHarness(provider, github, chatService, gateService, logSink, dbPath);
    }

    /// <summary>
    /// Wait until the workflow reaches a specific phase.
    /// </summary>
    public async Task<bool> WaitForPhaseAsync(
        ProjectPhase targetPhase,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        return await Helpers.PhaseWaiter.WaitForPhaseAsync(
            Workflow, targetPhase, timeout, ct);
    }

    /// <summary>
    /// Send a signal and try to advance the workflow.
    /// </summary>
    public void Signal(string signalName)
    {
        Workflow.Signal(signalName);
        Workflow.TryAdvancePhase(out _);
    }

    /// <summary>
    /// Start the HealthMonitor timer (required for auto-signal detection and phase advancement).
    /// </summary>
    public async Task StartHealthMonitorAsync(CancellationToken ct = default)
    {
        await HealthMonitor.StartAsync(ct);
    }

    /// <summary>
    /// Full run startup: start project, spawn agents, start health monitor.
    /// This mirrors the production VirtualDevTeamWorker bootstrap sequence.
    /// </summary>
    public async Task<ActiveRun> StartFullRunAsync(CancellationToken ct = default)
    {
        var run = await Coordinator.StartProjectAsync(ct);
        await Coordinator.SpawnAgentsForRunAsync(ct);
        await StartHealthMonitorAsync(ct);

        // Start artifact-based signal detection alongside HealthMonitor.
        // In tests, agents complete so fast that HealthMonitor's status-string polling
        // misses transient states. This helper checks durable artifacts (merged PRs, issues)
        // which persist regardless of agent lifecycle timing.
        _ = Task.Run(async () => await RunArtifactSignalLoopAsync(ct), ct);

        return run;
    }

    /// <summary>
    /// Polls GitHub artifacts (merged PRs, open issues) and fires workflow signals
    /// that HealthMonitor may have missed due to agents exiting their loops too quickly.
    /// </summary>
    private async Task RunArtifactSignalLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                var prs = await GitHub.GetAllPullRequestsAsync(ct);
                var issues = await GitHub.GetAllIssuesAsync(ct);

                // Research: if a Research PR is merged, research is complete
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.ResearchComplete))
                {
                    if (prs.Any(p => p.IsMerged && p.Title.Contains("Research", StringComparison.OrdinalIgnoreCase)))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.ResearchDocReady);
                        Workflow.Signal(WorkflowStateMachine.Signals.ResearchComplete);
                    }
                }

                // Architecture: if Architecture PR is merged
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.ArchitectureComplete))
                {
                    if (prs.Any(p => p.IsMerged && p.Title.Contains("Architecture", StringComparison.OrdinalIgnoreCase)))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.ArchitectureDocReady);
                        Workflow.Signal(WorkflowStateMachine.Signals.ArchitectureComplete);
                    }
                }

                // Engineering plan: if Enhancement issues exist (PM created them after plan)
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.EngineeringPlanReady))
                {
                    if (issues.Any(i => i.Title.Contains("Enhancement", StringComparison.OrdinalIgnoreCase)))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.EngineeringPlanReady);
                        Workflow.Signal(WorkflowStateMachine.Signals.SoftwareEngineerReady);
                    }
                }

                // Auto-merge: PRs with approval labels that agents added
                // (TestWorkflow="none" has no agent-side merge path once Architect removes ready-for-review)
                // Check for architect-approved label (always reliable — Architect does full label replace)
                // AND verify approval comments exist from both reviewers via GetPullRequestCommentsAsync
                foreach (var pr in prs.Where(p =>
                    string.Equals(p.State, "open", StringComparison.OrdinalIgnoreCase)
                    && !p.IsMerged
                    && p.Labels.Contains("architect-approved", StringComparer.OrdinalIgnoreCase)))
                {
                    try
                    {
                        // Verify both reviewers posted approval comments
                        var comments = await GitHub.GetPullRequestCommentsAsync(pr.Number, ct);
                        var hasArchitectApproval = comments.Any(c =>
                            c.Body.Contains("[Architect] APPROVED", StringComparison.OrdinalIgnoreCase));
                        var hasPmApproval = comments.Any(c =>
                            c.Body.Contains("[ProgramManager] APPROVED", StringComparison.OrdinalIgnoreCase));

                        if (!hasArchitectApproval || !hasPmApproval)
                            continue;

                        await GitHub.MergePullRequestAsync(pr.Number, "Auto-merged after dual approval", ct);
                        // Close linked issues
                        var body = (await GitHub.GetPullRequestAsync(pr.Number, ct))?.Body ?? "";
                        var linkedMatch = System.Text.RegularExpressions.Regex.Match(body, @"Closes #(\d+)");
                        if (linkedMatch.Success && int.TryParse(linkedMatch.Groups[1].Value, out var issueNum))
                            await GitHub.CloseIssueAsync(issueNum, ct);
                    }
                    catch { /* Ignore merge errors */ }
                }

                // All engineering complete: all SE implementation PRs must be merged
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.AllEngineeringComplete))
                {
                    var implPrs = prs.Where(p =>
                        p.Title.Contains("Implement", StringComparison.OrdinalIgnoreCase) ||
                        p.Title.Contains("Foundation", StringComparison.OrdinalIgnoreCase) ||
                        p.Title.Contains("Scaffolding", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (implPrs.Count > 0 && implPrs.All(p => p.IsMerged))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.AllEngineeringComplete);
                    }
                }

                // Testing complete: if all implementation PRs are merged
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.TestCoverageMet))
                {
                    var allEngComplete = Workflow.HasSignal(WorkflowStateMachine.Signals.AllEngineeringComplete);
                    var implPrs = prs.Where(p =>
                        p.Title.Contains("Implement", StringComparison.OrdinalIgnoreCase) ||
                        p.Title.Contains("Foundation", StringComparison.OrdinalIgnoreCase) ||
                        p.Title.Contains("Scaffolding", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (allEngComplete && implPrs.Count > 0 && implPrs.All(p => p.IsMerged))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.TestCoverageMet);
                    }
                }

                // Reviews complete: if all enhancement issues are closed
                if (!Workflow.HasSignal(WorkflowStateMachine.Signals.AllReviewsApproved))
                {
                    var enhancements = issues.Where(i =>
                        i.Title.Contains("Enhancement", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (enhancements.Count > 0 && enhancements.All(i => i.State == "closed"))
                    {
                        Workflow.Signal(WorkflowStateMachine.Signals.AllReviewsApproved);
                    }
                }

                // After firing any new signals, attempt phase advancement immediately
                Workflow.TryAdvancePhase(out _);
            }
            catch (OperationCanceledException) { break; }
            catch { /* Swallow errors in signal helper */ }
        }
    }

    /// <summary>
    /// Register a fake stub agent so workflow gates pass.
    /// </summary>
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

    private static VirtualDevTeamConfig DefaultConfig() => new()
    {
        Project = new ProjectConfig
        {
            Name = "Hello World Web App",
            Description = "A simple Hello World ASP.NET Core web application for E2E testing.",
            GitHubRepo = "test-owner/hello-world",
            GitHubToken = "fake-token-for-e2e",
            DefaultBranch = "main",
            QuickDocumentCreation = true
        },
        Limits = new LimitsConfig
        {
            MaxAdditionalEngineers = 0,
            AgentTimeoutMinutes = 5,
            GitHubPollIntervalSeconds = 1,
            MaxConcurrentAgents = 5
        },
        Models = new Dictionary<string, ModelConfig>
        {
            ["premium"] = new() { Provider = "openai", Model = "gpt-4", ApiKey = "test-key" },
            ["standard"] = new() { Provider = "openai", Model = "gpt-3.5-turbo", ApiKey = "test-key" },
            ["budget"] = new() { Provider = "openai", Model = "gpt-4o-mini", ApiKey = "test-key" },
            ["local"] = new() { Provider = "ollama", Model = "codellama", Endpoint = "http://localhost:11434" }
        },
        CopilotCli = new CopilotCliConfig { Enabled = false },
        Workspace = new WorkspaceConfig
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"e2e-workspace-{Guid.NewGuid():N}"),
            BuildCommand = "dotnet build",
            TestCommand = "dotnet test",
            TestWorkflow = "none"  // Skip inline test workflow so SE can merge PRs directly
        }
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { HealthMonitor.StopAsync(default).GetAwaiter().GetResult(); } catch { }
        try { HealthMonitor.Dispose(); } catch { }
        _provider.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}

/// <summary>No-op agent for satisfying workflow gates in E2E tests.</summary>
internal sealed class StubAgent : AgentBase
{
    public StubAgent(AgentIdentity identity, ILogger<AgentBase> logger) : base(identity, logger) { }
    protected override Task RunAgentLoopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Minimal IOptionsMonitor stub for tests.</summary>
internal sealed class E2EOptionsMonitor<T> : IOptionsMonitor<T>
{
    public E2EOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Thread-safe log sink that captures Warning+ log entries for test diagnostics.</summary>
public sealed class E2ELogSink
{
    private readonly List<string> _entries = new();
    private readonly object _lock = new();

    public void Add(LogLevel level, string category, string message, Exception? ex)
    {
        lock (_lock)
        {
            var entry = $"[{level}] {category}: {message}";
            if (ex != null) entry += $"\n  Exception: {ex.GetType().Name}: {ex.Message}";
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<string> Entries { get { lock (_lock) return _entries.ToList(); } }
    public IReadOnlyList<string> Errors => Entries.Where(e => e.StartsWith("[Error]") || e.StartsWith("[Critical]")).ToList();
    public IReadOnlyList<string> Warnings => Entries.Where(e => e.StartsWith("[Warning]")).ToList();
}

internal sealed class E2ELoggerFactory : ILoggerFactory
{
    private readonly E2ELogSink _sink;
    private readonly LogLevel _minLevel;
    public E2ELoggerFactory(E2ELogSink sink, LogLevel minLevel = LogLevel.Warning) { _sink = sink; _minLevel = minLevel; }
    public ILogger CreateLogger(string categoryName) => new E2ESinkLogger(categoryName, _sink, _minLevel);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

internal sealed class E2ELogger<T> : ILogger<T>
{
    private readonly E2ESinkLogger _inner;
    public E2ELogger(E2ELogSink sink) => _inner = new E2ESinkLogger(typeof(T).Name, sink, LogLevel.Information);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

internal sealed class E2ESinkLogger : ILogger
{
    private readonly string _category;
    private readonly E2ELogSink _sink;
    private readonly LogLevel _minLevel;
    public E2ESinkLogger(string category, E2ELogSink sink, LogLevel minLevel = LogLevel.Warning) { _category = category; _sink = sink; _minLevel = minLevel; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= _minLevel)
            _sink.Add(logLevel, _category, formatter(state, exception), exception);
    }
}

/// <summary>
/// ModelRegistry subclass that injects ScriptedChatCompletionService into all Semantic Kernels.
/// This ensures that when agents call kernel.GetRequiredService&lt;IChatCompletionService&gt;(),
/// they get our scripted mock instead of a real OpenAI/Anthropic service.
/// </summary>
internal sealed class ScriptedModelRegistry : ModelRegistry
{
    private readonly IChatCompletionService _scriptedService;
    private readonly Dictionary<string, Kernel> _scriptedKernelCache = new();
    private readonly object _cacheLock = new();

    public ScriptedModelRegistry(
        IChatCompletionService scriptedService,
        VirtualDevTeamConfig config,
        ILoggerFactory loggerFactory,
        AgentUsageTracker usageTracker,
        ActiveLlmCallTracker llmCallTracker,
        CopilotCliProcessManager? processManager = null,
        IOptionsMonitor<VirtualDevTeamConfig>? configMonitor = null)
        : base(config, loggerFactory, usageTracker, llmCallTracker, processManager, configMonitor)
    {
        _scriptedService = scriptedService;
    }

    public override Kernel GetKernel(string modelTier, string? agentId)
    {
        var cacheKey = agentId is not null ? $"{modelTier}:{agentId}" : modelTier;
        lock (_cacheLock)
        {
            if (_scriptedKernelCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var builder = Kernel.CreateBuilder();
            builder.Services.AddSingleton(_scriptedService);
            var kernel = builder.Build();
            _scriptedKernelCache[cacheKey] = kernel;
            return kernel;
        }
    }
}
