using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using Microsoft.Extensions.Configuration;
using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;
using AgentSquad.Orchestrator;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration (reads from Runner's appsettings.json via shared path)
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentSquad.Runner", "appsettings.json"),
    optional: true, reloadOnChange: true);
// Re-add user secrets so they override Runner's appsettings (e.g., GitHubToken)
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>();
builder.Services.Configure<AgentSquadConfig>(
    builder.Configuration.GetSection("AgentSquad"));
builder.Services.Configure<LimitsConfig>(
    builder.Configuration.GetSection("AgentSquad:Limits"));

var dashboardPort = builder.Configuration.GetValue("AgentSquad:Dashboard:StandalonePort", 5051);
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Core services needed by DashboardDataService
builder.Services.AddInProcessMessageBus();
builder.Services.AddSingleton<AgentSquad.Core.AI.AgentUsageTracker>(sp =>
    new AgentSquad.Core.AI.AgentUsageTracker(sp.GetRequiredService<AgentStateStore>()));
builder.Services.AddSingleton<AgentSquad.Core.Diagnostics.RequirementsCache>();
builder.Services.AddSingleton<AgentSquad.Core.Diagnostics.AgentChatService>();
builder.Services.AddSemanticKernelModels();
builder.Services.AddGitHubIntegration();

// Persistence — use Runner's database so standalone dashboard sees live agent data
var repoSlug = builder.Configuration["AgentSquad:Project:GitHubRepo"]?.Replace('/', '_') ?? "default";
var runnerDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentSquad.Runner"));
var dbPath = Path.Combine(runnerDir, $"agentsquad_{repoSlug}.db");
if (!File.Exists(dbPath))
    dbPath = $"agentsquad_{repoSlug}.db"; // fallback to local
builder.Services.AddSingleton(new AgentStateStore(dbPath));
builder.Services.AddSingleton(new AgentMemoryStore(dbPath));

// Orchestrator (AgentRegistry, HealthMonitor, DeadlockDetector, WorkflowStateMachine, AgentSpawnManager)
builder.Services.AddOrchestrator();

// Stub IAgentFactory — standalone dashboard doesn't spawn agents
builder.Services.AddSingleton<IAgentFactory>(new NoOpAgentFactory());

// Gate service
builder.Services.AddSingleton<IGateCheckService, GateCheckService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DashboardDataService>();
builder.Services.AddSingleton<IDashboardDataService>(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());
builder.Services.AddSingleton<DirectorCliService>();
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
