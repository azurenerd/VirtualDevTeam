using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps Pull Request operations using the Git PR REST API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/pull-requests
/// </summary>
public sealed class AdoPullRequestService : AdoHttpClientBase, IPullRequestService
{
    private readonly ILogger<AdoPullRequestService> _logger;

    public AdoPullRequestService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.AgentSquadConfig> config,
        ILogger<AdoPullRequestService> logger)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
    }

    public async Task<PlatformPullRequest> CreateAsync(
        string title, string body, string headBranch, string baseBranch,
        IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests");
        var payload = new
        {
            sourceRefName = $"refs/heads/{headBranch}",
            targetRefName = $"refs/heads/{baseBranch}",
            title,
            description = body
        };

        var result = await PostAsync<AdoPullRequest>(url, payload, ct)
            ?? throw new InvalidOperationException("ADO returned null for PR creation");

        if (labels.Count > 0)
            await AddLabelsInternalAsync(result.PullRequestId, labels, ct);

        _logger.LogInformation("Created ADO PR #{Id}: {Title}", result.PullRequestId, title);
        return AdoModelMapper.ToPlatform(result, Organization, Project);
    }

    public async Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{id}");
        var pr = await GetAsync<AdoPullRequest>(url, ct);
        return pr is not null ? AdoModelMapper.ToPlatform(pr, Organization, Project) : null;
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests",
            "searchCriteria.status=active");
        var response = await GetAsync<AdoListResponse<AdoPullRequest>>(url, ct);
        return response?.Value.Select(p => AdoModelMapper.ToPlatform(p, Organization, Project)).ToList()
            ?? new List<PlatformPullRequest>();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests",
            "searchCriteria.status=all&$top=200");
        var response = await GetAsync<AdoListResponse<AdoPullRequest>>(url, ct);
        return response?.Value.Select(p => AdoModelMapper.ToPlatform(p, Organization, Project)).ToList()
            ?? new List<PlatformPullRequest>();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests",
            "searchCriteria.status=completed&$top=200");
        var response = await GetAsync<AdoListResponse<AdoPullRequest>>(url, ct);
        return response?.Value.Select(p => AdoModelMapper.ToPlatform(p, Organization, Project)).ToList()
            ?? new List<PlatformPullRequest>();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(
        string agentName, CancellationToken ct = default)
    {
        var all = await ListAllAsync(ct);
        return all.Where(p => p.Title.StartsWith($"{agentName}:", StringComparison.OrdinalIgnoreCase)
            || p.AssignedAgent?.Equals(agentName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{id}");
        var payload = new Dictionary<string, object?>();
        if (title is not null) payload["title"] = title;
        if (body is not null) payload["description"] = body;

        if (payload.Count > 0)
            await PatchAsync<AdoPullRequest>(url, payload, ct);

        if (labels is not null)
            await SetLabelsInternalAsync(id, labels, ct);
    }

    public async Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default)
    {
        var pr = await GetAsync<AdoPullRequest>(
            BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{id}"), ct);
        if (pr is null) throw new InvalidOperationException($"PR #{id} not found");

        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{id}");
        var payload = new
        {
            status = "completed",
            lastMergeSourceCommit = new { commitId = pr.LastMergeSourceCommit?.CommitId ?? "" },
            completionOptions = new
            {
                mergeCommitMessage = commitMessage ?? $"Merge PR #{id}",
                deleteSourceBranch = true,
                mergeStrategy = "squash"
            }
        };

        await PatchAsync<AdoPullRequest>(url, payload, ct);
        _logger.LogInformation("Merged ADO PR #{Number}", id);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{id}");
        await PatchAsync<AdoPullRequest>(url, new { status = "abandoned" }, ct);
        _logger.LogInformation("Closed (abandoned) ADO PR #{Number}", id);
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default)
    {
        var diffs = await GetFileDiffsAsync(prId, ct);
        return diffs.Select(d => d.FileName).ToList();
    }

    public async Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default)
    {
        var iterUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/iterations");
        var iterations = await GetAsync<AdoListResponse<AdoPrIteration>>(iterUrl, ct);
        var lastIter = iterations?.Value.LastOrDefault();
        if (lastIter is null) return new List<PlatformFileDiff>();

        var changesUrl = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/iterations/{lastIter.Id}/changes");
        var changes = await GetAsync<AdoListResponse<AdoPrChange>>(changesUrl, ct);

        return changes?.Value.Select(c => new PlatformFileDiff
        {
            FileName = c.Item?.Path ?? "",
            Status = c.ChangeType ?? "edit"
        }).ToList() ?? new List<PlatformFileDiff>();
    }

    public async Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default)
    {
        var commits = await GetCommitsWithDatesAsync(prId, ct);
        return commits.Select(c => c.Message).ToList();
    }

    public async Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prId}/commits");
        var commits = await GetAsync<AdoListResponse<AdoCommitRef>>(url, ct);
        return commits?.Value.Select(c => new PlatformCommitInfo
        {
            Sha = c.CommitId,
            Message = c.Comment ?? ""
        }).ToList() ?? new List<PlatformCommitInfo>();
    }

    public Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default)
    {
        _logger.LogDebug("UpdateBranch not needed for ADO PR #{Number}", prId);
        return Task.FromResult(true);
    }

    public Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default)
    {
        _logger.LogDebug("RebaseBranch not supported for ADO PR #{Number}", prId);
        return Task.FromResult(false);
    }

    private async Task AddLabelsInternalAsync(int prNumber, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prNumber}/labels");
        foreach (var label in labels)
        {
            await PostAsync<object>(url, new { name = label }, ct);
        }
    }

    private async Task SetLabelsInternalAsync(int prNumber, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var url = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prNumber}/labels");
        var current = await GetAsync<AdoListResponse<AdoPrLabel>>(url, ct);

        if (current?.Value is not null)
        {
            foreach (var existing in current.Value.Where(l => !labels.Contains(l.Name)))
            {
                var deleteUrl = BuildUrl(
                    $"{Project}/_apis/git/repositories/{Repository}/pullrequests/{prNumber}/labels/{Uri.EscapeDataString(existing.Name)}");
                await DeleteAsync(deleteUrl, ct);
            }
        }

        var existingNames = current?.Value?.Select(l => l.Name).ToHashSet() ?? new();
        foreach (var label in labels.Where(l => !existingNames.Contains(l)))
        {
            await PostAsync<object>(url, new { name = label }, ct);
        }
    }
}
