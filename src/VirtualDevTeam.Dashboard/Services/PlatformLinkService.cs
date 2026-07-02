using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Config;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Default <see cref="IPlatformLinkService"/> — flag-driven via
/// <c>Dashboard:InternalNavigationDefault</c> on <see cref="VirtualDevTeamConfig"/>.
/// </summary>
public sealed class PlatformLinkService : IPlatformLinkService
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;

    public PlatformLinkService(IOptionsMonitor<VirtualDevTeamConfig> config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public bool InternalNavigationDefault =>
        _config.CurrentValue?.Dashboard?.InternalNavigationDefault ?? true;

    public string PlatformDisplayName =>
        _config.CurrentValue?.DevPlatform?.Platform switch
        {
            DevPlatformType.AzureDevOps => "Azure DevOps",
            DevPlatformType.Local => "Local",
            _ => "GitHub"
        };

    public string BuildPullRequestUrl(int prNumber, string? platformUrl = null)
    {
        if (!InternalNavigationDefault && !string.IsNullOrWhiteSpace(platformUrl))
            return platformUrl!;
        return $"/repository/pull-request/{prNumber}";
    }

    public string BuildIssueUrl(int issueNumber, string? platformUrl = null)
    {
        if (!InternalNavigationDefault && !string.IsNullOrWhiteSpace(platformUrl))
            return platformUrl!;
        return $"/repository/issue/{issueNumber}";
    }

    public string BuildFileUrl(string path, string? branch = null, string? platformUrl = null)
    {
        if (!InternalNavigationDefault && !string.IsNullOrWhiteSpace(platformUrl))
            return platformUrl!;
        var trimmed = (path ?? string.Empty).TrimStart('/');
        // RepositoryFiles.razor reads branch from query string (?branch=...) and falls back to
        // the configured default branch.  Including it explicitly keeps deep-links stable.
        var qs = string.IsNullOrWhiteSpace(branch) ? "" : $"?branch={Uri.EscapeDataString(branch!)}";
        return $"/repository/files/{trimmed}{qs}";
    }

    public bool IsInternal(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url!.StartsWith("/", StringComparison.Ordinal);

    public string? TargetForUrl(string? url) => IsInternal(url) ? null : "_blank";
}
