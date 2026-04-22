using AgentSquad.Core.Configuration;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Notifications;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Strategies;
using AgentSquad.Core.Workspace;
using AgentSquad.Orchestrator;
using AgentSquad.Agents;
using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;
using AgentSquad.Runner;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Always load user-secrets so PAT is never stored in tracked appsettings.json
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Bind configuration
builder.Services.Configure<AgentSquadConfig>(
    builder.Configuration.GetSection("AgentSquad"));
builder.Services.Configure<LimitsConfig>(
    builder.Configuration.GetSection("AgentSquad:Limits"));
builder.Services.Configure<StrategyFrameworkConfig>(
    builder.Configuration.GetSection("AgentSquad:StrategyFramework"));

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
builder.Services.AddHostedService<PlaywrightHealthService>();
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
builder.Services.AddHostedService<AgentSquad.Core.Prompts.PromptFileWatcher>();

// Agentic loop: self-assessment and reasoning observability
builder.Services.AddSingleton<AgentSquad.Core.Agents.Reasoning.IAgentReasoningLog, AgentSquad.Core.Agents.Reasoning.AgentReasoningLog>();
builder.Services.AddSingleton<AgentSquad.Core.Agents.Reasoning.SelfAssessmentService>();

// Agent task step tracking
builder.Services.AddSingleton<AgentSquad.Core.Agents.Steps.IAgentTaskTracker, AgentSquad.Core.Agents.Steps.AgentTaskTracker>();

// Decision impact classification and gating
builder.Services.AddSingleton<AgentSquad.Core.Agents.Decisions.IDecisionLog, AgentSquad.Core.Agents.Decisions.DecisionLog>();
builder.Services.AddSingleton<AgentSquad.Core.Agents.Decisions.DecisionGateService>();

// Orchestrator (registry, health monitor, deadlock detector, spawn manager, workflow)
builder.Services.AddOrchestrator();
builder.Services.AddStrategyFramework();
builder.Services.AddStrategyDashboard();
builder.Services.AddSingleton<AgentSquad.Core.Strategies.IStrategyBroadcaster,
    AgentSquad.Dashboard.Services.SignalRStrategyBroadcaster>();

// Strategy framework: real baseline code generator (lives in Agents project so it can
// use ModelRegistry + IPromptTemplateService). When this isn't registered, BaselineStrategy
// falls back to its marker-only stub behavior — useful for orchestrator unit tests.
builder.Services.AddSingleton<AgentSquad.Core.Strategies.IBaselineCodeGenerator,
    AgentSquad.Agents.AI.BaselineCodeGenerator>();

// Strategy framework: real LLM judge (overrides the NullLlmJudge from AddStrategyFramework).
// Lives in Agents project so it can use ModelRegistry. When this isn't registered, the
// evaluator falls back to deterministic tokens-then-time tiebreaks.
builder.Services.AddSingleton<AgentSquad.Core.Strategies.ILlmJudge,
    AgentSquad.Agents.AI.LlmJudge>();

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
builder.Services.AddSingleton<IStrategiesDataService, InProcessStrategiesDataService>();

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

api.MapGet("/health/playwright", (PlaywrightRunner pw) =>
    Results.Ok(new { pw.IsReady, pw.NotReadyReason, pw.LastValidatedUtc,
        pw.OccupiedPortCount, pw.LastPortCheckUtc }));

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

api.MapGet("/metrics/aggregates", async (AgentSquad.Core.Metrics.BuildTestMetrics metrics, CancellationToken ct) =>
    Results.Ok(await metrics.GetAggregatesAsync(DateTime.MinValue, ct)));

api.MapGet("/repo-info", (IGitHubService github) =>
    Results.Ok(new { FullName = github.RepositoryFullName }));

api.MapGet("/github/file", async (string path, IGitHubService github, CancellationToken ct) =>
{
    var content = await github.GetFileContentAsync(path, ct: ct);
    return content is not null ? Results.Ok(new { content }) : Results.NotFound();
});

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

// ── Prompt Template REST API (consumed by standalone Dashboard.Host) ──
var promptApi = app.MapGroup("/api/prompts").WithTags("Prompts");

promptApi.MapGet("/roles", (IPromptTemplateService svc) =>
{
    var basePath = app.Configuration.GetValue<string>("AgentSquad:Prompts:BasePath") ?? "prompts";
    var fullPath = Path.GetFullPath(basePath);
    if (!Directory.Exists(fullPath)) return Results.Ok(Array.Empty<string>());
    var roles = Directory.GetDirectories(fullPath)
        .Select(d => Path.GetFileName(d))
        .OrderBy(n => n)
        .ToArray();
    return Results.Ok(roles);
});

