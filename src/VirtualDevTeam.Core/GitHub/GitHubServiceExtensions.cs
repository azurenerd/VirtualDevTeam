// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using Microsoft.Extensions.DependencyInjection;

namespace VirtualDevTeam.Core.GitHub;

public static class GitHubServiceExtensions
{
    public static IServiceCollection AddGitHubIntegration(this IServiceCollection services)
    {
        services.AddSingleton<RateLimitManager>();
        services.AddSingleton<IGitHubService, GitHubService>();
        return services;
    }
}
