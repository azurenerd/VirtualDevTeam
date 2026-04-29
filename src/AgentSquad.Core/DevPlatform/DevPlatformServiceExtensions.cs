using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Config;
using AgentSquad.Core.DevPlatform.Providers.AzureDevOps;
using AgentSquad.Core.DevPlatform.Providers.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform;

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
                DevPlatformType.GitHub => new PatAuthProvider(
                    sp.GetRequiredService<IOptions<Configuration.AgentSquadConfig>>().Value.Project?.GitHubToken ?? ""),
                DevPlatformType.AzureDevOps => config.AuthMethod switch
                {
                    DevPlatformAuthMethod.Pat => new PatAuthProvider(config.AzureDevOps?.Pat ?? ""),
                    DevPlatformAuthMethod.AzureCliBearer => !string.IsNullOrWhiteSpace(config.AzureDevOps?.BearerToken)
                        ? new StaticBearerAuthProvider(config.AzureDevOps.BearerToken)
                        : ActivatorUtilities.CreateInstance<AzureCliBearerProvider>(
                            sp, config.AzureDevOps?.TenantId ?? (object)""),
                    _ => new PatAuthProvider(config.AzureDevOps?.Pat ?? "")
                },
                _ => new PatAuthProvider("")
            };
        });

        // Register adapters based on configured platform.
        services.AddSingleton<IPullRequestService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DevPlatformConfig>>().Value;
            return config.Platform switch
            {
                DevPlatformType.GitHub => ActivatorUtilities.CreateInstance<GitHubPullRequestAdapter>(sp),
                DevPlatformType.AzureDevOps => CreateAdoService<AdoPullRequestService>(sp),
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
        var config = sp.GetRequiredService<IOptions<Configuration.AgentSquadConfig>>();
        var logger = sp.GetRequiredService<ILogger<T>>();

        return ActivatorUtilities.CreateInstance<T>(sp, httpClient, authProvider, config, logger);
    }
}
