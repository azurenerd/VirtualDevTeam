// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using Microsoft.Extensions.Options;
using VirtualDevTeam.Agents;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Notifications;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.Dashboard.Components;
using VirtualDevTeam.Dashboard.Hubs;
using VirtualDevTeam.Dashboard.Services;
using VirtualDevTeam.Orchestrator;
using VirtualDevTeam.Runner;
using VirtualDevTeam.Runner.Startup;

// ── CLI entry point: handle non-server commands before building the host ──
if (await VirtualDevTeam.Runner.VdtCli.HandleCliAsync(args))
    return; // Command handled (version, check-deps, help) — exit

var cliOptions = VirtualDevTeam.Runner.VdtCli.ParseStartupOptions(args);

// Apply CLI flags to configuration before builder reads it
if (cliOptions.Headless)
{
    Environment.SetEnvironmentVariable("VirtualDevTeam__Headless__Enabled", "true");
}
if (cliOptions.AutoApprove)
{
    Environment.SetEnvironmentVariable("VirtualDevTeam__Headless__AutoApproveAllGates", "true");
}
// CLI --project flag sets InPlace mode; without it, let develop-settings or appsettings decide
if (cliOptions.ProjectPath is not null)
{
    Environment.SetEnvironmentVariable("VirtualDevTeam__Workspace__LocalCheckoutPath", cliOptions.ProjectPath);
    Environment.SetEnvironmentVariable("VirtualDevTeam__Workspace__WorkspaceMode", "InPlace");
}

var builder = WebApplication.CreateBuilder(args);

// Always load user-secrets so PAT is never stored in tracked appsettings.json
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Configure Kestrel to use the dashboard port from config (default 5050).
// CLI --port flag overrides config.
var dashboardPort = cliOptions.Port != 5050
    ? cliOptions.Port
    : builder.Configuration.GetValue("VirtualDevTeam:Dashboard:Port", 5050);
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Always resolve RCL static web assets (Dashboard CSS/JS) — needed for _content/ paths.
// In headless mode, skip static web assets since there's no UI.
var isHeadless = cliOptions.Headless || builder.Configuration.GetValue<bool>("VirtualDevTeam:Headless:Enabled");
if (!isHeadless)
{
    builder.WebHost.UseStaticWebAssets();
}

// NoMessyCodePlan Theme 4d — service registrations live in topical extension methods under
// VirtualDevTeam.Runner.Startup. Each extension covers one architectural slice. Order matters
// only insofar as registration order affects:
//   • IHostedService start order (preserved within HealthMonitor + Agents + Orchestration)
//   • Configure<T>/PostConfigure<T>/IPostConfigureOptions<T> chaining (kept in CoreServices)
//   • Alias registrations like sp.GetService<Impl>() pointing at the AddSingleton<Impl>() above them
// All DI resolution is lazy — singletons are constructed when first requested, not at registration.
builder.Services
    .AddRunnerCoreServices(builder.Configuration)
    .AddRunnerDevPlatform(builder.Configuration)
    .AddRunnerHealthMonitor(builder.Configuration)
    .AddRunnerAgents()
    .AddRunnerOrchestration(builder.Configuration);

// Dashboard data services — always registered (API endpoints depend on them).
// Includes SignalR so IHubContext<T> resolves for hosted services even in headless.
builder.Services.AddRunnerDashboardServices();

// Register "RunnerApi" named HttpClient so Dashboard .razor pages can call the
// Runner's own API endpoints. In standalone Dashboard.Host mode this is registered
// separately pointing at the Runner's external URL; in bundled mode (here) it
// points at localhost on the Runner's own port.
builder.Services.AddHttpClient("RunnerApi", client =>
{
    client.BaseAddress = new Uri($"http://localhost:{dashboardPort}");
});

if (!isHeadless)
{
    // Blazor Server + UI-only services (Director CLI, prerequisite checker, scenario wizard).
    builder.Services.AddRunnerDashboardUI();
}
else
{
    // Headless: register the event stream for JSONL stdout output
    builder.Services.AddSingleton<VirtualDevTeam.Runner.HeadlessEventStream>();
}

