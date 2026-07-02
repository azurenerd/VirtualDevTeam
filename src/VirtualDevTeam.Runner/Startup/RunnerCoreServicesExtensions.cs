// NoMessyCodePlan Theme 4d: Program.cs split — service-registration extensions.
// IGitHubService is the legacy bridge being wrapped here; CS0618 suppression matches Program.cs.
#pragma warning disable CS0618
using VirtualDevTeam.Core.Agents.Playtest;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Foundation services every other module depends on: message bus, persistence stores,
/// AI tracking, image-gen auth+service, prompt templates, decision-gate scaffolding,
/// HTTP factory, the runner-scoped Win32 Job Object, and role-context customization.
/// Zero domain dependencies — safe to call first in the chain.
/// </summary>
public static class RunnerCoreServicesExtensions
{
    public static IServiceCollection AddRunnerCoreServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind the master VirtualDevTeamConfig section first — every other registration in the
        // chain depends on it being available via IOptions<VirtualDevTeamConfig>.
        services.Configure<VirtualDevTeamConfig>(
            configuration.GetSection("VirtualDevTeam"));

        // Resolve relative workspace paths (e.g., ".agents") against the git repo root.
        services.PostConfigure<VirtualDevTeamConfig>(config =>
        {
            config.Workspace.ResolveRootPath();

            // Resolve prompts BasePath for published/installed CLI builds.
            // When running from a published single-file exe, CWD is typically the user's
            // project directory — Path.GetFullPath("prompts") would resolve against CWD,
            // missing the prompts/ folder that ships next to the exe. Fall back to
            // AppContext.BaseDirectory (the exe's directory) when CWD resolution fails.
            var cwdResolved = Path.GetFullPath(config.Prompts.BasePath);
            if (!Directory.Exists(cwdResolved))
            {
                // First try the configured relative path under the exe directory
                var exeDirResolved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.Prompts.BasePath));
                if (Directory.Exists(exeDirResolved))
                {
                    config.Prompts.BasePath = exeDirResolved;
                }
                else
                {
                    // For published builds where BasePath is a dev-time relative path
                    // (e.g., "../../prompts"), fall back to "prompts" directly under the exe dir.
                    var exeDirDirect = Path.Combine(AppContext.BaseDirectory, "prompts");
                    if (Directory.Exists(exeDirDirect))
                        config.Prompts.BasePath = exeDirDirect;
                }
            }
        });

        // Merge develop-settings.json into VirtualDevTeamConfig as part of the options pipeline.
        // Registered as IPostConfigureOptions so it runs EVERY time the options framework builds
        // the snapshot (separately for IOptions, IOptionsMonitor, IOptionsSnapshot). Without this,
        // IOptionsMonitor.CurrentValue keeps returning the appsettings.json defaults (with
        // AzureOpenAIImage=null) — losing the wizard-provisioned endpoint + apikey + project repo.
        // Observed 2026-05-12: Artist agentic session saw ENDPOINT: '' because the runtime monitor
        // kept rebuilding from appsettings.json without seeing the wizard-saved settings.
        services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<VirtualDevTeamConfig>>(sp =>
            new DevelopSettingsPostConfigure(
                sp.GetService<ILoggerFactory>()?.CreateLogger<DevelopSettingsPostConfigure>()));

        // Message bus (in-process channels, bounded capacity).
        services.AddInProcessMessageBus();

        // Per-repo SQLite stores. The DB filename is namespaced by GitHub repo slug so cross-project
        // contamination is impossible. NOTE: the slug is read from the BUILD-TIME appsettings value;
        // wizard-set repos that override Project.GitHubRepo at runtime still target the slug captured here.
        var repoSlug = configuration["VirtualDevTeam:Project:GitHubRepo"]?.Replace('/', '_') ?? "default";
        var dbPath = $"virtualdevteam_{repoSlug}.db";
        services.AddSingleton(new AgentStateStore(dbPath));
        services.AddSingleton(new AgentMemoryStore(dbPath));

        // Centralized task lifecycle state machine — tracks engineering task state
        // with compare-and-set transitions and a persistent audit log.
        services.AddSingleton<VirtualDevTeam.Core.Lifecycle.TaskStateMachine>();

        // AI tracking — AgentUsageTracker wraps AgentStateStore so cost stats survive runner restarts.
        services.AddSingleton<VirtualDevTeam.Core.AI.AgentUsageTracker>(sp =>
            new VirtualDevTeam.Core.AI.AgentUsageTracker(sp.GetRequiredService<AgentStateStore>()));
        services.AddSingleton<VirtualDevTeam.Core.AI.ActiveLlmCallTracker>();
        services.AddSingleton<VirtualDevTeam.Core.AI.AgentCliLogService>();

        // Diagnostic services used by the agent chat panels + requirements snapshot.
        services.AddSingleton<VirtualDevTeam.Core.Diagnostics.RequirementsCache>();
        services.AddSingleton<VirtualDevTeam.Core.Diagnostics.AgentChatService>();

        // Semantic Kernel model registry — wraps Copilot CLI when enabled, API-key providers as fallback.
        services.AddSemanticKernelModels();
        services.AddSingleton<VirtualDevTeam.Core.AI.IChatCompletionRunner, VirtualDevTeam.Core.AI.ChatCompletionRunner>();

        // Image generation: REST endpoint auth (DefaultAzureCredential or ApiKey) + service for
        // operator-driven smoke tests on the Configuration page. Agent-side image gen uses the
        // same auth provider but issues REST calls directly from the agentic CLI session — see
        // CopilotCliProcessManager.ApplyImageGenEnvVars and prompts/_shared/image-gen-instructions.md.
        services.AddSingleton<VirtualDevTeam.Core.AI.IAzureImageAuthProvider, VirtualDevTeam.Core.AI.AzureImageAuthProvider>();
        services.AddSingleton<VirtualDevTeam.Core.AI.IImageGenerationService, VirtualDevTeam.Core.AI.ImageGenerationService>();

        // Runner-scoped Win32 Job Object — every long-lived child process the runner spawns
        // (Copilot CLI, Squad, MCP servers, candidate worktree dev servers) is assigned to this
        // job. When the runner exits, the OS atomically terminates the entire tree. Prevents the
        // orphan-node-process pile-up that consumed 14 GB of RAM on long sessions before this
        // was wired in.
        services.AddSingleton<VirtualDevTeam.Core.AI.RunnerProcessJob>();

        // Prompt template system — file-backed, watched for hot-reload during development.
        services.AddSingleton<VirtualDevTeam.Core.Prompts.IPromptTemplateService, VirtualDevTeam.Core.Prompts.PromptTemplateService>();
        services.AddHostedService<VirtualDevTeam.Core.Prompts.PromptFileWatcher>();

        // Agentic loop: self-assessment + reasoning observability (the Reasoning dashboard page reads this).
        services.AddSingleton<VirtualDevTeam.Core.Agents.Reasoning.IAgentReasoningLog, VirtualDevTeam.Core.Agents.Reasoning.AgentReasoningLog>();
        services.AddSingleton<VirtualDevTeam.Core.Agents.Reasoning.SelfAssessmentService>();

        // Per-agent task step tracking (timeline page) — keyed by agent id.
        services.AddSingleton<VirtualDevTeam.Core.Agents.Steps.IAgentTaskTracker, VirtualDevTeam.Core.Agents.Steps.AgentTaskTracker>();

        // Decision impact classification + clarification questions store (Approvals page reads these).
        services.AddSingleton<VirtualDevTeam.Core.Agents.Decisions.IDecisionLog, VirtualDevTeam.Core.Agents.Decisions.DecisionLog>();
        services.AddSingleton<VirtualDevTeam.Core.Agents.Decisions.DecisionGateService>();
        services.AddSingleton<VirtualDevTeam.Core.Agents.Decisions.PrePRClarificationStore>();

        // Flow Timeline — records pipeline milestones for the Flow Timeline page.
        services.AddSingleton<VirtualDevTeam.Core.HealthMonitor.FlowTimelineTracker>();

        // Shared Clone Manager — coordinates worktree creation for Worktree/InPlace modes.
        // In Clone mode (default), this is registered but unused.
        services.AddSingleton<VirtualDevTeam.Core.Workspace.SharedCloneManager>();

        // Service Context Resolver — routes build/test commands to per-service definitions.
        services.AddSingleton<VirtualDevTeam.Core.Workspace.ServiceContextResolver>();

        // PR Review Context Cache — shared between SE (writer) and PM/Architect/TE (readers)
        // to avoid redundant GitHub API calls for the same PR diff data.
        services.AddSingleton<VirtualDevTeam.Core.DevPlatform.PrReviewContextCache>();

        // Task claim coordination — prevents multiple engineers from racing on the same issue.
        services.AddSingleton<VirtualDevTeam.Core.Agents.ClaimedTaskRegistry>();

        // Role context customization: per-agent role descriptions, MCP servers, knowledge links.
        services.AddSingleton<VirtualDevTeam.Core.AI.RoleContextProvider>();

        // Scenario registry — wizard-approved scenarios → PMSpec prompt + sidecar sync.
        services.AddScenarios();

        // App Playtester — T-FINAL scenario-by-scenario behavioral verification.
        // Registers IAppPlaytester + 3 IPlaytestAdapter implementations (Web/Api/Cli)
        // + named HttpClient ("ApiPlaytestAdapter").
        services.AddPlaytester();

        // HttpClient factory — used by the dashboard's /platform/img proxy + image-gen REST + various adapters.
        services.AddHttpClient();

        // Pipeline checkpoints — captures/restores state at key milestones for quick recovery.
        services.AddSingleton<VirtualDevTeam.Core.Checkpoints.IPipelineCheckpointService,
                              VirtualDevTeam.Core.Checkpoints.PipelineCheckpointService>();
        services.AddHostedService<VirtualDevTeam.Core.Checkpoints.CheckpointAutoTrigger>();

        return services;
    }
}
