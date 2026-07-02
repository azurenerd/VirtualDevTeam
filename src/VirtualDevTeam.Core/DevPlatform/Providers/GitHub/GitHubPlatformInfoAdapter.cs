// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Provides platform metadata and rate limiting for GitHub.
/// </summary>
public sealed class GitHubPlatformInfoAdapter : IPlatformInfoService
{
    private readonly IGitHubService _github;

    public GitHubPlatformInfoAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public string PlatformName => "GitHub";
    public string RepositoryDisplayName => _github.RepositoryFullName;
    public PlatformCapabilities Capabilities => PlatformCapabilities.GitHub;

    public async Task<PlatformRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
    {
        var info = await _github.GetRateLimitAsync(ct);
        return GitHubModelMapper.ToPlatform(info);
    }
}
