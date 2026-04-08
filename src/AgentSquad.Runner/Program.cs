using AgentSquad.Core.Configuration;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
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
builder.Services.AddSingleton<PullRequestWorkflow>(sp =>
{
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSquadConfig>>().Value;
    return new PullRequestWorkflow(
        sp.GetRequiredService<IGitHubService>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PullRequestWorkflow>>(),
        config.Project.DefaultBranch);
});
builder.Services.AddSingleton<IssueWorkflow>();
builder.Services.AddSingleton<ConflictResolver>();

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