promptApi.MapGet("/templates/{role}", (string role, IPromptTemplateService svc) =>
    Results.Ok(svc.ListTemplates(role)));

promptApi.MapGet("/content/{**templatePath}", async (string templatePath, IPromptTemplateService svc, CancellationToken ct) =>
{
    var content = await svc.GetRawContentAsync(templatePath, ct);
    return content is not null ? Results.Ok(new { content }) : Results.NotFound();
});

promptApi.MapPut("/content/{**templatePath}", async (string templatePath, HttpContext ctx, IPromptTemplateService svc, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PromptSaveRequest>(ct);
    if (body?.Content is null) return Results.BadRequest();
    await svc.SaveRawContentAsync(templatePath, body.Content, ct);
    return Results.Ok();
});

promptApi.MapGet("/metadata/{**templatePath}", async (string templatePath, IPromptTemplateService svc, CancellationToken ct) =>
{
    var metadata = await svc.GetMetadataAsync(templatePath, ct);
    return metadata is not null ? Results.Ok(metadata) : Results.NotFound();
});

promptApi.MapPost("/reset/{**templatePath}", async (string templatePath, IPromptTemplateService svc, IOptions<AgentSquadConfig> config, CancellationToken ct) =>
{
    var promptsBase = Path.GetFullPath(config.Value.Prompts.BasePath);
    var defaultsPath = Path.Combine(Path.GetDirectoryName(promptsBase)!, "prompts-defaults");
    var defaultFile = Path.Combine(defaultsPath, templatePath + ".md");
    if (!File.Exists(defaultFile)) return Results.NotFound();
    var defaultContent = await File.ReadAllTextAsync(defaultFile, ct);
    await svc.SaveRawContentAsync(templatePath, defaultContent, ct);
    return Results.Ok();
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

// ── Agent Task Steps REST API ──
var stepsApi = app.MapGroup("/api/steps").WithTags("Steps");

stepsApi.MapGet("/{agentId}", (string agentId, AgentSquad.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetSteps(agentId)));

stepsApi.MapGet("/{agentId}/current", (string agentId, AgentSquad.Core.Agents.Steps.IAgentTaskTracker tracker) =>
{
    var step = tracker.GetCurrentStep(agentId);
    return step is not null ? Results.Ok(step) : Results.NotFound();
});

stepsApi.MapGet("/{agentId}/progress", (string agentId, AgentSquad.Core.Agents.Steps.IAgentTaskTracker tracker) =>
{
    var (completed, total) = tracker.GetProgress(agentId);
    return Results.Ok(new { completed, total });
});

stepsApi.MapGet("/active", (AgentSquad.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetActiveSteps()));

stepsApi.MapGet("/{agentId}/grouped", (string agentId, AgentSquad.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetGroupedSteps(agentId)));

stepsApi.MapGet("/templates/{role}", (string role) =>
{
    if (!Enum.TryParse<AgentSquad.Core.Agents.AgentRole>(role, true, out var agentRole))
        return Results.BadRequest($"Unknown role: {role}");
    return Results.Ok(AgentSquad.Core.Agents.Steps.AgentStepTemplates.GetTemplateSteps(agentRole));
});

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

// ── Strategy framework REST API (Phase 4) ──
var strategiesApi = app.MapGroup("/api/strategies").WithTags("Strategies");
strategiesApi.MapGet("/active", (AgentSquad.Core.Strategies.CandidateStateStore store) =>
    Results.Ok(store.GetActiveTasks()));
strategiesApi.MapGet("/recent", (AgentSquad.Core.Strategies.CandidateStateStore store, int? limit) =>
    Results.Ok(store.GetRecentTasks(limit ?? 50)));
strategiesApi.MapGet("/enabled", (IOptions<StrategyFrameworkConfig> cfg) =>
{
    var c = cfg.Value;
    return Results.Ok(new
    {
        masterEnabled = c.Enabled,
        enabledStrategies = c.EnabledStrategies,
    });
});
// Phase 6: per-strategy cost attribution rollup.
strategiesApi.MapGet("/cost", (AgentSquad.Core.AI.AgentUsageTracker usage) =>
    Results.Ok(new
    {
        total = usage.GetTotalStrategyCost(),
        byStrategy = usage.GetAllStrategyStats(),
    }));

// ── Run management REST API (for project/feature lifecycle) ──
var runsApi = app.MapGroup("/api/runs").WithTags("Runs");

runsApi.MapGet("/active", (RunCoordinator coordinator) =>
{
    var run = coordinator.ActiveRun;
    var profile = coordinator.ActiveProfile;
    return Results.Ok(new
    {
        run,
        profile = profile is not null ? new
        {
            mode = profile.Mode.ToString(),
            displayName = profile.DisplayName,
            requiredRoles = profile.RequiredAgentRoles,
            artifactBasePath = profile.ArtifactBasePath,
            specDocName = profile.SpecDocName,
            decomposeToMultipleTasks = profile.DecomposeToMultipleTasks
        } : null
    });
});

runsApi.MapPost("/start-project", async (RunCoordinator coordinator, CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.StartProjectAsync(ct);
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(ct); }
            catch (Exception ex)
            {
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, ct);
        return Results.Ok(new { run, message = "Project run started" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

runsApi.MapPost("/start-feature/{featureId}", async (string featureId, RunCoordinator coordinator, CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.StartFeatureAsync(featureId, ct);
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(ct); }
            catch (Exception ex)
            {
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, ct);
        return Results.Ok(new { run, message = $"Feature run started for {featureId}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

runsApi.MapPost("/stop", async (RunCoordinator coordinator, CancellationToken ct) =>
{
    await coordinator.StopAsync(ct);
    return Results.Ok(new { message = "Run stopped" });
});

runsApi.MapGet("/history", async (AgentStateStore stateStore, int? limit, CancellationToken ct) =>
{
    var history = await stateStore.GetRunHistoryAsync(limit ?? 20, ct);
    return Results.Ok(history);
});

// ── Features CRUD REST API ──
var featuresApi = app.MapGroup("/api/features").WithTags("Features");

featuresApi.MapGet("/", async (AgentStateStore stateStore, int? limit, CancellationToken ct) =>
    Results.Ok(await stateStore.ListFeaturesAsync(limit ?? 50, ct)));

featuresApi.MapGet("/{id}", async (string id, AgentStateStore stateStore, CancellationToken ct) =>
{
    var feature = await stateStore.GetFeatureAsync(id, ct);
    return feature is not null ? Results.Ok(feature) : Results.NotFound();
});

featuresApi.MapPost("/", async (HttpContext ctx, AgentStateStore stateStore, CancellationToken ct) =>
{
    var feature = await ctx.Request.ReadFromJsonAsync<FeatureDefinition>(ct);
    if (feature is null || string.IsNullOrWhiteSpace(feature.Title))
        return Results.BadRequest(new { error = "Title is required" });

    // Ensure ID and defaults
    var toSave = feature with
    {
        Id = string.IsNullOrWhiteSpace(feature.Id) ? Guid.NewGuid().ToString("N") : feature.Id,
        Status = FeatureStatus.Draft,
        CreatedAt = DateTime.UtcNow
    };
    await stateStore.SaveFeatureAsync(toSave, ct);
    return Results.Created($"/api/features/{toSave.Id}", toSave);
});

featuresApi.MapPut("/{id}", async (string id, HttpContext ctx, AgentStateStore stateStore, CancellationToken ct) =>
{
    var existing = await stateStore.GetFeatureAsync(id, ct);
    if (existing is null) return Results.NotFound();
    if (existing.Status != FeatureStatus.Draft)
        return Results.Conflict(new { error = "Only Draft features can be edited" });

    var update = await ctx.Request.ReadFromJsonAsync<FeatureDefinition>(ct);
    if (update is null) return Results.BadRequest();

    var toSave = update with { Id = id, Status = FeatureStatus.Draft, CreatedAt = existing.CreatedAt };
    await stateStore.SaveFeatureAsync(toSave, ct);
    return Results.Ok(toSave);
});

featuresApi.MapDelete("/{id}", async (string id, AgentStateStore stateStore, CancellationToken ct) =>
{
    var existing = await stateStore.GetFeatureAsync(id, ct);
    if (existing is null) return Results.NotFound();
    if (existing.Status is not (FeatureStatus.Draft or FeatureStatus.Cancelled))
        return Results.Conflict(new { error = "Only Draft or Cancelled features can be deleted" });

    await stateStore.DeleteFeatureAsync(id, ct);
    return Results.Ok(new { message = $"Feature '{id}' deleted" });
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
record PromptSaveRequest(string? Content);
