// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;
using VirtualDevTeam.Core.DevPlatform.Providers.GitHub;
using VirtualDevTeam.Core.DevPlatform.Providers.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.DevPlatform;

/// <summary>
/// DI registration for the dev platform abstraction layer.
/// Registers capability interfaces based on the configured platform.
/// </summary>
public static class DevPlatformServiceExtensions
{
    /// <summary>
    /// Register all platform capability interfaces.
    /// For GitHub (default): wraps the existing IGitHubService via adapters.
    /// For AzureDevOps: registers ADO REST API implementations.
    /// </summary>
    public static IServiceCollection AddDevPlatform(this IServiceCollection services)
    {
        // Register auth provider based on config
        services.AddSingleton<IDevPlatformAuthProvider>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => config.AuthMethod switch
                {
                    DevPlatformAuthMethod.GhCli => ActivatorUtilities.CreateInstance<GhCliAuthProvider>(sp),
                    _ => new PatAuthProvider(
                        sp.GetRequiredService<IOptions<Configuration.VirtualDevTeamConfig>>().Value.Project?.GitHubToken ?? "")
                },
                DevPlatformType.AzureDevOps => config.AuthMethod switch
                {
                    DevPlatformAuthMethod.Pat => new PatAuthProvider(config.AzureDevOps?.Pat ?? ""),
                    DevPlatformAuthMethod.AzureCliBearer => !string.IsNullOrWhiteSpace(config.AzureDevOps?.BearerToken)
                        ? new StaticBearerAuthProvider(config.AzureDevOps.BearerToken)
                        : ActivatorUtilities.CreateInstance<AzureCliBearerProvider>(
                            sp, config.AzureDevOps?.TenantId ?? (object)""),
                    _ => new PatAuthProvider(config.AzureDevOps?.Pat ?? "")
                },
                DevPlatformType.Local => new PatAuthProvider(""), // Local platform needs no auth
                _ => new PatAuthProvider("")
            };
        });

        // Local platform shared context (singleton — holds SQLite + bare repo)
        services.AddSingleton<LocalPlatformSchema>();
        services.AddSingleton<LocalBareRepoManager>();
        services.AddSingleton<LocalPlatformContext>();
        // Eager init at startup — prevents sync-over-async deadlock when Blazor pages trigger CreateConnection
        services.AddHostedService<LocalPlatformInitializer>();

        // Final submission service — only registered in LDP mode.
        // Routes to GitHub or ADO service based on which target platform config is populated.
        services.AddSingleton<IFinalSubmissionService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            if (config.Platform != DevPlatformType.Local)
                return new NoOpFinalSubmissionService();

            // Determine target platform: if ADO config has org/project/repo, target is ADO
            var adoConfig = config.AzureDevOps;
            if (adoConfig is not null
                && !string.IsNullOrWhiteSpace(adoConfig.Organization)
                && !string.IsNullOrWhiteSpace(adoConfig.Project)
                && !string.IsNullOrWhiteSpace(adoConfig.Repository))
            {
                // Check if GitHub repo is also configured — ADO takes priority only when
                // the original Platform was AzureDevOps (before Local override)
                var fullConfig = sp.GetRequiredService<IOptions<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>>().Value;
                var hasGitHub = !string.IsNullOrWhiteSpace(fullConfig.Project.GitHubRepo);

                // If ONLY ADO is configured, or GitHub repo is empty, use ADO
                if (!hasGitHub)
                    return ActivatorUtilities.CreateInstance<AdoFinalSubmissionService>(sp);
            }

            // Default: GitHub final submission
            return ActivatorUtilities.CreateInstance<GitHubFinalSubmissionService>(sp);
        });

        // Register adapters based on configured platform.
        services.AddSingleton<IPullRequestService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubPullRequestAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoPullRequestService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalPullRequestService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IWorkItemService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubWorkItemAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoWorkItemService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalWorkItemService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IRepositoryContentService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubRepositoryContentAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoRepositoryContentService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalRepositoryContentService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IBranchService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubBranchAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoBranchService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalBranchService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IReviewService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubReviewAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoReviewService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalReviewService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IPlatformInfoService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubPlatformInfoAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoPlatformInfoService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalPlatformInfoService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IPlatformHostContext>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubHostContext>(sp),
                DevPlatformType.AzureDevOps => ActivatorUtilities.CreateInstance<AdoHostContext>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalPlatformInfoService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IRepositoryManagementService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubRepositoryManagementAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoRepositoryManagementService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalRepositoryManagementService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        services.AddSingleton<IWorkItemSearchService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubWorkItemSearchAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoWorkItemSearchService>(sp),
                DevPlatformType.Local => ActivatorUtilities.CreateInstance<LocalWorkItemSearchService>(sp),
                _ => throw new ArgumentOutOfRangeException(nameof(config.Platform))
            };
        });

        // Cross-cutting services that use capability interfaces
        services.AddSingleton<MergeCloseoutService>();
        services.AddSingleton<IDocumentReferenceResolver, DocumentReferenceResolver>();

        return services;
    }

    /// <summary>
    /// Create an ADO service instance with a new HttpClient and the auth provider.
    /// </summary>
    private static T CreateAdoService<T>(IServiceProvider sp) where T : class
    {
        var httpClient = new HttpClient();
        var authProvider = sp.GetRequiredService<IDevPlatformAuthProvider>();
        var config = sp.GetRequiredService<IOptions<Configuration.VirtualDevTeamConfig>>();
        var logger = sp.GetRequiredService<ILogger<T>>();

        return ActivatorUtilities.CreateInstance<T>(sp, httpClient, authProvider, config, logger);
    }
}
