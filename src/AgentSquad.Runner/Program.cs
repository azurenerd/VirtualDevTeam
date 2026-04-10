using AgentSquad.Core.Configuration;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
using AgentSquad.Orchestrator;
using AgentSquad.Agents;
using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;
using AgentSquad.Runner;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<AgentSquadConfig>(
    builder.Configuration.GetSection("AgentSquad"));
builder.Services.Configure<LimitsConfig>(
    builder.Configuration.GetSection("AgentSquad:Limits"));

// Configure Kestrel to use the dashboard port from config
var dashboardPort = builder.Configuration.GetValue("AgentSquad:Dashboard:Port", 5050);
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Ensure RCL static web assets (Dashboard CSS/JS) are served in all environments
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// Core services
builder.Services.AddInProcessMessageBus();
builder.Services.AddSingleton<AgentSquad.Core.AI.AgentUsageTracker>();
builder.Services.AddSingleton<AgentSquad.Core.Diagnostics.RequirementsCache>();
builder.Services.AddSingleton<AgentSquad.Core.Diagnostics.AgentChatService>();
builder.Services.AddSemanticKernelModels();
builder.Services.AddGitHubIntegration();

// Persistence — database scoped per repo to prevent cross-project contamination
var repoSlug = builder.Configuration["AgentSquad:Project:GitHubRepo"]?.Replace('/', '_') ?? "default";
var dbPath = $"agentsquad_{repoSlug}.db";
builder.Services.AddSingleton(new AgentStateStore(dbPath));
builder.Services.AddSingleton(new AgentMemoryStore(dbPath));
builder.Services.AddSingleton<ProjectFileManager>(sp =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSquadConfig>>().Value;
    return new ProjectFileManager(
        sp.GetRequiredService<IGitHubService>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProjectFileManager>>(),
        config.Project.DefaultBranch);
});

// GitHub workflows
builder.Services.AddSingleton<ConflictDetector>();
builder.Services.AddSingleton<PullRequestWorkflow>(sp =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSquadConfig>>().Value;
    return new PullRequestWorkflow(
        sp.GetRequiredService<IGitHubService>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PullRequestWorkflow>>(),
        config.Project.DefaultBranch,
        sp.GetRequiredService<ConflictDetector>());
});
builder.Services.AddSingleton<IssueWorkflow>();
builder.Services.AddSingleton<ConflictResolver>();

// Workspace services (local build + test verification)
builder.Services.AddSingleton<BuildRunner>();
builder.Services.AddSingleton<TestRunner>();
builder.Services.AddSingleton<PlaywrightRunner>();
builder.Services.AddSingleton<TestStrategyAnalyzer>();
builder.Services.AddSingleton<AgentSquad.Core.Metrics.BuildTestMetrics>();

// Orchestrator (registry, health monitor, deadlock detector, spawn manager, workflow)
builder.Services.AddOrchestrator();

// Agent factory
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();

// Dashboard: Blazor Server + SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DashboardDataService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddScoped<EngineeringPlanDataService>();

// Worker service that starts the core agents and kicks off the workflow
builder.Services.AddHostedService<AgentSquadWorker>();

var app = builder.Build();

// Configure HTTP pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

// SignalR hub for real-time dashboard updates
app.MapHub<AgentHub>("/agenthub");

// Blazor Server components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
