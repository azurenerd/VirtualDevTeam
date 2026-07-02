// NoMessyCodePlan Theme 4d: Program.cs split — DevPlatform (GitHub + ADO) registration.
// IGitHubService is the legacy bridge being wrapped here; CS0618 suppression matches Program.cs.
#pragma warning disable CS0618
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Pipeline;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Runner.Startup;

/// <summary>
/// Dev-platform abstraction: GitHub + Azure DevOps capability adapters, auth providers,
/// branch resolvers, PR/Issue/Review workflows, conflict detector/resolver. Must be
/// registered AFTER <see cref="RunnerCoreServicesExtensions"/> — it depends on the
/// HttpClient factory + config binding from there.
/// </summary>
public static class RunnerDevPlatformExtensions
{
    public static IServiceCollection AddRunnerDevPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        // Legacy GitHub integration. The capability layer wraps this; agents should NEVER
        // depend on IGitHubService directly (see "DevPlatform Abstraction" in copilot-instructions.md).
        services.AddGitHubIntegration();

        // Capability layer — IPullRequestService, IWorkItemService, IReviewService,
        // IRepositoryContentService, IBranchService, IPlatformHostContext. Bound from the
        // VirtualDevTeam:DevPlatform section (Platform=GitHub|AzureDevOps + auth config).
        services.Configure<DevPlatformConfig>(
            configuration.GetSection("VirtualDevTeam:DevPlatform"));

        // PostConfigure: sync UseLocalDevMode override from develop-settings.json into DevPlatformConfig.
        // DevelopSettingsPostConfigure sets VirtualDevTeamConfig.DevPlatform.Platform = Local,
        // but IOptions<DevPlatformConfig> is a separate binding that doesn't see that override.
        services.PostConfigure<DevPlatformConfig>(dpc =>
        {
            // Read develop-settings.json to check if Local Dev Platform is active.
            // Check CWD first (VDT CLI runs with CWD = project dir), then exe dir, then walk-up for dev builds.
            var settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "develop-settings.json");
            if (!File.Exists(settingsPath))
                settingsPath = Path.Combine(AppContext.BaseDirectory, "develop-settings.json");
            // Walk up from bin to project dir (dev builds: bin/Release/net8.0 → project root)
            if (!File.Exists(settingsPath))
                settingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "develop-settings.json");
            try
            {
                if (File.Exists(Path.GetFullPath(settingsPath)))
                {
                    var json = File.ReadAllText(Path.GetFullPath(settingsPath));
                    var ds = System.Text.Json.JsonSerializer.Deserialize<DevelopSettings>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (ds?.UseLocalDevMode == true)
                        dpc.Platform = DevPlatformType.Local;
                }
            }
            catch { /* non-fatal — appsettings defaults remain */ }
        });

        services.AddDevPlatform();

        // Run-scoped branch provider — single source of truth for "what's the working branch for
        // this run?" Agents resolve their branch through IRunBranchProvider so any agent rooted
        // off a feature branch sees a consistent target without per-call recomputation.
        services.AddSingleton<RunBranchProvider>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            return new RunBranchProvider(config.Project.DefaultBranch);
        });
        services.AddSingleton<IRunBranchProvider>(sp => sp.GetRequiredService<RunBranchProvider>());

        // Doc/file resolver used by PMSpec + Architecture readers + multi-doc agents.
        services.AddSingleton<ProjectFileManager>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            return new ProjectFileManager(
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<ILogger<ProjectFileManager>>(),
                sp.GetRequiredService<IRunBranchProvider>(),
                config.Project.DefaultBranch);
        });

        // Workflow services — PR review/merge/conflict + Issue lifecycle. Both consume the
        // capability layer above so they live HERE not in Orchestration.
        services.AddSingleton<ConflictDetector>(sp =>
            new ConflictDetector(
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<ILogger<ConflictDetector>>(),
                sp.GetRequiredService<IRunBranchProvider>()));
        services.AddSingleton<PullRequestWorkflow>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>().Value;
            return new PullRequestWorkflow(
                sp.GetRequiredService<IPullRequestService>(),
                sp.GetRequiredService<IRepositoryContentService>(),
                sp.GetRequiredService<IReviewService>(),
                sp.GetRequiredService<IBranchService>(),
                sp.GetRequiredService<ILogger<PullRequestWorkflow>>(),
                sp.GetRequiredService<IRunBranchProvider>(),
                config.Project.DefaultBranch,
                sp.GetRequiredService<ConflictDetector>(),
                sp.GetRequiredService<IPlatformHostContext>(),
                sp.GetRequiredService<IDevPlatformAuthProvider>(),
                config.Workspace.RootPath);
        });
        services.AddSingleton<IssueWorkflow>();
        services.AddSingleton<ConflictResolver>();

        // PR Merge-Flow Timeline — deterministic snapshot resolver (data path only; UI is follow-up).
        services.AddSingleton<IPrMergeFlowSource, PrMergeFlowResolver>();

        return services;
    }
}
