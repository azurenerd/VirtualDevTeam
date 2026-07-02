// NoMessyCodePlan Theme 4d: Program.cs split — runtime orchestration registration.
using Microsoft.Extensions.Options;
using VirtualDevTeam.Agents;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Runtime coordination layer: workspace runners (build / test / Playwright), agent service
/// bundles, candidate preview producers, the orchestrator (registry + workflow + spawn manager
/// + health monitor), the strategy framework, judges, the agent factory, and the main worker
/// that boots the team. Depends on <see cref="RunnerCoreServicesExtensions"/> and
/// <see cref="RunnerDevPlatformExtensions"/>.
/// </summary>
public static class RunnerOrchestrationExtensions
{
    public static IServiceCollection AddRunnerOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        // Config bindings consumed by the orchestrator + strategy framework.
        services.Configure<LimitsConfig>(
            configuration.GetSection("VirtualDevTeam:Limits"));

        // StrategyFrameworkConfig: use BindConfiguration(sectionPath) instead of
        // Configure<T>(section). The latter captures the IConfigurationSection at
        // registration time via NamedConfigureFromConfigurationOptions. In .NET 8,
        // the captured section's Bind() silently returns defaults when the same parent
        // section ("VirtualDevTeam") is also bound to VirtualDevTeamConfig — the child
        // section obtained from ConfigurationManager.GetSection("Parent:Child") at
        // registration time doesn't reliably enumerate its children during Bind().
        //
        // BindConfiguration resolves IConfiguration from DI at resolution time and calls
        // GetSection(path) fresh, sidestepping the stale-section capture. It also
        // registers a ConfigurationChangeTokenSource so IOptionsMonitor picks up changes.
        services.AddOptions<StrategyFrameworkConfig>()
            .BindConfiguration("VirtualDevTeam:StrategyFramework");

        // Workspace services— local-machine build + test + Playwright. Per-agent workspaces live
        // under <Workspace.RootPath>/<agentId>/<repoName>. PlaywrightHealthService runs at startup
        // to verify the playwright binary is available + warm the browser cache.
        services.AddSingleton<BuildRunner>();
        services.AddSingleton<TestRunner>();
        services.AddSingleton<AppLauncher>();
        services.AddSingleton<MediaRecorder>();
        services.AddSingleton<ApiSmokeRunner>();
        services.AddSingleton<PlaywrightRunner>();
        services.AddSingleton<IMediaCaptureService>(sp => sp.GetRequiredService<PlaywrightRunner>());
        services.AddHostedService<PlaywrightHealthService>();
        services.AddSingleton<TestStrategyAnalyzer>();
        services.AddSingleton<VirtualDevTeam.Core.Metrics.BuildTestMetrics>();

