using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps platform info — rate limit tracking and display name.
/// ADO doesn't expose rate limit headers the same way GitHub does,
/// so we track request counts and 429 responses internally.
/// </summary>
public sealed class AdoPlatformInfoService : AdoHttpClientBase, IPlatformInfoService
{
    public AdoPlatformInfoService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.VirtualDevTeamConfig> config,
        ILogger<AdoPlatformInfoService> logger)
        : base(http, authProvider, config, logger)
    {
    }

    public string PlatformName => "Azure DevOps";

    public string RepositoryDisplayName => $"{Organization}/{Project}/{Repository}";

    public PlatformCapabilities Capabilities => PlatformCapabilities.AzureDevOps;

    public Task<PlatformRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new PlatformRateLimitInfo
        {
            Remaining = Remaining,
            Limit = 200,
            ResetAt = WindowResetUtc,
            TotalApiCalls = TotalCalls,
            IsRateLimited = IsRateLimited,
            PlatformName = "Azure DevOps"
        });
    }
}
