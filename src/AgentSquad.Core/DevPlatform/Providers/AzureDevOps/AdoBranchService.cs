using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps branch operations using Git Refs API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/refs
/// </summary>
public sealed class AdoBranchService : AdoHttpClientBase, IBranchService
{
    private readonly ILogger<AdoBranchService> _logger;

    public AdoBranchService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.AgentSquadConfig> config,
        ILogger<AdoBranchService> logger)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
    }

    public async Task CreateAsync(string branchName, string? fromBranch = null, CancellationToken ct = default)
    {
        fromBranch ??= "main";

        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs",
            $"filter=heads/{fromBranch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var sourceRef = refs?.Value.FirstOrDefault()
            ?? throw new InvalidOperationException($"Source branch '{fromBranch}' not found in ADO repo");

        var createUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs");
        var payload = new[]
        {
            new
            {
                name = $"refs/heads/{branchName}",
                oldObjectId = new string('0', 40),
                newObjectId = sourceRef.ObjectId
            }
        };

        await PostAsync<object>(createUrl, payload, ct);
        _logger.LogInformation("Created ADO branch {Branch} from {Source}", branchName, fromBranch);
    }

    public async Task<bool> ExistsAsync(string branchName, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs",
            $"filter=heads/{branchName}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(url, ct);
        return refs?.Value.Any() == true;
    }

    public async Task DeleteAsync(string branchName, CancellationToken ct = default)
    {
        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs",
            $"filter=heads/{branchName}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var branchRef = refs?.Value.FirstOrDefault();
        if (branchRef is null)
        {
            _logger.LogDebug("Branch {Branch} doesn't exist, nothing to delete", branchName);
            return;
        }

        var deleteUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs");
        var payload = new[]
        {
            new
            {
                name = $"refs/heads/{branchName}",
                oldObjectId = branchRef.ObjectId,
                newObjectId = new string('0', 40)
            }
        };

        await PostAsync<object>(deleteUrl, payload, ct);
        _logger.LogInformation("Deleted ADO branch {Branch}", branchName);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs",
            "filter=heads/" + (prefix ?? ""));
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(url, ct);

        return refs?.Value
            .Select(r => r.Name.Replace("refs/heads/", ""))
            .ToList() ?? new List<string>();
    }

    public async Task CleanToBaselineAsync(
        IReadOnlyList<string> preserveFiles, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        branch ??= "main";

        var treeUrl = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/items",
            $"recursionLevel=Full&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch");
        var tree = await GetAsync<AdoListResponse<AdoGitItem>>(treeUrl, ct);

        if (tree?.Value is null) return;

        var preserveSet = preserveFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesToDelete = tree.Value
            .Where(i => i.GitObjectType == "blob" && !preserveSet.Contains(i.Path.TrimStart('/')))
            .Select(i => i.Path)
            .ToList();

        if (filesToDelete.Count == 0) return;

        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs", $"filter=heads/{branch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var oldObjectId = refs?.Value.FirstOrDefault()?.ObjectId ?? new string('0', 40);

        var changes = filesToDelete.Select(path => new AdoGitChange
        {
            ChangeType = "delete",
            Item = new AdoGitItemDescriptor { Path = path }
        }).ToList();

        var push = new AdoGitPushRequest
        {
            RefUpdates = [new AdoGitRefUpdate { Name = $"refs/heads/{branch}", OldObjectId = oldObjectId }],
            Commits = [new AdoGitCommit { Comment = commitMessage, Changes = changes }]
        };

        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");
        await PostAsync<object>(pushUrl, push, ct);
        _logger.LogInformation("Cleaned branch {Branch}: deleted {Count} files, preserved {Preserve}",
            branch, filesToDelete.Count, preserveFiles.Count);
    }
}
