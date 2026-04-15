using AgentSquad.Core.Configuration;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Notifications;
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

// Always resolve RCL static web assets (Dashboard CSS/JS) — needed for _content/ paths
builder.WebHost.UseStaticWebAssets();

// Core services
builder.Services.AddInProcessMessageBus();
builder.Services.AddSingleton<AgentSquad.Core.AI.AgentUsageTracker>(sp =>
    new AgentSquad.Core.AI.AgentUsageTracker(sp.GetRequiredService<AgentStateStore>()));
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

// Human interaction gate service + notification system
builder.Services.AddSingleton<AgentSquad.Core.Notifications.GateNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentSquad.Core.Notifications.GateNotificationService>());
builder.Services.AddSingleton<INotificationChannel, AgentSquad.Core.Notifications.EmailNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel, AgentSquad.Core.Notifications.TeamsNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel, AgentSquad.Core.Notifications.SlackNotificationChannel>();
builder.Services.AddSingleton<IGateCheckService, GateCheckService>();

// Role context customization: per-agent role descriptions, MCP servers, knowledge links
builder.Services.AddSingleton<AgentSquad.Core.AI.RoleContextProvider>();

// SME Agent infrastructure: MCP registry, definition service, CLI MCP config management
builder.Services.AddSingleton<AgentSquad.Core.Services.McpServerRegistry>();
builder.Services.AddSingleton<AgentSquad.Core.Services.McpServerAvailabilityChecker>();
builder.Services.AddSingleton<AgentSquad.Core.Services.McpServerSecurityPolicy>();
builder.Services.AddSingleton<AgentSquad.Core.Services.SMEAgentDefinitionService>();
builder.Services.AddSingleton<AgentSquad.Core.Services.AgentTeamComposer>();
builder.Services.AddSingleton<AgentSquad.Core.Services.SmeDefinitionGenerator>();
builder.Services.AddSingleton<AgentSquad.Core.Services.SmeMetrics>();
builder.Services.AddSingleton<AgentSquad.Core.AI.CopilotCliMcpConfigManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentSquad.Core.AI.CopilotCliMcpConfigManager>());

// Prompt template system
builder.Services.AddSingleton<AgentSquad.Core.Prompts.IPromptTemplateService, AgentSquad.Core.Prompts.PromptTemplateService>();

// Agentic loop: self-assessment and reasoning observability
builder.Services.AddSingleton<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog, AgentSquad.Core.Agents.Reasoning.AgentReasoningLog>();
builder.Services.AddSingleton<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>();

// Orchestrator (registry, health monitor, deadlock detector, spawn manager, workflow)
builder.Services.AddOrchestrator();

// Agent factory
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();

// Dashboard: Blazor Server + SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DashboardDataService>();
builder.Services.AddSingleton<IDashboardDataService>(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());
builder.Services.AddSingleton<DirectorCliService>();
builder.Services.AddSingleton(new DashboardMode(IsStandalone: false));

// Worker service that starts the core agents and kicks off the workflow
builder.Services.AddHostedService<AgentSquadWorker>();