// CORS for the standalone Dashboard.Host scenario (cross-origin API access).
builder.Services.AddCors(o => o.AddPolicy("DashboardApi", p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Load persisted role description overrides from previous sessions
var roleProvider = app.Services.GetService<VirtualDevTeam.Core.AI.RoleContextProvider>();
var stateStore = app.Services.GetService<VirtualDevTeam.Core.Persistence.AgentStateStore>();
if (roleProvider is not null && stateStore is not null)
{
    roleProvider.LoadPersistedOverrides(stateStore);
}

// ── Apply develop-settings.json to the in-memory config at STARTUP ──
// Without this, AzureOpenAIImage / Project / GatePreferences / etc. stay at appsettings.json
// defaults (which are null for AzureOpenAIImage) until the operator clicks "Start project"
// in the wizard. On a warm runner restart while a project is mid-flight, MergeIntoConfig
// would never fire — and any agent that depends on the merged config (image-gen env var
// injection, gate preferences, working branch resolution) would see the empty defaults.
// Observed 2026-05-12: Artist's agentic session saw ENDPOINT: '' and KEY length: 0 even
// though develop-settings.json had a valid endpoint, because the runner had restarted
// without going through StartProject.
//
// IMPORTANT: we MUST merge into the IOptionsMonitor's cached CurrentValue, NOT just
// IOptions.Value. AzureImageAuthProvider and other downstream consumers use the monitor.
// In .NET 8 these resolve to different object instances for some binding patterns; relying
// on them sharing the same reference is fragile. Merging into both is belt-and-suspenders.
try
{
    var developSettingsService = app.Services.GetService<VirtualDevTeam.Core.Configuration.DevelopSettingsService>();
    var optionsMonitor = app.Services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>();
    var optionsValue = app.Services.GetService<Microsoft.Extensions.Options.IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>();
    if (developSettingsService is not null)
    {
        var settings = developSettingsService.LoadAsync().GetAwaiter().GetResult();
        if (settings is not null)
        {
            // Merge into the IOptionsMonitor snapshot (downstream consumers like
            // AzureImageAuthProvider read via _config.CurrentValue).
            if (optionsMonitor?.CurrentValue is { } monitorCfg)
                developSettingsService.MergeIntoConfig(monitorCfg, settings);
            // Belt-and-suspenders: also merge into the IOptions snapshot in case any
            // consumer resolves IOptions.Value to a separate instance.
            if (optionsValue?.Value is { } optionsCfg
                && !ReferenceEquals(optionsCfg, optionsMonitor?.CurrentValue))
                developSettingsService.MergeIntoConfig(optionsCfg, settings);

            var effectiveCfg = optionsMonitor?.CurrentValue ?? optionsValue?.Value;
            app.Logger.LogInformation(
                "Applied develop-settings.json to in-memory config at startup (AzureOpenAIImage.IsConfigured={IsConfigured}, GitHubRepo={Repo}, IOptionsAndMonitorSameInstance={Same})",
                effectiveCfg?.AzureOpenAIImage?.IsConfigured() == true,
                effectiveCfg?.Project?.GitHubRepo ?? "(none)",
                ReferenceEquals(optionsValue?.Value, optionsMonitor?.CurrentValue));
        }
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "Failed to merge develop-settings.json into config at startup — agents that depend on wizard-provisioned settings (image-gen, gate prefs, working branch) may fall back to appsettings.json defaults");
}

// Configure HTTP pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseCors("DashboardApi");
if (!isHeadless)
{
    app.UseStaticFiles();
    app.UseAntiforgery();
}

// ── Dashboard REST API (consumed by standalone Dashboard.Host) ──
var api = app.MapGroup("/api/dashboard").WithTags("Dashboard");

api.MapGet("/agents", (DashboardDataService svc) =>
    Results.Ok(svc.GetAllAgentSnapshots()));

// 2026-05-12 (frameworks-artifact-clickable-preview + eval-horizontal-artifact-viewer):
// Serve files from active candidate worktrees + durable strategy-artifacts/ for the
// Strategies page artifact-strip thumbnails and click-to-popup preview. Token format
// is base64url(absolute-path); CandidateArtifactService validates the path is inside an
// allowed workspace root before opening the file.
api.MapGet("/candidate-artifact", (string token,
    VirtualDevTeam.Core.Frameworks.CandidateArtifactService svc) =>
{
    var resolution = svc.Resolve(token);
    if (resolution is null) return Results.NotFound();
    return Results.File(resolution.FullPath, resolution.ContentType, Path.GetFileName(resolution.FullPath));
});

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

api.MapPost("/agents/{agentId}/restart", async (string agentId, AgentSpawnManager spawnMgr, CancellationToken ct) =>
{
    try
    {
        await spawnMgr.RespawnAgentAsync(agentId, ct);
        return Results.Ok(new { message = $"Agent '{agentId}' restarted." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPost("/pull-requests/{prNumber:int}/operator-feedback", async (
    int prNumber,
    HttpContext ctx,
    IPullRequestService prService,
    IReviewService reviewService,
    IMessageBus messageBus,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<OperatorFeedbackRequest>(ct);
    var normalizedFeedback = (body?.Feedback ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim();

    if (string.IsNullOrWhiteSpace(normalizedFeedback))
        return Results.BadRequest(new { error = "Feedback is required" });

    try
    {
        var pr = await prService.GetAsync(prNumber, ct);
        if (pr is null)
            return Results.NotFound(new { error = $"PR #{prNumber} not found" });

        if (pr.IsMerged || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "PR is not open" });

        var hasEligibleLabel = pr.Labels.Any(label =>
            string.Equals(label, PullRequestWorkflow.Labels.ReadyForReview, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, PullRequestWorkflow.Labels.InProgress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "status:in-progress", StringComparison.OrdinalIgnoreCase));

        if (!hasEligibleLabel)
            return Results.BadRequest(new { error = "PR must be ready-for-review or in-progress" });

        var sanitizedFeedback = normalizedFeedback
            .Replace("<!--", "&lt;!--", StringComparison.Ordinal)
            .Replace("-->", "--&gt;", StringComparison.Ordinal);

        var comment = $"**[Operator] CHANGES REQUESTED**\n\n<!-- vdt:operator-feedback v1 -->\n{sanitizedFeedback}\n<!-- /vdt:operator-feedback -->";

        var previousAgentId = VirtualDevTeam.Core.AI.AgentCallContext.CurrentAgentId;
        try
        {
            VirtualDevTeam.Core.AI.AgentCallContext.CurrentAgentId = "Operator";
            await reviewService.AddCommentAsync(prNumber, comment, ct);
        }
        finally
        {
            VirtualDevTeam.Core.AI.AgentCallContext.CurrentAgentId = previousAgentId;
        }

        await messageBus.PublishAsync(new ChangesRequestedMessage
        {
            FromAgentId = "Operator",
            ToAgentId = "*",
            MessageType = nameof(ChangesRequestedMessage),
            PrNumber = pr.Number,
            PrTitle = pr.Title,
            ReviewerAgent = "Operator",
            Feedback = sanitizedFeedback
        }, ct);

        return Results.Ok(new { prNumber = pr.Number, status = "feedback-submitted" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to submit operator feedback for PR #{PrNumber}", prNumber);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

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

// Rate-limit status — current throttle state and per-caller breakdown
api.MapGet("/health/rate-limit", (VirtualDevTeam.Core.GitHub.RateLimitManager rateLimitMgr) =>
{
    return Results.Ok(new
    {
        IsRateLimited = rateLimitMgr.IsRateLimited,
        Remaining = rateLimitMgr.Remaining,
        ResetAtUtc = rateLimitMgr.ResetAtUtc,
        TotalApiCalls = rateLimitMgr.TotalApiCalls,
        CallsByCallerTag = rateLimitMgr.GetCallsByCallerTag()
    });
});

// FlowMonitor — recent findings, actions, and last-tick liveness
api.MapGet("/health/flow-monitor", (VirtualDevTeam.Core.HealthMonitor.FlowMonitorPersistence persistence,
    Microsoft.Extensions.Options.IOptionsMonitor<VirtualDevTeam.Core.HealthMonitor.FlowMonitorConfig> cfg) =>
{
    var lastTick = persistence.GetLastTick();
    var findings = persistence.GetRecentFindings(50);
    var actions = persistence.GetRecentActions(50);
    return Results.Ok(new
    {
        Enabled = cfg.CurrentValue.Enabled,
        PollIntervalSeconds = cfg.CurrentValue.PollIntervalSeconds,
        MaxActionsPerHour = cfg.CurrentValue.MaxActionsPerHour,
        LastTickUtc = lastTick?.UtcDateTime,
        Findings = findings,
        Actions = actions,
    });
});

// Toggle a detector or action on/off at runtime. Mutation is in-memory on the bound
// FlowMonitorConfig instance — IOptionsMonitor.OnChange isn't fired by direct mutation,
// but the FlowMonitor service reads cfg.CurrentValue every tick, so the new value is
// observed within one PollIntervalSeconds window. Restart loses the override (config
// is reloaded from appsettings.json on next start).
api.MapPost("/health/flow-monitor/toggle", async (HttpContext ctx,
    Microsoft.Extensions.Options.IOptionsMonitor<VirtualDevTeam.Core.HealthMonitor.FlowMonitorConfig> cfg) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<FlowMonitorToggleRequest>();
    if (body is null || string.IsNullOrEmpty(body.Kind) || string.IsNullOrEmpty(body.Id))
        return Results.BadRequest(new { error = "kind + id required" });
    var current = cfg.CurrentValue;
    if (string.Equals(body.Kind, "detector", StringComparison.OrdinalIgnoreCase))
        current.Detectors[body.Id] = body.Enabled;
    else if (string.Equals(body.Kind, "action", StringComparison.OrdinalIgnoreCase))
        current.Actions[body.Id] = body.Enabled;
    else
        return Results.BadRequest(new { error = "kind must be 'detector' or 'action'" });
    return Results.Ok(new { kind = body.Kind, id = body.Id, enabled = body.Enabled });
});

// ──────────────────────────────────────────────────────────────────────────────
// FixRecommendation API surface — list / get / approve / rework / reject.
// Recommendation execution is centralized behind IDiagnosticActionExecutor so the
// minimal API layer stays thin and the allowlisted apply/dismiss behavior is shared.
// Rework remains planner-owned because it creates a brand-new recommendation row.
// ──────────────────────────────────────────────────────────────────────────────

api.MapGet("/health/flow-monitor/recommendations",
    (VirtualDevTeam.Core.HealthMonitor.FlowMonitorPersistence persistence) =>
    Results.Ok(persistence.GetRecentRecommendations(50)));

api.MapGet("/health/flow-monitor/recommendations/{id}",
    (string id, VirtualDevTeam.Core.HealthMonitor.FlowMonitorPersistence persistence) =>
{
    var rec = persistence.GetRecommendation(id);
    return rec is null ? Results.NotFound() : Results.Ok(rec);
});

// ──────────────────────────────────────────────────────────────────────────────
// Pipeline Assessment API — latest assessment, recent history, on-demand trigger,
// config read/write, prompt read/write, and pipeline status snapshot.
// ──────────────────────────────────────────────────────────────────────────────

api.MapGet("/health/assessment/latest",
    (VirtualDevTeam.Core.HealthMonitor.PipelineAssessmentStore store) =>
{
    var latest = store.GetLatestAssessment();
    return latest is null ? Results.NoContent() : Results.Ok(latest);
});

api.MapGet("/health/assessment/recent",
    (int? count, VirtualDevTeam.Core.HealthMonitor.PipelineAssessmentStore store) =>
    Results.Ok(store.GetRecentAssessments(count ?? 10)));

api.MapPost("/health/assessment/run-now", (HttpContext ctx,
    VirtualDevTeam.Orchestrator.PipelineAssessmentService assessmentService) =>
{
    assessmentService.RunNowAsync(focusQuery: null);
    return Results.Accepted(value: new { message = "Assessment triggered" });
});

api.MapGet("/health/assessment/config",
    (IOptionsMonitor<VirtualDevTeam.Core.HealthMonitor.FlowMonitorConfig> cfg) =>
    Results.Ok(cfg.CurrentValue.Assessment));

api.MapPost("/health/assessment/config", async (HttpContext ctx,
    IOptionsMonitor<VirtualDevTeam.Core.HealthMonitor.FlowMonitorConfig> cfg) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<VirtualDevTeam.Core.HealthMonitor.AssessmentConfig>();
    if (body is null) return Results.BadRequest(new { error = "body required" });
    var current = cfg.CurrentValue.Assessment;
    current.Enabled = body.Enabled;
    current.IntervalSeconds = Math.Clamp(body.IntervalSeconds, body.MinIntervalSeconds, body.MaxIntervalSeconds);
    current.ModelTier = body.ModelTier;
    current.ConfidenceThreshold = body.ConfidenceThreshold;
    current.CreateFindingsOnIssues = body.CreateFindingsOnIssues;
    return Results.Ok(current);
});

api.MapGet("/health/assessment/prompt",
    (VirtualDevTeam.Core.Prompts.IPromptTemplateService? promptService) =>
{
    if (promptService is null) return Results.NotFound(new { error = "Prompt service unavailable" });
    var raw = promptService.GetRawContentAsync("flow-monitor/pipeline-assessment").GetAwaiter().GetResult();
    return raw is null ? Results.NotFound() : Results.Ok(new { content = raw });
});

api.MapPost("/health/assessment/prompt", async (HttpContext ctx,
    VirtualDevTeam.Core.Prompts.IPromptTemplateService? promptService) =>
{
    if (promptService is null)
        return Results.NotFound(new { error = "Prompt service unavailable" });
    var body = await ctx.Request.ReadFromJsonAsync<PromptSaveRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "content required" });
    await promptService.SaveRawContentAsync("flow-monitor/pipeline-assessment", body.Content);
    promptService.InvalidateCache("flow-monitor/pipeline-assessment");
    return Results.Ok(new { message = "Prompt updated", length = body.Content.Length });
});

api.MapGet("/pipeline/status", async (
    VirtualDevTeam.Core.HealthMonitor.PipelineStatusSnapshotService snapshotService,
    CancellationToken ct) =>
    Results.Ok(await snapshotService.GetSnapshotAsync(ct)));

// Workspace mode info — used by Dashboard for mode badge + service health
api.MapGet("/workspace/mode", (IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig> cfg) =>
    Results.Ok(new
    {
        mode = cfg.Value.Workspace.WorkspaceMode.ToString(),
        isWorktreeMode = cfg.Value.Workspace.IsWorktreeMode,
        isInPlaceMode = cfg.Value.Workspace.IsInPlaceMode,
        localCheckoutPath = cfg.Value.Workspace.LocalCheckoutPath,
        worktreeRoot = cfg.Value.Workspace.WorktreeRoot,
    }));

api.MapGet("/workspace/services",
    (IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig> cfg) =>
{
    var lp = cfg.Value.LargeProject;
    if (lp is null || !lp.Enabled)
        return Results.Ok(new { enabled = false, services = Array.Empty<object>() });

    return Results.Ok(new
    {
        enabled = true,
        services = lp.Services.Select(s => new
        {
            s.Name,
            displayName = s.EffectiveDisplayName,
            s.Path,
            s.Port,
            s.HealthUrl,
            s.UseExistingDevServer,
            s.TechStack,
            expertiseTags = s.ExpertiseTags,
        })
    });
});

api.MapPost("/health/flow-monitor/recommendations/{id}/approve",
    async (string id,
           VirtualDevTeam.Core.HealthMonitor.Actions.IDiagnosticActionExecutor executor,
           CancellationToken ct) =>
{
    var result = await executor.ExecuteAsync(
        new VirtualDevTeam.Core.HealthMonitor.Actions.DiagnosticActionRequest
        {
            Kind = VirtualDevTeam.Core.HealthMonitor.Actions.DiagnosticActionKind.ApplyRecommendation,
            RecommendationId = id,
            RepoRoot = Directory.GetCurrentDirectory()
        },
        ct);

    return result is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            id = result.RecommendationId,
            state = result.State.ToString(),
            tier = result.Tier?.ToString(),
            detail = result.Detail,
            restartRequired = result.RestartRequired,
        });
});

api.MapPost("/health/flow-monitor/recommendations/{id}/rework",
    async (string id, HttpContext ctx,
           VirtualDevTeam.Core.HealthMonitor.FlowMonitorPersistence persistence,
           VirtualDevTeam.Core.HealthMonitor.FixRecommendationPlannerService planner,
           VirtualDevTeam.Core.Notifications.GateNotificationService notifications,
           CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<FixRecommendationReworkRequest>(ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Feedback))
        return Results.BadRequest(new { error = "feedback required" });

    var revised = await planner.ReviseAsync(id, body.Feedback, ct);
    if (revised is null) return Results.NotFound();

    // Persist the new revision (separate row keeps history intact for the operator).
    var repoRoot = Directory.GetCurrentDirectory();
    var path = await planner.SaveToFixRecommendationsFolderAsync(revised, repoRoot, ct);
    if (path is not null)
        revised = revised with { PlanFilePath = path };
    var newId = persistence.InsertRecommendation(revised);

    // Resolve the old notification, raise a new one for the revised plan so the operator
    // sees the rework cycle reflected on the Approvals page.
    notifications.Resolve($"flow-monitor:fix:{id}", resourceNumber: null);
    if (!string.IsNullOrEmpty(newId))
    {
        try
        {
            var ctxText =
                $"🔧 **Revised fix recommendation** (rework round {revised.ReworkCount}, " +
                $"{revised.Confidence:0%} confidence). Operator feedback applied: {body.Feedback}";
            await notifications.AddNotificationAsync(
                gateId: $"flow-monitor:fix:{newId}",
                context: ctxText,
                resourceNumber: null,
                ct: ct);
        }
        catch { /* best-effort */ }
    }
    return Results.Ok(revised);
});

api.MapPost("/health/flow-monitor/recommendations/{id}/reject",
    async (string id,
           VirtualDevTeam.Core.HealthMonitor.Actions.IDiagnosticActionExecutor executor,
           CancellationToken ct) =>
{
    var result = await executor.ExecuteAsync(
        new VirtualDevTeam.Core.HealthMonitor.Actions.DiagnosticActionRequest
        {
            Kind = VirtualDevTeam.Core.HealthMonitor.Actions.DiagnosticActionKind.DismissRecommendation,
            RecommendationId = id,
        },
        ct);

    return result is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            id = result.RecommendationId,
            state = result.State.ToString(),
            tier = result.Tier?.ToString(),
            detail = result.Detail,
            restartRequired = result.RestartRequired,
        });
});

// T1.7: Warm restart — graceful stop + auto re-launch via restart-runner.ps1.
// Workflow state, agent identities, and signals are checkpointed to SQLite, so the
// new runner resumes from where the old one stopped. In-flight LLM calls are cancelled;
// durable platform work (PRs, issues, branches) is unaffected.
api.MapPost("/runtime/restart",
    (Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime,
     ILogger<Program> logger) =>
{
    logger.LogWarning("T1.7: Warm restart requested via API. Spawning restart-runner.ps1 detached, then stopping application.");

    // Locate restart-runner.ps1. The runner runs from src/VirtualDevTeam.Runner/bin/Debug/net8.0,
    // so walk up to the repo root.
    var scriptPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "restart-runner.ps1"));

    if (!File.Exists(scriptPath))
    {
        logger.LogError("T1.7: restart-runner.ps1 not found at {Path}", scriptPath);
        return Results.Problem(
            $"Restart helper not found. Expected at: {scriptPath}. Stop and start manually.",
            statusCode: 500);
    }

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -StoppedByRunner",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        // Detach from runner job so it survives our exit (helper is intentionally not in the job tree)
        var p = System.Diagnostics.Process.Start(psi);
        logger.LogInformation("T1.7: Spawned restart helper PID {PID}", p?.Id);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "T1.7: Failed to spawn restart helper — runner will not auto-restart");
        return Results.Problem(
            "Failed to launch restart helper. The runner will stop but you'll need to start it manually.",
            statusCode: 500);
    }

    // Schedule shutdown after a brief delay so the HTTP response can flush to the client.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        lifetime.StopApplication();
    });

    return Results.Ok(new
    {
        status = "restart-initiated",
        message = "Runner stopping; helper will re-launch in ~5 seconds. The dashboard will auto-reload when it's back up.",
    });
});

// Start a project run programmatically (equivalent to clicking "Start Run" in the wizard).
// Useful for headless/monitoring scenarios where the dashboard UI isn't available.
api.MapPost("/project/start", async (
    VirtualDevTeam.Orchestrator.RunCoordinator coordinator,
    IHostApplicationLifetime lifetime,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.StartProjectAsync(ct, forceRestart: true);
        logger.LogInformation("Project started via API — RunId={RunId}, spawning agents...", run.RunId);
        // Use the application stopping token (not the HTTP request token) for background agent work
        var appToken = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(appToken); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent spawn failed after API-triggered project start");
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, appToken);
        return Results.Ok(new { status = "started", runId = run.RunId });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to start project via API");
        return Results.Problem($"Failed to start: {ex.Message}", statusCode: 500);
    }
});

api.MapGet("/models", (DashboardDataService svc) =>
    Results.Ok(svc.GetAvailableModels()));

api.MapPost("/models/refresh", (DashboardDataService svc) =>
    { svc.RefreshActiveModels(); return Results.Ok(); });

api.MapGet("/timeline", (DashboardDataService svc) =>
    Results.Ok(svc.GetExecutionTimeline()));

api.MapGet("/platform/work-items", async (DashboardDataService svc) =>
    Results.Ok(await svc.GetWorkItemsAsync()));

api.MapGet("/platform/pull-requests", async (DashboardDataService svc) =>
    Results.Ok(await svc.GetPullRequestsAsync()));

api.MapPost("/platform/invalidate", (DashboardDataService svc) =>
{
    svc.InvalidatePlatformCaches();
    return Results.Ok();
});

// In-dashboard PR detail page data (det-2). Aggregates the platform-neutral PR record with
// its conversation comments, review threads, and changed-file paths in a single round-trip.
// Each capability sub-call is wrapped in try/catch with empty fallback so a partial platform
// outage (e.g. review threads service is down) still renders the page header + body.
api.MapGet("/platform/pull-request/{number:int}", async (
    int number,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prSvc,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IReviewService reviewSvc,
    Microsoft.Extensions.Logging.ILogger<Program> logger) =>
{
    var pr = await prSvc.GetAsync(number);
    if (pr is null) return Results.NotFound(new { error = $"PR #{number} not found" });

    IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformComment> comments;
    try { comments = await reviewSvc.GetCommentsAsync(number); }
    catch (Exception ex) { logger.LogDebug(ex, "GetCommentsAsync failed for PR #{N} — defaulting to empty", number); comments = Array.Empty<VirtualDevTeam.Core.DevPlatform.Models.PlatformComment>(); }

    IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformReviewThread> threads;
    try { threads = await reviewSvc.GetThreadsAsync(number); }
    catch (Exception ex) { logger.LogDebug(ex, "GetThreadsAsync failed for PR #{N} — defaulting to empty", number); threads = Array.Empty<VirtualDevTeam.Core.DevPlatform.Models.PlatformReviewThread>(); }

    IReadOnlyList<string> files;
    try { files = await prSvc.GetChangedFilesAsync(number); }
    catch (Exception ex) { logger.LogDebug(ex, "GetChangedFilesAsync failed for PR #{N} — defaulting to empty", number); files = Array.Empty<string>(); }

    return Results.Ok(new VirtualDevTeam.Core.DevPlatform.Models.PullRequestDetailDto(pr, comments, threads, files));
});

