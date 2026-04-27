using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;

namespace AgentSquad.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts the existing <see cref="IGitHubService"/> to the platform-agnostic
/// <see cref="IPullRequestService"/> interface. Zero behavior change — pure delegation.
/// </summary>
public sealed class GitHubPullRequestAdapter : IPullRequestService
{
    private readonly IGitHubService _github;

    public GitHubPullRequestAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public async Task<PlatformPullRequest> CreateAsync(
        string title, string body, string headBranch, string baseBranch,
        IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        var pr = await _github.CreatePullRequestAsync(title, body, headBranch, baseBranch, labels.ToArray(), ct);
        return GitHubModelMapper.ToPlatform(pr);
    }

    public async Task<PlatformPullRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        var pr = await _github.GetPullRequestAsync(id, ct);
        return pr is null ? null : GitHubModelMapper.ToPlatform(pr);
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListOpenAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetOpenPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListAllAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetAllPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListMergedAsync(CancellationToken ct = default)
    {
        var prs = await _github.GetMergedPullRequestsAsync(ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<PlatformPullRequest>> ListForAgentAsync(string agentName, CancellationToken ct = default)
    {
        var prs = await _github.GetPullRequestsForAgentAsync(agentName, ct);
        return prs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        await _github.UpdatePullRequestAsync(id, title, body, labels?.ToArray(), ct);
    }

    public async Task MergeAsync(int id, string? commitMessage = null, CancellationToken ct = default)
    {
        await _github.MergePullRequestAsync(id, commitMessage, ct);
    }

    public async Task CloseAsync(int id, CancellationToken ct = default)
    {
        await _github.ClosePullRequestAsync(id, ct);
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(int prId, CancellationToken ct = default)
    {
        return await _github.GetPullRequestChangedFilesAsync(prId, ct);
    }

    public async Task<IReadOnlyList<PlatformFileDiff>> GetFileDiffsAsync(int prId, CancellationToken ct = default)
    {
        var diffs = await _github.GetPullRequestFilesWithPatchAsync(prId, ct);
        return diffs.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<IReadOnlyList<string>> GetCommitMessagesAsync(int prId, CancellationToken ct = default)
    {
        return await _github.GetPullRequestCommitMessagesAsync(prId, ct);
    }

    public async Task<IReadOnlyList<PlatformCommitInfo>> GetCommitsWithDatesAsync(int prId, CancellationToken ct = default)
    {
        var commits = await _github.GetPullRequestCommitsWithDatesAsync(prId, ct);
        return commits.Select(GitHubModelMapper.ToPlatform).ToList();
    }

    public async Task<bool> IsBehindBaseAsync(int prId, CancellationToken ct = default)
    {
        return await _github.IsBranchBehindMainAsync(prId, ct);
    }

    public async Task<bool> UpdateBranchAsync(int prId, CancellationToken ct = default)
    {
        return await _github.UpdatePullRequestBranchAsync(prId, ct);
    }

    public async Task<bool> RebaseBranchAsync(int prId, CancellationToken ct = default)
    {
        return await _github.RebaseBranchOnMainAsync(prId, ct);
    }

    public async Task LinkWorkItemAsync(int prId, int workItemId, CancellationToken ct = default)
    {
        // GitHub: ensure PR body contains "Closes #X" for auto-close on merge.
        // If already present, this is a no-op (idempotent).
        var pr = await _github.GetPullRequestAsync(prId, ct);
        if (pr is null) return;

        var body = pr.Body ?? "";

        // Use regex-parsed linked IDs to avoid numeric prefix false positives
        // (e.g., "Closes #12" would falsely match when checking for #123)
        var existingIds = await GetLinkedWorkItemIdsAsync(prId, ct);
        if (existingIds.Contains(workItemId))
            return; // Already linked

        var closePattern = $"Closes #{workItemId}";
        var updatedBody = string.IsNullOrWhiteSpace(body)
            ? closePattern
            : $"{body}\n\n{closePattern}";

        await _github.UpdatePullRequestAsync(prId, body: updatedBody, ct: ct);
    }

    public async Task<IReadOnlyList<int>> GetLinkedWorkItemIdsAsync(int prId, CancellationToken ct = default)
    {
        // GitHub: parse "Closes #X", "Fixes #X", "Resolves #X" patterns from PR body
        var pr = await _github.GetPullRequestAsync(prId, ct);
        if (pr?.Body is null) return Array.Empty<int>();

        var ids = new List<int>();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            pr.Body,
            @"(?:closes|fixes|resolves)\s+#(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }
}