        // Agent dependency bundles — pre-built service bags so agent constructors stay clean.
        // DI is lazy, so registration order here doesn't matter for resolution.
        services.AddSingleton(sp => new VirtualDevTeam.Core.Agents.AgentCoreServices(
            messageBus: sp.GetRequiredService<IMessageBus>(),
            modelRegistry: sp.GetRequiredService<ModelRegistry>(),
            chatRunner: sp.GetRequiredService<VirtualDevTeam.Core.AI.IChatCompletionRunner>(),
            projectFiles: sp.GetRequiredService<ProjectFileManager>(),
            memoryStore: sp.GetRequiredService<AgentMemoryStore>(),
            gateCheck: sp.GetRequiredService<IGateCheckService>(),
            config: sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>(),
            promptService: sp.GetService<VirtualDevTeam.Core.Prompts.IPromptTemplateService>(),
            roleContextProvider: sp.GetService<VirtualDevTeam.Core.AI.RoleContextProvider>(),
            selfAssessment: sp.GetService<VirtualDevTeam.Core.Agents.Reasoning.SelfAssessmentService>(),
            reasoningLog: sp.GetService<VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog>(),
            taskTracker: sp.GetService<VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker>(),
            stateStore: sp.GetService<AgentStateStore>(),
            sharedCloneManager: sp.GetService<VirtualDevTeam.Core.Workspace.SharedCloneManager>()));
        services.AddSingleton(sp => new VirtualDevTeam.Core.Agents.AgentPlatformServices(
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
        services.AddSingleton(sp => new VirtualDevTeam.Core.Agents.AgentWorkspaceServices(
            buildRunner: sp.GetService<BuildRunner>(),
            testRunner: sp.GetService<TestRunner>(),
            playwrightRunner: sp.GetService<PlaywrightRunner>(),
            metrics: sp.GetService<VirtualDevTeam.Core.Metrics.BuildTestMetrics>()));

        // Strategy-framework candidate preview producers (chain orchestrated by CandidatePreviewService).
        // Producers are tried in priority order; the first non-null result wins.
        //   image-assets (Priority=10) — raster files in conventional asset dirs
        //   diagrams     (Priority=20) — Mermaid/PlantUML/SVG/Draw.io rendered to a contact sheet
        //   playwright   (Priority=100) — fallback running-app screenshot
        services.AddSingleton<VirtualDevTeam.Core.Strategies.Preview.ICandidatePreviewProducer,
            VirtualDevTeam.Core.Strategies.Preview.ImageAssetCandidatePreviewProducer>();
        services.AddSingleton<VirtualDevTeam.Core.Strategies.Preview.ICandidatePreviewProducer,
            VirtualDevTeam.Core.Strategies.Preview.DiagramCandidatePreviewProducer>();
        services.AddSingleton<VirtualDevTeam.Core.Strategies.Preview.ICandidatePreviewProducer,
            VirtualDevTeam.Core.Strategies.Preview.PlaywrightCandidatePreviewProducer>();
        services.AddSingleton<VirtualDevTeam.Core.Strategies.Preview.CandidatePreviewService>();

        // Orchestrator — registry, health monitor, deadlock detector, spawn manager, workflow.
        services.AddOrchestrator();

        // Strategy framework — candidate orchestrator, evaluator, judge contracts (real
        // judges below override the Null defaults from this call).
        services.AddStrategyFramework();
        services.AddStrategyDashboard();
        services.AddSingleton<VirtualDevTeam.Core.Strategies.IStrategyBroadcaster,
            VirtualDevTeam.Dashboard.Services.SignalRStrategyBroadcaster>();

        // Real implementations override the NullXxx defaults registered by AddStrategyFramework.
        services.AddSingleton<VirtualDevTeam.Core.Strategies.IBaselineCodeGenerator,
            VirtualDevTeam.Agents.AI.BaselineCodeGenerator>();
        services.AddSingleton<VirtualDevTeam.Agents.AI.LlmJudge>();
        services.AddSingleton<VirtualDevTeam.Core.Review.ICliReviewService,
            VirtualDevTeam.Agents.AI.CliReviewService>();
        // Judge selection: CLI-native (browses the worktree via shell tools) when enabled,
        // falls back to text-only LlmJudge otherwise.
        services.AddSingleton<VirtualDevTeam.Core.Strategies.ILlmJudge>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptionsMonitor<StrategyFrameworkConfig>>();
            if (cfg.CurrentValue.Evaluator.UseCliNativeJudge)
            {
                return new VirtualDevTeam.Agents.AI.CliNativeJudge(
                    sp.GetRequiredService<VirtualDevTeam.Core.Review.ICliReviewService>(),
                    sp.GetRequiredService<VirtualDevTeam.Agents.AI.LlmJudge>(),
                    sp.GetRequiredService<ILogger<VirtualDevTeam.Agents.AI.CliNativeJudge>>(),
                    cfg.CurrentValue.Evaluator.JudgeMaxRetries);
            }
            return sp.GetRequiredService<VirtualDevTeam.Agents.AI.LlmJudge>();
        });
        // Vision judge — scores candidate screenshots on visual quality.
        services.AddSingleton<VirtualDevTeam.Core.Strategies.IVisualJudge,
            VirtualDevTeam.Agents.AI.VisualJudge>();

        // Merge coordinator — serializes merge attempts to prevent N² thrash
        // when multiple SEs try to merge concurrently.
        services.AddSingleton<VirtualDevTeam.Core.Merging.IMergeCoordinator,
            VirtualDevTeam.Orchestrator.Merging.MergeCoordinator>();

        // Agent factory + main team-bootstrapping worker.
        services.AddSingleton<IAgentFactory, AgentFactory>();
        services.AddHostedService<VirtualDevTeamWorker>();

        // Hot-toggleable Test Engineer disable: listens to wizard config changes + spawns/stops
        // the TE agent without requiring a runner restart.
        services.AddHostedService<TestEngineerToggleHandler>();

        return services;
    }
}