// PR Merge-Flow Timeline — returns a deterministic lifecycle snapshot (data path; UI follow-up).
api.MapGet("/pr-merge-flow/{prNumber:int}", async (
    int prNumber,
    VirtualDevTeam.Core.Pipeline.IPrMergeFlowSource source,
    CancellationToken ct) =>
{
    var snap = await source.GetSnapshotAsync(prNumber, ct);
    return snap is null ? Results.NotFound() : Results.Ok(snap);
});

// In-dashboard issue / work-item detail page data (det-3). Same partial-failure tolerance.
api.MapGet("/platform/work-item/{number:int}", async (
    int number,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiSvc,
    Microsoft.Extensions.Logging.ILogger<Program> logger) =>
{
    var item = await wiSvc.GetAsync(number);
    if (item is null) return Results.NotFound(new { error = $"Work item #{number} not found" });

    IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformComment> comments;
    try { comments = await wiSvc.GetCommentsAsync(number); }
    catch (Exception ex) { logger.LogDebug(ex, "GetCommentsAsync failed for work item #{N} — defaulting to empty", number); comments = Array.Empty<VirtualDevTeam.Core.DevPlatform.Models.PlatformComment>(); }

    return Results.Ok(new VirtualDevTeam.Core.DevPlatform.Models.WorkItemDetailDto(item, comments));
});

api.MapGet("/platform/rate-limited", (DashboardDataService svc) =>
    Results.Ok(new { IsRateLimited = svc.IsRateLimited }));

api.MapGet("/platform/rate-limit-info", (DashboardDataService svc) =>
    Results.Ok(svc.GetRateLimitInfo()));

// Image proxy for PR / Issue comment images that live behind auth (e.g. GitHub Releases
// download URLs in a private repo, ADO attachments). Detail-page MarkdownBody rewrites every
// rendered <img src> through here so the browser fetches via the dashboard, which adds the
// bot's auth header server-side. Origin allowlist limits proxy targets to platform hosts.
//
// **Redirect handling**: GitHub Releases-download URLs respond 302 → S3 pre-signed URL. The
// Authorization header is rejected by S3 (it has its own auth in the query string), so we MUST
// strip auth before following the redirect. We disable HttpClient auto-redirect and handle 3xx
// manually so we can clear the Authorization header before the second request.
api.MapGet("/platform/img", async (
    string url,
    VirtualDevTeam.Core.DevPlatform.Auth.IDevPlatformAuthProvider authProvider,
    Microsoft.Extensions.Logging.ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest("url required");
    if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
        (target.Scheme != "https" && target.Scheme != "http"))
        return Results.BadRequest("invalid url");
    // Origin allowlist — only forward to known platform hosts. Prevents the proxy from being
    // used to fetch arbitrary internet content.
    var host = target.Host.ToLowerInvariant();
    var allowed = host == "github.com" || host.EndsWith(".github.com") ||
                  host.EndsWith(".githubusercontent.com") ||
                  host == "dev.azure.com" || host.EndsWith(".dev.azure.com") ||
                  host.EndsWith(".visualstudio.com");
    if (!allowed) return Results.BadRequest("host not in allowlist");

    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    string? authHeader = null;
    var token = await authProvider.GetTokenAsync().ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(token))
    {
        var scheme = authProvider.AuthScheme ?? "token";
        authHeader = scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            ? "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + token))
            : scheme + " " + token;
    }

    // Translate user-facing GitHub release-asset URLs to the API path. The user-facing URL
    // (https://github.com/owner/repo/releases/download/<tag>/<filename>) uses browser-session
    // auth and rejects PAT tokens with a 302→login redirect. The API path
    // (https://api.github.com/repos/owner/repo/releases/assets/<id>) accepts PAT tokens and
    // returns the binary via 302 to S3. So we resolve the asset ID first, then fetch via API.
    var (resolvedUrl, useApiOctetStream) = await TryResolveReleaseAssetApiUrlAsync(target, authHeader, http, logger);

    try
    {
        var current = resolvedUrl;
        var hops = 0;
        while (hops++ < 5)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            // Send auth only on the first hop (the platform-host call). Redirects to S3 / CDN
            // get a clean request — those URLs are pre-signed and reject Authorization headers.
            if (hops == 1 && authHeader is not null)
                req.Headers.TryAddWithoutValidation("Authorization", authHeader);
            // For the GitHub API release-asset endpoint we MUST send application/octet-stream
            // to get the binary (otherwise API returns JSON metadata). For everything else send
            // browser-style */* so user-facing URLs don't 406.
            req.Headers.Accept.ParseAdd(useApiOctetStream && hops == 1 ? "application/octet-stream" : "*/*");
            req.Headers.UserAgent.ParseAdd("VirtualDevTeam-Dashboard/1.0");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)resp.StatusCode;
            if (status >= 300 && status < 400 && resp.Headers.Location is not null)
            {
                current = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(current, resp.Headers.Location);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogInformation("Image proxy upstream {Url} returned {Status}", current, status);
                return Results.StatusCode(status);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            // Sniff when upstream content-type is missing, generic (octet-stream / application/*),
            // or text/* (login HTML masquerading as an image). Trust only explicit image/* content-types.
            if (string.IsNullOrEmpty(contentType)
                || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                var sniffed = SniffImageContentType(bytes);
                if (sniffed is null)
                {
                    logger.LogInformation("Image proxy: upstream {Url} returned {CT} that isn't an image (bytes={N})",
                        current, contentType, bytes.Length);
                    return Results.StatusCode(502);
                }
                contentType = sniffed;
            }
            return Results.File(bytes, contentType);
        }
        return Results.StatusCode(508); // loop detected
    }
    catch (Exception ex)
    {
        logger.LogInformation(ex, "Image proxy failed for {Url}", resolvedUrl);
        return Results.StatusCode(502);
    }
});

static async Task<(Uri Url, bool UseApiOctetStream)> TryResolveReleaseAssetApiUrlAsync(
    Uri target, string? authHeader, HttpClient http,
    Microsoft.Extensions.Logging.ILogger logger)
{
    // Match https://github.com/owner/repo/releases/download/<tag>/<filename>
    if (!target.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        return (target, false);
    var segs = target.AbsolutePath.Trim('/').Split('/');
    if (segs.Length < 6) return (target, false);
    if (!segs[2].Equals("releases", StringComparison.OrdinalIgnoreCase) ||
        !segs[3].Equals("download", StringComparison.OrdinalIgnoreCase))
        return (target, false);
    var owner = segs[0];
    var repo = segs[1];
    var tag = segs[4];
    var filename = Uri.UnescapeDataString(segs[5]);

    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        if (authHeader is not null)
            req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.UserAgent.ParseAdd("VirtualDevTeam-Dashboard/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode) return (target, false);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return (target, false);
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, filename, StringComparison.Ordinal)) continue;
            var id = a.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
            if (id <= 0) return (target, false);
            return (new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/assets/{id}"), true);
        }
    }
    catch (Exception ex)
    {
        logger.LogInformation(ex, "Release-asset API resolution failed for {Url} — falling back to direct fetch", target);
    }
    return (target, false);
}

static string? SniffImageContentType(byte[] bytes)
{
    if (bytes.Length < 8) return null;
    if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
    if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
    if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
    if (bytes.Length > 11 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
        && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
    return null;
}

api.MapPost("/reset", (DashboardDataService svc) =>
    { svc.ResetCaches(); return Results.Ok(); });

// ── Image generation validation (Phase 2 of the image-gen integration plan) ──
api.MapPost("/validate-image-gen", async (
    VirtualDevTeam.Dashboard.Services.ImageGenValidationRequest request,
    VirtualDevTeam.Dashboard.Services.ConfigurationService configSvc,
    CancellationToken ct) =>
{
    if (request is null)
        return Results.BadRequest(new { error = "Request body is required" });

    try
    {
        var report = await configSvc.ValidateImageGenAsync(request, ct);
        return Results.Ok(report);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "validate-image-gen failed");
    }
});

api.MapGet("/cost-summary", (DashboardDataService svc, VirtualDevTeam.Core.AI.AgentUsageTracker usage) =>
{
    var agentStats = svc.GetAgentUsageStats().ToDictionary(
        kvp => kvp.Key,
        kvp => new
        {
            kvp.Value.PromptTokens,
            kvp.Value.CompletionTokens,
            kvp.Value.TotalCalls,
            kvp.Value.EstimatedCost,
            kvp.Value.LastModel,
            kvp.Value.PremiumRequests,
            kvp.Value.ApiDurationMs
        });

    // Role-aggregated stats: collapses per-restart agent_id duplicates into one row per role.
    // The user sees a stable view across restarts (e.g. one "programmanager" row with cumulative
    // totals instead of N orphan rows). The dashboard CostBadge can switch to this view via the
    // ByRole field. See AgentUsageTracker.GetAggregatedStatsByRole for the role-inference rule.
    var byRole = usage.GetAggregatedStatsByRole().ToDictionary(
        kvp => kvp.Key,
        kvp => new
        {
            kvp.Value.PromptTokens,
            kvp.Value.CompletionTokens,
            kvp.Value.TotalCalls,
            kvp.Value.EstimatedCost,
            kvp.Value.LastModel,
            kvp.Value.PremiumRequests,
            kvp.Value.ApiDurationMs
        });

    return Results.Ok(new
    {
        TotalCost = svc.GetTotalEstimatedCost(),
        TotalCalls = svc.GetTotalAiCalls(),
        PremiumRequests = svc.GetTotalPremiumRequests(),
        AgentStats = agentStats,
        ByRole = byRole,
        Diagnostics = new
        {
            RestoredRowCount = usage.RestoredRowCount,
            RestoreError = usage.RestoreError,
            CurrentInMemoryRows = agentStats.Count,
        },
    });
});

api.MapGet("/metrics/aggregates", async (VirtualDevTeam.Core.Metrics.BuildTestMetrics metrics, CancellationToken ct) =>
    Results.Ok(await metrics.GetAggregatesAsync(DateTime.MinValue, ct)));

api.MapGet("/repo-info", (DashboardDataService svc) =>
    Results.Ok(new { FullName = svc.RepositoryDisplayName }));

api.MapGet("/platform/file", async (string path, string? branch, IRepositoryContentService repoContent, IRunBranchProvider branchProvider, CancellationToken ct) =>
{
    var effectiveBranch = branch ?? branchProvider.EffectiveBranch;
    var isBinary = VirtualDevTeam.Core.DevPlatform.Models.RepositoryFileContentResult.IsBinaryPath(path);

    if (isBinary)
    {
        return Results.Ok(new VirtualDevTeam.Core.DevPlatform.Models.RepositoryFileContentResult
        {
            Path = path,
            IsBinary = true,
            Content = null,
            ContentType = VirtualDevTeam.Core.DevPlatform.Models.RepositoryFileContentResult.InferContentType(path)
        });
    }

    var content = await repoContent.GetFileContentAsync(path, effectiveBranch, ct);
    if (content is null) return Results.NotFound();

    const int maxDisplayBytes = 100 * 1024; // 100KB
    var wasTruncated = content.Length > maxDisplayBytes;
    var displayContent = wasTruncated ? content[..maxDisplayBytes] : content;

    return Results.Ok(new VirtualDevTeam.Core.DevPlatform.Models.RepositoryFileContentResult
    {
        Path = path,
        IsBinary = false,
        SizeBytes = content.Length,
        Content = displayContent,
        WasTruncated = wasTruncated,
        ContentType = VirtualDevTeam.Core.DevPlatform.Models.RepositoryFileContentResult.InferContentType(path)
    });
});

api.MapGet("/platform/tree", async (string? branch, IRepositoryContentService repoContent, IRunBranchProvider branchProvider, CancellationToken ct) =>
{
    var effectiveBranch = branch ?? branchProvider.EffectiveBranch;
    var files = await repoContent.GetRepositoryTreeAsync(effectiveBranch, ct);
    return Results.Ok(new { branch = effectiveBranch, files });
});

// ── MissingWork proposed-issue API (Approvals page Phase 1.9) ──────────────────────
api.MapGet("/missing-work-proposals", (VirtualDevTeam.Core.MissingWork.MissingWorkPersistence store) =>
    Results.Ok(store.ListPendingProposals(50)));

