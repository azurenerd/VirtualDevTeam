using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Dashboard.Components;
using VirtualDevTeam.Dashboard.Hubs;
using VirtualDevTeam.Dashboard.Services;
using VirtualDevTeam.Orchestrator;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration (reads from Runner's appsettings.json via shared path)
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VirtualDevTeam.Runner", "appsettings.json"),
    optional: true, reloadOnChange: true);
// Re-add user secrets so they override Runner's appsettings (e.g., GitHubToken)
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>();
// Re-add env vars and cmdline AFTER the Runner appsettings.json we just loaded,
// so they retain standard ASP.NET Core precedence (env > json, cmdline > env).
// Without this, callers setting VirtualDevTeam__* env vars (e.g. test fixtures,
// container deployments) would have their overrides silently lost. Note:
// `builder.WebHost.UseUrls(...)` below still hard-overrides ASPNETCORE_URLS;
// set VirtualDevTeam:Dashboard:StandalonePort to change the bind port.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Services.Configure<VirtualDevTeamConfig>(
    builder.Configuration.GetSection("VirtualDevTeam"));
builder.Services.Configure<LimitsConfig>(
    builder.Configuration.GetSection("VirtualDevTeam:Limits"));

var dashboardPort = builder.Configuration.GetValue("VirtualDevTeam:Dashboard:StandalonePort", 5051);
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Core services needed by DashboardDataService
builder.Services.AddInProcessMessageBus();
builder.Services.AddSingleton<VirtualDevTeam.Core.AI.ActiveLlmCallTracker>();
builder.Services.AddSingleton<VirtualDevTeam.Core.AI.AgentUsageTracker>(sp =>
    new VirtualDevTeam.Core.AI.AgentUsageTracker(sp.GetRequiredService<AgentStateStore>()));
builder.Services.AddSingleton<VirtualDevTeam.Core.Diagnostics.RequirementsCache>();
builder.Services.AddSingleton<VirtualDevTeam.Core.Diagnostics.AgentChatService>();
builder.Services.AddSemanticKernelModels();
builder.Services.AddGitHubIntegration();
builder.Services.AddDevPlatform();

// Persistence — use Runner's database so standalone dashboard sees live agent data
var repoSlug = builder.Configuration["VirtualDevTeam:Project:GitHubRepo"]?.Replace('/', '_') ?? "default";
var runnerDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VirtualDevTeam.Runner"));
var dbPath = Path.Combine(runnerDir, $"virtualdevteam_{repoSlug}.db");
if (!File.Exists(dbPath))
    dbPath = $"virtualdevteam_{repoSlug}.db"; // fallback to local
builder.Services.AddSingleton(new AgentStateStore(dbPath));
builder.Services.AddSingleton(new AgentMemoryStore(dbPath));
builder.Services.AddSingleton<VirtualDevTeam.Core.Metrics.BuildTestMetrics>();

// Services required by RunCoordinator (registered via AddOrchestrator)
builder.Services.AddSingleton<VirtualDevTeam.Core.Configuration.RunBranchProvider>(sp =>
{
    var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
    return new VirtualDevTeam.Core.Configuration.RunBranchProvider(config.Project.DefaultBranch);
});
builder.Services.AddSingleton<IRunBranchProvider>(sp =>
    sp.GetRequiredService<VirtualDevTeam.Core.Configuration.RunBranchProvider>());
builder.Services.AddSingleton<ProjectFileManager>(sp =>
{
    var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
    return new ProjectFileManager(
        sp.GetRequiredService<VirtualDevTeam.Core.DevPlatform.Capabilities.IRepositoryContentService>(),
        sp.GetRequiredService<ILogger<ProjectFileManager>>(),
        sp.GetRequiredService<IRunBranchProvider>(),
        config.Project.DefaultBranch);
});
builder.Services.AddSingleton<ConflictResolver>();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DevelopSettingsService>>();
    var config = sp.GetService<IOptions<VirtualDevTeamConfig>>();
    var filePath = Path.Combine(runnerDir, "develop-settings.json");
    return new DevelopSettingsService(logger, config, filePath);
});

// Orchestrator (AgentRegistry, HealthMonitor, DeadlockDetector, WorkflowStateMachine, AgentSpawnManager)
builder.Services.AddOrchestrator();

// CandidateStateStore — needed by ProjectTimeline page for strategy visualization
builder.Services.AddSingleton<VirtualDevTeam.Core.Strategies.CandidateStateStore>(sp =>
    new VirtualDevTeam.Core.Strategies.CandidateStateStore(sp.GetService<AgentStateStore>()));

// Stub IAgentFactory — standalone dashboard doesn't spawn agents
builder.Services.AddSingleton<IAgentFactory>(new NoOpAgentFactory());

// Gate service
builder.Services.AddSingleton<IGateCheckService, GateCheckService>();

// Prompt template service (needed by Configuration page)
builder.Services.AddSingleton<VirtualDevTeam.Core.Prompts.IPromptTemplateService, VirtualDevTeam.Core.Prompts.PromptTemplateService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("RunnerApi", client =>
{
    var runnerPort = builder.Configuration.GetValue("VirtualDevTeam:Dashboard:RunnerPort", 5050);
    client.BaseAddress = new Uri($"http://localhost:{runnerPort}");
});
// Standalone mode: use HttpDashboardDataService which polls Runner API
builder.Services.AddSingleton<HttpDashboardDataService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("RunnerApi");
    return new HttpDashboardDataService(http, sp.GetRequiredService<ILogger<HttpDashboardDataService>>());
});
builder.Services.AddSingleton<IDashboardDataService>(sp => sp.GetRequiredService<HttpDashboardDataService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HttpDashboardDataService>());
builder.Services.AddSingleton<IPlatformLinkService, PlatformLinkService>();
// Strategies page: in standalone mode, proxy to the Runner REST API at /api/strategies/*.
builder.Services.AddSingleton<IStrategiesDataService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("RunnerApi");
    return new HttpStrategiesDataService(http, sp.GetRequiredService<ILogger<HttpStrategiesDataService>>());
});
// Configuration: proxy to Runner API (save, cleanup, validate) with local file fallback
builder.Services.AddSingleton<IConfigurationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var localPath = Path.Combine(runnerDir, "appsettings.json");
    return new HttpConfigurationService(
        factory,
        "RunnerApi",
        sp.GetRequiredService<ILogger<HttpConfigurationService>>(),
        localAppSettingsPath: File.Exists(localPath) ? localPath : null);
});
builder.Services.AddSingleton<DirectorCliService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectorCliService>());
builder.Services.AddTestArtifactServices();
builder.Services.AddSingleton(new DashboardMode(IsStandalone: true));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<AgentHub>("/agenthub");
app.MapHub<VirtualDevTeam.Dashboard.Hubs.AgentLogHub>("/hubs/agentlog");

app.MapTestArtifactEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Stub agent factory for standalone dashboard — never actually spawns agents.</summary>
internal sealed class NoOpAgentFactory : IAgentFactory
{
    public IAgent Create(AgentRole role, AgentIdentity identity) =>
        throw new NotSupportedException("Standalone dashboard does not spawn agents");

    public IAgent CreateSme(AgentIdentity identity, SMEAgentDefinition definition) =>
        throw new NotSupportedException("Standalone dashboard does not spawn agents");
}