// Enable CORS for standalone dashboard
builder.Services.AddCors(o => o.AddPolicy("DashboardApi", p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Configure HTTP pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseCors("DashboardApi");
app.UseStaticFiles();
app.UseAntiforgery();

// ── Dashboard REST API (consumed by standalone Dashboard.Host) ──
var api = app.MapGroup("/api/dashboard").WithTags("Dashboard");

api.MapGet("/agents", (DashboardDataService svc) =>
    Results.Ok(svc.GetAllAgentSnapshots()));

api.MapGet("/agents/{agentId}", (string agentId, DashboardDataService svc) =>
    svc.GetAgentSnapshot(agentId) is { } snap ? Results.Ok(snap) : Results.NotFound());

api.MapGet("/agents/{agentId}/errors", (string agentId, DashboardDataService svc) =>
    Results.Ok(svc.GetAgentErrors(agentId)));

api.MapPost("/agents/{agentId}/errors/clear", (string agentId, DashboardDataService svc) =>
    { svc.ClearAgentErrors(agentId); return Results.Ok(); });

api.MapGet("/agents/{agentId}/activity", async (string agentId, DashboardDataService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetActivityLogAsync(agentId, 100, ct)));

api.MapPost("/agents/{agentId}/model", async (string agentId, HttpContext ctx, DashboardDataService svc) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SetModelRequest>();
    if (body?.ModelName is null) return Results.BadRequest();
    svc.SetAgentModel(agentId, body.ModelName);
    return Results.Ok();
});

api.MapPost("/agents/{agentId}/chat", async (string agentId, HttpContext ctx, DashboardDataService svc, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ChatRequest>();
    if (body?.Message is null) return Results.BadRequest();
    var reply = await svc.SendAgentChatAsync(agentId, body.Message, ct);
    return Results.Ok(reply);
});

api.MapGet("/agents/{agentId}/chat-history", (string agentId, DashboardDataService svc) =>
    Results.Ok(svc.GetAgentChatHistory(agentId)));

api.MapPost("/agents/{agentId}/chat/clear", (string agentId, DashboardDataService svc) =>
    { svc.ClearAgentChat(agentId); return Results.Ok(); });

api.MapGet("/health/snapshot", (DashboardDataService svc) =>
    Results.Ok(svc.GetCurrentHealthSnapshot()));

api.MapGet("/health/assessment", (DashboardDataService svc) =>
    Results.Ok(svc.GetExecutionHealthAssessment()));

api.MapGet("/health/deadlock", (DashboardDataService svc) =>
{
    var hasDeadlock = svc.HasDeadlock(out var cycle);
    return Results.Ok(new { HasDeadlock = hasDeadlock, Cycle = cycle });
});

api.MapGet("/health/diagnostics", (string? agentId, bool? compliant, int? limit, DashboardDataService svc) =>
    Results.Ok(svc.GetDiagnosticHistory(agentId, compliant, limit ?? 200)));

api.MapGet("/models", (DashboardDataService svc) =>
    Results.Ok(svc.GetAvailableModels()));

api.MapPost("/models/refresh", (DashboardDataService svc) =>
    { svc.RefreshActiveModels(); return Results.Ok(); });

api.MapGet("/timeline", (DashboardDataService svc) =>
    Results.Ok(svc.GetExecutionTimeline()));

api.MapGet("/github/issues", async (DashboardDataService svc) =>
    Results.Ok(await svc.GetIssuesAsync()));

api.MapGet("/github/pull-requests", async (DashboardDataService svc) =>
    Results.Ok(await svc.GetPullRequestsAsync()));

api.MapGet("/github/rate-limited", (DashboardDataService svc) =>
    Results.Ok(new { IsRateLimited = svc.IsGitHubRateLimited }));

api.MapGet("/github/rate-limit-info", (DashboardDataService svc) =>
    Results.Ok(svc.GetRateLimitInfo()));

api.MapPost("/reset", (DashboardDataService svc) =>
    { svc.ResetCaches(); return Results.Ok(); });

api.MapGet("/cost-summary", (DashboardDataService svc) =>
    Results.Ok(new { TotalCost = svc.GetTotalEstimatedCost(), TotalCalls = svc.GetTotalAiCalls() }));

api.MapGet("/repo-info", (IGitHubService github) =>
    Results.Ok(new { FullName = github.RepositoryFullName }));

// ── Configuration REST API (consumed by standalone Dashboard.Host) ──
var configApi = app.MapGroup("/api/configuration").WithTags("Configuration");

configApi.MapGet("/current", (ConfigurationService svc) =>
    Results.Ok(svc.GetCurrentConfig()));

configApi.MapPost("/save", async (AgentSquadConfig config, ConfigurationService svc) =>
{
    await svc.SaveConfigAsync(config);
    return Results.Ok();
});

configApi.MapPost("/validate-pat", async (HttpContext ctx, ConfigurationService svc, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ValidatePatRequest>(ct);
    if (body?.Token is null || body.RepoFullName is null) return Results.BadRequest();
    var result = await svc.ValidatePatAsync(body.Token, body.RepoFullName, ct);
    return Results.Ok(result);
});

configApi.MapGet("/cleanup/scan", async (ConfigurationService svc, CancellationToken ct) =>
    Results.Ok(await svc.ScanRepoForCleanupAsync(ct)));

configApi.MapPost("/cleanup/execute", async (HttpContext ctx, ConfigurationService svc, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CleanupExecuteRequest>(ct);
    var result = await svc.ExecuteCleanupAsync(body?.Caveats, ct);
    return Results.Ok(result);
});

// ── Reasoning Log REST API (consumed by standalone Dashboard.Host) ──
var reasoningApi = app.MapGroup("/api/reasoning").WithTags("Reasoning");

reasoningApi.MapGet("/agents", (AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetAgentIds()));

reasoningApi.MapGet("/events/{agentId}", (string agentId, AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetEvents(agentId)));

reasoningApi.MapGet("/events/{agentId}/since", (string agentId, DateTime since, AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetEventsSince(agentId, since)));

reasoningApi.MapGet("/recent", (AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog log, int? count) =>
    Results.Ok(log.GetRecentEvents(count ?? 50)));

// Gate approval API — for workflow-level gates that have no associated PR
var gateApi = app.MapGroup("/api/gates").WithTags("Gates");

gateApi.MapGet("/pending", (IGateCheckService gateCheck) =>
{
    var svc = gateCheck as GateCheckService;
    if (svc is null) return Results.Ok(Array.Empty<object>());
    return Results.Ok(svc.GetPendingGates());
});

gateApi.MapGet("/approved", (IGateCheckService gateCheck) =>
{
    var svc = gateCheck as GateCheckService;
    if (svc is null) return Results.Ok(new Dictionary<string, DateTime>());
    return Results.Ok(svc.GetApprovedGates());
});

gateApi.MapPost("/{gateId}/approve", (string gateId, IGateCheckService gateCheck) =>
{
    gateCheck.ApproveGate(gateId);
    return Results.Ok(new { gateId, approved = true, message = $"Gate '{gateId}' approved" });
});

// Notification API — for standalone dashboard to poll gate notifications
var notificationApi = app.MapGroup("/api/notifications").WithTags("Notifications");

notificationApi.MapGet("/", (GateNotificationService notificationSvc, string? filter) =>
{
    var f = filter?.ToLowerInvariant() switch
    {
        "open" => NotificationFilter.Open,
        "resolved" => NotificationFilter.Resolved,
        _ => NotificationFilter.All,
    };
    return Results.Ok(notificationSvc.GetByStatus(f));
});

notificationApi.MapGet("/counts", (GateNotificationService notificationSvc) =>
    Results.Ok(new
    {
        unread = notificationSvc.UnreadCount,
        open = notificationSvc.OpenCount,
        resolved = notificationSvc.ResolvedCount
    }));

notificationApi.MapPost("/{notificationId}/read", (string notificationId, GateNotificationService notificationSvc) =>
{
    notificationSvc.MarkAsRead(notificationId);
    return Results.Ok();
});

notificationApi.MapPost("/read-all", (GateNotificationService notificationSvc) =>
{
    notificationSvc.MarkAllAsRead();
    return Results.Ok();
});

// SignalR hub for real-time dashboard updates
app.MapHub<AgentHub>("/agenthub");

// Blazor Server components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Request DTOs for POST endpoints
record SetModelRequest(string ModelName);
record ChatRequest(string Message);
record ValidatePatRequest(string? Token, string? RepoFullName);
record CleanupExecuteRequest(string? Caveats);
