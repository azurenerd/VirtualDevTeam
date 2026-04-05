using Microsoft.Extensions.DependencyInjection;

namespace AgentSquad.Core.GitHub;

public static class GitHubServiceExtensions
{
    public static IServiceCollection AddGitHubIntegration(this IServiceCollection services)
    {
        services.AddSingleton<RateLimitManager>();
        services.AddSingleton<IGitHubService, GitHubService>();
        return services;
    }
}