api.MapPost("/missing-work-proposals/{id}/approve", async (
    string id,
    MissingWorkApprovePayload payload,
    VirtualDevTeam.Core.MissingWork.MissingWorkPersistence store,
    IWorkItemService? workItems,
    CancellationToken ct) =>
{
    var proposals = store.ListPendingProposals(1000);
    var proposal  = proposals.FirstOrDefault(p => p.Id == id);
    if (proposal is null) return Results.NotFound();
    if (workItems is null) return Results.Problem("IWorkItemService not registered — cannot create issue.");
    var finalTitle  = payload.Title  ?? proposal.ProposedTitle;
    var finalBody   = payload.Body   ?? proposal.ProposedBody;
    var finalLabels = payload.Labels ?? proposal.ProposedLabels;
    try
    {
        var created = await workItems.CreateAsync(finalTitle, finalBody, finalLabels, ct);
        store.UpdateProposalState(id,
            VirtualDevTeam.Core.MissingWork.ProposedIssueState.Created,
            VirtualDevTeam.Core.MissingWork.OperatorAction.ApproveAsIs,
            payload.Rationale,
            finalTitle, finalBody, finalLabels,
            created.Number);
        return Results.Ok(new { issueNumber = created.Number });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

api.MapPost("/missing-work-proposals/{id}/reject", (
    string id,
    MissingWorkRejectPayload payload,
    VirtualDevTeam.Core.MissingWork.MissingWorkPersistence store) =>
{
    store.UpdateProposalState(id,
        VirtualDevTeam.Core.MissingWork.ProposedIssueState.Rejected,
        VirtualDevTeam.Core.MissingWork.OperatorAction.Reject,
        payload.Rationale,
        null, null, null, null);
    return Results.NoContent();
});

// ── FlowAction proposed-action API (Approvals page FlowMonitor operator actions) ──
api.MapGet("/flow-action-proposals", async (
    VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionProposalStore store,
    CancellationToken ct) =>
    Results.Ok(await store.ListPendingAsync(ct)));

api.MapPost("/flow-action-proposals/{id}/approve", async (
    string id,
    FlowActionApprovePayload payload,
    VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionProposalStore store,
    VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionExecutor? executor,
    CancellationToken ct) =>
{
    var proposal = await store.GetAsync(id, ct);
    if (proposal is null) return Results.NotFound();
    if (executor is not null)
    {
        try
        {
            var result = await executor.ExecuteAsync(proposal, ct);
            await store.UpdateStateAsync(id,
                VirtualDevTeam.Core.HealthMonitor.Actions.ProposedFlowActionState.Executed,
                payload.Rationale, result, ct);
            return Results.Ok(new { executed = true, message = result });
        }
        catch (Exception ex)
        {
            await store.UpdateStateAsync(id,
                VirtualDevTeam.Core.HealthMonitor.Actions.ProposedFlowActionState.Failed,
                payload.Rationale, ex.Message, ct);
            return Results.Problem(ex.Message);
        }
    }
    await store.UpdateStateAsync(id,
        VirtualDevTeam.Core.HealthMonitor.Actions.ProposedFlowActionState.Approved,
        payload.Rationale, null, ct);
    return Results.Ok(new { executed = false, message = "Approved (no executor registered)" });
});

api.MapPost("/flow-action-proposals/{id}/reject", async (
    string id,
    FlowActionRejectPayload payload,
    VirtualDevTeam.Core.HealthMonitor.Actions.IFlowActionProposalStore store,
    CancellationToken ct) =>
{
    await store.UpdateStateAsync(id,
        VirtualDevTeam.Core.HealthMonitor.Actions.ProposedFlowActionState.Rejected,
        payload.Rationale, null, ct);
    return Results.NoContent();
});

// ── Configuration REST API (consumed by standalone Dashboard.Host) ──
var configApi = app.MapGroup("/api/configuration").WithTags("Configuration");

configApi.MapGet("/current", (ConfigurationService svc) =>
{
    var config = svc.GetCurrentConfig();
    // Serialize to JsonNode and strip all secrets before exposing over the API
    var json = System.Text.Json.JsonSerializer.SerializeToNode(config);
    ConfigurationService.StripSecrets(json);
    return Results.Ok(json);
});

configApi.MapPost("/save", async (VirtualDevTeamConfig config, ConfigurationService svc) =>
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

promptApi.MapGet("/roles", (IPromptTemplateService svc, IOptions<VirtualDevTeamConfig> config) =>
{
    // Use IOptions (already resolved by PostConfigure) instead of raw config to respect
    // the AppContext.BaseDirectory fallback for published CLI builds.
    var fullPath = Path.GetFullPath(config.Value.Prompts.BasePath);
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

promptApi.MapPost("/reset/{**templatePath}", async (string templatePath, IPromptTemplateService svc, IOptions<VirtualDevTeamConfig> config, CancellationToken ct) =>
{
    var promptsBase = Path.GetFullPath(config.Value.Prompts.BasePath);
    var defaultsPath = Path.Combine(Path.GetDirectoryName(promptsBase)!, "prompts-defaults");
    var defaultFile = Path.Combine(defaultsPath, templatePath + ".md");
    if (!File.Exists(defaultFile)) return Results.NotFound();
    var defaultContent = await File.ReadAllTextAsync(defaultFile, ct);
    await svc.SaveRawContentAsync(templatePath, defaultContent, ct);
    return Results.Ok();
});

// ── Agent Role Description REST API (run-scoped overrides) ──
var roleApi = app.MapGroup("/api/agents").WithTags("AgentRoles");

// Helper: resolve customAgentName using the same key the agent uses internally
static string? ResolveCustomAgentNameForApi(VirtualDevTeam.Core.Agents.AgentIdentity identity)
{
    if (!string.IsNullOrWhiteSpace(identity.CustomAgentName))
        return identity.CustomAgentName;
    return null;
}

roleApi.MapGet("/{agentId}/role-description", (string agentId,
    VirtualDevTeam.Orchestrator.AgentRegistry registry,
    VirtualDevTeam.Core.AI.RoleContextProvider roleContext) =>
{
    var agent = registry.GetAgent(agentId);
    if (agent is null) return Results.NotFound(new { error = "Agent not found" });

    var identity = agent.Identity;
    var customName = ResolveCustomAgentNameForApi(identity);

    var hasOverride = roleContext.TryGetRoleDescriptionOverride(identity.Role, customName, out var overrideText);
    var configuredDescription = roleContext.GetConfiguredRoleDescription(identity.Role, customName);
    var effectiveDescription = hasOverride ? overrideText : configuredDescription;

    return Results.Ok(new
    {
        agentId = identity.Id,
        displayName = identity.DisplayName,
        role = identity.Role.ToString(),
        effectiveDescription,
        overrideDescription = overrideText,
        configuredDescription,
        hasOverride
    });
});

roleApi.MapPut("/{agentId}/role-description", async (string agentId, HttpContext ctx,
    VirtualDevTeam.Orchestrator.AgentRegistry registry,
    VirtualDevTeam.Core.AI.RoleContextProvider roleContext,
    AgentStateStore stateStoreForRole) =>
{
    var agent = registry.GetAgent(agentId);
    if (agent is null) return Results.NotFound(new { error = "Agent not found" });

    var body = await ctx.Request.ReadFromJsonAsync<RoleDescriptionRequest>();
    var description = body?.Description?.Trim();

    var identity = agent.Identity;
    var customName = ResolveCustomAgentNameForApi(identity);

    // Blank/whitespace normalizes to a clear (revert to default)
    if (string.IsNullOrWhiteSpace(description))
    {
        roleContext.ClearRoleDescriptionOverride(identity.Role, customName);
        roleContext.ClearPersistedOverride(stateStoreForRole, identity.Role, customName);
        return Results.Ok(new { cleared = true });
    }

    roleContext.SetRoleDescriptionOverride(identity.Role, description, customName);
    roleContext.PersistOverride(stateStoreForRole, identity.Role, customName, description);
    return Results.Ok(new { saved = true });
});

roleApi.MapDelete("/{agentId}/role-description", (string agentId,
    VirtualDevTeam.Orchestrator.AgentRegistry registry,
    VirtualDevTeam.Core.AI.RoleContextProvider roleContext,
    AgentStateStore stateStoreForRole) =>
{
    var agent = registry.GetAgent(agentId);
    if (agent is null) return Results.NotFound(new { error = "Agent not found" });

    var identity = agent.Identity;
    var customName = ResolveCustomAgentNameForApi(identity);

    var cleared = roleContext.ClearRoleDescriptionOverride(identity.Role, customName);
    roleContext.ClearPersistedOverride(stateStoreForRole, identity.Role, customName);
    return Results.Ok(new { cleared });
});

// ── Reasoning Log REST API (consumed by standalone Dashboard.Host) ──
var reasoningApi = app.MapGroup("/api/reasoning").WithTags("Reasoning");

reasoningApi.MapGet("/agents", (VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetAgentIds()));

reasoningApi.MapGet("/events/{agentId}", (string agentId, VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetEvents(agentId)));

reasoningApi.MapGet("/events/{agentId}/since", (string agentId, DateTime since, VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog log) =>
    Results.Ok(log.GetEventsSince(agentId, since)));

reasoningApi.MapGet("/recent", (VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog log, int? count) =>
    Results.Ok(log.GetRecentEvents(count ?? 50)));

// ── Agent Task Steps REST API ──
var stepsApi = app.MapGroup("/api/steps").WithTags("Steps");

stepsApi.MapGet("/{agentId}", (string agentId, VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetSteps(agentId)));

stepsApi.MapGet("/{agentId}/current", (string agentId, VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker tracker) =>
{
    var step = tracker.GetCurrentStep(agentId);
    return step is not null ? Results.Ok(step) : Results.NotFound();
});

stepsApi.MapGet("/{agentId}/progress", (string agentId, VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker tracker) =>
{
    var (completed, total) = tracker.GetProgress(agentId);
    return Results.Ok(new { completed, total });
});

stepsApi.MapGet("/active", (VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetActiveSteps()));

stepsApi.MapGet("/{agentId}/grouped", (string agentId, VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker tracker) =>
    Results.Ok(tracker.GetGroupedSteps(agentId)));

stepsApi.MapGet("/templates/{role}", (string role) =>
{
    if (!Enum.TryParse<VirtualDevTeam.Core.Agents.AgentRole>(role, true, out var agentRole))
        return Results.BadRequest($"Unknown role: {role}");
    return Results.Ok(VirtualDevTeam.Core.Agents.Steps.AgentStepTemplates.GetTemplateSteps(agentRole));
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

gateApi.MapPost("/{gateId}/approve", (string gateId, IGateCheckService gateCheck, int? resourceNumber) =>
{
    gateCheck.ApproveGate(gateId, resourceNumber);
    return Results.Ok(new { gateId, approved = true, resourceNumber, message = $"Gate '{gateId}' approved" });
});

gateApi.MapPost("/{gateId}/reject", (string gateId, IGateCheckService gateCheck, string? feedback, int? resourceNumber) =>
{
    gateCheck.RejectGate(gateId, feedback, resourceNumber);
    return Results.Ok(new { gateId, rejected = true, resourceNumber, feedback, message = $"Gate '{gateId}' rejected" });
});

// Decision Gate API — programmatic approval/rejection of decision gates
var decisionApi = app.MapGroup("/api/decisions").WithTags("Decisions");

decisionApi.MapGet("/pending", (VirtualDevTeam.Core.Agents.Decisions.IDecisionLog decisionLog) =>
{
    var pending = decisionLog.GetPendingDecisions();
    return Results.Ok(pending.Select(d => new
    {
        d.Id, d.AgentId, d.AgentDisplayName, d.Title, d.Rationale,
        d.ImpactLevel, d.Category, d.CreatedAt, d.AssociatedPrNumber,
        d.Alternatives, d.AffectedFiles, d.RiskAssessment, d.Plan
    }));
});

decisionApi.MapPost("/{decisionId}/approve", (
    string decisionId,
    VirtualDevTeam.Core.Agents.Decisions.DecisionGateService decisionGateSvc,
    VirtualDevTeam.Core.Agents.Decisions.IDecisionLog decisionLog,
    string? feedback) =>
{
    var decision = decisionLog.GetDecision(decisionId);
    if (decision is null)
        return Results.NotFound(new { decisionId, message = "Decision not found" });
    if (decision.Status is not VirtualDevTeam.Core.Agents.Decisions.DecisionStatus.Pending)
        return Results.Ok(new { decisionId, alreadyResolved = true, status = decision.Status.ToString() });

    decisionGateSvc.ApproveDecision(decisionId, feedback, "api");
    return Results.Ok(new { decisionId, approved = true, message = $"Decision '{decision.Title}' approved" });
});

decisionApi.MapPost("/{decisionId}/reject", (
    string decisionId,
    VirtualDevTeam.Core.Agents.Decisions.DecisionGateService decisionGateSvc,
    VirtualDevTeam.Core.Agents.Decisions.IDecisionLog decisionLog,
    string? feedback) =>
{
    var decision = decisionLog.GetDecision(decisionId);
    if (decision is null)
        return Results.NotFound(new { decisionId, message = "Decision not found" });
    if (decision.Status is not VirtualDevTeam.Core.Agents.Decisions.DecisionStatus.Pending)
        return Results.Ok(new { decisionId, alreadyResolved = true, status = decision.Status.ToString() });

    decisionGateSvc.RejectDecision(decisionId, feedback);
    return Results.Ok(new { decisionId, rejected = true, message = $"Decision '{decision.Title}' rejected" });
});

// Pre-PR Clarification API — programmatic approval for headless/CLI mode
var clarificationApi = app.MapGroup("/api/clarifications").WithTags("Clarifications");

clarificationApi.MapGet("/pending", (VirtualDevTeam.Core.Agents.Decisions.PrePRClarificationStore store) =>
{
    var pending = store.GetPending();
    return Results.Ok(pending.Select(s => new
    {
        s.Id, s.AgentId, s.AgentDisplayName, s.IssueNumber, s.IssueTitle, s.CreatedAt, s.IsFinalized,
        Questions = s.Questions.Select(q => new { q.Question, q.ProposedAnswer, q.ImpactLevel, q.Category })
    }));
});

clarificationApi.MapPost("/{setId}/approve", (string setId, VirtualDevTeam.Core.Agents.Decisions.PrePRClarificationStore store) =>
{
    var set = store.Get(setId);
    if (set is null)
        return Results.NotFound(new { setId, message = "Clarification set not found" });
    if (set.IsFinalized)
        return Results.Ok(new { setId, alreadyFinalized = true });

    store.AutoApprove(setId);
    return Results.Ok(new { setId, approved = true, message = $"Clarification set for '{set.IssueTitle}' auto-approved" });
});

clarificationApi.MapPost("/approve-all", (VirtualDevTeam.Core.Agents.Decisions.PrePRClarificationStore store) =>
{
    var pending = store.GetPending();
    var approved = 0;
    foreach (var set in pending)
    {
        store.AutoApprove(set.Id);
        approved++;
    }
    return Results.Ok(new { approved, message = $"Auto-approved {approved} pending clarification set(s)" });
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

notificationApi.MapPost("/{notificationId}/dismiss", (string notificationId, GateNotificationService notificationSvc) =>
{
    notificationSvc.Dismiss(notificationId);
    return Results.Ok();
});

notificationApi.MapPost("/dismiss-flow-monitor", (GateNotificationService notificationSvc) =>
{
    var count = notificationSvc.DismissAllFlowMonitorInfo();
    return Results.Ok(new { dismissed = count });
});

// ── Strategy framework REST API (Phase 4) ──
var strategiesApi = app.MapGroup("/api/strategies").WithTags("Strategies");
strategiesApi.MapGet("/active", (VirtualDevTeam.Core.Strategies.CandidateStateStore store) =>
    Results.Ok(store.GetActiveTasks()));
strategiesApi.MapGet("/recent", (VirtualDevTeam.Core.Strategies.CandidateStateStore store, int? limit) =>
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
strategiesApi.MapGet("/cost", (VirtualDevTeam.Core.AI.AgentUsageTracker usage) =>
    Results.Ok(new
    {
        total = usage.GetTotalStrategyCost(),
        byStrategy = usage.GetAllStrategyStats(),
    }));

// Cancel a running framework orchestration task.
strategiesApi.MapPost("/cancel/{runId}/{taskId}", (
    string runId, string taskId,
    VirtualDevTeam.Core.Strategies.IOrchestrationCancellationService cancelService) =>
{
    var cancelled = cancelService.RequestCancellation(runId, taskId);
    return cancelled
        ? Results.Ok(new { cancelled = true, runId, taskId })
        : Results.NotFound(new { cancelled = false, message = "Orchestration not found or already completed" });
});

// Cancel a specific candidate within a running orchestration task.
strategiesApi.MapPost("/cancel/{runId}/{taskId}/{strategyId}", (
    string runId, string taskId, string strategyId,
    VirtualDevTeam.Core.Strategies.IOrchestrationCancellationService cancelService) =>
{
    var cancelled = cancelService.RequestCandidateCancellation(runId, taskId, strategyId);
    return cancelled
        ? Results.Ok(new { cancelled = true, runId, taskId, strategyId })
        : Results.NotFound(new { cancelled = false, message = "Candidate not found or already completed" });
});

// Reset a specific candidate — cancels current run and triggers retry in fresh worktree.
strategiesApi.MapPost("/reset/{runId}/{taskId}/{strategyId}", (
    string runId, string taskId, string strategyId,
    VirtualDevTeam.Core.Strategies.IOrchestrationCancellationService cancelService) =>
{
    var reset = cancelService.RequestCandidateReset(runId, taskId, strategyId);
    return reset
        ? Results.Ok(new { reset = true, runId, taskId, strategyId })
        : Results.NotFound(new { reset = false, message = "Candidate not found or already completed" });
});

// ── Pipeline Checkpoints REST API ──
var checkpointsApi = app.MapGroup("/api/checkpoints").WithTags("Checkpoints");
checkpointsApi.MapGet("/", async (VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListAsync(ct)));
checkpointsApi.MapGet("/latest", async (VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService svc, CancellationToken ct) =>
{
    var latest = await svc.GetLatestAsync(ct);
    return latest is not null ? Results.Ok(latest) : Results.NotFound();
});
checkpointsApi.MapPost("/capture", async (
    VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService svc,
    string name, string? trigger, CancellationToken ct) =>
{
    var trig = Enum.TryParse<VirtualDevTeam.Core.Checkpoints.CheckpointTrigger>(trigger, true, out var t)
        ? t : VirtualDevTeam.Core.Checkpoints.CheckpointTrigger.Manual;
    var result = await svc.CaptureAsync(name, trig, ct);
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});
checkpointsApi.MapPost("/{name}/restore", async (
    string name, VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService svc, CancellationToken ct) =>
{
    var result = await svc.RestoreAsync(name, ct);
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});
checkpointsApi.MapDelete("/{name}", async (
    string name, VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService svc, CancellationToken ct) =>
{
    var deleted = await svc.DeleteAsync(name, ct);
    return deleted ? Results.Ok() : Results.NotFound();
});

// ── Label Management REST API (works across Local/GitHub/ADO) ──
var labelApi = app.MapGroup("/api/platform").WithTags("Labels");

// PR labels
labelApi.MapGet("/prs/{prNumber:int}/labels", async (int prNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    var pr = await prService.GetAsync(prNumber, ct);
    return pr is not null ? Results.Ok(pr.Labels) : Results.NotFound();
});

labelApi.MapPost("/prs/{prNumber:int}/labels/add", async (int prNumber, LabelRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    await VirtualDevTeam.Core.DevPlatform.Capabilities.PullRequestServiceExtensions
        .AddLabelAsync(prService, prNumber, req.Label, ct);
    return Results.Ok(new { prNumber, added = req.Label });
});

labelApi.MapPost("/prs/{prNumber:int}/labels/remove", async (int prNumber, LabelRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    await VirtualDevTeam.Core.DevPlatform.Capabilities.PullRequestServiceExtensions
        .RemoveLabelAsync(prService, prNumber, req.Label, ct);
    return Results.Ok(new { prNumber, removed = req.Label });
});

// Work item labels
labelApi.MapGet("/work-items/{wiNumber:int}/labels", async (int wiNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService, CancellationToken ct) =>
{
    var wi = await wiService.GetAsync(wiNumber, ct);
    return wi is not null ? Results.Ok(wi.Labels) : Results.NotFound();
});

labelApi.MapPost("/work-items/{wiNumber:int}/labels/add", async (int wiNumber, LabelRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService, CancellationToken ct) =>
{
    var wi = await wiService.GetAsync(wiNumber, ct);
    if (wi is null) return Results.NotFound();
    var labels = wi.Labels.ToList();
    if (!labels.Contains(req.Label, StringComparer.OrdinalIgnoreCase))
        labels.Add(req.Label);
    await wiService.UpdateAsync(wiNumber, labels: labels, ct: ct);
    return Results.Ok(new { wiNumber, added = req.Label });
});

labelApi.MapPost("/work-items/{wiNumber:int}/labels/remove", async (int wiNumber, LabelRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService, CancellationToken ct) =>
{
    var wi = await wiService.GetAsync(wiNumber, ct);
    if (wi is null) return Results.NotFound();
    var labels = wi.Labels
        .Where(l => !l.Equals(req.Label, StringComparison.OrdinalIgnoreCase))
        .ToList();
    await wiService.UpdateAsync(wiNumber, labels: labels, ct: ct);
    return Results.Ok(new { wiNumber, removed = req.Label });
});

// PR and work item state management
labelApi.MapPost("/prs/{prNumber:int}/close", async (int prNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    await prService.CloseAsync(prNumber, ct);
    return Results.Ok(new { prNumber, state = "closed" });
});

labelApi.MapPost("/prs/{prNumber:int}/merge", async (int prNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    await prService.MergeAsync(prNumber, $"Merged PR #{prNumber} via admin API", ct);
    return Results.Ok(new { prNumber, state = "merged" });
});

labelApi.MapPost("/work-items/{wiNumber:int}/close", async (int wiNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService, CancellationToken ct) =>
{
    await wiService.UpdateAsync(wiNumber, state: "closed", ct: ct);
    return Results.Ok(new { wiNumber, state = "closed" });
});

labelApi.MapPost("/work-items/{wiNumber:int}/reopen", async (int wiNumber,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService, CancellationToken ct) =>
{
    await wiService.UpdateAsync(wiNumber, state: "open", ct: ct);
    return Results.Ok(new { wiNumber, state = "open" });
});

// Admin: create a PR record (for DB recovery after checkpoint restore)
labelApi.MapPost("/prs/create", async (CreatePrRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    var pr = await prService.CreateAsync(req.Title, req.Body ?? "", req.HeadBranch, req.BaseBranch,
        req.Labels ?? Array.Empty<string>(), ct);
    return Results.Ok(new { pr.Number, pr.Title, pr.State });
});

// Admin: force PR state (for DB recovery — bypasses git merge/close logic)
labelApi.MapPost("/prs/{prNumber:int}/set-state", async (int prNumber, SetStateRequest req,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService, CancellationToken ct) =>
{
    await prService.SetStateAsync(prNumber, req.State, ct);
    return Results.Ok(new { prNumber, state = req.State });
});

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

runsApi.MapPost("/start-project", async (RunCoordinator coordinator, IHostApplicationLifetime lifetime, HttpContext httpContext, CancellationToken ct) =>
{
    try
    {
        var forceRestart = httpContext.Request.Query.ContainsKey("force");
        var run = await coordinator.StartProjectAsync(ct, forceRestart: forceRestart);
        // Use the application stopping token (not the HTTP request token) for background agent work
        var appToken = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(appToken); }
            catch (Exception ex)
            {
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, appToken);
        return Results.Ok(new { run, message = "Project run started" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

runsApi.MapPost("/start-feature/{featureId}", async (string featureId, RunCoordinator coordinator, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.StartFeatureAsync(featureId, ct);
        var appToken = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(appToken); }
            catch (Exception ex)
            {
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, appToken);
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
    return Results.Ok(new { message = "Run paused" });
});

runsApi.MapPost("/cancel", async (RunCoordinator coordinator, CancellationToken ct) =>
{
    await coordinator.CancelRunAsync(ct);
    return Results.Ok(new { message = "Run cancelled" });
});

runsApi.MapPost("/resume", async (RunCoordinator coordinator, AgentSpawnManager spawnManager, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.ResumeAsync(ct);
        // Spawn agents in background (same path as startup)
        var appToken = lifetime.ApplicationStopping;
        _ = Task.Run(async () => await coordinator.SpawnAgentsForRunAsync(appToken), appToken);
        return Results.Ok(new { run, message = "Run resumed" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
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

// ── Develop Wizard API ──
var developApi = app.MapGroup("/api/develop").WithTags("Develop");

developApi.MapGet("/settings", async (DevelopSettingsService svc, CancellationToken ct) =>
    Results.Ok(await svc.LoadAsync(ct)));

developApi.MapPost("/settings", async (DevelopSettings settings, DevelopSettingsService svc, CancellationToken ct) =>
{
    await svc.SaveAsync(settings, ct);
    return Results.Ok();
});

developApi.MapPost("/validate", async (DevelopSettingsService svc, CancellationToken ct) =>
{
    var settings = await svc.LoadAsync(ct);
    var isValid = !string.IsNullOrWhiteSpace(settings.Description);
    return Results.Ok(new { valid = isValid, message = isValid ? "Settings valid" : "Description is required" });
});

developApi.MapPost("/repo/create", async (HttpContext ctx, IRepositoryManagementService repoSvc, CancellationToken ct) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RepoCreateRequest>(ct);
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest("Repository name is required");
    var result = await repoSvc.CreateRepositoryAsync(body.Name, isPrivate: true, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

developApi.MapPost("/start", async (DevelopSettingsService settingsSvc, CancellationToken ct) =>
{
    var settings = await settingsSvc.LoadAsync(ct);
    if (string.IsNullOrWhiteSpace(settings.Description))
        return Results.BadRequest(new { error = "Description is required to start" });
    return Results.Ok(new { started = true, description = settings.Description });
});

// Diagnostic: trigger the full StartProjectAsync flow and return detailed error
developApi.MapPost("/start-project", async (
    VirtualDevTeam.Orchestrator.RunCoordinator coordinator,
    IHostApplicationLifetime lifetime,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var run = await coordinator.StartProjectAsync(ct);
        // Spawn agents in the background using the app-scoped token (not the HTTP request token)
        var appToken = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try { await coordinator.SpawnAgentsForRunAsync(appToken); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent spawn failed after develop/start-project");
                coordinator.FailRunAsync($"Agent spawn failed: {ex.Message}").GetAwaiter().GetResult();
            }
        }, appToken);
        return Results.Ok(new { success = true, runId = run.RunId, repo = run.Repo });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "start-project diagnostic endpoint failed");
        return Results.BadRequest(new { error = ex.Message, type = ex.GetType().Name, stack = ex.StackTrace });
    }
});

developApi.MapPost("/clarify", async (
    DevelopSettingsService settingsSvc,
    CopilotCliProcessManager cliManager,
    IPromptTemplateService promptService,
    IOptions<VirtualDevTeamConfig> config,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var settings = await settingsSvc.LoadAsync(ct);
    if (string.IsNullOrWhiteSpace(settings.Description))
        return Results.BadRequest(new { error = "Description is required to generate clarifying questions" });

    if (!cliManager.IsAvailable)
        return Results.BadRequest(new { error = "Copilot CLI is not available. You can skip this step." });

    var techContext = string.IsNullOrWhiteSpace(settings.TechStack)
        ? ""
        : $"\n\nSpecified tech stack: {settings.TechStack}";

    string prompt;
    try
    {
        var rendered = await promptService.RenderAsync("wizard/clarifying-questions", new Dictionary<string, string>
        {
            ["description"] = settings.Description,
            ["techContext"] = techContext,
        });
        prompt = !string.IsNullOrWhiteSpace(rendered)
            ? rendered
            : BuildClarifyPromptFallback(settings.Description, techContext);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Develop API: failed to render clarifying question template; using fallback prompt");
        prompt = BuildClarifyPromptFallback(settings.Description, techContext);
    }

    var result = await ExecuteDevelopWizardCliPromptAsync(cliManager, config, prompt, null, ct);
    if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Output))
        return Results.BadRequest(new { error = result.Error ?? "Failed to generate clarifying questions." });

    var textOutput = CliOutputParser.ParseJsonOutput(result.Output) ?? result.Output;
    var questions = ParseNumberedQuestions(textOutput);
    if (questions.Count == 0)
        questions = ParseNumberedQuestions(result.Output);

    return Results.Ok(questions.Select(q => new DevelopClarifyQuestionDto(q.Question, q.ProposedAnswer)).ToList());
});

developApi.MapPost("/clarify/save", async (
    DevelopClarifySaveRequest request,
    DevelopSettingsService settingsSvc,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceHash))
        return Results.BadRequest(new { error = "sourceHash is required" });

    var settings = await settingsSvc.LoadAsync(ct);
    var existing = settings.ClarifyingAnswers
        .Where(qa => !string.IsNullOrWhiteSpace(qa.Question))
        .GroupBy(qa => qa.Question, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    settings.ClarifyingAnswers = (request.Answers ?? Array.Empty<DevelopClarifyAnswerDto>())
        .Where(answer => !string.IsNullOrWhiteSpace(answer.Question))
        .Select(answer =>
        {
            existing.TryGetValue(answer.Question, out var prior);
            return new ClarifyingQA
            {
                Question = answer.Question.Trim(),
                Answer = string.IsNullOrWhiteSpace(answer.Answer) ? null : answer.Answer.Trim(),
                ProposedAnswer = prior?.ProposedAnswer,
                Iteration = prior?.Iteration ?? 1,
            };
        })
        .ToList();
    settings.ClarifyingSourceHash = request.SourceHash.Trim();
    await settingsSvc.SaveAsync(settings, ct);
    return Results.Ok(new { saved = settings.ClarifyingAnswers.Count });
});

developApi.MapPost("/scenarios", async (
    DevelopSettingsService settingsSvc,
    CopilotCliProcessManager cliManager,
    IPromptTemplateService promptService,
    IOptions<VirtualDevTeamConfig> config,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var settings = await settingsSvc.LoadAsync(ct);
    if (string.IsNullOrWhiteSpace(settings.Description))
        return Results.BadRequest(new { error = "Description is required to generate scenarios" });

    if (!cliManager.IsAvailable)
        return Results.BadRequest(new { error = "Copilot CLI is not available. You can skip this step." });

    var projectDescription = BuildScenarioProjectDescription(settings);
    var projectName = InferProjectName(settings);
    var qaPairsText = FormatQaPairs(settings.ClarifyingAnswers);

    string prompt;
    try
    {
        var rendered = await promptService.RenderAsync("wizard/scenario-generation", new Dictionary<string, string>
        {
            ["project_name"] = projectName,
            ["project_description"] = projectDescription,
            ["clarifying_qa_pairs"] = qaPairsText,
        });
        prompt = !string.IsNullOrWhiteSpace(rendered)
            ? rendered
            : BuildScenarioPromptFallback(projectName, projectDescription, qaPairsText);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Develop API: failed to render scenario generation template; using fallback prompt");
        prompt = BuildScenarioPromptFallback(projectName, projectDescription, qaPairsText);
    }

    var scenarios = await TryGenerateScenariosAsync(cliManager, config, logger, prompt, ct);
    return Results.Ok(scenarios.Select(s => new DevelopScenarioDto(
        s.Id,
        s.Title,
        s.Trigger,
        s.Actor,
        s.Trigger,
        s.JourneyKind.ToString(),
        s.Steps.ToList(),
        s.ExpectedTerminalState.ToList(),
        s.ExpectedTerminalState.ToList(),
        s.SubsystemsInvolved.ToList(),
        s.Priority.ToString(),
        s.Status.ToString())).ToList());
});

developApi.MapPost("/scenarios/save", async (
    DevelopScenarioSaveRequest request,
    DevelopSettingsService settingsSvc,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceHash))
        return Results.BadRequest(new { error = "sourceHash is required" });

    var settings = await settingsSvc.LoadAsync(ct);
    settings.GeneratedScenarios = (request.Scenarios ?? Array.Empty<DevelopScenarioDto>())
        .Where(scenario => !string.IsNullOrWhiteSpace(scenario.Title))
        .Select((scenario, index) => new PersistedScenario
        {
            Id = string.IsNullOrWhiteSpace(scenario.Id) ? $"S{index + 1:00}" : scenario.Id.Trim(),
            Title = scenario.Title.Trim(),
            JourneyKind = NormalizeJourneyKind(scenario.JourneyKind),
            Priority = NormalizePriority(scenario.Priority),
            Status = NormalizeStatus(scenario.Status),
            Actor = string.IsNullOrWhiteSpace(scenario.Actor) ? "" : scenario.Actor.Trim(),
            Trigger = !string.IsNullOrWhiteSpace(scenario.Trigger)
                ? scenario.Trigger.Trim()
                : scenario.Description?.Trim() ?? "",
            Steps = NormalizeStringList(scenario.Steps),
            ExpectedTerminalState = NormalizeStringList(scenario.ExpectedTerminalState ?? scenario.ExpectedOutcome),
            SubsystemsInvolved = NormalizeStringList(scenario.SubsystemsInvolved),
        })
        .ToList();
    settings.ScenarioSourceHash = request.SourceHash.Trim();
    await settingsSvc.SaveAsync(settings, ct);
    return Results.Ok(new { saved = settings.GeneratedScenarios.Count });
});

static async Task<CopilotCliResult> ExecuteDevelopWizardCliPromptAsync(
    CopilotCliProcessManager cliManager,
    IOptions<VirtualDevTeamConfig> config,
    string prompt,
    string? modelOverride,
    CancellationToken ct)
{
    var workspaceRoot = config.Value.Workspace?.RootPath;
    if (string.IsNullOrWhiteSpace(workspaceRoot))
        workspaceRoot = Path.Combine(AppContext.BaseDirectory, ".agents");

    var scratchDir = Path.Combine(workspaceRoot, ".wizard");
    Directory.CreateDirectory(scratchDir);

    var options = new CopilotCliRequestOptions
    {
        Pool = CopilotCliPool.Agentic,
        AllowAll = true,
        CloseStdinAfterPrompt = true,
        WorkingDirectory = scratchDir,
        WatchdogMode = CopilotCliWatchdogMode.Agentic,
        ModelOverride = modelOverride,
    };

    try
    {
        var result = await cliManager.ExecuteAgenticSessionAsync(prompt, options, ct);
        return result.Succeeded
            ? CopilotCliResult.Success(result.LogBuffer ?? "", 0)
            : CopilotCliResult.Failure(result.ErrorMessage ?? "Agentic session failed");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return CopilotCliResult.Failure($"Exception: {ex.Message}");
    }
}

static async Task<IReadOnlyList<Scenario>> TryGenerateScenariosAsync(
    CopilotCliProcessManager cliManager,
    IOptions<VirtualDevTeamConfig> config,
    ILogger logger,
    string prompt,
    CancellationToken ct)
{
    var primary = await TryGenerateScenariosOnceAsync(cliManager, config, logger, prompt, "primary", ct);
    if (primary.Count > 0)
        return primary;

    var retryPrompt = prompt +
        "\n\nIMPORTANT: Your previous response could not be parsed. " +
        "You MUST respond with ONLY valid YAML. No markdown code fences (```), no preamble text, and no explanation. " +
        "Start your response with the literal text 'project_archetype:' on the first line. " +
        "The 'scenarios:' key must contain a YAML list of scenario objects.";

    return await TryGenerateScenariosOnceAsync(cliManager, config, logger, retryPrompt, "retry", ct);
}

static async Task<IReadOnlyList<Scenario>> TryGenerateScenariosOnceAsync(
    CopilotCliProcessManager cliManager,
    IOptions<VirtualDevTeamConfig> config,
    ILogger logger,
    string prompt,
    string attemptLabel,
    CancellationToken ct)
{
    var result = await ExecuteDevelopWizardCliPromptAsync(cliManager, config, prompt, null, ct);
    if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Output))
    {
        logger.LogWarning("Develop API: scenario generation failed ({Attempt}): {Error}", attemptLabel, result.Error ?? "empty output");
        return Array.Empty<Scenario>();
    }

    var textOutput = CliOutputParser.ParseJsonOutput(result.Output) ?? result.Output;
    try
    {
        var scenarios = ScenarioYamlExtractor.ExtractFromYamlString(textOutput, logger);
        if (scenarios.Count == 0)
            logger.LogWarning("Develop API: scenario generation produced 0 scenarios ({Attempt})", attemptLabel);
        return scenarios;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Develop API: failed to parse scenario output ({Attempt})", attemptLabel);
        return Array.Empty<Scenario>();
    }
}

static string BuildClarifyPromptFallback(string description, string techContext) => $@"You are a senior technical product manager. A user has described a software project they want built. Analyze the description and generate clarifying questions that would help reduce ambiguity and improve the quality of the resulting specification.

Rules:
- Generate questions where the answer would materially affect architecture, scope, or implementation decisions
- Do NOT ask questions that are already clearly answered in the description
- Short descriptions (under 5 sentences) are inherently ambiguous — generate at LEAST 5 questions for them
- Maximum 10 questions
- Each question should be concise (1-2 sentences)
- For each question, also provide your best proposed answer based on the description and common best practices
- Focus on: scope boundaries, target audience, user roles, data requirements, integration points, specific features expected, design/UX preferences, performance expectations, deployment environment, and key behavioral decisions
- Output ONLY a numbered list in format: ""1. Question text | Proposed answer text"" (pipe-delimited, question first, then proposed answer)
- NEVER output an empty response — even well-described projects have decisions worth clarifying

Project description:
{description}{techContext}";

static List<(string Question, string? ProposedAnswer)> ParseNumberedQuestions(string output)
{
    var questions = new List<(string Question, string? ProposedAnswer)>();
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = line.Trim().Replace("**", "").Replace("__", "");
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:\d+[\.\)\:]|[-•\*])\s*(.+)$");
        if (!match.Success)
            continue;

        var content = match.Groups[1].Value.Trim();
        if (content.Length <= 10)
            continue;

        var pipeIndex = content.IndexOf('|');
        if (pipeIndex > 0 && pipeIndex < content.Length - 1)
        {
            var question = content[..pipeIndex].Trim();
            var proposed = content[(pipeIndex + 1)..].Trim();
            if (question.Length > 5 && proposed.Length > 3)
            {
                questions.Add((question, proposed));
                continue;
            }
        }

        questions.Add((content, null));
    }

    return questions.Take(10).ToList();
}

static string BuildScenarioProjectDescription(DevelopSettings settings)
{
    if (string.IsNullOrWhiteSpace(settings.TechStack))
        return settings.Description;

    return $"{settings.Description}\n\nSpecified tech stack: {settings.TechStack}";
}

static string InferProjectName(DevelopSettings settings)
{
    if (string.Equals(settings.Platform, "GitHub", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(settings.GitHub.Repo))
    {
        var parts = settings.GitHub.Repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
            return parts[^1];
    }

    if (string.Equals(settings.Platform, "AzureDevOps", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(settings.AzureDevOps.Repository))
    {
        return settings.AzureDevOps.Repository;
    }

    var first = settings.Description.Trim().Split(['\n', '\r', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? settings.Description;
    return first.Length > 60 ? first[..60].TrimEnd() + "…" : first.Trim();
}

static string FormatQaPairs(IReadOnlyList<ClarifyingQA> pairs)
{
    if (pairs.Count == 0)
        return "(none)";

    return string.Join("\n", pairs
        .Where(qa => !string.IsNullOrWhiteSpace(qa.Question))
        .Select(qa => $"Q: {qa.Question}\nA: {(string.IsNullOrWhiteSpace(qa.Answer) ? "(not answered)" : qa.Answer)}"));
}

static string BuildScenarioPromptFallback(string projectName, string description, string qaPairs) =>
    $$"""
    You are a senior product analyst generating behavioral scenarios for a software project.
    Your response will be parsed directly as YAML — output ONLY a YAML document:
    no preamble, no markdown code fences, no explanation.
    Your response must begin with `project_archetype:`.

    Project name: {{projectName}}
    Project description:
    {{description}}

    Clarifying Q&A pairs:
    {{qaPairs}}

    Generate 5-15 scenarios. Each scenario must have: id (S01, S02...), title, journey_kind,
    actor, trigger, preconditions, steps, expected_terminal_state, observation_surfaces,
    subsystems_involved, priority (critical/important/nice-to-have), status (always proposed).
    """;

static List<string> NormalizeStringList(IReadOnlyList<string>? values) =>
    (values ?? Array.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToList();

static string NormalizeJourneyKind(string? value) => NormalizeToken(value) switch
{
    "apicall" => JourneyKind.ApiCall.ToString(),
    "scheduledjob" => JourneyKind.ScheduledJob.ToString(),
    "eventarrival" => JourneyKind.EventArrival.ToString(),
    "webhook" => JourneyKind.Webhook.ToString(),
    "messageconsume" => JourneyKind.MessageConsume.ToString(),
    "cliinvocation" => JourneyKind.CliInvocation.ToString(),
    "systeminitiated" => JourneyKind.SystemInitiated.ToString(),
    "datapipeline" => JourneyKind.DataPipeline.ToString(),
    _ => JourneyKind.UiInteraction.ToString(),
};

static string NormalizePriority(string? value) => NormalizeToken(value) switch
{
    "critical" => ScenarioPriority.Critical.ToString(),
    "nicetohave" => ScenarioPriority.NiceToHave.ToString(),
    _ => ScenarioPriority.Important.ToString(),
};

static string NormalizeStatus(string? value) => NormalizeToken(value) switch
{
    "approved" => ScenarioStatus.Approved.ToString(),
    "edited" => ScenarioStatus.Edited.ToString(),
    "rejected" => ScenarioStatus.Rejected.ToString(),
    _ => ScenarioStatus.Proposed.ToString(),
};

static string NormalizeToken(string? value) => string.Concat((value ?? string.Empty)
    .Where(char.IsLetterOrDigit))
    .ToLowerInvariant();

// Diagnostic: inspect GitHubService state
developApi.MapGet("/diag", (IGitHubService ghSvc, IDevPlatformAuthProvider authProvider, IOptions<VirtualDevTeamConfig> config) =>
{
    var gs = ghSvc as VirtualDevTeam.Core.GitHub.GitHubService;
    return Results.Ok(new
    {
        gitHubService = new
        {
            repositoryFullName = ghSvc.RepositoryFullName,
            isConfigured = ghSvc.IsConfigured,
            tokenPrefix = gs != null ? (gs.HasTokenChanged("") ? "has-token" : "empty") : "unknown"
        },
        authProvider = new
        {
            type = authProvider.GetType().Name,
            scheme = authProvider.AuthScheme,
            requiresRefresh = authProvider.RequiresRefresh
        },
        config = new
        {
            repo = config.Value.Project?.GitHubRepo,
            tokenLength = config.Value.Project?.GitHubToken?.Length ?? 0,
            tokenPrefix = (config.Value.Project?.GitHubToken ?? "").Length >= 8
                ? config.Value.Project.GitHubToken[..8] + "..."
                : "(empty)",
            authMethod = config.Value.DevPlatform?.AuthMethod.ToString(),
            platform = config.Value.DevPlatform?.Platform.ToString()
        }
    });
});

developApi.MapGet("/work-items/search", async (string q, IWorkItemSearchService searchSvc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<WorkItemSearchResult>());
    var results = await searchSvc.SearchAsync(q, maxResults: 15, ct);
    return Results.Ok(results);
});

// === Health Snapshot API ===
// Single-call equivalent of the §2 monitoring table from docs/MonitoringLoops.md.
// An agent walking the monitoring loop can hit this once per loop instead of
// hitting 8 separate endpoints. Computed eagerly (not cached) — runs in <50ms
// because the underlying state is in-memory.
app.MapGet("/api/health-snapshot", (
    VirtualDevTeam.Orchestrator.RunCoordinator coordinator,
    VirtualDevTeam.Orchestrator.AgentRegistry registry,
    VirtualDevTeam.Orchestrator.WorkflowStateMachine workflow,
    VirtualDevTeam.Core.Strategies.CandidateStateStore strategyStore,
    VirtualDevTeam.Core.AI.ActiveLlmCallTracker llmTracker,
    VirtualDevTeam.Core.AI.AgentUsageTracker usage,
    VirtualDevTeam.Core.Merging.IMergeCoordinator mergeCoordinator) =>
{
    var run = coordinator.ActiveRun;
    var agents = registry.GetAllAgents();
    var statusCounts = agents
        .GroupBy(a => a.Status.ToString())
        .ToDictionary(g => g.Key, g => g.Count());

    var active = strategyStore.GetActiveTasks();
    var recent = strategyStore.GetRecentTasks(10);
    var lastRecent = recent.FirstOrDefault();
    var failingCandidates = active
        .SelectMany(t => t.Candidates.Values
            .Where(c => c.Succeeded == false || (c.State == VirtualDevTeam.Core.Strategies.CandidateState.Completed && c.Survived == false))
            .Select(c => new { task = t.TaskId, strategy = c.StrategyId, reason = c.FailureReason }))
        .ToList();

    var llmCalls = llmTracker.GetAllActiveCalls();
    var llmInFlight = llmCalls.Select(kv => new
    {
        agentId = kv.Key,
        model = kv.Value.ModelName,
        context = kv.Value.Context,
        elapsedSec = (DateTime.UtcNow - kv.Value.StartedAt).TotalSeconds,
    }).ToList();

    return Results.Ok(new
    {
        timestamp = DateTimeOffset.UtcNow,
        run = run is null ? null : new { runId = run.RunId, repo = run.Repo, startedAt = run.StartedAt },
        phase = workflow.CurrentPhase.ToString(),
        agents = new
        {
            total = agents.Count,
            byStatus = statusCounts,
        },
        strategies = new
        {
            activeTasks = active.Count,
            recentTasks = recent.Count,
            lastWinner = lastRecent is null ? null : new
            {
                taskId = lastRecent.TaskId,
                winner = lastRecent.WinnerStrategyId,
                tieBreak = lastRecent.TieBreakReason,
                evalSec = lastRecent.EvaluationElapsedSec,
                prNumber = lastRecent.PrNumber,
            },
            failingCandidates = failingCandidates,
        },
        llm = new
        {
            inFlight = llmCalls.Count,
            calls = llmInFlight,
        },
        usage = new
        {
            totalCost = usage.GetTotalCost(),
            strategyCost = usage.GetTotalStrategyCost(),
        },
        mergeQueue = new
        {
            pending = mergeCoordinator.GetStatus().PendingCount,
            activePr = mergeCoordinator.GetStatus().ActivePrNumber,
            activeAgent = mergeCoordinator.GetStatus().ActiveAgentId,
            activeDurationSec = mergeCoordinator.GetStatus().ActiveDuration?.TotalSeconds,
        },
    });
}).WithTags("Health");

app.MapPost("/api/pipeline/stories/clarify", async (
    StoryClarifyRequest req,
    IPromptTemplateService promptService,
    IChatCompletionRunner chatRunner,
    IOptions<VirtualDevTeamConfig> config,
    Microsoft.Extensions.Logging.ILogger<Program> logger,
    CancellationToken ct) =>
{
    var validationErrors = ValidateStoryDraft(req.Title, req.Description, req.Wave);
    if (validationErrors.Count > 0)
        return Results.ValidationProblem(validationErrors);

    try
    {
        var storyContext = BuildStoryClarifyContext(req.Title, req.Description, req.AcceptanceCriteria, req.Wave, req.Complexity, req.DependsOnIssueNumbers);
        var rendered = await promptService.RenderAsync("wizard/clarifying-questions", new Dictionary<string, string>
        {
            ["description"] = storyContext,
            ["techContext"] = string.Empty
        });

        var prompt = !string.IsNullOrWhiteSpace(rendered)
            ? rendered
            : BuildStoryClarifyFallbackPrompt(storyContext);

        var output = await chatRunner.InvokeAsync(
            "You are a senior technical product manager. Follow the user's instructions exactly and return only the requested numbered list.",
            prompt,
            config.Value.Agents.ProgramManager.ModelTier,
            "new-story-wizard",
            ct);

        return Results.Ok(new StoryClarifyResponse(ParseClarifyingQuestions(output)));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to generate clarifying questions for new story '{Title}'", req.Title);
        return Results.Problem("Failed to generate clarifying questions.");
    }
});

app.MapPost("/api/pipeline/stories", async (
    CreateStoryRequest req,
    IWorkItemService workItems,
    DashboardDataService dashboard,
    Microsoft.Extensions.Logging.ILogger<Program> logger,
    CancellationToken ct) =>
{
    var validationErrors = ValidateStoryDraft(req.Title, req.Description, req.Wave);
    if (validationErrors.Count > 0)
        return Results.ValidationProblem(validationErrors);

    try
    {
        var normalizedWave = NormalizeStoryWave(req.Wave);
        var normalizedComplexity = NormalizeStoryComplexity(req.Complexity);
        var body = BuildStoryBody(req, normalizedWave, normalizedComplexity);
        var validatedBody = IssueBodyValidator.ValidateAndClean(body, req.Title, logger);
        if (validatedBody is null)
            return Results.Problem("Generated story body was invalid.");

        // Hybrid semantics: keep enhancement for ADO User Story mapping while also
        // tagging the item as an engineering task so it shows up in wave-based views
        // and is actionable by the engineering pipeline.
        var labels = new[]
        {
            IssueWorkflow.Labels.Enhancement,
            IssueWorkflow.Labels.EngineeringTask,
            $"complexity:{normalizedComplexity.ToLowerInvariant()}",
            "status:pending"
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var created = await workItems.CreateAsync(req.Title.Trim(), validatedBody, labels, ct);
        dashboard.InvalidatePlatformCaches();

        return Results.Ok(new CreateStoryResponse(
            created.Number,
            created.PlatformId,
            created.Title,
            created.Url,
            normalizedWave,
            labels,
            created.WorkItemType));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to create new story '{Title}'", req.Title);
        return Results.Problem(ex.Message);
    }
});

// === Pipeline Status API ===
// Single-call comprehensive pipeline status for CLI monitoring, FlowMonitor, and external tools.
// Returns agents, tasks (work items), linked PRs with lifecycle steps, dependencies, and summary.
// Designed to replace querying 5+ separate endpoints when checking pipeline state.
app.MapGet("/api/pipeline/status", async (
    DashboardDataService dashSvc,
    VirtualDevTeam.Orchestrator.WorkflowStateMachine workflow,
    VirtualDevTeam.Orchestrator.AgentRegistry registry,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IPullRequestService prService,
    VirtualDevTeam.Core.DevPlatform.Capabilities.IWorkItemService wiService,
    VirtualDevTeam.Core.AI.AgentUsageTracker usage,
    IOptions<VirtualDevTeamConfig> configOpts,
    Microsoft.Extensions.Logging.ILogger<Program> logger,
    CancellationToken ct) =>
{
    var config = configOpts.Value;
    var now = DateTimeOffset.UtcNow;

    // ── Agents ──
    var agentSnapshots = dashSvc.GetAllAgentSnapshots();
    var agentDtos = agentSnapshots.Select(a => new
    {
        id = a.Id,
        displayName = a.DisplayName,
        role = a.Role.ToString(),
        status = a.Status.ToString(),
        statusReason = a.StatusReason,
        since = a.LastStatusChange,
        durationSeconds = (now - new DateTimeOffset(a.LastStatusChange, TimeSpan.Zero)).TotalSeconds,
        currentPrNumber = a.CurrentPrNumber,
        currentPrUrl = a.CurrentPrUrl,
        currentTaskName = a.CurrentTaskName,
        currentStepName = a.CurrentStepName,
        activeModel = a.ActiveModel,
        aiCallElapsedSeconds = a.LlmCallElapsedTime?.TotalSeconds,
        specialty = a.Specialty,
        capabilities = a.Capabilities,
        estimatedCost = a.EstimatedCost,
        aiCalls = a.AiCalls,
    }).ToList();

    // ── Work items + PRs ──
    IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformWorkItem> allWorkItems;
    IReadOnlyList<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest> allPRs;
    try
    {
        var wiTask = wiService.ListAllAsync(ct);
        var prTask = prService.ListAllAsync(ct);
        await Task.WhenAll(wiTask, prTask);
        allWorkItems = await wiTask;
        allPRs = await prTask;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Pipeline status: platform query failed — returning partial data");
        allWorkItems = Array.Empty<VirtualDevTeam.Core.DevPlatform.Models.PlatformWorkItem>();
        allPRs = Array.Empty<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>();
    }

    // Build PR-to-issue lookup (from PR body "Closes #N" pattern)
    var prsByIssue = new Dictionary<int, List<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>>();
    foreach (var pr in allPRs)
    {
        var linked = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
        if (linked.HasValue)
        {
            if (!prsByIssue.ContainsKey(linked.Value))
                prsByIssue[linked.Value] = new();
            prsByIssue[linked.Value].Add(pr);
        }
    }

    // Wave/dependency regex (same as ProjectTimeline.razor)
    var waveRx = new System.Text.RegularExpressions.Regex(
        @"\*\*Wave:\*\*\s*(W\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    var depsRx = new System.Text.RegularExpressions.Regex(
        @"\*\*Depends On:\*\*\s*((?:#\d+(?:,\s*)?)+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    string ParseWave(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var m = waveRx.Match(body);
        return m.Success ? m.Groups[1].Value : "";
    }

    List<int> ParseDeps(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new();
        var m = depsRx.Match(body);
        if (!m.Success) return new();
        return System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"#(\d+)")
            .Select(dm => int.Parse(dm.Groups[1].Value))
            .ToList();
    }

    string? ParseTaskId(string title)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title, @"\[T-?(\w+)\]");
        return m.Success ? $"T-{m.Groups[1].Value}" : null;
    }

    // Determine the effective status of a work item from labels
    string DeriveTaskStatus(VirtualDevTeam.Core.DevPlatform.Models.PlatformWorkItem wi,
        List<VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest>? linkedPrs)
    {
        var labels = wi.Labels;
        if (labels.Any(l => l.Equals("status:done", StringComparison.OrdinalIgnoreCase))
            || wi.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
            return "done";
        if (labels.Any(l => l.Equals("status:blocked", StringComparison.OrdinalIgnoreCase)))
            return "blocked";
        if (labels.Any(l => l.Equals("status:in-progress", StringComparison.OrdinalIgnoreCase)
                         || l.Equals("in-progress", StringComparison.OrdinalIgnoreCase)))
            return "in-progress";
        if (linkedPrs?.Any(p => p.State.Equals("open", StringComparison.OrdinalIgnoreCase)) == true)
            return "in-progress";
        if (linkedPrs?.Any(p => p.IsMerged) == true)
            return "done";
        return "pending";
    }

    // Check if peer review agents exist (for lifecycle computation)
    var hasPeerReviewAgents = agentSnapshots.Count(a =>
        a.Role is AgentRole.SoftwareEngineer) > 1;

    // ── Build task DTOs with linked PRs + lifecycle ──
    var engTasks = allWorkItems
        .Where(wi => wi.Labels.Any(l => l.Equals("engineering-task", StringComparison.OrdinalIgnoreCase)))
        .OrderBy(wi => wi.Number)
        .ToList();

    var taskDtos = new List<object>();
    foreach (var wi in engTasks)
    {
        var linkedPrs = prsByIssue.GetValueOrDefault(wi.Number);
        var prDtos = new List<object>();

        if (linkedPrs is not null)
        {
            foreach (var pr in linkedPrs.OrderBy(p => p.Number))
            {
                // Compute lifecycle from labels only (no comment fetching = fast).
                // Labels give accurate stage status; comments only add timestamps/actors.
                var lifecycle = VirtualDevTeam.Core.Lifecycle.PrLifecycleCalculator.Compute(
                    pr, config, comments: null, hasPeerReviewAgents);

                var elapsed = pr.IsMerged && pr.MergedAt.HasValue
                    ? (pr.MergedAt.Value - pr.CreatedAt)
                    : (DateTime.UtcNow - pr.CreatedAt);

                prDtos.Add(new
                {
                    prNumber = pr.Number,
                    title = pr.Title,
                    state = pr.IsMerged ? "merged"
                        : pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open",
                    headBranch = pr.HeadBranch,
                    labels = pr.Labels,
                    createdAt = pr.CreatedAt,
                    mergedAt = pr.MergedAt,
                    elapsedMinutes = Math.Round(elapsed.TotalMinutes, 1),
                    lifecycle = new
                    {
                        stages = lifecycle.Stages.Select(s => new
                        {
                            id = s.Id,
                            name = s.Name,
                            icon = s.Icon,
                            status = s.Status.ToString(),
                            completedAt = s.CompletedAt,
                            actor = s.Actor,
                            skipReason = s.SkipReason,
                        }),
                        nextActor = lifecycle.NextRequiredActor,
                        missingRequirements = lifecycle.MissingRequirements,
                        isReadyForMerge = lifecycle.IsReadyForMerge,
                        isMerged = lifecycle.IsMerged,
                    },
                });
            }
        }

        var taskElapsed = wi.ClosedAt.HasValue
            ? (wi.ClosedAt.Value - wi.CreatedAt)
            : (DateTime.UtcNow - wi.CreatedAt);

        taskDtos.Add(new
        {
            issueNumber = wi.Number,
            title = wi.Title,
            taskId = ParseTaskId(wi.Title),
            status = DeriveTaskStatus(wi, linkedPrs),
            state = wi.State,
            labels = wi.Labels,
            wave = ParseWave(wi.Body),
            dependencies = ParseDeps(wi.Body),
            createdAt = wi.CreatedAt,
            elapsedMinutes = Math.Round(taskElapsed.TotalMinutes, 1),
            linkedPRs = prDtos,
        });
    }

    // ── Summary ──
    var statusGroups = taskDtos.Cast<dynamic>()
        .GroupBy(t => (string)t.status)
        .ToDictionary(g => g.Key, g => g.Count());

    var prStates = allPRs
        .GroupBy(p => p.IsMerged ? "merged"
            : p.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open")
        .ToDictionary(g => g.Key, g => g.Count());

    return Results.Ok(new
    {
        computedAt = now,
        phase = workflow.CurrentPhase.ToString(),
        agents = agentDtos,
        tasks = taskDtos,
        summary = new
        {
            totalTasks = engTasks.Count,
            tasksByStatus = statusGroups,
            totalPRs = allPRs.Count,
            prsByState = prStates,
            totalCost = usage.GetTotalCost(),
        },
    });
}).WithTags("Pipeline");

// === Preview Build API ===
var previewApi = app.MapGroup("/api/preview").WithTags("Preview");

previewApi.MapGet("/settings", async (VirtualDevTeam.Core.Preview.PreviewBuildService svc, CancellationToken ct) =>
    Results.Ok(await svc.LoadSettingsAsync(ct)));

previewApi.MapPost("/settings", async (VirtualDevTeam.Core.Preview.PreviewSettings settings,
    VirtualDevTeam.Core.Preview.PreviewBuildService svc, CancellationToken ct) =>
{
    await svc.SaveSettingsAsync(settings, ct);
    return Results.Ok();
});

previewApi.MapPost("/start", async (VirtualDevTeam.Core.Preview.PreviewBuildService svc, CancellationToken ct) =>
{
    var settings = await svc.LoadSettingsAsync(ct);
    if (string.IsNullOrWhiteSpace(settings.ClonePath))
        return Results.BadRequest(new { error = "Clone path must be configured first." });

    // Start in background — client streams output via SSE.
    // Use CancellationToken.None because the preview build must outlive the HTTP request.
    _ = Task.Run(async () =>
    {
        try { await svc.StartAsync(settings, CancellationToken.None); }
        catch { /* errors surfaced via state/output */ }
    });

    return Results.Ok(new { started = true });
});

previewApi.MapPost("/stop", (VirtualDevTeam.Core.Preview.PreviewBuildService svc) =>
{
    svc.Stop();
    return Results.Ok(new { stopped = true });
});

previewApi.MapGet("/status", async (VirtualDevTeam.Core.Preview.PreviewBuildService svc, CancellationToken ct) =>
{
    var settings = await svc.LoadSettingsAsync(ct);
    var status = await svc.GetStatusAsync(settings, ct);
    return Results.Ok(status);
});

// === Test Artifacts API ===
previewApi.MapGet("/artifacts", (VirtualDevTeam.Core.Preview.TestArtifactIndexService svc,
    string? pr, string? agent, bool? refresh) =>
{
    var artifacts = svc.GetArtifacts(forceRefresh: refresh == true);

    if (!string.IsNullOrWhiteSpace(pr))
        artifacts = svc.GetArtifactsByPR(pr);
    else if (!string.IsNullOrWhiteSpace(agent))
        artifacts = svc.GetArtifactsByAgent(agent);

    return Results.Ok(artifacts);
});

previewApi.MapGet("/artifacts/{id}", (string id, VirtualDevTeam.Core.Preview.TestArtifactIndexService svc) =>
{
    var artifact = svc.GetArtifactById(id);
    if (artifact is null || !File.Exists(artifact.FullPath))
        return Results.NotFound();

    var contentType = artifact.Type switch
    {
        VirtualDevTeam.Core.Preview.TestArtifactType.Screenshot =>
            artifact.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/png",
        VirtualDevTeam.Core.Preview.TestArtifactType.Video => "video/webm",
        VirtualDevTeam.Core.Preview.TestArtifactType.Trace => "application/zip",
        _ => "application/octet-stream"
    };

    return Results.File(artifact.FullPath, contentType, artifact.FileName);
});

if (!isHeadless)
{
    // SignalR hubs for real-time dashboard updates
    app.MapHub<AgentHub>("/agenthub");
    // T1.4: FlowMonitor live-log hub — drains FlowMonitorEventBus + broadcasts to subscribed clients.
    app.MapHub<VirtualDevTeam.Dashboard.Hubs.FlowMonitorHub>("/hubs/flowmonitor");
    app.MapHub<VirtualDevTeam.Dashboard.Hubs.AgentLogHub>("/hubs/agentlog");

    // Blazor Server components
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();
}

static Dictionary<string, string[]> ValidateStoryDraft(string title, string description, string wave)
{
    var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    if (string.IsNullOrWhiteSpace(title))
        errors["title"] = ["Title is required."];

    if (string.IsNullOrWhiteSpace(description))
        errors["description"] = ["Description is required."];

    var normalizedWave = NormalizeStoryWave(wave);
    if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedWave, @"^W\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        errors["wave"] = ["Wave must be in the format W0, W1, W2, etc."];

    return errors;
}

static string NormalizeStoryWave(string? wave)
{
    var normalized = (wave ?? string.Empty).Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
    return normalized.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? normalized : $"W{normalized}";
}

static string NormalizeStoryComplexity(string? complexity) =>
    complexity?.Trim().ToLowerInvariant() switch
    {
        "low" => "Low",
        "high" => "High",
        _ => "Medium"
    };

static string BuildStoryClarifyContext(
    string title,
    string description,
    string? acceptanceCriteria,
    string wave,
    string? complexity,
    IReadOnlyList<int>? dependsOnIssueNumbers)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Title: {title.Trim()}");
    sb.AppendLine($"Wave: {NormalizeStoryWave(wave)}");
    sb.AppendLine($"Complexity: {NormalizeStoryComplexity(complexity)}");
    if (dependsOnIssueNumbers is { Count: > 0 })
        sb.AppendLine($"Depends On: {string.Join(", ", dependsOnIssueNumbers.Distinct().Select(n => $"#{n}"))}");

    sb.AppendLine();
    sb.AppendLine("Description:");
    sb.AppendLine(description.Trim());

    if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
    {
        sb.AppendLine();
        sb.AppendLine("Acceptance Criteria:");
        sb.AppendLine(acceptanceCriteria.Trim());
    }

    return sb.ToString().Trim();
}

static string BuildStoryClarifyFallbackPrompt(string storyContext) => $@"You are a senior technical product manager. A user has described a software project they want built. Analyze the description and generate clarifying questions that would help reduce ambiguity and improve the quality of the resulting specification.

Rules:
- Generate questions where the answer would materially affect architecture, scope, or implementation decisions.
- Do NOT ask questions that are already clearly answered in the description.
- Short descriptions (under 5 sentences) are inherently ambiguous — generate at LEAST 5 questions for them.
- Maximum 10 questions.
- Each question should be concise (1-2 sentences).
- For each question, also provide your best proposed answer based on the description and common best practices.
- Output ONLY a numbered list in format: ""1. Question text | Proposed answer text"".
- NEVER output an empty response.

Project description:
{storyContext}";

static IReadOnlyList<StoryClarifyQuestion> ParseClarifyingQuestions(string output)
{
    if (string.IsNullOrWhiteSpace(output))
        return [];

    var questions = new List<StoryClarifyQuestion>();
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = line.Trim().Replace("**", string.Empty).Replace("__", string.Empty);
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:\d+[\.\)\:]|[-•\*])\s*(.+)$");
        if (!match.Success)
            continue;

        var content = match.Groups[1].Value.Trim();
        if (content.Length <= 10)
            continue;

        var pipeIndex = content.IndexOf('|');
        if (pipeIndex > 0 && pipeIndex < content.Length - 1)
        {
            var question = content[..pipeIndex].Trim();
            var proposed = content[(pipeIndex + 1)..].Trim();
            questions.Add(new StoryClarifyQuestion(question, string.IsNullOrWhiteSpace(proposed) ? null : proposed));
        }
        else
        {
            questions.Add(new StoryClarifyQuestion(content, null));
        }
    }

    return questions.Take(10).ToList();
}

static string BuildStoryBody(CreateStoryRequest req, string normalizedWave, string normalizedComplexity)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("## User Story");
    sb.AppendLine(req.Description.Trim());
    sb.AppendLine();
    sb.AppendLine("## Acceptance Criteria");
    if (string.IsNullOrWhiteSpace(req.AcceptanceCriteria))
        sb.AppendLine("- Confirm the implemented behavior matches this story before marking the work complete.");
    else
        sb.AppendLine(req.AcceptanceCriteria.Trim());

    var clarifications = req.Clarifications?
        .Where(c => !string.IsNullOrWhiteSpace(c.Question) && !string.IsNullOrWhiteSpace(c.Answer))
        .ToList();
    if (clarifications is { Count: > 0 })
    {
        sb.AppendLine();
        sb.AppendLine("## Clarifying Decisions");
        for (var i = 0; i < clarifications.Count; i++)
        {
            var clarification = clarifications[i];
            sb.AppendLine($"{i + 1}. **{clarification.Question.Trim()}**");
            sb.AppendLine($"   - Answer: {clarification.Answer!.Trim()}");
        }
    }

    sb.AppendLine();
    sb.AppendLine("## Metadata");
    sb.AppendLine($"- **Complexity:** {normalizedComplexity}");
    sb.AppendLine($"- **Wave:** {normalizedWave}");
    if (req.DependsOnIssueNumbers is { Count: > 0 })
        sb.AppendLine($"- **Depends On:** {string.Join(", ", req.DependsOnIssueNumbers.Distinct().Select(n => $"#{n}"))}");

    sb.AppendLine();
    sb.AppendLine("_Created by the New Story wizard._");
    return sb.ToString().Trim();
}

app.Run();

// Request DTOs for POST endpoints
record SetModelRequest(string ModelName);
record ChatRequest(string Message);
record ValidatePatRequest(string? Token, string? RepoFullName);
record CleanupExecuteRequest(string? Caveats);
record PromptSaveRequest(string? Content);
record RepoCreateRequest(string Name);
record DevelopClarifyQuestionDto(string Question, string? ProposedAnswer);
record DevelopClarifyAnswerDto(string Question, string? Answer);
record DevelopClarifySaveRequest(IReadOnlyList<DevelopClarifyAnswerDto>? Answers, string? SourceHash);
record DevelopScenarioDto(
    string? Id,
    string Title,
    string? Description,
    string? Actor,
    string? Trigger,
    string? JourneyKind,
    IReadOnlyList<string>? Steps,
    IReadOnlyList<string>? ExpectedOutcome,
    IReadOnlyList<string>? ExpectedTerminalState,
    IReadOnlyList<string>? SubsystemsInvolved,
    string? Priority,
    string? Status);
record DevelopScenarioSaveRequest(IReadOnlyList<DevelopScenarioDto>? Scenarios, string? SourceHash);
record FlowMonitorToggleRequest(string Kind, string Id, bool Enabled);
record FixRecommendationReworkRequest(string Feedback);
record RoleDescriptionRequest(string? Description);
record OperatorFeedbackRequest(string Feedback);

// DTOs for missing-work-proposals and flow-action-proposals endpoints (Approvals page)
record MissingWorkApprovePayload(string? Title, string? Body, IReadOnlyList<string>? Labels, string? Rationale);
record MissingWorkRejectPayload(string? Rationale);
record FlowActionApprovePayload(string? Rationale);
record FlowActionRejectPayload(string? Rationale);
record LabelRequest(string Label);
record CreatePrRequest(string Title, string? Body, string HeadBranch, string BaseBranch, string[]? Labels);
record SetStateRequest(string State);
record StoryClarifyRequest(string Title, string Description, string? AcceptanceCriteria, string Wave, string? Complexity, IReadOnlyList<int>? DependsOnIssueNumbers);
record StoryClarifyQuestion(string Question, string? ProposedAnswer);
record StoryClarifyResponse(IReadOnlyList<StoryClarifyQuestion> Questions);
record StoryClarification(string Question, string? Answer, string? ProposedAnswer);
record CreateStoryRequest(string Title, string Description, string? AcceptanceCriteria, string Wave, string? Complexity, IReadOnlyList<int>? DependsOnIssueNumbers, IReadOnlyList<StoryClarification>? Clarifications);
record CreateStoryResponse(int Number, long PlatformId, string Title, string Url, string Wave, IReadOnlyList<string> Labels, string WorkItemType);
