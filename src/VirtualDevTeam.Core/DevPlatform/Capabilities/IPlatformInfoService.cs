using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

/// <summary>
/// Platform metadata and rate limiting.
/// </summary>
public interface IPlatformInfoService
{
    /// <summary>"GitHub" or "AzureDevOps".</summary>
    string PlatformName { get; }

    /// <summary>Display name for the repository (e.g., "owner/repo" or "org/project/repo").</summary>
    string RepositoryDisplayName { get; }

    /// <summary>Platform capabilities (feature flags).</summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>Current rate limit status.</summary>
    Task<PlatformRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default);
}
