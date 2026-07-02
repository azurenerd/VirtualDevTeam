using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Platform identity, URL generation, and metadata for the Local platform.
/// Implements both IPlatformHostContext (URL generation) and IPlatformInfoService (metadata).
/// </summary>
public sealed class LocalPlatformInfoService : IPlatformHostContext, IPlatformInfoService
{
    private readonly LocalPlatformContext _ctx;

    public LocalPlatformInfoService(LocalPlatformContext ctx)
    {
        _ctx = ctx;
    }

    // ── IPlatformHostContext ──
    public string DefaultBranch => _ctx.DefaultBranch;
    public string GetCloneUrl(string token) => $"local://{_ctx.RepoName}";
    public string GetPullRequestWebUrl(int prId) => $"/repository/pull-request/{prId}";
    public string GetWorkItemWebUrl(int workItemId) => $"/repository/issue/{workItemId}";
    public string GetRawFileUrl(string path, string branch) => $"/repository/files?path={Uri.EscapeDataString(path)}&branch={branch}";
    public string GetFileWebUrl(string path, string branch) => $"/repository/files?path={Uri.EscapeDataString(path)}&branch={branch}";

    // ── IPlatformInfoService ──
    public string PlatformName => "Local";
    public string RepositoryDisplayName => _ctx.RepoName;
    public PlatformCapabilities Capabilities => new()
    {
        SupportsWorkItemHierarchy = false,
        SupportsWorkItemDependencies = false,
        SupportsWorkItemDeletion = true,
        SupportsInlineReviewComments = true,
        SupportsLabelsOnWorkItems = true,
        SupportsLabelsOnPullRequests = true,
        SupportedWorkItemTypes = ["Issue"],
        SupportsAtomicTreeReset = false,
    };

    public Task<PlatformRateLimitInfo> GetRateLimitAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new PlatformRateLimitInfo
        {
            Remaining = 999999,
            Limit = 999999,
            ResetAt = DateTime.UtcNow.AddHours(1),
            PlatformName = "Local",
        });
    }
}
